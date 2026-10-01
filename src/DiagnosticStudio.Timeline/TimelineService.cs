using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Core.Timeline;

namespace DiagnosticStudio.Timeline;

/// <summary>
/// Indexes the moments recorded in a bundle's event logs and text logs. Only times that are plainly stated are used:
/// an event's own timestamp, or a timestamp <see cref="LogLineAnalyzer"/> recognises without guessing. A line that
/// has none is counted as skipped, never given a time.
/// </summary>
public sealed class TimelineService : ITimelineService
{
    private const int MaxParallelism = 4;
    private const int PreviewLength = 600;

    // Anything outside this is a mis-parse or a placeholder (0001-01-01, 1601, 9999), not an event.
    private static readonly long EarliestTicks = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long LatestTicks = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private readonly IDocumentLoader _loader;
    private readonly int _maxEntries;

    public TimelineService(IDocumentLoader loader)
        : this(loader, TimelineIndex.MaxEntries)
    {
    }

    internal TimelineService(IDocumentLoader loader, int maxEntries)
    {
        _loader = loader;
        _maxEntries = maxEntries;
    }

    public async Task<TimelineIndex> BuildAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IProgress<TimelineProgress>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = artifacts
            .Where(a => !a.IsContainer && a.ArtifactType is ArtifactType.EventLog or ArtifactType.TextLog)
            .OrderBy(a => a.ProvenanceDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Id)
            .ToList();

