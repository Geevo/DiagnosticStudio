using System.Collections;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.App.ViewModels.TableViewer;

/// <summary>One displayed record.</summary>
public sealed class TableRowViewModel
{
    public TableRowViewModel(int row, int position, TableRowData data, LogSeverity severity, object owner)
    {
        Row = row;
        Position = position;
        Data = data;
        Owner = owner;
        Severity = severity switch
        {
            LogSeverity.Error => "Error",
            LogSeverity.Warning => "Warning",
            _ => string.Empty,
        };
    }

    /// <summary>The list this row belongs to; a row of a replaced list is not selectable in the new one.</summary>
    public object Owner { get; }

    /// <summary>Zero-based record number in the file.</summary>
    public int Row { get; }

    /// <summary>Zero-based place in the list being shown (differs from <see cref="Row"/> when filtered).</summary>
    public int Position { get; }

    public TableRowData Data { get; }

    public IReadOnlyList<string> Cells => Data.Cells;

    public int FirstLine => Data.FirstLine;

    /// <summary>Used by the grid to colour rows: <c>Error</c>, <c>Warning</c> or empty.</summary>
    public string Severity { get; }
}

/// <summary>
/// Read-only list of rows for WPF virtualisation. A list is immutable: filtering creates a new instance. Rows are read
/// from the file a page at a time, and consecutive records of a page are read together.
/// </summary>
public sealed class VirtualTableList : IList, IReadOnlyList<TableRowViewModel>
{
    public const int PageSize = 128;
    private const int MaxCachedPages = 40;

    private readonly ITableSource _table;
    private readonly int[]? _view;
    private readonly Dictionary<int, TableRowViewModel[]> _pages = new();
    private readonly LinkedList<int> _recent = new();

    /// <param name="view">Ascending record numbers to show, or <c>null</c> for every record.</param>
    public VirtualTableList(ITableSource table, int[]? view)
    {
        _table = table;
        _view = view;
    }

    public static VirtualTableList Empty { get; } = new(new EmptyTable(), Array.Empty<int>());

    public int Count => _view?.Length ?? _table.RowCount;

    public bool IsFiltered => _view is not null;

    public TableRowViewModel this[int position]
    {
        get
        {
            if ((uint)position >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(position));
            }

            return GetPage(position / PageSize)[position % PageSize];
        }
    }

    /// <summary>Place of record <paramref name="row"/> in this list, or -1 when the filter hides it.</summary>
    public int PositionOfRow(int row)
    {
        if (_view is null)
        {
            return row >= 0 && row < _table.RowCount ? row : -1;
        }

        var at = Array.BinarySearch(_view, row);
        return at >= 0 ? at : -1;
    }

    private int RowAt(int position) => _view is null ? position : _view[position];

    private TableRowViewModel[] GetPage(int pageIndex)
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
        var page = new TableRowViewModel[count];

        // Read each run of consecutive records with one pass over the file.
        var i = 0;
        while (i < count)
        {
            var first = RowAt(start + i);
            var run = 1;
            while (i + run < count && RowAt(start + i + run) == first + run)
            {
                run++;
            }

            var rows = _table.GetRows(first, run);
            for (var k = 0; k < rows.Count; k++)
            {
                var row = first + k;
                page[i + k] = new TableRowViewModel(row, start + i + k, rows[k], _table.SeverityOf(row), this);
            }

            i += run;
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

    public IEnumerator<TableRowViewModel> GetEnumerator()
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

    // A row built by a page that was since evicted still maps to its place in this list.
    int IList.IndexOf(object? value) =>
        value is TableRowViewModel row && ReferenceEquals(row.Owner, this) ? row.Position : -1;

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

    private sealed class EmptyTable : ITableSource
    {
        public IReadOnlyList<TableColumn> Columns => Array.Empty<TableColumn>();
        public int RowCount => 0;
        public int HeaderLines => 0;
        public bool HasLevels => false;
        public TableRowData GetRow(int row) => throw new ArgumentOutOfRangeException(nameof(row));
        public IReadOnlyList<TableRowData> GetRows(int start, int count) => Array.Empty<TableRowData>();
        public int RowOfLine(int line) => -1;
        public LogSeverity SeverityOf(int row) => LogSeverity.None;
    }
}
