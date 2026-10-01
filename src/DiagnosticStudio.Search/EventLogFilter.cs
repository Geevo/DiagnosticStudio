using System.Globalization;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Search;

/// <summary>A set of event ids written as a list of numbers and ranges, e.g. <c>7031, 7036, 100-200, 0x1b58</c>.</summary>
public sealed class EventIdSet
{
    private readonly List<(uint From, uint To)> _ranges;

    private EventIdSet(List<(uint, uint)> ranges)
    {
        _ranges = ranges;
    }

    public bool Contains(uint id)
    {
        foreach (var (from, to) in _ranges)
        {
            if (id >= from && id <= to)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses the text. An empty string yields <c>null</c> with no error (no id filter).</summary>
    public static bool TryParse(string? text, out EventIdSet? set, out string? error)
    {
        set = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var ranges = new List<(uint, uint)>();
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-', 2);
            if (!TryNumber(bounds[0], out var from) || (bounds.Length == 2 && !TryNumber(bounds[1], out var _)))
            {
                error = $"'{part}' is not an event id or range.";
                return false;
            }

            var to = from;
            if (bounds.Length == 2)
            {
                TryNumber(bounds[1], out to);
            }

            if (to < from)
            {
                error = $"Range '{part}' runs backwards.";
                return false;
            }

            ranges.Add((from, to));
        }

        set = new EventIdSet(ranges);
        return true;
    }

    private static bool TryNumber(string text, out uint value)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>What to keep. Every criterion that is set must match; unset criteria match everything.</summary>
public sealed record EventFilterCriteria
{
    public IReadOnlySet<byte>? Levels { get; init; }
    public IReadOnlySet<ushort>? ProviderIds { get; init; }
    public EventIdSet? EventIds { get; init; }

    /// <summary>Plain-text match over the provider name, event id and the event's data values (and the message if <see cref="IncludeMessage"/>).</summary>
    public string? Text { get; init; }

    public bool MatchCase { get; init; }

    /// <summary>
    /// Also match the message text rendered from local provider metadata. Considerably slower than matching data
    /// values alone, so it is opt-in.
    /// </summary>
    public bool IncludeMessage { get; init; }

    public bool IsEmpty =>
        Levels is null && ProviderIds is null && EventIds is null && string.IsNullOrEmpty(Text);
}

public static class EventLogFilter
{
    /// <summary>
    /// Returns the ascending indices of events that satisfy <paramref name="criteria"/>, or <c>null</c> when the
    /// criteria are empty (meaning every event). Index-level criteria are cheap; text matching decodes events.
    /// </summary>
    public static Task<int[]?> ApplyAsync(
        IEventLogSource source,
        EventFilterCriteria criteria,
        CancellationToken cancellationToken) =>
        Task.Run(() => Apply(source, criteria, cancellationToken), cancellationToken);

    public static int[]? Apply(IEventLogSource source, EventFilterCriteria criteria, CancellationToken cancellationToken)
    {
        if (criteria.IsEmpty)
        {
            return null;
        }

        var comparison = criteria.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var text = criteria.Text;
        var matches = new List<int>();

        for (var i = 0; i < source.Count; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var summary = source.GetSummary(i);
            if ((criteria.Levels is not null && !criteria.Levels.Contains(summary.Level))
                || (criteria.ProviderIds is not null && !criteria.ProviderIds.Contains(summary.ProviderId))
                || (criteria.EventIds is not null && !criteria.EventIds.Contains(summary.EventId)))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(text)
                && !summary.EventId.ToString(CultureInfo.InvariantCulture).Contains(text, comparison)
                && !source.ReadSearchText(i, criteria.IncludeMessage).Contains(text, comparison))
            {
                continue;
            }

            matches.Add(i);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return matches.ToArray();
    }
}
