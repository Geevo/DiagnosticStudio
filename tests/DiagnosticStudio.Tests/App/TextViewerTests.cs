using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.App;

public sealed class TextViewerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-viewer-" + Guid.NewGuid().ToString("N"));

    public TextViewerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ITextLineSource Source(IEnumerable<string> lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return IndexedTextFile.Open(path);
    }

    private static IEnumerable<string> Numbered(int count, Func<int, string>? text = null) =>
        Enumerable.Range(1, count).Select(i => text?.Invoke(i) ?? $"line {i}");

    // ---- VirtualLineList ----

    [Fact]
    public void Virtual_list_serves_lines_with_one_based_numbers_across_page_boundaries()
    {
        var list = new VirtualLineList(Source(Numbered(1000)));

        Assert.Equal(1000, list.Count);
        Assert.Equal(1, list[0].LineNumber);
        Assert.Equal("line 256", list[255].Text);
        Assert.Equal("line 257", list[256].Text);
        Assert.Equal("line 1000", list[999].Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => list[1000]);
    }

    [Fact]
    public void Virtual_list_stays_correct_after_cached_pages_are_evicted()
    {
        var pages = 80; // more than the cache holds
        var list = new VirtualLineList(Source(Numbered(pages * VirtualLineList.PageSize)));

        for (var p = 0; p < pages; p++)
        {
            Assert.Equal(p * VirtualLineList.PageSize + 1, list[p * VirtualLineList.PageSize].LineNumber);
        }

        Assert.Equal("line 1", list[0].Text);
    }

    [Fact]
    public void Virtual_list_index_of_is_value_based_so_evicted_items_still_map_to_their_row()
    {
        var list = new VirtualLineList(Source(Numbered(100)));
        var item = list[41];

        Assert.Equal(41, ((System.Collections.IList)list).IndexOf(item));
        Assert.Equal(41, ((System.Collections.IList)list).IndexOf(new LineViewModel(42, "different instance")));
        Assert.Equal(-1, ((System.Collections.IList)list).IndexOf("not a line"));
    }

    [Fact]
    public void Setting_matches_updates_already_cached_lines_and_later_pages()
    {
        var list = new VirtualLineList(Source(Numbered(600)));
        var early = list[9];
        Assert.False(early.IsMatch);

        list.SetMatches(new[] { 10, 500 });

        Assert.True(early.IsMatch);
        Assert.False(list[10].IsMatch);
        Assert.True(list[499].IsMatch);
    }

    [Fact]
    public void Lines_carry_recognised_severity()
    {
        var list = new VirtualLineList(Source(new[] { "2026-07-23 10:00:00 ERROR boom", "plain" }));

        Assert.Equal(LogSeverity.Error, list[0].Severity);
        Assert.Equal(LogSeverity.None, list[1].Severity);
    }

    // ---- Find ----

    private static async Task Find(TextViewerViewModel vm, string text)
    {
        vm.FindText = text;
        await vm.PendingSearch;
    }

    [Fact]
    public async Task Typing_a_query_finds_matches_and_lands_on_the_first()
    {
        var vm = new TextViewerViewModel(Source(Numbered(100, i => i % 10 == 0 ? $"needle {i}" : $"hay {i}")));
        var scrolled = new List<int>();
        vm.ScrollToLineRequested += (_, line) => scrolled.Add(line);

        await Find(vm, "needle");

        Assert.Equal(10, vm.MatchCount);
        Assert.Equal(10, vm.CurrentLine);
        Assert.Equal("1 of 10", vm.SearchStatus);
        Assert.Equal("needle", vm.HighlightText);
        Assert.Equal(new[] { 10 }, scrolled);
        Assert.True(vm.Lines[9].IsMatch);
        Assert.False(vm.Lines[10].IsMatch);
    }

    [Fact]
    public async Task Find_next_and_previous_wrap_around()
    {
        var vm = new TextViewerViewModel(Source(Numbered(30, i => i % 10 == 0 ? "hit" : "miss")));
        await Find(vm, "hit"); // on line 10

        vm.FindNextCommand.Execute(null);
        Assert.Equal(20, vm.CurrentLine);
        vm.FindNextCommand.Execute(null);
        Assert.Equal(30, vm.CurrentLine);
        vm.FindNextCommand.Execute(null);
        Assert.Equal(10, vm.CurrentLine);
        Assert.Equal("1 of 3", vm.SearchStatus);

        vm.FindPreviousCommand.Execute(null);
        Assert.Equal(30, vm.CurrentLine);
        vm.FindPreviousCommand.Execute(null);
        Assert.Equal(20, vm.CurrentLine);
    }

    [Fact]
    public async Task Find_next_continues_from_the_line_the_user_selected()
    {
        var vm = new TextViewerViewModel(Source(Numbered(30, i => i % 10 == 0 ? "hit" : "miss")));
        await Find(vm, "hit");

        vm.SetCurrentLineFromSelection(25);
        vm.FindNextCommand.Execute(null);

        Assert.Equal(30, vm.CurrentLine);
    }

    [Fact]
    public async Task Incremental_find_starts_from_the_current_line()
    {
        var vm = new TextViewerViewModel(Source(Numbered(30, i => i % 10 == 0 ? "hit" : "miss")));
        vm.SetCurrentLineFromSelection(15);

        await Find(vm, "hit");

        Assert.Equal(20, vm.CurrentLine);
    }

    [Fact]
    public async Task Match_case_toggle_reruns_the_search()
    {
        var vm = new TextViewerViewModel(Source(new[] { "Error one", "error two", "ERROR three" }));
        await Find(vm, "error");
        Assert.Equal(3, vm.MatchCount);

        vm.MatchCase = true;
        await vm.PendingSearch;

        Assert.Equal(1, vm.MatchCount);
    }

    [Fact]
    public async Task No_matches_is_reported_and_clearing_the_query_resets_status()
    {
        var vm = new TextViewerViewModel(Source(new[] { "a", "b" }));

        await Find(vm, "zzz");
        Assert.Equal("No matches", vm.SearchStatus);
        Assert.Equal(0, vm.MatchCount);

        await Find(vm, "");
        Assert.Equal(string.Empty, vm.SearchStatus);
        Assert.Equal(string.Empty, vm.HighlightText);
    }

    [Fact]
    public async Task Find_commands_do_nothing_without_matches()
    {
        var vm = new TextViewerViewModel(Source(new[] { "a" }));
        await Find(vm, "zzz");

        vm.FindNextCommand.Execute(null);
        vm.FindPreviousCommand.Execute(null);

        Assert.Equal(0, vm.CurrentLine);
    }

    // ---- Go to line ----

    [Fact]
    public void Go_to_line_selects_clamps_and_reports_user_navigation()
    {
        var vm = new TextViewerViewModel(Source(Numbered(50)));
        var scrolled = new List<int>();
        var navigated = new List<int>();
        vm.ScrollToLineRequested += (_, l) => scrolled.Add(l);
        vm.LineNavigated += (_, l) => navigated.Add(l);

        vm.GoToLineText = "20";
        vm.GoToLineCommand.Execute(null);
        vm.GoToLineText = "9999";
        vm.GoToLineCommand.Execute(null);

        Assert.Equal(new[] { 20, 50 }, scrolled);
        Assert.Equal(new[] { 20, 50 }, navigated);
        Assert.Equal(50, vm.CurrentLine);
        Assert.Null(vm.GoToLineError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Go_to_line_rejects_invalid_input(string text)
    {
        var vm = new TextViewerViewModel(Source(Numbered(5)));
        var scrolled = false;
        vm.ScrollToLineRequested += (_, _) => scrolled = true;

        vm.GoToLineText = text;
        vm.GoToLineCommand.Execute(null);

        Assert.False(scrolled);
        Assert.NotNull(vm.GoToLineError);
    }

    [Fact]
    public void Programmatic_navigation_scrolls_but_is_not_reported_as_user_navigation()
    {
        var vm = new TextViewerViewModel(Source(Numbered(50)));
        var navigated = false;
        vm.LineNavigated += (_, _) => navigated = true;

        vm.NavigateToLine(7);

        Assert.Equal(7, vm.CurrentLine);
        Assert.False(navigated);
    }

    [Fact]
    public void Navigation_in_an_empty_file_is_a_no_op()
    {
        var path = Path.Combine(_dir, "empty.log");
        File.WriteAllBytes(path, Array.Empty<byte>());
        var vm = new TextViewerViewModel(IndexedTextFile.Open(path));

        vm.NavigateToLine(3);
        vm.GoToLineText = "1";
        vm.GoToLineCommand.Execute(null);

        Assert.Equal(0, vm.CurrentLine);
    }

    // ---- Filter ----

    private static async Task Filter(TextViewerViewModel vm, string text)
    {
        vm.FilterText = text;
        await vm.PendingFilter;
    }

    private TextViewerViewModel NeedleEveryTenth() =>
        new(Source(Numbered(100, i => i % 10 == 0 ? $"needle {i}" : $"hay {i}")));

    [Fact]
    public async Task Filter_shows_only_the_matching_lines_with_their_real_numbers()
    {
        var vm = NeedleEveryTenth();

        await Filter(vm, "NEEDLE");

        Assert.Equal(10, vm.Lines.Count);
        Assert.Equal(10, vm.Lines[0].LineNumber);
        Assert.Equal("needle 100", vm.Lines[9].Text);
        Assert.Contains("10 of 100", vm.FilterStatus);
        Assert.Equal(1, vm.Lines.PositionOfLine(20) - vm.Lines.PositionOfLine(10));
        Assert.Equal(-1, vm.Lines.PositionOfLine(11));
    }

    [Fact]
    public async Task Clearing_the_filter_shows_every_line_again()
    {
        var vm = NeedleEveryTenth();
        await Filter(vm, "needle");

        await Filter(vm, string.Empty);

        Assert.Equal(100, vm.Lines.Count);
        Assert.False(vm.Lines.IsFiltered);
        Assert.Equal(string.Empty, vm.FilterStatus);
    }

    [Fact]
    public async Task Find_steps_only_through_the_lines_the_filter_shows()
    {
        var vm = NeedleEveryTenth();
        await Find(vm, "5");
        await Filter(vm, "needle");

        vm.FindNextCommand.Execute(null);

        Assert.Equal(50, vm.CurrentLine);
        Assert.Equal("1 of 1", vm.SearchStatus);
    }

    [Fact]
    public async Task Go_to_line_clears_a_filter_that_hides_the_line()
    {
        var vm = NeedleEveryTenth();
        await Filter(vm, "needle");

        vm.GoToLineText = "7";
        vm.GoToLineCommand.Execute(null);

        Assert.Equal(string.Empty, vm.FilterText);
        Assert.Equal(100, vm.Lines.Count);
        Assert.Equal(7, vm.CurrentLine);
    }

    [Fact]
    public async Task Go_to_line_keeps_the_filter_when_the_line_is_shown()
    {
        var vm = NeedleEveryTenth();
        await Filter(vm, "needle");

        vm.GoToLineText = "30";
        vm.GoToLineCommand.Execute(null);

        Assert.Equal("needle", vm.FilterText);
        Assert.Equal(10, vm.Lines.Count);
        Assert.Equal(30, vm.CurrentLine);
    }

    [Fact]
    public async Task Marked_text_leaves_out_the_lines_the_filter_hides()
    {
        var vm = NeedleEveryTenth();
        await Filter(vm, "needle");

        vm.SelectAll();

        var lines = vm.SelectedText(out _).Split(Environment.NewLine);
        Assert.Equal(10, lines.Length);
        Assert.All(lines, l => Assert.StartsWith("needle", l));
    }

    // ---- Copy / info ----

    [Fact]
    public void Copy_text_is_in_file_order_regardless_of_selection_order()
    {
        var vm = new TextViewerViewModel(Source(Numbered(10)));

        var text = vm.BuildCopyText(new[] { vm.Lines[4], vm.Lines[2], vm.Lines[3] });

        Assert.Equal(string.Join(Environment.NewLine, "line 3", "line 4", "line 5"), text);
    }

    [Fact]
    public void Info_text_describes_encoding_lines_and_size()
    {
        var vm = new TextViewerViewModel(Source(Numbered(1234)));

        Assert.Contains("UTF-8", vm.InfoText);
        Assert.Contains("1,234", vm.InfoText.Replace(' ', ','));
    }

    [Fact]
    public void Gutter_grows_with_line_number_digits()
    {
        var small = new TextViewerViewModel(Source(Numbered(9)));
        var large = new TextViewerViewModel(Source(Numbered(100_000)));

        Assert.True(large.GutterWidth > small.GutterWidth);
    }
}
