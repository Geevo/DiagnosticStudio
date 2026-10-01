using System.Text;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Line-addressable view of a text file. One sequential pass records the byte offset of every
/// <see cref="CheckpointInterval"/>th line; reads seek to the nearest checkpoint. Nothing but the
/// checkpoints stays in memory and no file handle is held between calls.
/// </summary>
public sealed class IndexedTextFile : ITextLineSource
{
    public const int CheckpointInterval = 128;
    public const int DefaultMaxLineChars = 20_000;
    public const string TruncationMarker = " … [line truncated]";

    private const int DefaultBufferSize = 1 << 20;

    private readonly string _path;
    private readonly TextEncodingInfo _encoding;
    private readonly long[] _checkpoints;
    private readonly int _bufferSize;
    private readonly int _maxLineChars;

    private IndexedTextFile(
        string path,
        TextEncodingInfo encoding,
        long[] checkpoints,
        int lineCount,
        long byteLength,
        int bufferSize,
        int maxLineChars)
    {
        _path = path;
        _encoding = encoding;
        _checkpoints = checkpoints;
        LineCount = lineCount;
        ByteLength = byteLength;
        _bufferSize = bufferSize;
        _maxLineChars = maxLineChars;
    }

    public int LineCount { get; }
    public long ByteLength { get; }
    public string EncodingName => _encoding.Name;

    public static Task<IndexedTextFile> OpenAsync(
        string path,
        CancellationToken cancellationToken,
        int bufferSize = DefaultBufferSize,
        int maxLineChars = DefaultMaxLineChars) =>
        Task.Run(() => Open(path, cancellationToken, bufferSize, maxLineChars), cancellationToken);

    public static IndexedTextFile Open(
        string path,
        CancellationToken cancellationToken = default,
        int bufferSize = DefaultBufferSize,
        int maxLineChars = DefaultMaxLineChars)
    {
        using var stream = OpenRead(path);
        var length = stream.Length;

        var head = new byte[Math.Min(length, 4096)];
        stream.ReadExactly(head);
        var encoding = TextEncodingInfo.Detect(head);

        var checkpoints = new List<long>();
        var lineCount = 0;
        var unit = encoding.UnitSize;

        // Start of the line being scanned; -1 until the first unit of content is seen.
        long lineStart = encoding.BomLength;
        stream.Position = lineStart;

        // Buffer size must be a multiple of the code unit so units never straddle two reads.
        var buffer = new byte[Math.Max(unit, bufferSize - (bufferSize % unit))];
        var carry = 0;
        long position = lineStart;

        if (lineStart < length)
        {
            checkpoints.Add(lineStart);
            lineCount = 1;
        }

        int read;
        while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = carry + read;
            var usable = available - (available % unit);

            var from = 0;
            int newline;
            while ((newline = encoding.FindNewline(buffer, from, usable)) >= 0)
            {
                from = newline + unit;
                lineStart = position + from;
                if (lineStart < length)
                {
                    if (lineCount % CheckpointInterval == 0)
                    {
                        checkpoints.Add(lineStart);
                    }

                    lineCount++;
                }
            }

            carry = available - usable;
            if (carry > 0)
            {
                Buffer.BlockCopy(buffer, usable, buffer, 0, carry);
            }

            position += usable;
        }

