using System.Globalization;
using System.Threading.Channels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Search;

/// <param name="MatchField">What matched, e.g. "file name", "key", "value name", "value data", "event"; <c>null</c> for plain lines.</param>
public sealed record SearchHit(
    Guid ArtifactId,
    DiagnosticLocation Location,
    string Preview,
    int MatchStart,
    int MatchLength,
    string? MatchField = null,
    DateTime? Timestamp = null);

/// <param name="Hits">The first hits in source order, up to <see cref="SearchOptions.MaxHitsPerArtifact"/>.</param>
/// <param name="TotalHits">All hits found, which can exceed <c>Hits.Count</c>.</param>
/// <param name="CountIsLowerBound">The artifact has even more hits than the per-artifact scan cap.</param>
public sealed record ArtifactSearchResult(
    DiagnosticArtifact Artifact,
    IReadOnlyList<SearchHit> Hits,
    int TotalHits,
    bool CountIsLowerBound = false)
{
    public bool IsTruncated => TotalHits > Hits.Count;
}

public sealed record SearchIssue(DiagnosticArtifact Artifact, string Message);

/// <summary>Emitted after each artifact has been searched, in completion order.</summary>
/// <param name="Result">Hits for the artifact, or <c>null</c> when it had none.</param>
public sealed record SearchUpdate(
    ArtifactSearchResult? Result,
    SearchIssue? Issue,
    int ArtifactsSearched,
    int ArtifactsTotal,
    long TotalHits);

public sealed record SearchOptions
{
    public int MaxHitsPerArtifact { get; init; } = 500;
    public int MaxParallelism { get; init; } = Math.Min(4, Environment.ProcessorCount);
    public int PreviewLength { get; init; } = 240;

    /// <summary>Also match the artifact's name and provenance path.</summary>
    public bool IncludeFileNames { get; init; } = true;

    /// <summary>Also match event message text (rendered from local provider metadata). Much slower.</summary>
    public bool IncludeEventMessages { get; init; }
}

public interface IGlobalSearchService
{
    /// <summary>Searches <paramref name="artifacts"/> in parallel, yielding an update as each one finishes.</summary>
    IAsyncEnumerable<SearchUpdate> SearchAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        SearchQuery query,
        SearchOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Searches every artifact by streaming it (nothing is indexed or kept resident): text lines, registry keys/values,
/// event log events and file names. A failure in one artifact is reported and does not stop the search.
/// </summary>
public sealed class GlobalSearchService : IGlobalSearchService
{
    private readonly IDocumentLoader _loader;

    public GlobalSearchService(IDocumentLoader loader)
    {
        _loader = loader;
    }

    public async IAsyncEnumerable<SearchUpdate> SearchAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        SearchQuery query,
        SearchOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (query.IsEmpty)
        {
            yield break;
        }

