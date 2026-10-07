using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.EventLogViewer;

/// <summary>Columns the event table can be sorted by. The message preview is decoded on demand, so it is not sortable.</summary>
public enum EventSortColumn
{
    Time,
    Level,
    Provider,
    EventId,
    Record,
}

public static class EventSort
{
    /// <summary>
    /// Ascending order of the events in <paramref name="view"/> (or of every event when <c>null</c>) by
    /// <paramref name="column"/>, ties broken by position in the log. Returns <c>null</c> when that order is already the
    /// order of <paramref name="view"/>, which is always so for the record column and usually for time.
    /// </summary>
    public static int[]? Order(IEventLogSource source, int[]? view, EventSortColumn column, CancellationToken token = default)
    {
        if (column == EventSortColumn.Record)
        {
            return null;
        }

        var count = view?.Length ?? source.Count;
        var keys = new (long Key, int Index)[count];
        var ranks = column == EventSortColumn.Provider ? ProviderRanks(source) : null;
        var inOrder = true;
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var index = view is null ? i : view[i];
            var summary = source.GetSummary(index);
            var key = column switch
            {
                EventSortColumn.Time => summary.TimeUtc.Ticks,
                EventSortColumn.Level => summary.Level,
                EventSortColumn.EventId => summary.EventId,
                _ => ranks![summary.ProviderId],
            };
            keys[i] = (key, index);
            inOrder &= i == 0 || keys[i - 1].Key <= key;
        }

        if (inOrder)
        {
            return null;
        }

        token.ThrowIfCancellationRequested();
        Array.Sort(keys, static (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Index.CompareTo(b.Index));

        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = keys[i].Index;
        }

        return order;
    }

    // Provider ids are assigned as providers are met, so sort by name through a rank per id.
    private static long[] ProviderRanks(IEventLogSource source)
    {
        var ranks = new long[ushort.MaxValue + 1];
        var ordered = source.Providers.Select(p => p.Id).OrderBy(source.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
        for (var rank = 0; rank < ordered.Count; rank++)
        {
            ranks[ordered[rank]] = rank + 1;
        }

        return ranks;
    }
}