        return new IndexedTextFile(path, encoding, checkpoints.ToArray(), lineCount, length, bufferSize, maxLineChars);
    }

    public IReadOnlyList<string> ReadLines(int startLine, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startLine);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var result = new List<string>(Math.Min(count, Math.Max(0, LineCount - startLine)));
        if (count == 0 || startLine >= LineCount)
        {
            return result;
        }

        foreach (var line in EnumerateLines(startLine))
        {
            result.Add(line);
            if (result.Count == count)
            {
                break;
            }
        }

        return result;
    }

    public IEnumerable<string> EnumerateLines(int startLine = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startLine);
        if (startLine >= LineCount)
        {
            yield break;
        }

        var checkpoint = startLine / CheckpointInterval;
        var skip = startLine - (checkpoint * CheckpointInterval);

        foreach (var raw in ScanLines(_checkpoints[checkpoint]))
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }

            yield return Decode(raw);
        }
    }

    private string Decode(byte[] line)
    {
        var text = _encoding.Encoding.GetString(line);
        var end = text.Length;
        if (end > 0 && text[end - 1] == '\r')
        {
            end--;
        }

        if (end > _maxLineChars)
        {
            return string.Concat(text.AsSpan(0, _maxLineChars), TruncationMarker);
        }

        return end == text.Length ? text : text[..end];
    }

    // Yields raw line bytes (without the terminator) from the given offset. Lines longer than the
    // display cap are cut off in bytes so a multi-megabyte line is never fully buffered.
    private IEnumerable<byte[]> ScanLines(long offset)
    {
        using var stream = OpenRead(_path);
        stream.Position = offset;

        var unit = _encoding.UnitSize;
        var buffer = new byte[Math.Max(unit, _bufferSize - (_bufferSize % unit))];
        var maxLineBytes = _maxLineChars * (unit == 2 ? 2 : 4) + 8;
        var line = new List<byte>(256);
        var carry = 0;

        int read;
        while ((read = stream.Read(buffer, carry, buffer.Length - carry)) > 0)
        {
            var available = carry + read;
            var usable = available - (available % unit);
            var segmentStart = 0;

            int newline;
            while ((newline = _encoding.FindNewline(buffer, segmentStart, usable)) >= 0)
            {
                Append(line, buffer, segmentStart, newline - segmentStart, maxLineBytes);
                yield return line.ToArray();
                line.Clear();
                segmentStart = newline + unit;
            }

            Append(line, buffer, segmentStart, usable - segmentStart, maxLineBytes);

            carry = available - usable;
            if (carry > 0)
            {
                Buffer.BlockCopy(buffer, usable, buffer, 0, carry);
            }
        }

        // Final line without a terminator. A trailing terminator leaves nothing buffered.
        if (line.Count > 0)
        {
            yield return line.ToArray();
        }
    }

    private static void Append(List<byte> line, byte[] buffer, int start, int length, int maxBytes)
    {
        var room = maxBytes - line.Count;
        if (length <= 0 || room <= 0)
        {
            return;
        }

        line.AddRange(new ArraySegment<byte>(buffer, start, Math.Min(length, room)));
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
}

/// <summary>Detected text encoding and how to find line terminators in its raw bytes.</summary>
internal sealed class TextEncodingInfo
{
    private readonly bool _utf16LittleEndian;
    private readonly bool _utf16BigEndian;

    private TextEncodingInfo(string name, Encoding encoding, int bomLength, bool le, bool be)
    {
        Name = name;
        Encoding = encoding;
        BomLength = bomLength;
        _utf16LittleEndian = le;
        _utf16BigEndian = be;
    }

    public string Name { get; }
    public Encoding Encoding { get; }
    public int BomLength { get; }
    public int UnitSize => _utf16LittleEndian || _utf16BigEndian ? 2 : 1;

    /// <summary>
    /// Index of the next line feed code unit in <c>buffer[start..end)</c>, or -1. For UTF-16 the scan stays
    /// aligned to code units relative to <paramref name="start"/>, which callers keep unit-aligned.
    /// </summary>
    public int FindNewline(byte[] buffer, int start, int end)
    {
        if (!_utf16LittleEndian && !_utf16BigEndian)
        {
            var found = buffer.AsSpan(start, end - start).IndexOf((byte)0x0A);
            return found < 0 ? -1 : start + found;
        }

        for (var i = start; i + 1 < end; i += 2)
        {
            var isNewline = _utf16LittleEndian
                ? buffer[i] == 0x0A && buffer[i + 1] == 0
                : buffer[i] == 0 && buffer[i + 1] == 0x0A;
            if (isNewline)
            {
                return i;
            }
        }

        return -1;
    }

    public static TextEncodingInfo Detect(byte[] head)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return new("UTF-8 (BOM)", new UTF8Encoding(false), 3, false, false);
        }

        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return new("UTF-16 LE", new UnicodeEncoding(false, false), 2, true, false);
        }

        if (head.Length >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return new("UTF-16 BE", new UnicodeEncoding(true, false), 2, false, true);
        }

        // BOM-less UTF-16: mostly-ASCII text has a NUL in every other byte.
        if (head.Length >= 4)
        {
            var even = 0;
            var odd = 0;
            for (var i = 0; i < head.Length; i++)
            {
                if (head[i] == 0)
                {
                    if ((i & 1) == 0)
                    {
                        even++;
                    }
                    else
                    {
                        odd++;
                    }
                }
            }

            var half = head.Length / 2;
            if (odd > half * 0.6 && even == 0)
            {
                return new("UTF-16 LE", new UnicodeEncoding(false, false), 0, true, false);
            }

            if (even > half * 0.6 && odd == 0)
            {
                return new("UTF-16 BE", new UnicodeEncoding(true, false), 0, false, true);
            }
        }

        return new("UTF-8", new UTF8Encoding(false), 0, false, false);
    }
}
