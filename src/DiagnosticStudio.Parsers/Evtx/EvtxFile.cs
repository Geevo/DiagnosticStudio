using System.Text;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Parsers.Evtx;

/// <summary>
/// An offline <c>.evtx</c> file. Opening makes one pass that records a compact per-event index (31 bytes per
/// event); event content is decoded from its 64 KiB chunk only when asked for. Problems in individual chunks
/// or records are collected as issues and never abort the file.
/// </summary>
public sealed class EvtxFile : IEventLogSource
{
    public const int FileHeaderSize = 4096;
    public const int ChunkSize = 65536;
    public const int ChunkHeaderSize = 512;
    public const int MaxRetainedIssues = 500;
    private const int ChunkCacheCapacity = 12;
    private const string UnreadableProvider = "(unreadable record)";

    private static readonly byte[] FileSignature = "ElfFile\0"u8.ToArray();
    private static readonly byte[] ChunkSignature = "ElfChnk\0"u8.ToArray();

    private readonly string _path;
    private readonly IEventMessageFormatter? _formatter;
    private readonly long[] _recordIds;
    private readonly long[] _timeTicks;
    private readonly long[] _fileOffsets;
    private readonly uint[] _eventIds;
    private readonly ushort[] _providerIds;
    private readonly byte[] _levels;
    private readonly string[] _providerNames;
    private readonly List<EventLogProvider> _providers;

    private readonly object _cacheLock = new();
    private readonly Dictionary<int, byte[]> _chunkCache = new();
    private readonly LinkedList<int> _chunkRecency = new();

    private EvtxFile(
        string path,
        IEventMessageFormatter? formatter,
        IndexData index,
        List<EventLogIssue> issues,
        int totalIssues,
        bool dirty,
        string? version)
    {
        _path = path;
        _formatter = formatter;
        _recordIds = index.RecordIds;
        _timeTicks = index.TimeTicks;
        _fileOffsets = index.FileOffsets;
        _eventIds = index.EventIds;
        _providerIds = index.ProviderIds;
        _levels = index.Levels;
        _providerNames = index.ProviderNames;
        _providers = index.Providers;
        Issues = issues;
        TotalIssueCount = totalIssues;
        IsDirty = dirty;
        FormatVersion = version;
    }

    public int Count => _recordIds.Length;
    public IReadOnlyList<EventLogProvider> Providers => _providers;
    public IReadOnlyList<EventLogIssue> Issues { get; }
    public int TotalIssueCount { get; }
    public bool IsDirty { get; }
    public string? FormatVersion { get; }

    public static Task<EvtxFile> OpenAsync(
        string path,
        IEventMessageFormatter? formatter,
        CancellationToken cancellationToken) =>
        Task.Run(() => Open(path, formatter, cancellationToken), cancellationToken);

    /// <exception cref="EvtxFormatException">The file is not an EVTX file at all.</exception>
    internal static EvtxFile Open(string path, IEventMessageFormatter? formatter, CancellationToken cancellationToken)
    {
        using var stream = OpenRead(path);
        var length = stream.Length;
        if (length < FileHeaderSize)
        {
            throw new EvtxFormatException("The file is too small to be an event log.");
        }

        var header = new byte[FileHeaderSize];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual(FileSignature))
        {
            throw new EvtxFormatException("The file does not start with the EVTX signature.");
        }

        var issues = new List<EventLogIssue>();
        var totalIssues = 0;

        void Report(string message, long? offset = null)
        {
            totalIssues++;
            if (issues.Count < MaxRetainedIssues)
            {
                issues.Add(new EventLogIssue(message, offset));
            }
        }

        var flags = BitConverter.ToUInt32(header, 0x78);
        var dirty = (flags & 1) != 0;
        var version = $"{BitConverter.ToUInt16(header, 0x26)}.{BitConverter.ToUInt16(header, 0x24)}";
        if (Crc32.Compute(header.AsSpan(0, 120)) != BitConverter.ToUInt32(header, 0x7C))
        {
            Report("File header checksum does not match.", 0);
        }