        var results = new SourceScan?[candidates.Count];
        var issues = new System.Collections.Concurrent.ConcurrentBag<string>();
        long entriesSoFar = 0;
        var done = 0;
        progress?.Report(new TimelineProgress(0, candidates.Count, 0));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, candidates.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var scan = await ScanAsync(candidates[i], issues, token).ConfigureAwait(false);
                results[i] = scan;
                var total = Interlocked.Add(ref entriesSoFar, scan?.Entries.Count ?? 0);
                progress?.Report(new TimelineProgress(Interlocked.Increment(ref done), candidates.Count, total));
            }).ConfigureAwait(false);

        return Assemble(candidates, results, issues);
    }

    private TimelineIndex Assemble(
        IReadOnlyList<DiagnosticArtifact> candidates,
        SourceScan?[] results,
        IEnumerable<string> issues)
    {
        var sources = new List<TimelineSource>();
        var all = new List<(SourceScan Scan, int SourceIndex, int Keep)>();
        long total = 0;
        long dropped = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (results[i] is not { } scan || scan.Entries.Count == 0)
            {
                continue;
            }

            var room = (int)Math.Max(0, _maxEntries - total);
            var keep = Math.Min(scan.Entries.Count, room);
            dropped += scan.Entries.Count - keep;
            if (keep == 0)
            {
                continue;
            }

            var index = sources.Count;
            var unzoned = 0;
            for (var k = 0; k < keep; k++)
            {
                if (scan.Entries[k].Unzoned)
                {
                    unzoned++;
                }
            }

            sources.Add(new TimelineSource(index, candidates[i], scan.Kind, keep, unzoned, scan.Skipped));
            all.Add((scan, index, keep));
            total += keep;
        }

        var entries = new TimelineEntry[total];
        var at = 0;
        foreach (var (scan, sourceIndex, keep) in all)
        {
            for (var k = 0; k < keep; k++)
            {
                entries[at++] = scan.Entries[k] with { Source = sourceIndex };
            }
        }

        return new TimelineIndex(sources, entries, candidates.Count, dropped, issues.OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    private sealed record SourceScan(TimelineSourceKind Kind, List<TimelineEntry> Entries, long Skipped);

    private async Task<SourceScan?> ScanAsync(
        DiagnosticArtifact artifact,
        System.Collections.Concurrent.ConcurrentBag<string> issues,
        CancellationToken token)
    {
        DocumentLoadResult loaded;
        try
        {
            loaded = await _loader.LoadAsync(artifact, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"{artifact.ProvenanceDisplay}: could not be opened for the timeline: {ex.Message}");
            return null;
        }

        try
        {
            switch (loaded.Document)
            {
                case EventLogDocument events:
                    return ScanEvents(events.Source, token);
                case TextDocument text:
                    return ScanText(text.Lines, token);
                default:
                    return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"{artifact.ProvenanceDisplay}: reading its times failed: {ex.Message}");
            return null;
        }
    }

    private static SourceScan ScanEvents(IEventLogSource source, CancellationToken token)
    {
        var entries = new List<TimelineEntry>(source.Count);
        long skipped = 0;
        for (var i = 0; i < source.Count; i++)
        {
            if ((i & 0x3FFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var summary = source.GetSummary(i);
            var ticks = summary.TimeUtc.Ticks;
            if (ticks < EarliestTicks || ticks > LatestTicks)
            {
                skipped++;
                continue;
            }

            entries.Add(new TimelineEntry(ticks, summary.RecordId, 0, EventSeverity(summary.Level), Unzoned: false));
        }

        return new SourceScan(TimelineSourceKind.EventLog, entries, skipped);
    }

    private static LogSeverity EventSeverity(byte level) => level switch
    {
        EventLevels.Critical or EventLevels.Error => LogSeverity.Error,
        EventLevels.Warning => LogSeverity.Warning,
        EventLevels.Information => LogSeverity.Information,
        EventLevels.Verbose => LogSeverity.Debug,
        _ => LogSeverity.None,
    };

    private static SourceScan ScanText(ITextLineSource lines, CancellationToken token)
    {
        var entries = new List<TimelineEntry>();
        long skipped = 0;
        var number = 0;
        foreach (var line in lines.EnumerateLines())
        {
            number++;
            if ((number & 0x3FFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            // Shortest recognised timestamp is "h:mm:ss"-sized; nothing shorter can carry a date.
            if (line.Length < 12)
            {
                skipped++;
                continue;
            }

            var info = LogLineAnalyzer.Analyze(line);
            if (info.Timestamp is not { } time || time.Ticks < EarliestTicks || time.Ticks > LatestTicks)
            {
                skipped++;
                continue;
            }

            // A time converted from an explicit offset or 'Z' is UTC; anything else is as written, zone unknown.
            entries.Add(new TimelineEntry(time.Ticks, number, 0, info.Severity, Unzoned: time.Kind != DateTimeKind.Utc));
        }

        return new SourceScan(TimelineSourceKind.TextLog, entries, skipped);
    }

    public async Task<string> DescribeAsync(TimelineIndex index, TimelineEntry entry, CancellationToken cancellationToken)
    {
        var source = index.Sources[entry.Source];
        var loaded = await _loader.LoadAsync(source.Artifact, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => Describe(loaded.Document, entry), cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(DiagnosticDocument document, TimelineEntry entry)
    {
        switch (document)
        {
            case TextDocument text:
                var lines = text.Lines.ReadLines((int)Math.Max(0, entry.Position - 1), 1);
                return lines.Count == 0 ? string.Empty : Truncate(lines[0].Trim());

            case EventLogDocument events:
                var at = events.Source.FindByRecordId(entry.Position);
                if (at < 0)
                {
                    return "(event not found)";
                }

                var detail = events.Source.ReadDetail(at);
                var head = $"{detail.Provider} · {detail.Summary.EventId}";
                if (detail.DecodeError is not null)
                {
                    return head + " · (record could not be decoded)";
                }

                if (!string.IsNullOrWhiteSpace(detail.Message))
                {
                    return head + ": " + FirstLine(detail.Message);
                }

                return detail.Data.Count == 0
                    ? head
                    : head + ": " + Truncate(string.Join(", ", detail.Data.Select(d => d.Name is null ? d.Value : d.Name + "=" + d.Value)));

            default:
                return string.Empty;
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return Truncate(end >= 0 ? text[..end] : text);
    }

    private static string Truncate(string text) =>
        text.Length <= PreviewLength ? text : text[..PreviewLength] + "…";
}
