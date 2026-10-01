using System.Collections;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.TextViewer;

/// <summary>
/// Read-only list over a huge text source for WPF virtualisation. Lines are loaded a page at a time
/// when the indexer is hit and a small number of pages is cached, so memory does not grow with file size.
/// </summary>
public sealed class VirtualLineList : IList, IReadOnlyList<LineViewModel>
{
    public const int PageSize = 256;
    private const int MaxCachedPages = 48;

    private readonly ITextLineSource _source;
    private readonly Dictionary<int, LineViewModel[]> _pages = new();
    private readonly LinkedList<int> _recentPages = new();
    private int[] _matchLines = Array.Empty<int>();

    public VirtualLineList(ITextLineSource source)
    {
        _source = source;
    }

    public int Count => _source.LineCount;

    public LineViewModel this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var page = GetPage(index / PageSize);
            return page[index % PageSize];
        }
    }

    /// <summary>Marks which lines match the current find query. <paramref name="lines"/> must be ascending, one-based.</summary>
    public void SetMatches(int[] lines)
    {
        _matchLines = lines;
        foreach (var page in _pages.Values)
        {
            foreach (var line in page)
            {
                line.IsMatch = IsMatch(line.LineNumber);
            }
        }
    }

    private bool IsMatch(int lineNumber) => Array.BinarySearch(_matchLines, lineNumber) >= 0;

    private LineViewModel[] GetPage(int pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var cached))
        {
            Touch(pageIndex);
            return cached;
        }

        var start = pageIndex * PageSize;
        var texts = _source.ReadLines(start, PageSize);
        var page = new LineViewModel[texts.Count];
        for (var i = 0; i < page.Length; i++)
        {
            var number = start + i + 1;
            page[i] = new LineViewModel(number, texts[i]) { IsMatch = IsMatch(number) };
        }

        _pages[pageIndex] = page;
        _recentPages.AddFirst(pageIndex);
        while (_recentPages.Count > MaxCachedPages)
        {
            _pages.Remove(_recentPages.Last!.Value);
            _recentPages.RemoveLast();
        }

        return page;
    }

    private void Touch(int pageIndex)
    {
        if (_recentPages.First?.Value == pageIndex)
        {
            return;
        }

        _recentPages.Remove(pageIndex);
        _recentPages.AddFirst(pageIndex);
    }

    public int IndexOf(LineViewModel item) => item.LineNumber - 1;

    public IEnumerator<LineViewModel> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Non-generic IList is what WPF's ItemCollection binds to. Everything mutating is unsupported.
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    // Value-based on purpose: a cached page may be evicted and rebuilt, and an item selected earlier must still map to its row.
    int IList.IndexOf(object? value) => value is LineViewModel line && line.LineNumber >= 1 && line.LineNumber <= Count
        ? line.LineNumber - 1
        : -1;

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