        // Locate chunks by signature: the header's chunk count is unreliable in logs that were not cleanly closed.
        // A partial final chunk (truncated file) is still read: its records up to the cut are valid.
        var chunkCount = (int)Math.Min(int.MaxValue, (length - FileHeaderSize + ChunkSize - 1) / ChunkSize);
        var chunks = new List<ChunkInfo>();
        var headerBuffer = new byte[ChunkHeaderSize];
        for (var i = 0; i < chunkCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = FileHeaderSize + ((long)i * ChunkSize);
            stream.Position = offset;
            if (stream.Read(headerBuffer, 0, ChunkHeaderSize) < ChunkHeaderSize)
            {
                break;
            }

            if (!headerBuffer.AsSpan(0, 8).SequenceEqual(ChunkSignature))
            {
                if (headerBuffer.Any(b => b != 0))
                {
                    Report($"Chunk {i} has an invalid signature and was skipped.", offset);
                }

                continue;
            }

            var firstId = BitConverter.ToUInt64(headerBuffer, 0x18);
            var lastId = BitConverter.ToUInt64(headerBuffer, 0x20);
            if (Crc32.Compute(headerBuffer.AsSpan(0, 0x78)) is var c1
                && Crc32.Compute(headerBuffer.AsSpan(0x80, ChunkHeaderSize - 0x80), c1) != BitConverter.ToUInt32(headerBuffer, 0x7C))
            {
                Report($"Chunk {i} header checksum does not match; parsing it anyway.", offset);
            }

            chunks.Add(new ChunkInfo(i, offset, firstId, lastId));
        }

        // Ring-buffer logs wrap: read chunks in record-id order so events come out chronologically.
        chunks.Sort((a, b) => a.FirstRecordId != b.FirstRecordId
            ? a.FirstRecordId.CompareTo(b.FirstRecordId)
            : a.Number.CompareTo(b.Number));

        var builder = new IndexBuilder();
        var chunkBuffer = new byte[ChunkSize];

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = chunk.FileOffset;
            var read = stream.Read(chunkBuffer, 0, ChunkSize);
            if (read < ChunkSize)
            {
                Array.Clear(chunkBuffer, read, ChunkSize - read);
                Report($"Chunk {chunk.Number} is truncated.", chunk.FileOffset);
            }

