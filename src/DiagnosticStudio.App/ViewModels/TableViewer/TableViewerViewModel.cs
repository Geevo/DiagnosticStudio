using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.App.ViewModels.TableViewer;

public sealed record TableLevelOption(string Label, LogSeverity Minimum);

/// <summary>
/// Table viewer for CMTrace logs and CSV files: the records as rows, a detail pane for the selected one, a text and level
/// filter, and the raw text on a second tab. Every row knows its raw lines, so the table and the text stay linked.
/// </summary>
public sealed partial class TableViewerViewModel : ObservableObject, ILocationNavigable, ISearchHighlightable, ICurrentPosition
{
    public const int TableTab = 0;
    public const int RawTab = 1;

    private const int FilterDebounceMilliseconds = 250;
    private const int MaxCopyRows = 100_000;

    public static IReadOnlyList<TableLevelOption> LevelOptions { get; } = new[]
    {
        new TableLevelOption("All levels", LogSeverity.None),
        new TableLevelOption("Warnings and errors", LogSeverity.Warning),
        new TableLevelOption("Errors only", LogSeverity.Error),
    };

    private CancellationTokenSource? _filterCts;
    private int _run;
    private bool _quiet;

    public TableViewerViewModel(TableDocument document)
    {
        Document = document;
        Table = document.Table;
        Raw = new TextViewerViewModel(document.RawSource);
        _rows = new VirtualTableList(Table, view: null);
        _selectedLevel = LevelOptions[0];

        InfoText = string.Create(
            CultureInfo.CurrentCulture,
            $"{(document.Format == TableFormat.CmTrace ? "CMTrace log" : "Delimited text")} · {Table.RowCount:N0} records · {document.RawSource.LineCount:N0} lines · {document.RawSource.EncodingName}");
        if (document.Note is { Length: > 0 } note)
        {
            InfoText += " · " + note;
        }

        UnreadText = document.UnreadLines > 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{document.UnreadLines:N0} lines are not part of any record, first at line {document.FirstUnreadLine:N0}. They are in the raw source.")
            : null;

        UpdateStatus();
    }

    public TableDocument Document { get; }

    public ITableSource Table { get; }

    /// <summary>The complete file as text; always available.</summary>
    public TextViewerViewModel Raw { get; }

    public IReadOnlyList<TableColumn> Columns => Table.Columns;

    public bool HasLevels => Table.HasLevels;

    /// <summary>Log lines read best in a fixed-width font; delimited data in the normal one.</summary>
    public bool IsLog => Document.Format == TableFormat.CmTrace;

    public string InfoText { get; }

    /// <summary>Set when some lines belong to no record.</summary>
    public string? UnreadText { get; }

    public IReadOnlyList<TableLevelOption> Levels => LevelOptions;

    /// <summary>Raised with a place in the list when the view should scroll that row into view.</summary>
    public event EventHandler<int>? ScrollRequested;

    [ObservableProperty]
    private VirtualTableList _rows;

    [ObservableProperty]
    private TableRowViewModel? _selectedRow;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _detailInfo = string.Empty;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private TableLevelOption _selectedLevel;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isFiltering;

    [ObservableProperty]
    private int _selectedTabIndex = TableTab;

    /// <summary>The filter being applied; lets callers and tests await it.</summary>
    public Task PendingFilter { get; private set; } = Task.CompletedTask;

    // ---- selection ----

    partial void OnSelectedRowChanged(TableRowViewModel? value)
    {
        if (value is null)
        {
            DetailText = string.Empty;
            DetailInfo = string.Empty;
            return;
        }

        DetailText = value.Data.Detail;
        var first = value.Data.FirstLine;
        DetailInfo = value.Data.LineCount > 1
            ? string.Create(CultureInfo.CurrentCulture, $"Lines {first:N0}–{value.Data.LastLine:N0}")
            : string.Create(CultureInfo.CurrentCulture, $"Line {first:N0}");
    }

    // ---- filtering ----

    partial void OnFilterTextChanged(string value) => Restart(debounce: true);

    partial void OnSelectedLevelChanged(TableLevelOption value) => Restart(debounce: false);

    private void Restart(bool debounce)
    {
        if (_quiet)
        {
            return;
        }

        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();
        var run = ++_run;
        PendingFilter = RunFilterAsync(run, FilterText.Trim(), SelectedLevel.Minimum, debounce, _filterCts.Token);
    }

