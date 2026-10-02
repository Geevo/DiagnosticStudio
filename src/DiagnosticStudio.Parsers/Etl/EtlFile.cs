using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using DiagnosticStudio.Core.Documents;
using static DiagnosticStudio.Parsers.Etl.EtlNative;

namespace DiagnosticStudio.Parsers.Etl;

/// <summary>
/// The events of an <c>.etl</c> trace file, read with the Windows trace API and held in memory (an ETL file cannot be
/// read at random, only replayed from the start). At most <see cref="MaxEvents"/> are kept; the rest are counted out
/// with an issue saying so. Shown through the event log viewer: a record id is the event's position (1-based).
/// </summary>
public sealed class EtlFile : IEventLogSource, IMemorySizedSource
{
    public const int MaxEvents = 300_000;
    private const ushort OtherProvidersId = ushort.MaxValue;

    private static readonly Guid TraceHeaderProvider = new("68fdd900-4a3e-11d1-84f4-0000f80464e3");

    private readonly List<StoredEvent> _events;
    private readonly List<EventLogProvider> _providers;
    private readonly string[] _providerNames;
    private readonly Guid[] _providerGuids;

    private sealed class StoredEvent
    {
        public long Ticks;
        public byte Level;
        public ushort ProviderId;
        public ushort EventId;
        public byte Version;
        public byte Opcode;
        public ushort Task;
        public ulong Keywords;
        public int ProcessId;
        public int ThreadId;
        public Guid Activity;
        public string? Message;
        public string? TaskName;
        public string? OpcodeName;
        public bool HasSchema;
        public IReadOnlyList<EventDataItem> Data = Array.Empty<EventDataItem>();
    }

    private EtlFile(
        List<StoredEvent> events,
        List<EventLogProvider> providers,
        string[] providerNames,
        Guid[] providerGuids,
        List<string> notes,
        int eventsWithoutSchema,
        bool truncated)
    {
        _events = events;
        _providers = providers;
        _providerNames = providerNames;
        _providerGuids = providerGuids;
        Notes = notes;
        ApproximateMemoryBytes = events.Sum(EstimateBytes);
        EventsWithoutSchema = eventsWithoutSchema;
        IsTruncated = truncated;
    }

    public int Count => _events.Count;

    public IReadOnlyList<EventLogProvider> Providers => _providers;

    /// <summary>What to know about the events shown: a limit reached, a trace that ended early, events without a description.</summary>
    public IReadOnlyList<string> Notes { get; }

    public long ApproximateMemoryBytes { get; private set; }

    /// <summary>Events whose layout is not known on this machine; their payload is shown as raw text and bytes.</summary>
    public int EventsWithoutSchema { get; }

    /// <summary>The file held more than <see cref="MaxEvents"/> events and only the first were read.</summary>
    public bool IsTruncated { get; }

    // ---- reading the file ----

