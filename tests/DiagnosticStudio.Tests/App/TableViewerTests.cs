using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.TableViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Parsers.Tables;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public class TableViewerTests
{
    private static string Rec(string message, string type = "1", string component = "Comp", string time = "14:12:00.000+000") =>
        $"<![LOG[{message}]LOG]!><time=\"{time}\" date=\"7-23-2026\" component=\"{component}\" context=\"\" type=\"{type}\" thread=\"7\" file=\"\">";

    // Lines: 1 info, 2 error, 3-4 warning (two lines), 5 info, 6 error
    private static readonly string[] Log =
    {
        Rec("started", "1", "Agent"),
        Rec("disk failure on D:", "3", "Disk"),
        "<![LOG[retry later",
        "  with backoff]LOG]!><time=\"14:12:02.000+000\" date=\"7-23-2026\" component=\"Agent\" context=\"\" type=\"2\" thread=\"7\" file=\"\">",
        Rec("completed", "1", "Agent"),
        Rec("Disk failure again", "3", "Disk"),
    };

    private static readonly DiagnosticArtifact Art = Artifact("agent.log", ArtifactType.TextLog);

    private static TableDocument LogDocument(params string[] lines)
    {
        var raw = new ListLines(lines.Length == 0 ? Log : lines);
        var table = CmTraceTable.Build(raw);
        return new TableDocument
        {
            Artifact = Art,
            Format = TableFormat.CmTrace,
            Table = table,
            RawSource = raw,
            UnreadLines = table.UnreadLines,
            FirstUnreadLine = table.FirstUnreadLine,
        };
    }

    private static TableDocument CsvDocument(params string[] lines)
    {
        var raw = new ListLines(lines);
        var table = CsvTable.Build(raw);
        return new TableDocument { Artifact = Art, Format = TableFormat.Csv, Table = table, RawSource = raw, Note = "Delimiter: comma." };
    }

    private static TableViewerViewModel Viewer(params string[] lines) => new(LogDocument(lines));

    // ---- opening ----

    [Fact]
    public void A_log_opens_with_every_record_listed_and_the_viewer_knows_it_is_a_log()
    {
        var vm = Viewer();

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal("5 records", vm.StatusText);
        Assert.True(vm.IsLog);
        Assert.True(vm.HasLevels);
        Assert.StartsWith("CMTrace log · 5 records · 6 lines", vm.InfoText);
        Assert.Equal(new[] { "Time", "Level", "Component", "Thread", "Message" }, vm.Columns.Select(c => c.Name));
        Assert.Null(vm.UnreadText);
    }

    [Fact]
    public void Rows_carry_their_cells_lines_and_a_colour_class()
    {
        var vm = Viewer();

        Assert.Equal("started", vm.Rows[0].Cells[4]);
        Assert.Equal(string.Empty, vm.Rows[0].Severity);
        Assert.Equal("Error", vm.Rows[1].Severity);
        Assert.Equal("Warning", vm.Rows[2].Severity);
        Assert.Equal(3, vm.Rows[2].FirstLine);
        Assert.Equal(0, vm.Rows[0].Row);
        Assert.Equal(1, vm.Rows[1].Position);
    }

    [Fact]
    public void A_csv_file_opens_as_a_table_without_levels()
    {
        var vm = new TableViewerViewModel(CsvDocument("Name,Count", "alpha,1", "beta,2"));

        Assert.False(vm.IsLog);
        Assert.False(vm.HasLevels);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains("Delimited text · 2 records", vm.InfoText);
        Assert.Contains("Delimiter: comma.", vm.InfoText);
        Assert.Equal(new[] { "Name", "Count" }, vm.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Lines_outside_any_record_are_announced_and_can_be_shown()
    {
        var vm = Viewer("junk first", Rec("a"), "junk between", Rec("b"));

        Assert.NotNull(vm.UnreadText);

        vm.ShowFirstUnreadLineCommand.Execute(null);

        Assert.Equal(TableViewerViewModel.RawTab, vm.SelectedTabIndex);
        Assert.Equal(1, vm.Raw.CurrentLine);
    }

    // ---- selection and detail ----

    [Fact]
    public void Selecting_a_record_shows_all_of_it_and_where_it_is()
    {
        var vm = Viewer();

        vm.SelectedRow = vm.Rows[2];

        Assert.Equal("Lines 3–4", vm.DetailInfo);
        Assert.Contains("retry later\n  with backoff", vm.DetailText);
        Assert.Contains("Component: Agent", vm.DetailText);

        vm.SelectedRow = vm.Rows[0];
        Assert.Equal("Line 1", vm.DetailInfo);

        vm.SelectedRow = null;
        Assert.Equal(string.Empty, vm.DetailText);
        Assert.Equal(string.Empty, vm.DetailInfo);
    }

    [Fact]
    public void The_selected_record_is_the_current_position_for_the_timeline()
    {
        var vm = Viewer();
        var id = Guid.NewGuid();
        Assert.Null(vm.CurrentPosition(id));

        vm.SelectedRow = vm.Rows[2];

        Assert.Equal(DiagnosticLocation.ForLine(id, 3), vm.CurrentPosition(id));
    }

    [Fact]
    public void Show_in_raw_source_goes_to_the_first_line_of_the_record()
    {
        var vm = Viewer();
        vm.SelectedRow = vm.Rows[2];

        vm.ShowInRawSourceCommand.Execute(null);

        Assert.Equal(TableViewerViewModel.RawTab, vm.SelectedTabIndex);
        Assert.Equal(3, vm.Raw.CurrentLine);
    }

    // ---- filtering ----

    [Fact]
    public async Task The_level_filter_keeps_records_at_or_above_it()
    {
        var vm = Viewer();

        vm.SelectedLevel = TableViewerViewModel.LevelOptions[2]; // errors only
        await vm.PendingFilter;
        Assert.Equal(new[] { 1, 4 }, vm.Rows.Select(r => r.Row));
        Assert.Equal("2 of 5 records", vm.StatusText);

        vm.SelectedLevel = TableViewerViewModel.LevelOptions[1]; // warnings and errors
        await vm.PendingFilter;
        Assert.Equal(new[] { 1, 2, 4 }, vm.Rows.Select(r => r.Row));
    }

    [Fact]
    public async Task The_text_filter_matches_any_line_of_a_record_ignoring_case()
    {
        var vm = Viewer();

        vm.FilterText = "WITH BACKOFF"; // on the second line of a two-line record
        await vm.PendingFilter;

        Assert.Equal(new[] { 2 }, vm.Rows.Select(r => r.Row));
        Assert.Equal("1 of 5 records", vm.StatusText);
    }

    [Fact]
    public async Task Text_and_level_filters_combine()
    {
        var vm = Viewer();

        vm.FilterText = "disk";
        vm.SelectedLevel = TableViewerViewModel.LevelOptions[2];
        await vm.PendingFilter;
        Assert.Equal(new[] { 1, 4 }, vm.Rows.Select(r => r.Row));

        vm.FilterText = "again";
        await vm.PendingFilter;
        Assert.Equal(new[] { 4 }, vm.Rows.Select(r => r.Row));
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_leaves_an_empty_list_and_says_so()
    {
        var vm = Viewer();

        vm.FilterText = "zzz-not-there";
        await vm.PendingFilter;

        Assert.Empty(vm.Rows);
        Assert.Equal("0 of 5 records", vm.StatusText);
    }

    [Fact]
    public async Task Typing_quickly_applies_only_the_last_text()
    {
        var vm = Viewer();

        vm.FilterText = "co";
        vm.FilterText = "comp";
        vm.FilterText = "completed";
        await vm.PendingFilter;

        Assert.Equal(new[] { 3 }, vm.Rows.Select(r => r.Row));
        Assert.False(vm.IsFiltering);
    }

    [Fact]
    public async Task Clearing_the_filters_shows_everything_again_at_once()
    {
        var vm = Viewer();
        vm.FilterText = "disk";
        vm.SelectedLevel = TableViewerViewModel.LevelOptions[2];
        await vm.PendingFilter;

        vm.ClearFiltersCommand.Execute(null);

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(string.Empty, vm.FilterText);
        Assert.Equal(TableViewerViewModel.LevelOptions[0], vm.SelectedLevel);
        Assert.Equal("5 records", vm.StatusText);
        Assert.False(vm.IsFiltering);
        await vm.PendingFilter; // nothing left running
        Assert.Equal(5, vm.Rows.Count);
    }

    [Fact]
    public async Task The_selection_survives_a_filter_when_the_record_is_still_shown()
    {
        var vm = Viewer();
        vm.SelectedRow = vm.Rows[1];

        vm.FilterText = "disk";
        await vm.PendingFilter;

        Assert.Equal(1, vm.SelectedRow!.Row);
        Assert.Equal(0, vm.SelectedRow.Position);
    }

    // ---- find ----

    [Fact]
    public async Task Find_marks_the_records_that_hold_the_text_without_hiding_any()
    {
        var vm = Viewer();

        vm.FindText = "disk";
        await vm.PendingFind;

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(new[] { 1, 4 }, vm.Rows.Where(r => r.IsMatch).Select(r => r.Row));
        Assert.Equal(2, vm.FindMatchCount);
    }

    [Fact]
    public async Task Find_lands_on_the_first_match_and_says_which_of_how_many()
    {
        var vm = Viewer();
        var scrolled = new List<int>();
        vm.ScrollRequested += (_, position) => scrolled.Add(position);

        vm.FindText = "disk";
        await vm.PendingFind;

        Assert.Equal(1, vm.SelectedRow!.Row);
        Assert.Equal("1 of 2", vm.FindStatus);
        Assert.Equal(new[] { 1 }, scrolled);
    }

    [Fact]
    public async Task Next_and_previous_step_through_the_matches_and_wrap_round()
    {
        var vm = Viewer();
        vm.FindText = "disk";
        await vm.PendingFind;

        vm.FindNextCommand.Execute(null);
        Assert.Equal(4, vm.SelectedRow!.Row);
        Assert.Equal("2 of 2", vm.FindStatus);

        vm.FindNextCommand.Execute(null);
        Assert.Equal(1, vm.SelectedRow!.Row);

        vm.FindPreviousCommand.Execute(null);
        Assert.Equal(4, vm.SelectedRow!.Row);

        vm.FindPreviousCommand.Execute(null);
        Assert.Equal(1, vm.SelectedRow!.Row);
    }

    [Fact]
    public async Task Find_starts_from_the_selected_record()
    {
        var vm = Viewer();
        vm.SelectedRow = vm.Rows[2];

        vm.FindText = "disk";
        await vm.PendingFind;

        Assert.Equal(4, vm.SelectedRow!.Row);
    }

    [Fact]
    public async Task Find_can_match_case_and_sees_every_line_of_a_record()
    {
        var vm = Viewer();

        vm.FindText = "disk";
        vm.FindMatchCase = true;
        await vm.PendingFind;
        Assert.Equal(new[] { 1 }, vm.Rows.Where(r => r.IsMatch).Select(r => r.Row));

        vm.FindMatchCase = false;
        vm.FindText = "WITH BACKOFF"; // on the second line of a two-line record
        await vm.PendingFind;
        Assert.Equal(new[] { 2 }, vm.Rows.Where(r => r.IsMatch).Select(r => r.Row));
    }

    [Fact]
    public async Task Find_only_steps_through_the_records_the_filter_shows()
    {
        var vm = Viewer();
        vm.FindText = "agent"; // records 0, 2 and 3 (their component)
        await vm.PendingFind;
        Assert.Equal(3, vm.FindMatchCount);

        vm.FilterText = "completed";
        await vm.PendingFilter;

        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.FindMatchCount);
        Assert.True(vm.Rows[0].IsMatch);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, vm.FindMatchCount);
        Assert.Equal(new[] { 0, 2, 3 }, vm.Rows.Where(r => r.IsMatch).Select(r => r.Row));
    }

    [Fact]
    public async Task A_filter_keeps_the_marks_of_a_find_made_before_it()
    {
        var vm = Viewer();
        vm.FindText = "disk";
        await vm.PendingFind;

        vm.SelectedLevel = TableViewerViewModel.LevelOptions[2]; // errors only
        await vm.PendingFilter;

        Assert.Equal(new[] { 1, 4 }, vm.Rows.Where(r => r.IsMatch).Select(r => r.Row));
        Assert.Equal("1 of 2", vm.FindStatus); // the record the find landed on is still the selected one
    }

    [Fact]
    public async Task Clearing_the_find_text_removes_the_marks_and_the_status()
    {
        var vm = Viewer();
        vm.FindText = "disk";
        await vm.PendingFind;

        vm.FindText = string.Empty;
        await vm.PendingFind;

        Assert.DoesNotContain(vm.Rows, r => r.IsMatch);
        Assert.Equal(0, vm.FindMatchCount);
        Assert.Equal(string.Empty, vm.FindStatus);
    }

    [Fact]
    public async Task A_find_that_matches_nothing_says_so_and_next_does_nothing()
    {
        var vm = Viewer();
        vm.FindText = "zzz-not-there";
        await vm.PendingFind;

        vm.FindNextCommand.Execute(null);
        vm.FindPreviousCommand.Execute(null);

        Assert.Equal("No matches", vm.FindStatus);
        Assert.Null(vm.SelectedRow);
        Assert.Equal(5, vm.Rows.Count);
    }

    // ---- links in ----

    [Fact]
    public void A_line_link_selects_the_record_that_holds_it_and_scrolls_to_it()
    {
        var vm = Viewer();
        var scrolled = new List<int>();
        vm.ScrollRequested += (_, position) => scrolled.Add(position);

        Assert.True(vm.NavigateTo(DiagnosticLocation.ForLine(Art.Id, 4))); // the second line of a record

        Assert.Equal(2, vm.SelectedRow!.Row);
        Assert.Equal(new[] { 2 }, scrolled);
        Assert.Equal(TableViewerViewModel.TableTab, vm.SelectedTabIndex);
    }

    [Fact]
    public async Task A_link_to_a_record_the_filter_hides_clears_the_filter_first()
    {
        var vm = Viewer();
        vm.SelectedLevel = TableViewerViewModel.LevelOptions[2];
        await vm.PendingFilter;
        Assert.Equal(2, vm.Rows.Count);

        vm.NavigateTo(DiagnosticLocation.ForLine(Art.Id, 5)); // an information record

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(3, vm.SelectedRow!.Row);
        Assert.Equal(TableViewerViewModel.LevelOptions[0], vm.SelectedLevel);
    }

    [Fact]
    public void A_link_to_a_line_outside_any_record_opens_the_raw_text_there()
    {
        var vm = Viewer("a header line", Rec("a"), Rec("b"));

        Assert.True(vm.NavigateTo(DiagnosticLocation.ForLine(Art.Id, 1)));

        Assert.Equal(TableViewerViewModel.RawTab, vm.SelectedTabIndex);
        Assert.Equal(1, vm.Raw.CurrentLine);
        Assert.Null(vm.SelectedRow);
    }

    [Fact]
    public void A_csv_header_line_link_opens_the_raw_text_and_a_data_line_selects_its_row()
    {
        var vm = new TableViewerViewModel(CsvDocument("Name,Count", "alpha,1", "beta,2"));

        vm.NavigateTo(DiagnosticLocation.ForLine(Art.Id, 1));
        Assert.Equal(TableViewerViewModel.RawTab, vm.SelectedTabIndex);

        vm.NavigateTo(DiagnosticLocation.ForLine(Art.Id, 3));
        Assert.Equal(TableViewerViewModel.TableTab, vm.SelectedTabIndex);
        Assert.Equal("beta", vm.SelectedRow!.Cells[0]);
    }

    [Fact]
    public void Other_kinds_of_location_are_not_understood()
    {
        var vm = Viewer();

        Assert.False(vm.NavigateTo(DiagnosticLocation.ForEventRecord(Art.Id, 5)));
        Assert.False(vm.NavigateTo(DiagnosticLocation.ForRegistry(Art.Id, "HKLM\\X")));
    }

    [Fact]
    public void A_search_highlight_goes_to_the_raw_text()
    {
        var vm = Viewer();

        vm.Highlight(new SearchHighlight("disk", MatchCase: false));

        Assert.Equal("disk", vm.Raw.FindText);
    }

    // ---- copy ----

    [Fact]
    public void Copying_rows_gives_tab_separated_values_in_list_order()
    {
        var vm = Viewer();

        var text = vm.BuildCopyText(new[] { vm.Rows[3], vm.Rows[0] });

        var lines = text.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-07-23 14:12:00.000\tInformation\tAgent\t7\tstarted", lines[0]);
        Assert.EndsWith("\tcompleted", lines[1]);
    }

    // ---- the list ----

    private sealed class CountingTable : ITableSource
    {
        private readonly ITableSource _inner;

        public CountingTable(ITableSource inner) => _inner = inner;

        public int GetRowsCalls { get; private set; }
        public IReadOnlyList<TableColumn> Columns => _inner.Columns;
        public int RowCount => _inner.RowCount;
        public int HeaderLines => _inner.HeaderLines;
        public bool HasLevels => _inner.HasLevels;
        public TableRowData GetRow(int row) => _inner.GetRow(row);

        public IReadOnlyList<TableRowData> GetRows(int start, int count)
        {
            GetRowsCalls++;
            return _inner.GetRows(start, count);
        }

        public int RowOfLine(int line) => _inner.RowOfLine(line);
        public LogSeverity SeverityOf(int row) => _inner.SeverityOf(row);
    }

    [Fact]
    public void A_page_of_consecutive_records_is_read_with_one_read_of_the_file()
    {
        var lines = Enumerable.Range(0, 400).Select(i => Rec("row " + i)).ToArray();
        var table = new CountingTable(CmTraceTable.Build(new ListLines(lines)));
        var list = new VirtualTableList(table, view: null);

        Assert.Equal("row 5", list[5].Cells[4]);
        Assert.Equal(1, table.GetRowsCalls);

        Assert.Equal("row 70", list[70].Cells[4]); // same page: cached
        Assert.Equal(1, table.GetRowsCalls);

        Assert.Equal("row 300", list[300].Cells[4]); // another page
        Assert.Equal(2, table.GetRowsCalls);
    }

    [Fact]
    public void A_filtered_list_reads_each_run_of_consecutive_records_together()
    {
        var lines = Enumerable.Range(0, 100).Select(i => Rec("row " + i)).ToArray();
        var table = new CountingTable(CmTraceTable.Build(new ListLines(lines)));
        var list = new VirtualTableList(table, new[] { 10, 11, 12, 13, 40, 41, 90 });

        Assert.Equal("row 90", list[6].Cells[4]);

        Assert.Equal(3, table.GetRowsCalls); // three runs: 10-13, 40-41, 90
        Assert.Equal("row 12", list[2].Cells[4]);
        Assert.Equal(3, table.GetRowsCalls);
    }

    [Fact]
    public void A_record_is_found_in_the_list_by_its_number_and_the_filter_can_hide_it()
    {
        var table = CmTraceTable.Build(new ListLines(Enumerable.Range(0, 20).Select(i => Rec("r" + i)).ToArray()));
        var all = new VirtualTableList(table, view: null);
        var some = new VirtualTableList(table, new[] { 3, 7, 11 });

        Assert.Equal(9, all.PositionOfRow(9));
        Assert.Equal(-1, all.PositionOfRow(99));
        Assert.Equal(1, some.PositionOfRow(7));
        Assert.Equal(-1, some.PositionOfRow(8));
    }

    [Fact]
    public void A_row_of_an_evicted_page_still_maps_to_its_place_and_a_row_of_another_list_does_not()
    {
        var table = CmTraceTable.Build(new ListLines(Enumerable.Range(0, 20_000).Select(i => Rec("r" + i)).ToArray()));
        var list = new VirtualTableList(table, view: null);
        var other = new VirtualTableList(table, view: null);
        var first = list[3];

        for (var page = 1; page < 60; page++)
        {
            _ = list[page * VirtualTableList.PageSize]; // pushes the first page out of the cache
        }

        Assert.Equal(3, ((System.Collections.IList)list).IndexOf(first));
        Assert.Equal(-1, ((System.Collections.IList)other).IndexOf(first));
    }

    [Fact]
    public void An_out_of_range_position_throws_and_the_empty_list_is_empty()
    {
        var list = new VirtualTableList(CmTraceTable.Build(new ListLines(new[] { Rec("a") })), view: null);

        Assert.Throws<ArgumentOutOfRangeException>(() => list[1]);
        Assert.Empty(VirtualTableList.Empty);
    }
}
