using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.Core.Timeline;

public enum TimelineSourceKind
{
    EventLog,
    TextLog,
}

/// <summary>An artifact that contributed at least one timestamped entry.</summary>
/// <param name="Index">Position in <see cref="TimelineIndex.Sources"/>; entries refer to it.</param>
/// <param name="EntryCount">Entries kept from this artifact.</param>
/// <param name="UnzonedCount">Entries whose time was written without a time zone, so its zone is an assumption.</param>
/// <param name="SkippedCount">Text lines with no recognisable timestamp (continuation lines, free text). Always 0 for event logs.</param>
public sealed record TimelineSource(
    int Index,
    DiagnosticArtifact Artifact,
    TimelineSourceKind Kind,
    int EntryCount,
    int UnzonedCount,
    long SkippedCount)
{
    /// <summary>The user can say which zone this source is in.</summary>
    public bool HasUnzonedTimes => UnzonedCount > 0;
}

/// <summary>
/// One timestamped moment in one artifact. Kept small (24 bytes) because a large log can contribute millions; the
/// text of the entry is read back from its source on demand.
/// </summary>
/// <param name="Ticks">
/// <see cref="DateTime.Ticks"/> of the time. UTC when <paramref name="Unzoned"/> is false; otherwise the time exactly as
/// written, in a zone nobody told us.
/// </param>
/// <param name="Source">Index into <see cref="TimelineIndex.Sources"/>.</param>
/// <param name="Position">One-based line number for text logs; event record id for event logs.</param>
public readonly record struct TimelineEntry(long Ticks, long Position, int Source, LogSeverity Severity, bool Unzoned);

/// <summary>Everything timestamped in a bundle. Immutable once built; ordering and filtering are done by <see cref="TimelineSelector"/>.</summary>
public sealed class TimelineIndex
{
    /// <summary>Upper bound on entries kept (about 150 MB). Beyond it entries of the later sources are dropped and the index says so.</summary>
    public const int MaxEntries = 6_000_000;

    public static TimelineIndex Empty { get; } = new(Array.Empty<TimelineSource>(), Array.Empty<TimelineEntry>(), 0, 0, Array.Empty<string>());

    public TimelineIndex(
        IReadOnlyList<TimelineSource> sources,
        TimelineEntry[] entries,
        int artifactsExamined,
        long droppedEntries,
        IReadOnlyList<string> issues)
    {
        Sources = sources;
        Entries = entries;
        ArtifactsExamined = artifactsExamined;
        DroppedEntries = droppedEntries;
        Issues = issues;
    }

    public IReadOnlyList<TimelineSource> Sources { get; }

    /// <summary>All entries, grouped by source in source order and in position order within each source.</summary>
    public TimelineEntry[] Entries { get; }

    /// <summary>Event logs and text logs that were looked at, whether or not they held timestamps.</summary>
    public int ArtifactsExamined { get; }

    /// <summary>Entries not kept because of <see cref="MaxEntries"/>.</summary>
    public long DroppedEntries { get; }

    /// <summary>Artifacts that could not be read, and similar problems, for the Output panel.</summary>
    public IReadOnlyList<string> Issues { get; }

    public int Count => Entries.Length;

    public long SkippedCount => Sources.Sum(s => s.SkippedCount);

    public bool IsTruncated => DroppedEntries > 0;

    public DiagnosticLocation LocationOf(in TimelineEntry entry)
    {
        var source = Sources[entry.Source];
        return source.Kind == TimelineSourceKind.EventLog
            ? DiagnosticLocation.ForEventRecord(source.Artifact.Id, entry.Position)
            : DiagnosticLocation.ForLine(source.Artifact.Id, entry.Position);
    }

    /// <summary>
    /// The entry that best stands for <paramref name="location"/>: the entry itself, or for a text line the nearest
    /// timestamped line above it (a stack trace belongs to the line that announced it). <c>-1</c> if none.
    /// </summary>
    public int FindEntry(DiagnosticLocation location)
    {
        var position = location.NumericPosition ?? 0;
        var wanted = location.Kind switch
        {
            DiagnosticLocationKind.EventRecord => TimelineSourceKind.EventLog,
            DiagnosticLocationKind.Line => TimelineSourceKind.TextLog,
            _ => (TimelineSourceKind?)null,
        };
        if (wanted is null)
        {
            return -1;
        }

        var best = -1;
        for (var i = 0; i < Entries.Length; i++)
        {
            ref readonly var entry = ref Entries[i];
            var source = Sources[entry.Source];
            if (source.Kind != wanted || source.Artifact.Id != location.ArtifactId)
            {
                continue;
            }

            if (entry.Position == position)
            {
                return i;
            }

            if (wanted == TimelineSourceKind.TextLog && entry.Position < position && (best < 0 || entry.Position > Entries[best].Position))
            {
                best = i;
            }
        }

        return best;
    }
}

public readonly record struct TimelineProgress(int ArtifactsDone, int ArtifactsTotal, long EntriesSoFar);

public interface ITimelineService
{
    /// <summary>Reads every event log and text log of <paramref name="artifacts"/> and indexes the moments they record.</summary>
    Task<TimelineIndex> BuildAsync(
        IReadOnlyList<DiagnosticArtifact> artifacts,
        IProgress<TimelineProgress>? progress,
        CancellationToken cancellationToken);

    /// <summary>The text of an entry as its source shows it: the log line, or the event's message or data.</summary>
    Task<string> DescribeAsync(TimelineIndex index, TimelineEntry entry, CancellationToken cancellationToken);
}
