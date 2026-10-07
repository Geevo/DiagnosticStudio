using System.Collections;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.EventLogViewer;

/// <summary>
/// Read-only list of event rows for WPF virtualisation. A list is immutable: filtering or reordering creates a new
/// instance (so the control rebinds rather than re-reading a huge collection). Rows are built a page at a time.
/// </summary>
public sealed class VirtualEventList : IList, IReadOnlyList<EventRowViewModel>
{
    public const int PageSize = 128;
    private const int MaxCachedPages = 40;

    private readonly IEventLogSource _source;
    private readonly int[]? _view;
    private readonly int[]? _order;
    private readonly bool _newestFirst;
    private int[]? _positionOfEvent;
    private readonly Dictionary<int, EventRowViewModel[]> _pages = new();
    private readonly LinkedList<int> _recent = new();

    /// <param name="view">Ascending event indices to show, or <c>null</c> for every event.</param>
    /// <param name="newestFirst">Show the last of the rows first.</param>
    /// <param name="order">
    /// The events of <paramref name="view"/> in the order to show them (before <paramref name="newestFirst"/> reverses
    /// it), or <c>null</c> for the order of <paramref name="view"/>.
    /// </param>
    public VirtualEventList(IEventLogSource source, int[]? view, bool newestFirst, int[]? order = null)
    {
        _source = source;
        _view = view;
        _newestFirst = newestFirst;
        _order = order;
    }

    public int Count => _order?.Length ?? _view?.Length ?? _source.Count;

    public bool IsFiltered => _view is not null;

    public EventRowViewModel this[int row]
    {
        get
        {
            if ((uint)row >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }

            return GetPage(row / PageSize)[row % PageSize];
        }
    }

    /// <summary>Row showing the event at <paramref name="eventIndex"/>, or -1 when it is not in this view.</summary>
    public int RowOfEvent(int eventIndex)
    {
        var position = _order is not null ? PositionInOrder(eventIndex)
            : _view is null ? eventIndex
            : Array.BinarySearch(_view, eventIndex);
        if (position < 0 || position >= Count)
        {
            return -1;
        }

        return _newestFirst ? Count - 1 - position : position;
    }

    private int PositionInOrder(int eventIndex)
    {
        if (_positionOfEvent is null)
        {
            var positions = new int[_source.Count];
            Array.Fill(positions, -1);
            for (var i = 0; i < _order!.Length; i++)
            {
                positions[_order[i]] = i;
            }

            _positionOfEvent = positions;
        }

        return (uint)eventIndex < (uint)_positionOfEvent.Length ? _positionOfEvent[eventIndex] : -1;
    }

    private int EventAt(int row)
    {
        var position = _newestFirst ? Count - 1 - row : row;
        return _order is not null ? _order[position] : _view is null ? position : _view[position];
    }

    private EventRowViewModel[] GetPage(int pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var cached))
        {
            if (_recent.First?.Value != pageIndex)
            {
                _recent.Remove(pageIndex);
                _recent.AddFirst(pageIndex);
            }

            return cached;
        }

        var start = pageIndex * PageSize;
        var count = Math.Min(PageSize, Count - start);
        var page = new EventRowViewModel[count];
        for (var i = 0; i < count; i++)
        {
            page[i] = new EventRowViewModel(_source, _source.GetSummary(EventAt(start + i)));
        }

        _pages[pageIndex] = page;
        _recent.AddFirst(pageIndex);
        while (_recent.Count > MaxCachedPages)
        {
            _pages.Remove(_recent.Last!.Value);
            _recent.RemoveLast();
        }

        return page;
    }

    public IEnumerator<EventRowViewModel> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Non-generic IList is what WPF binds to; nothing mutating is supported.
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    // Value-based so an item selected earlier still maps to its row after its page was evicted and rebuilt.
    int IList.IndexOf(object? value) => value is EventRowViewModel row ? RowOfEvent(row.EventIndex) : -1;

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }
}
