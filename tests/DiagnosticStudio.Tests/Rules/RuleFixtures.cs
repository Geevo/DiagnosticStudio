using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Tests.Rules;

internal static class RuleFixtures
{
    public static readonly DateTime T0 = new(2026, 7, 23, 14, 12, 0, DateTimeKind.Utc);

    public static DiagnosticArtifact Artifact(string name, ArtifactType type) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OriginalPath = name,
        Provenance = new[] { "Bundle.zip", name },
        ArtifactType = type,
    };

    public sealed class ListLines : ITextLineSource
    {
        private readonly string[] _lines;

        public ListLines(IEnumerable<string> lines) => _lines = lines.ToArray();

        public int LineCount => _lines.Length;
        public long ByteLength => 0;
        public string EncodingName => "test";
        public IReadOnlyList<string> ReadLines(int startLine, int count) => _lines.Skip(startLine).Take(count).ToArray();
        public IEnumerable<string> EnumerateLines(int startLine = 0) => _lines.Skip(startLine);
    }

    /// <summary>In-memory event log whose events carry data items, one minute apart.</summary>
    public sealed class DataEventSource : IEventLogSource
    {
        private readonly List<(long Id, ushort Provider, uint EventId, string[] Data)> _events = new();
        private readonly List<string> _providers = new();

        public int Count => _events.Count;

        public IReadOnlyList<EventLogProvider> Providers =>
            _providers.Select((p, i) => new EventLogProvider((ushort)i, p, _events.Count(e => e.Provider == i))).ToList();

        public DataEventSource Add(string provider, uint eventId, params string[] data)
        {
            var index = _providers.IndexOf(provider);
            if (index < 0)
            {
                _providers.Add(provider);
                index = _providers.Count - 1;
            }

            _events.Add((_events.Count + 1000, (ushort)index, eventId, data));
            return this;
        }

        public string ProviderName(ushort providerId) => _providers[providerId];

        public EventSummary GetSummary(int index)
        {
            var e = _events[index];
            return new EventSummary(index, e.Id, T0.AddMinutes(index), EventLevels.Error, e.Provider, e.EventId);
        }

        public int FindByRecordId(long recordId) => _events.FindIndex(e => e.Id == recordId);

        public EventDetail ReadDetail(int index)
        {
            var e = _events[index];
            return new EventDetail
            {
                Summary = GetSummary(index),
                Provider = _providers[e.Provider],
                Xml = "<Event/>",
                Data = e.Data.Select((v, i) => new EventDataItem("param" + (i + 1), v)).ToList(),
            };
        }

        public string ReadSearchText(int index, bool includeMessage) => string.Empty;
    }

    public static EventLogDocument EventLog(DiagnosticArtifact artifact, IEventLogSource source) =>
        new() { Artifact = artifact, Source = source };

    public static TextDocument Text(DiagnosticArtifact artifact, params string[] lines) =>
        new() { Artifact = artifact, Lines = new ListLines(lines) };

    public sealed class FakeLoader : IDocumentLoader
    {
        private readonly Dictionary<Guid, DocumentLoadResult> _results = new();
        private readonly Dictionary<Guid, Exception> _throws = new();
        public List<Guid> Loaded { get; } = new();

        public void Set(DiagnosticArtifact artifact, DocumentLoadResult result) => _results[artifact.Id] = result;

        public void Throw(DiagnosticArtifact artifact, Exception ex) => _throws[artifact.Id] = ex;

        public Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
        {
            lock (Loaded)
            {
                Loaded.Add(artifact.Id);
            }

            if (_throws.TryGetValue(artifact.Id, out var ex))
            {
                throw ex;
            }

            return Task.FromResult(_results[artifact.Id]);
        }
    }
}
