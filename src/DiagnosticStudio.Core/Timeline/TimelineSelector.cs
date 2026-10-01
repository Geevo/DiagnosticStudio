using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Core.Timeline;

public enum TimelineSeverityFilter
{
    /// <summary>Every entry, including lines with no recognised level.</summary>
    All = 0,

    /// <summary>Everything except debug and trace output.</summary>
    HideDebug,

    WarningsAndErrors,

    ErrorsOnly,
}

/// <summary>What the timeline shows. Times are UTC; unzoned entries are moved by their source's offset first.</summary>
public sealed class TimelineFilter
{
    /// <summary>Included sources by source index; <c>null</c> means all.</summary>
    public IReadOnlyList<bool>? IncludedSources { get; init; }

    public TimelineSeverityFilter Severity { get; init; } = TimelineSeverityFilter.All;

    /// <summary>Inclusive lower bound, UTC.</summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>Inclusive upper bound, UTC.</summary>
    public DateTime? ToUtc { get; init; }

    /// <summary>
    /// For each source, the offset of the zone its unzoned times are written in (UTC+02:00 = +2 h), or <c>null</c> for
    /// UTC. A written time of 14:00 in UTC+02:00 is 12:00 UTC.
    /// </summary>
    public IReadOnlyList<TimeSpan>? SourceOffsets { get; init; }
}

/// <summary>Ordering and filtering of a <see cref="TimelineIndex"/>. Pure functions over the entry array.</summary>
public static class TimelineSelector
{
    /// <summary>The UTC ticks of <paramref name="entry"/> once its source's offset is applied.</summary>
    public static long EffectiveTicks(in TimelineEntry entry, IReadOnlyList<TimeSpan>? offsets)
    {
        if (!entry.Unzoned || offsets is null || entry.Source >= offsets.Count)
        {
            return entry.Ticks;
        }

        var ticks = entry.Ticks - offsets[entry.Source].Ticks;
        return Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks);
    }

    public static bool PassesSeverity(LogSeverity severity, TimelineSeverityFilter filter) => filter switch
    {
        TimelineSeverityFilter.HideDebug => severity != LogSeverity.Debug,
        TimelineSeverityFilter.WarningsAndErrors => severity >= LogSeverity.Warning,
        TimelineSeverityFilter.ErrorsOnly => severity == LogSeverity.Error,
        _ => true,
    };

    /// <summary>
    /// Indices into <see cref="TimelineIndex.Entries"/> of the entries that pass <paramref name="filter"/>, oldest
    /// first. Entries with the same time keep their source order, then their position order, so the result is stable.
    /// </summary>
    public static int[] Select(TimelineIndex index, TimelineFilter filter, CancellationToken cancellationToken = default)
    {
        var entries = index.Entries;
        var keys = new long[entries.Length];
        var items = new int[entries.Length];
        var count = 0;

        var from = filter.FromUtc?.Ticks;
        var to = filter.ToUtc?.Ticks;
        var included = filter.IncludedSources;

        for (var i = 0; i < entries.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ref readonly var entry = ref entries[i];
            if (included is not null && entry.Source < included.Count && !included[entry.Source])
            {
                continue;
            }

            if (!PassesSeverity(entry.Severity, filter.Severity))
            {
                continue;
            }

            var ticks = EffectiveTicks(entry, filter.SourceOffsets);
            if ((from is { } f && ticks < f) || (to is { } t && ticks > t))
            {
                continue;
            }

            keys[count] = ticks;
            items[count] = i;
            count++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(keys, items, 0, count);

        // Array.Sort is not stable. Entries are stored source-major and position-ascending, so the entry index is
        // already the tie-break: put each run of equal times back into index order.
        for (var start = 0; start < count;)
        {
            var end = start + 1;
            while (end < count && keys[end] == keys[start])
            {
                end++;
            }

            if (end - start > 1)
            {
                Array.Sort(items, start, end - start);
            }

            start = end;
        }

        return count == items.Length ? items : items.AsSpan(0, count).ToArray();
    }

    /// <summary>Position in <paramref name="order"/> of the first entry at or after <paramref name="utcTicks"/> (the last one if all are earlier).</summary>
    public static int RowAtOrAfter(TimelineIndex index, int[] order, IReadOnlyList<TimeSpan>? offsets, long utcTicks)
    {
        if (order.Length == 0)
        {
            return -1;
        }

        int low = 0, high = order.Length - 1;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (EffectiveTicks(index.Entries[order[mid]], offsets) < utcTicks)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