        var candidates = artifacts.Where(a => query.Types is null || query.Types.Contains(a.ArtifactType)).ToList();
        var channel = Channel.CreateUnbounded<SearchUpdate>(new UnboundedChannelOptions { SingleReader = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;

        var searched = 0;
        long totalHits = 0;
        var gate = new object();

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    candidates,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism), CancellationToken = token },
                    async (artifact, ct) =>
                    {
                        var (result, issue) = await SearchArtifactAsync(artifact, query, options, ct).ConfigureAwait(false);

                        // One lock covers the counters and the write, so updates arrive with monotonic progress.
                        lock (gate)
                        {
                            searched++;
                            totalHits += result?.TotalHits ?? 0;
                            channel.Writer.TryWrite(new SearchUpdate(result, issue, searched, candidates.Count, totalHits));
                        }
                    }).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            await producer.ConfigureAwait(false);
        }
        finally
        {
            // Consumer finished or abandoned the enumeration: stop any remaining work and wait for it.
            cts.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task<(ArtifactSearchResult? Result, SearchIssue? Issue)> SearchArtifactAsync(
        DiagnosticArtifact artifact,
        SearchQuery query,
        SearchOptions options,
        CancellationToken ct)
    {
        var hits = new HitCollector(options.MaxHitsPerArtifact);
        var lowerBound = false;
        var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // File name / path, or (for an operator-only query such as "type:evtx") the artifact itself.
        if (!query.HasEventOperators && (options.IncludeFileNames || query.Text.Length == 0))
        {
            var path = artifact.ProvenanceDisplay;
            if (query.Text.Length == 0)
            {
                hits.Add(new SearchHit(artifact.Id, DiagnosticLocation.ForArtifact(artifact.Id), path, 0, 0, "artifact"));
            }
            else if (path.IndexOf(query.Text, comparison) is var at and >= 0)
            {
                var (preview, start) = Crop(path, at, query.Text.Length, options.PreviewLength);
                hits.Add(new SearchHit(artifact.Id, DiagnosticLocation.ForArtifact(artifact.Id), preview, start, query.Text.Length, "file name"));
            }
        }

        SearchIssue? issue = null;
        // Event-only operators can only be satisfied by event logs, so other artifacts are not even opened.
        var contentRelevant = query.HasEventOperators
            ? artifact.ArtifactType == ArtifactType.EventLog
            : query.Text.Length > 0;
        if (contentRelevant && HasSearchableContent(artifact))
        {
            try
            {
                var loaded = await _loader.LoadAsync(artifact, ct).ConfigureAwait(false);
                switch (loaded.Document)
                {
                    case TextDocument text when !query.HasEventOperators:
                        lowerBound |= SearchText(text.Lines, artifact, query, options, comparison, hits, ct);
                        break;
                    // XML and JSON are searched as text, like any other file: a hit is a line, and the structured
                    // viewer resolves a line to the node it belongs to as well as to the raw source.
                    case StructuredDocument structured when !query.HasEventOperators:
                        lowerBound |= SearchText(structured.RawSource, artifact, query, options, comparison, hits, ct);
                        break;
                    case RegistryDocument registry when !query.HasEventOperators:
                        lowerBound |= SearchRegistry(registry, artifact, query, options, comparison, hits, ct);
                        break;
                    case EventLogDocument events:
                        SearchEvents(events, artifact, query, options, comparison, hits, ct);
                        break;
                }

                if (loaded.FailureMessage is { } failure && hits.Total == 0)
                {
                    issue = new SearchIssue(artifact, failure);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                issue = new SearchIssue(artifact, $"Could not search {artifact.ProvenanceDisplay}: {ex.Message}");
            }
        }

        var result = hits.Total == 0 ? null : new ArtifactSearchResult(artifact, hits.Hits, hits.Total, lowerBound);
        return (result, issue);
    }

    /// <summary>Keeps the first hits in full and merely counts the rest, so a flood of matches costs no memory.</summary>
    private sealed class HitCollector
    {
        private readonly int _max;

        public HitCollector(int max)
        {
            _max = max;
        }

        public List<SearchHit> Hits { get; } = new();
        public int Total { get; private set; }

        /// <summary>False once the stored hits are full; callers should then use <see cref="CountOnly"/>.</summary>
        public bool WantsDetail => Hits.Count < _max;

        public void Add(SearchHit hit)
        {
            Total++;
            if (Hits.Count < _max)
            {
                Hits.Add(hit);
            }
        }

        public void CountOnly(int count) => Total += count;
    }

    private static bool HasSearchableContent(DiagnosticArtifact artifact) =>
        artifact.ArtifactType is not (ArtifactType.Archive or ArtifactType.Binary or ArtifactType.Trace or ArtifactType.Unknown)
        && artifact.ExtractedPath is not null;

    // ---- per-type scanners; each returns true when its count is only a lower bound ----

    private static bool SearchText(
        ITextLineSource lines,
        DiagnosticArtifact artifact,
        SearchQuery query,
        SearchOptions options,
        StringComparison comparison,
        HitCollector hits,
        CancellationToken ct)
    {
        var found = TextSearch.FindLines(lines, query.Text, query.MatchCase, ct);
        for (var i = 0; i < found.Lines.Count; i++)
        {
            if (!hits.WantsDetail)
            {
                hits.CountOnly(found.Lines.Count - i); // the rest are counted, not previewed
                break;
            }

            var lineNumber = found.Lines[i];
            var matchedLine = lines.ReadLines(lineNumber - 1, 1);
            var line = matchedLine.Count > 0 ? matchedLine[0] : string.Empty;
            var trimmed = line.TrimStart();
            var at = trimmed.IndexOf(query.Text, comparison);
            var (preview, start) = at >= 0
                ? Crop(trimmed, at, query.Text.Length, options.PreviewLength)
                : (Truncate(trimmed, options.PreviewLength), 0);
            hits.Add(new SearchHit(
                artifact.Id,
                DiagnosticLocation.ForLine(artifact.Id, lineNumber),
                preview,
                at >= 0 ? start : 0,
                at >= 0 ? query.Text.Length : 0));
        }

        return found.Truncated;
    }

    private static bool SearchRegistry(
        RegistryDocument document,
        DiagnosticArtifact artifact,
        SearchQuery query,
        SearchOptions options,
        StringComparison comparison,
        HitCollector hits,
        CancellationToken ct)
    {
        var found = RegistrySearch.Find(document, query.Text, query.MatchCase, ct);
        for (var i = 0; i < found.Matches.Count; i++)
        {
            if (!hits.WantsDetail)
            {
                hits.CountOnly(found.Matches.Count - i);
                break;
            }

            hits.Add(RegistryHit(artifact, found.Matches[i], query, options, comparison));
        }

        return found.Truncated;
    }

    private static SearchHit RegistryHit(
        DiagnosticArtifact artifact,
        RegistryMatch match,
        SearchQuery query,
        SearchOptions options,
        StringComparison comparison)
    {
        var key = match.Key;
        var location = DiagnosticLocation.ForRegistry(artifact.Id, key.FullPath, match.Value?.Name);

        if (match.Value is null)
        {
            var path = key.FullPath;
            var at = query.Text.Contains('\\')
                ? path.IndexOf(query.Text, comparison)
                : path.LastIndexOf(query.Text, comparison);
            var (preview, start) = Crop(path, Math.Max(0, at), query.Text.Length, options.PreviewLength);
            return new SearchHit(artifact.Id, location, preview, start, query.Text.Length, "key");
        }

        var value = match.Value;
        var prefix = key.FullPath + "\\" + value.DisplayName + " = ";
        var full = prefix + value.DisplayValue;
        var nameStart = key.FullPath.Length + 1;
        int matchAt;
        string field;
        if (match.Field == RegistryMatchField.ValueName)
        {
            field = "value name";
            matchAt = nameStart + Math.Max(0, value.DisplayName.IndexOf(query.Text, comparison));
        }
        else
        {
            field = "value data";
            matchAt = prefix.Length + Math.Max(0, value.DisplayValue.IndexOf(query.Text, comparison));
        }

        var (text, offset) = Crop(full, matchAt, query.Text.Length, options.PreviewLength);
        return new SearchHit(artifact.Id, location, text, offset, query.Text.Length, field);
    }

    private static void SearchEvents(
        EventLogDocument document,
        DiagnosticArtifact artifact,
        SearchQuery query,
        SearchOptions options,
        StringComparison comparison,
        HitCollector hits,
        CancellationToken ct)
    {
        var source = document.Source;

        HashSet<ushort>? providerIds = null;
        if (query.ProviderContains is { Length: > 0 } providerText)
        {
            providerIds = source.Providers
                .Where(p => p.Name.Contains(providerText, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id)
                .ToHashSet();
        }

        var criteria = new EventFilterCriteria
        {
            Levels = query.Levels,
            ProviderIds = providerIds,
            EventIds = query.EventIds,
            Text = query.Text.Length == 0 ? null : query.Text,
            MatchCase = query.MatchCase,
            IncludeMessage = options.IncludeEventMessages,
        };

        var indices = EventLogFilter.Apply(source, criteria, ct);
        if (indices is null)
        {
            return;
        }

        for (var i = 0; i < indices.Length; i++)
        {
            if (!hits.WantsDetail)
            {
                hits.CountOnly(indices.Length - i);
                break;
            }

            var summary = source.GetSummary(indices[i]);
            var location = DiagnosticLocation.ForEventRecord(artifact.Id, summary.RecordId);
            var detail = source.ReadDetail(indices[i]);
            var body = !string.IsNullOrWhiteSpace(detail.Message)
                ? FirstLine(detail.Message)
                : string.Join(", ", detail.Data.Select(d => d.Name is null ? d.Value : d.Name + "=" + d.Value));
            var full = string.Create(CultureInfo.InvariantCulture, $"{detail.Provider} · {summary.EventId} · {EventLevels.Name(summary.Level)} · {body}");
            var at = query.Text.Length > 0 ? full.IndexOf(query.Text, comparison) : -1;
            var (preview, start) = at >= 0
                ? Crop(full, at, query.Text.Length, options.PreviewLength)
                : (Truncate(full, options.PreviewLength), 0);
            hits.Add(new SearchHit(
                artifact.Id, location, preview, at >= 0 ? start : 0, at >= 0 ? query.Text.Length : 0, "event", summary.TimeUtc));
        }
    }

    // ---- preview helpers ----

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return end >= 0 ? text[..end] : text;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Crops <paramref name="text"/> to about <paramref name="max"/> characters around the match, with ellipses.</summary>
    internal static (string Text, int MatchStart) Crop(string text, int matchStart, int matchLength, int max)
    {
        if (text.Length <= max)
        {
            return (text, matchStart);
        }

        var before = Math.Max(0, (max - matchLength) / 3);
        var from = Math.Max(0, matchStart - before);
        var to = Math.Min(text.Length, from + max);
        from = Math.Max(0, to - max);

        var cropped = text[from..to];
        var offset = matchStart - from;
        if (from > 0)
        {
            cropped = "…" + cropped;
            offset++;
        }

        if (to < text.Length)
        {
            cropped += "…";
        }

        return (cropped, offset);
    }
}
