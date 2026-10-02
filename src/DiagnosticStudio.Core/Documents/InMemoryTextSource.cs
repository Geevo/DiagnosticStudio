using System.Text;

namespace DiagnosticStudio.Core.Documents;

/// <summary>Text that exists only in memory, such as a re-indented view of a file, presented as lines.</summary>
public sealed class InMemoryTextSource : ITextLineSource
{
    private readonly string[] _lines;

    public InMemoryTextSource(string text, string encodingName)
    {
        EncodingName = encodingName;
        ByteLength = Encoding.UTF8.GetByteCount(text);

        var lines = text.Split('\n');
        // A trailing newline ends the last line; it does not start another.
        _lines = lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public int LineCount => _lines.Length;

    public long ByteLength { get; }

    public string EncodingName { get; }

    public IReadOnlyList<string> ReadLines(int startLine, int count)
    {
        if (startLine < 0 || startLine >= _lines.Length || count <= 0)
        {
            return Array.Empty<string>();
        }

        return new ArraySegment<string>(_lines, startLine, Math.Min(count, _lines.Length - startLine));
    }

    public IEnumerable<string> EnumerateLines(int startLine = 0)
    {
        for (var i = Math.Max(0, startLine); i < _lines.Length; i++)
        {
            yield return _lines[i];
        }
    }
}