    private async Task RunFilterAsync(int run, string text, LogSeverity minimum, bool debounce, CancellationToken token)
    {
        try
        {
            if (text.Length == 0 && minimum == LogSeverity.None)
            {
                Show(new VirtualTableList(Table, view: null), run);
                return;
            }

            IsFiltering = true;
            if (debounce)
            {
                await Task.Delay(FilterDebounceMilliseconds, token).ConfigureAwait(true);
            }

            var view = await Task.Run(() => Select(text, minimum, token), token).ConfigureAwait(true);
            Show(new VirtualTableList(Table, view), run);
        }
        catch (OperationCanceledException)
        {
            // A newer filter owns the list now.
        }
        finally
        {
            if (run == _run)
            {
                IsFiltering = false;
            }
        }
    }

    /// <summary>The records that pass: with the text on any of their raw lines, and at or above the level.</summary>
    private int[] Select(string text, LogSeverity minimum, CancellationToken token)
    {
        var count = Table.RowCount;
        bool[]? hasText = null;
        if (text.Length > 0)
        {
            hasText = new bool[count];
            var number = 0;
            foreach (var line in Document.RawSource.EnumerateLines())
            {
                number++;
                if ((number & 0x3FFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (line.Contains(text, StringComparison.OrdinalIgnoreCase) && Table.RowOfLine(number) is var row and >= 0)
                {
                    hasText[row] = true;
                }
            }
        }

        var view = new List<int>();
        for (var row = 0; row < count; row++)
        {
            if ((row & 0xFFFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            if (hasText is not null && !hasText[row])
            {
                continue;
            }

            if (minimum != LogSeverity.None && Table.SeverityOf(row) < minimum)
            {
                continue;
            }

            view.Add(row);
        }

        return view.ToArray();
    }

    private void Show(VirtualTableList list, int run)
    {
        if (run != _run)
        {
            return;
        }

        var keep = SelectedRow?.Row;
        SelectedRow = null;
        Rows = list;
        UpdateStatus();

        if (keep is { } row && list.PositionOfRow(row) is var position and >= 0)
        {
            SelectPosition(position);
        }
    }

    private void UpdateStatus()
    {
        var culture = CultureInfo.CurrentCulture;
        StatusText = Rows.IsFiltered
            ? string.Create(culture, $"{Rows.Count:N0} of {Table.RowCount:N0} records")
            : string.Create(culture, $"{Table.RowCount:N0} records");
    }

    private void SelectPosition(int position)
    {
        SelectedRow = Rows[position];
        ScrollRequested?.Invoke(this, position);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _filterCts?.Cancel();
        var run = ++_run;

        // Setting the two properties would each start a filter run; this is the filter being removed, so none is wanted.
        _quiet = true;
        try
        {
            FilterText = string.Empty;
            SelectedLevel = LevelOptions[0];
        }
        finally
        {
            _quiet = false;
        }

        IsFiltering = false;
        Show(new VirtualTableList(Table, view: null), run);
        PendingFilter = Task.CompletedTask;
    }

    // ---- navigation ----

    public bool NavigateTo(DiagnosticLocation location)
    {
        if (location.Kind != DiagnosticLocationKind.Line || location.NumericPosition is not { } line)
        {
            return false;
        }

        var number = (int)Math.Min(line, int.MaxValue);
        var row = Table.RowOfLine(number);
        if (row < 0)
        {
            // A header or a line outside any record has no row; show it in the text.
            SelectedTabIndex = RawTab;
            Raw.NavigateToLine(number);
            return true;
        }

        if (Rows.PositionOfRow(row) < 0)
        {
            ClearFilters();
        }

        SelectedTabIndex = TableTab;
        SelectPosition(Rows.PositionOfRow(row));
        return true;
    }

    public void Highlight(SearchHighlight highlight) => Raw.Highlight(highlight);

    public DiagnosticLocation? CurrentPosition(Guid artifactId) =>
        SelectedRow is { } row ? DiagnosticLocation.ForLine(artifactId, row.FirstLine) : null;

    [RelayCommand]
    private void ShowInRawSource()
    {
        var line = SelectedRow?.FirstLine ?? 0;
        SelectedTabIndex = RawTab;
        if (line > 0)
        {
            Raw.NavigateToLine(line);
        }
    }

    [RelayCommand]
    private void ShowFirstUnreadLine()
    {
        SelectedTabIndex = RawTab;
        if (Document.FirstUnreadLine > 0)
        {
            Raw.NavigateToLine(Document.FirstUnreadLine);
        }
    }

    // ---- copying ----

    /// <summary>The rows as text for the clipboard: one record per line, values separated by tabs (pastes into a spreadsheet).</summary>
    public string BuildCopyText(IEnumerable<TableRowViewModel> rows) =>
        string.Join(
            Environment.NewLine,
            rows.OrderBy(r => r.Position).Take(MaxCopyRows).Select(r => string.Join('\t', r.Cells)));
}