            IndexChunk(chunk, chunkBuffer, builder, Report);
        }

        var index = builder.Build();
        return new EvtxFile(path, formatter, index, issues, totalIssues, dirty, version);
    }

    private static void IndexChunk(ChunkInfo chunk, byte[] data, IndexBuilder builder, Action<string, long?> report)
    {
        var freeSpace = (int)BitConverter.ToUInt32(data, 0x30);
        var end = freeSpace is >= ChunkHeaderSize and <= ChunkSize ? freeSpace : ChunkSize;

        var storedChecksum = BitConverter.ToUInt32(data, 0x34);
        if (end > ChunkHeaderSize && Crc32.Compute(data.AsSpan(ChunkHeaderSize, end - ChunkHeaderSize)) != storedChecksum)
        {
            report($"Chunk {chunk.Number} record checksum does not match; parsing it anyway.", chunk.FileOffset);
        }

        var decoder = new BinXmlDecoder(data);
        var pos = ChunkHeaderSize;
        while (pos + 28 <= end)
        {
            var signature = BitConverter.ToUInt32(data, pos);
            var size = (int)BitConverter.ToUInt32(data, pos + 4);
            if (signature != 0x00002A2A || size < 28 || pos + size > ChunkSize
                || BitConverter.ToUInt32(data, pos + size - 4) != (uint)size)
            {
                report($"Chunk {chunk.Number}: invalid record at offset {pos}; the rest of the chunk was skipped.", chunk.FileOffset + pos);
                return;
            }

            var recordId = (long)BitConverter.ToUInt64(data, pos + 8);
            var fileTime = BitConverter.ToUInt64(data, pos + 16);
            var ticks = FileTimeToTicks(fileTime);

            try
            {
                var root = decoder.DecodeRecord(pos + 24, pos + size - 4);
                var facts = EvtxXml.ReadSystemFacts(root);
                // EventRecordID (what Event Viewer shows) can differ from the file's own sequence number, e.g. in exported logs.
                builder.Add(facts.RecordId ?? recordId, ticks, chunk.FileOffset + pos, facts.EventId, facts.Level,
                    facts.Provider.Length > 0 ? facts.Provider : UnreadableProvider);
            }
            catch (Exception ex) when (ex is EvtxFormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                report($"Record {recordId} could not be decoded: {ex.Message}", chunk.FileOffset + pos);
                builder.Add(recordId, ticks, chunk.FileOffset + pos, 0, 0, UnreadableProvider);
            }

            pos += size;
        }
    }

    private static long FileTimeToTicks(ulong fileTime) =>
        fileTime is > 0 and <= 2650467743999999999UL ? DateTime.FromFileTimeUtc((long)fileTime).Ticks : 0;

    // ---- IEventLogSource ----

    public string ProviderName(ushort providerId) =>
        providerId < _providerNames.Length ? _providerNames[providerId] : string.Empty;

    public EventSummary GetSummary(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new EventSummary(
            index,
            _recordIds[index],
            _timeTicks[index] == 0 ? DateTime.MinValue : new DateTime(_timeTicks[index], DateTimeKind.Utc),
            _levels[index],
            _providerIds[index],
            _eventIds[index]);
    }

    public int FindByRecordId(long recordId)
    {
        var at = Array.BinarySearch(_recordIds, recordId);
        return at >= 0 ? at : -1;
    }

    public EventDetail ReadDetail(int index)
    {
        var summary = GetSummary(index);
        try
        {
            var root = Decode(index);
            return EvtxXml.ToDetail(root, summary, _formatter);
        }
        catch (Exception ex) when (ex is EvtxFormatException or IOException or ArgumentException or IndexOutOfRangeException
                                       or OverflowException or UnauthorizedAccessException)
        {
            return new EventDetail
            {
                Summary = summary,
                Provider = ProviderName(summary.ProviderId),
                Xml = $"<!-- Record {summary.RecordId} could not be decoded: {ex.Message.Replace("--", "- -")} -->",
                DecodeError = ex.Message,
            };
        }
    }

    public string ReadSearchText(int index, bool includeMessage)
    {
        try
        {
            return EvtxXml.SearchText(Decode(index), _eventIds[index], includeMessage ? _formatter : null);
        }
        catch (Exception ex) when (ex is EvtxFormatException or IOException or ArgumentException or IndexOutOfRangeException
                                       or OverflowException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private XmlElem Decode(int index)
    {
        var offset = _fileOffsets[index];
        var chunkNumber = (int)((offset - FileHeaderSize) / ChunkSize);
        var inChunk = (int)(offset - FileHeaderSize - ((long)chunkNumber * ChunkSize));
        var chunk = GetChunk(chunkNumber);

        var size = (int)BitConverter.ToUInt32(chunk, inChunk + 4);
        if (BitConverter.ToUInt32(chunk, inChunk) != 0x00002A2A || size < 28 || inChunk + size > ChunkSize)
        {
            throw new EvtxFormatException("The record header is no longer valid (the file may have changed).");
        }

        return new BinXmlDecoder(chunk).DecodeRecord(inChunk + 24, inChunk + size - 4);
    }

    private byte[] GetChunk(int chunkNumber)
    {
        lock (_cacheLock)
        {
            if (_chunkCache.TryGetValue(chunkNumber, out var cached))
            {
                _chunkRecency.Remove(chunkNumber);
                _chunkRecency.AddFirst(chunkNumber);
                return cached;
            }
        }

        var data = new byte[ChunkSize];
        using (var stream = OpenRead(_path))
        {
            stream.Position = FileHeaderSize + ((long)chunkNumber * ChunkSize);
            var read = stream.Read(data, 0, ChunkSize);
            if (read < ChunkSize)
            {
                Array.Clear(data, read, ChunkSize - read);
            }
        }

        lock (_cacheLock)
        {
            if (!_chunkCache.ContainsKey(chunkNumber))
            {
                _chunkCache[chunkNumber] = data;
                _chunkRecency.AddFirst(chunkNumber);
                while (_chunkRecency.Count > ChunkCacheCapacity)
                {
                    _chunkCache.Remove(_chunkRecency.Last!.Value);
                    _chunkRecency.RemoveLast();
                }
            }

            return _chunkCache[chunkNumber];
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);

    // ---- index construction ----

    private readonly record struct ChunkInfo(int Number, long FileOffset, ulong FirstRecordId, ulong LastRecordId);

    private sealed record IndexData(
        long[] RecordIds,
        long[] TimeTicks,
        long[] FileOffsets,
        uint[] EventIds,
        ushort[] ProviderIds,
        byte[] Levels,
        string[] ProviderNames,
        List<EventLogProvider> Providers);

    private sealed class IndexBuilder
    {
        private long[] _ids = new long[1024];
        private long[] _times = new long[1024];
        private long[] _offsets = new long[1024];
        private uint[] _eventIds = new uint[1024];
        private ushort[] _providers = new ushort[1024];
        private byte[] _levels = new byte[1024];
        private int _count;

        private readonly Dictionary<string, ushort> _providerIds = new(StringComparer.Ordinal);
        private readonly List<string> _providerNames = new();
        private readonly List<int> _providerCounts = new();

        public void Add(long recordId, long ticks, long fileOffset, uint eventId, byte level, string provider)
        {
            if (_count == _ids.Length)
            {
                var size = _count * 2;
                Array.Resize(ref _ids, size);
                Array.Resize(ref _times, size);
                Array.Resize(ref _offsets, size);
                Array.Resize(ref _eventIds, size);
                Array.Resize(ref _providers, size);
                Array.Resize(ref _levels, size);
            }

            if (!_providerIds.TryGetValue(provider, out var providerId))
            {
                // Provider ids are 16-bit; a log with more distinct providers than that shares the last slot.
                if (_providerNames.Count >= ushort.MaxValue)
                {
                    providerId = (ushort)(ushort.MaxValue - 1);
                }
                else
                {
                    providerId = (ushort)_providerNames.Count;
                    _providerIds[provider] = providerId;
                    _providerNames.Add(provider);
                    _providerCounts.Add(0);
                }
            }

            _providerCounts[providerId]++;
            _ids[_count] = recordId;
            _times[_count] = ticks;
            _offsets[_count] = fileOffset;
            _eventIds[_count] = eventId;
            _providers[_count] = providerId;
            _levels[_count] = level;
            _count++;
        }

        public IndexData Build()
        {
            var ids = _ids[.._count];
            var times = _times[.._count];
            var offsets = _offsets[.._count];
            var eventIds = _eventIds[.._count];
            var providers = _providers[.._count];
            var levels = _levels[.._count];

            if (!IsAscending(ids))
            {
                // Wrapped or damaged logs can leave chunks out of order: restore record-id order, dropping repeats.
                var order = Enumerable.Range(0, _count).ToArray();
                Array.Sort(order, (a, b) => ids[a] != ids[b] ? ids[a].CompareTo(ids[b]) : a.CompareTo(b));
                var kept = new List<int>(_count);
                foreach (var i in order)
                {
                    if (kept.Count == 0 || ids[kept[^1]] != ids[i])
                    {
                        kept.Add(i);
                    }
                }

                ids = kept.Select(i => ids[i]).ToArray();
                times = kept.Select(i => times[i]).ToArray();
                offsets = kept.Select(i => offsets[i]).ToArray();
                eventIds = kept.Select(i => eventIds[i]).ToArray();
                providers = kept.Select(i => providers[i]).ToArray();
                levels = kept.Select(i => levels[i]).ToArray();
            }

            var list = _providerNames
                .Select((name, i) => new EventLogProvider((ushort)i, name, _providerCounts[i]))
                .OrderByDescending(p => p.Count)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new IndexData(ids, times, offsets, eventIds, providers, levels, _providerNames.ToArray(), list);
        }

        private static bool IsAscending(long[] ids)
        {
            for (var i = 1; i < ids.Length; i++)
            {
                if (ids[i] <= ids[i - 1])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