    public static Task<EtlFile> OpenAsync(string path, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => Open(path, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <exception cref="InvalidDataException">The file is not a trace log, or this machine cannot read one.</exception>
    internal static EtlFile Open(string path, CancellationToken cancellationToken, int maxEvents = MaxEvents)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("Reading ETL files needs 64-bit Windows.");
        }

        var collector = new Collector(maxEvents, cancellationToken);
        var gc = GCHandle.Alloc(collector);
        try
        {
            ReadAll(path, gc, collector);
        }
        finally
        {
            gc.Free();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return collector.Build();
    }

    private static unsafe void ReadAll(string path, GCHandle handle, Collector collector)
    {
        fixed (char* name = path)
        {
            var logfile = default(EventTraceLogfile);
            logfile.LogFileName = name;
            logfile.ProcessTraceMode = ProcessTraceModeEventRecord;
            logfile.EventRecordCallback = &OnEvent;
            logfile.BufferCallback = &OnBuffer;
            logfile.Context = (void*)GCHandle.ToIntPtr(handle);

            var trace = OpenTraceW(&logfile);
            if (trace == InvalidHandle)
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidDataException(
                    "The file could not be opened as a trace log" + (error != 0 ? " (Windows error " + error + ")" : string.Empty) + ".");
            }

            try
            {
                var result = ProcessTrace(&trace, 1, null, null);
                collector.Finish(result);
            }
            finally
            {
                CloseTrace(trace);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnEvent(EventRecord* record)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)record->UserContext).Target is Collector collector)
            {
                collector.Add(record);
            }
        }
        catch (Exception)
        {
            // Nothing may escape into the trace API. The event is dropped.
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int OnBuffer(EventTraceLogfile* logfile)
    {
        try
        {
            return GCHandle.FromIntPtr((IntPtr)logfile->Context).Target is Collector { ShouldContinue: true } ? 1 : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private sealed class Collector
    {
        private readonly int _maxEvents;
        private readonly CancellationToken _cancellationToken;
        private readonly EtlEventDecoder _decoder = new();
        private readonly List<StoredEvent> _events = new();
        private readonly Dictionary<Guid, ushort> _providerIds = new();
        private readonly List<Guid> _guids = new();
        private readonly List<string?> _names = new();
        private readonly List<int> _counts = new();
        private readonly List<string> _issues = new();
        private int _withoutSchema;
        private int _result;

        public Collector(int maxEvents, CancellationToken cancellationToken)
        {
            _maxEvents = maxEvents;
            _cancellationToken = cancellationToken;
        }

        public bool Truncated { get; private set; }

        public bool ShouldContinue => !_cancellationToken.IsCancellationRequested && !Truncated;

        public unsafe void Add(EventRecord* record)
        {
            if (_events.Count >= _maxEvents)
            {
                Truncated = true;
                return;
            }

            if (_events.Count % 1024 == 0 && _cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var decoded = _decoder.Decode(record);
            var providerId = ProviderIdOf(record->ProviderId, decoded.ProviderName);
            _events.Add(new StoredEvent
            {
                Ticks = record->TimeStamp,
                Level = record->Level,
                ProviderId = providerId,
                EventId = record->EventId,
                Version = record->Version,
                Opcode = record->Opcode,
                Task = record->Task,
                Keywords = record->Keyword,
                ProcessId = (int)record->ProcessId,
                ThreadId = (int)record->ThreadId,
                Activity = record->ActivityId,
                Message = decoded.Message,
                TaskName = decoded.TaskName,
                OpcodeName = decoded.OpcodeName,
                HasSchema = decoded.HasSchema,
                Data = decoded.Data,
            });
            if (!decoded.HasSchema)
            {
                _withoutSchema++;
            }
        }

        private ushort ProviderIdOf(Guid guid, string? name)
        {
            if (_providerIds.TryGetValue(guid, out var id))
            {
                _counts[id]++;
                if (_names[id] is null && name is not null)
                {
                    _names[id] = name;
                }

                return id;
            }

            if (_guids.Count >= OtherProvidersId - 1)
            {
                return OtherProvidersId;
            }

            id = (ushort)_guids.Count;
            _providerIds[guid] = id;
            _guids.Add(guid);
            _names.Add(name);
            _counts.Add(1);
            return id;
        }

        public void Finish(int result) => _result = result;

        public EtlFile Build()
        {
            if (_events.Count == 0 && _result != ErrorSuccess)
            {
                throw new InvalidDataException("The file is not a readable trace log (Windows error " + _result + ").");
            }

            if (Truncated)
            {
                _issues.Add(
                    "The trace holds more than " + _maxEvents.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + " events. Only the first " + _maxEvents.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " are shown.");
            }
            else if (_result != ErrorSuccess && _result != 1223)
            {
                _issues.Add("The trace ended early (Windows error " + _result + "). The events read so far are shown.");
            }

            if (_withoutSchema > 0)
            {
                _issues.Add(
                    _withoutSchema.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + " events have no description on this machine (for example traces written with WPP need the program's symbol files). "
                    + "Their raw data is shown as text and bytes.");
            }

            var names = new string[_guids.Count + 1];
            var guids = new Guid[_guids.Count + 1];
            var providers = new List<EventLogProvider>();
            for (var i = 0; i < _guids.Count; i++)
            {
                names[i] = _names[i] ?? (_guids[i] == TraceHeaderProvider ? "Trace header" : _guids[i].ToString("B"));
                guids[i] = _guids[i];
                providers.Add(new EventLogProvider((ushort)i, names[i], _counts[i]));
            }

            names[^1] = "(other providers)";
            providers.Sort((a, b) => b.Count.CompareTo(a.Count));
            return new EtlFile(_events, providers, names, guids, _issues, _withoutSchema, Truncated);
        }
    }

    private static long EstimateBytes(StoredEvent e)
    {
        var bytes = 160L + ((e.Message?.Length ?? 0) * 2L);
        foreach (var item in e.Data)
        {
            bytes += 72 + (((item.Name?.Length ?? 0) + item.Value.Length) * 2L);
        }

        return bytes;
    }

    // ---- IEventLogSource ----

    public string ProviderName(ushort providerId) =>
        providerId < _providerNames.Length - 1 ? _providerNames[providerId] : _providerNames[^1];

    private DateTime TimeOf(StoredEvent e)
    {
        try
        {
            return DateTime.FromFileTimeUtc(e.Ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UnixEpoch;
        }
    }

    public EventSummary GetSummary(int index)
    {
        var e = _events[index];
        return new EventSummary(index, index + 1L, TimeOf(e), e.Level, e.ProviderId, e.EventId);
    }

    public int FindByRecordId(long recordId) =>
        recordId >= 1 && recordId <= _events.Count ? (int)(recordId - 1) : -1;

    public EventDetail ReadDetail(int index)
    {
        var e = _events[index];
        var summary = GetSummary(index);
        var provider = ProviderName(e.ProviderId);
        var guid = e.ProviderId < _providerGuids.Length - 1 ? _providerGuids[e.ProviderId] : (Guid?)null;
        return new EventDetail
        {
            Summary = summary,
            Provider = provider,
            ProviderGuid = guid,
            Version = e.Version,
            Task = e.Task,
            Opcode = e.Opcode,
            Keywords = e.Keywords,
            ProcessId = e.ProcessId,
            ThreadId = e.ThreadId,
            ActivityId = e.Activity == Guid.Empty ? null : e.Activity,
            DataSection = e.Data.Count > 0 ? "EventData" : null,
            Data = e.Data,
            Xml = ToXml(summary, provider, guid, e),
            Message = e.Message,
        };
    }

    public string ReadSearchText(int index, bool includeMessage)
    {
        var e = _events[index];
        var sb = new StringBuilder(ProviderName(e.ProviderId));
        if (e.Message is not null)
        {
            sb.Append(' ').Append(e.Message);
        }

        foreach (var item in e.Data)
        {
            sb.Append(' ');
            if (item.Name is not null)
            {
                sb.Append(item.Name).Append(' ');
            }

            sb.Append(item.Value);
        }

        return sb.ToString();
    }

    private static string ToXml(EventSummary summary, string provider, Guid? guid, StoredEvent e)
    {
        var sb = new StringBuilder();
        sb.Append("<Event>\n  <System>\n    <Provider Name=\"").Append(Escape(provider)).Append('"');
        if (guid is { } g)
        {
            sb.Append(" Guid=\"").Append(g.ToString("B")).Append('"');
        }

        sb.Append("/>\n    <EventID>").Append(summary.EventId).Append("</EventID>\n");
        sb.Append("    <Version>").Append(e.Version).Append("</Version>\n");
        sb.Append("    <Level>").Append(e.Level).Append("</Level>\n");
        sb.Append("    <Task>").Append(e.Task).Append("</Task>\n");
        sb.Append("    <Opcode>").Append(e.Opcode).Append("</Opcode>\n");
        sb.Append("    <Keywords>0x").Append(e.Keywords.ToString("x")).Append("</Keywords>\n");
        sb.Append("    <TimeCreated SystemTime=\"").Append(summary.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture)).Append("\"/>\n");
        sb.Append("    <Execution ProcessID=\"").Append(e.ProcessId).Append("\" ThreadID=\"").Append(e.ThreadId).Append("\"/>\n");
        sb.Append("  </System>\n");
        if (e.Data.Count > 0)
        {
            sb.Append("  <EventData>\n");
            foreach (var item in e.Data)
            {
                sb.Append("    <Data");
                if (item.Name is not null)
                {
                    sb.Append(" Name=\"").Append(Escape(item.Name)).Append('"');
                }

                sb.Append('>').Append(Escape(item.Value)).Append("</Data>\n");
            }

            sb.Append("  </EventData>\n");
        }

        return sb.Append("</Event>").ToString();
    }

    // Control characters are not allowed in XML text; they are shown as a replacement mark.
    private static string Escape(string value)
    {
        var clean = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            clean.Append(c < ' ' && c is not ('\t' or '\n' or '\r') ? '�' : c);
        }

        return SecurityElement.Escape(clean.ToString()) ?? string.Empty;
    }
}
