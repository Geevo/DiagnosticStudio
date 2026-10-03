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
    private readonly int[]? _view;
    private readonly Dictionary<int, LineViewModel[]> _pages = new();
    private readonly LinkedList<int> _recentPages = new();
    private int[] _matchLines = Array.Empty<int>();

    /// <param name="view">Ascending one-based numbers of the lines to show, or <c>null</c> for every line.</param>
    public VirtualLineList(ITextLineSource source, int[]? view = null)
    {
        _source = source;
        _view = view;
    }

    public int Count => _view?.Length ?? _source.LineCount;

    /// <summary>True when only some of the lines are shown.</summary>
    public bool IsFiltered => _view is not null;

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

    /// <summary>Place in this list of a line (one-based), or -1 when the filter hides it or it is outside the file.</summary>
    public int PositionOfLine(int line)
    {
        if (_view is null)
        {
            return line >= 1 && line <= _source.LineCount ? line - 1 : -1;
        }

        var at = Array.BinarySearch(_view, line);
        return at >= 0 ? at : -1;
    }

    /// <summary>True when the line (one-based) is shown by this list.</summary>
    public bool IsShown(int line) => PositionOfLine(line) >= 0;

    private int LineAt(int position) => _view is null ? position + 1 : _view[position];

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
        var page = new LineViewModel[Math.Min(PageSize, Count - start)];
        if (_view is null)
        {
            var texts = _source.ReadLines(start, PageSize);
            page = new LineViewModel[texts.Count];
            for (var i = 0; i < page.Length; i++)
            {
                var number = start + i + 1;
                page[i] = new LineViewModel(number, texts[i]) { IsMatch = IsMatch(number) };
            }
        }
        else
        {
            // Read each run of neighbouring lines together.
            var i = 0;
            while (i < page.Length)
            {
                var first = LineAt(start + i);
                var run = 1;
                while (i + run < page.Length && LineAt(start + i + run) == first + run)
                {
                    run++;
                }

                var texts = _source.ReadLines(first - 1, run);
                for (var k = 0; k < texts.Count; k++)
                {
                    page[i + k] = new LineViewModel(first + k, texts[k]) { IsMatch = IsMatch(first + k) };
                }

                i += run;
            }
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

    public int IndexOf(LineViewModel item) => PositionOfLine(item.LineNumber);

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
    int IList.IndexOf(object? value) => value is LineViewModel line ? PositionOfLine(line.LineNumber) : -1;

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
