using System.Collections;
using DiagnosticStudio.Core.Timeline;

namespace DiagnosticStudio.App.ViewModels.Timeline;

/// <summary>
/// Read-only list of timeline rows for WPF virtualisation. Changing the filter or the zone offsets creates a new
/// instance. Rows are built a page at a time and only a few pages are kept.
/// </summary>
public sealed class VirtualTimelineList : IList, IReadOnlyList<TimelineRowViewModel>
{
    public const int PageSize = 128;
    private const int MaxCachedPages = 40;

    private readonly TimelineIndex _index;
    private readonly int[] _order;
    private readonly ITimelineService _service;
    private readonly TimeSpan[]? _offsets;
    private readonly Dictionary<int, TimelineRowViewModel[]> _pages = new();
    private readonly LinkedList<int> _recent = new();

    /// <param name="order">Indices into the index's entries, oldest first.</param>
    public VirtualTimelineList(TimelineIndex index, int[] order, ITimelineService service, TimeSpan[]? offsets)
    {
        _index = index;
        _order = order;
        _service = service;
        _offsets = offsets;
    }

    public static VirtualTimelineList Empty { get; } = new(TimelineIndex.Empty, Array.Empty<int>(), new NoService(), null);

    public int Count => _order.Length;

    public int[] Order => _order;

    public TimelineRowViewModel this[int row]
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

    /// <summary>Row showing the entry at <paramref name="entryIndex"/> of the index, or -1 when the filter hides it.</summary>
    public int RowOfEntry(int entryIndex)
    {
        // The order is by time, not by entry index, so this is a scan; it runs once per jump, not per scroll.
        for (var i = 0; i < _order.Length; i++)
        {
            if (_order[i] == entryIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private TimelineRowViewModel[] GetPage(int pageIndex)
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
        var page = new TimelineRowViewModel[count];
        for (var i = 0; i < count; i++)
        {
            page[i] = new TimelineRowViewModel(_index, _order[start + i], start + i, _service, _offsets, this);
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

    public IEnumerator<TimelineRowViewModel> GetEnumerator()
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

    // A row built by a page that was since evicted still maps to its position in this list.
    int IList.IndexOf(object? value) =>
        value is TimelineRowViewModel row && ReferenceEquals(row.Owner, this) ? row.RowNumber : -1;

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

    private sealed class NoService : ITimelineService
    {
        public Task<TimelineIndex> BuildAsync(
            IReadOnlyList<DiagnosticStudio.Core.Artifacts.DiagnosticArtifact> artifacts,
            IProgress<TimelineProgress>? progress,
            CancellationToken cancellationToken) => Task.FromResult(TimelineIndex.Empty);

        public Task<string> DescribeAsync(TimelineIndex index, TimelineEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(string.Empty);
    }
}
