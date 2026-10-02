using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.App.Views;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public class TextSelectionTests
{
    private static TextPosition P(int line, int column) => new(line, column);

    private static TextViewerViewModel Viewer(params string[] lines) => new(new ListLines(lines));

    // ---- the model ----

    [Fact]
    public void A_selection_runs_from_the_earlier_place_to_the_later_whichever_way_it_was_dragged()
    {
        var forward = new LogSelection(P(2, 5), P(4, 1));
        var backward = new LogSelection(P(4, 1), P(2, 5));

        Assert.Equal(P(2, 5), forward.Start);
        Assert.Equal(P(4, 1), forward.End);
        Assert.Equal(forward.Start, backward.Start);
        Assert.Equal(forward.End, backward.End);
        Assert.Equal(3, forward.LineCount);
    }

    [Fact]
    public void Nothing_is_selected_when_anchor_and_caret_are_together()
    {
        Assert.True(LogSelection.None.IsEmpty);
        Assert.True(new LogSelection(P(3, 4), P(3, 4)).IsEmpty);
        Assert.Equal(0, LogSelection.None.LineCount);
        Assert.Null(new LogSelection(P(3, 4), P(3, 4)).SpanOnLine(3, 10));
    }

    [Fact]
    public void On_one_line_the_span_is_between_the_two_columns()
    {
        var selection = new LogSelection(P(3, 2), P(3, 7));

        Assert.Equal((2, 5), selection.SpanOnLine(3, 20));
        Assert.Null(selection.SpanOnLine(2, 20));
        Assert.Null(selection.SpanOnLine(4, 20));
    }

    [Fact]
    public void Across_lines_the_first_is_selected_from_the_start_the_last_to_the_end_and_those_between_whole()
    {
        var selection = new LogSelection(P(2, 4), P(4, 3));

        Assert.Equal((4, 6), selection.SpanOnLine(2, 10));  // from column 4 to the end
        Assert.Equal((0, 12), selection.SpanOnLine(3, 12)); // all of it
        Assert.Equal((0, 3), selection.SpanOnLine(4, 9));   // up to column 3
    }

    [Fact]
    public void Columns_past_the_end_of_a_line_are_brought_back_to_it()
    {
        var selection = new LogSelection(P(1, 50), P(2, 80));

        Assert.Null(selection.SpanOnLine(1, 10));           // starts after the end: nothing on the first line
        Assert.Equal((0, 5), new LogSelection(P(1, 0), P(1, 80)).SpanOnLine(1, 5));
        Assert.Equal((0, 5), selection.SpanOnLine(2, 5));
    }

    [Fact]
    public void An_empty_line_in_the_middle_has_nothing_to_mark_and_a_selection_ending_at_column_zero_leaves_its_last_line_unmarked()
    {
        var selection = new LogSelection(P(1, 2), P(3, 0));

        Assert.Null(selection.SpanOnLine(2, 0));
        Assert.Null(selection.SpanOnLine(3, 8));
    }

    [Fact]
    public void Positions_compare_by_line_then_column()
    {
        Assert.True(P(1, 9) < P(2, 0));
        Assert.True(P(2, 3) > P(2, 1));
        Assert.True(P(2, 3) >= P(2, 3));
        Assert.True(P(2, 3) <= P(2, 3));
    }

    // ---- the viewer ----

    [Fact]
    public void Marking_text_is_the_default_and_whole_line_selection_is_a_choice()
    {
        var vm = Viewer("a", "b");

        Assert.True(vm.FreeSelection);
        Assert.False(vm.LineSelection);
        Assert.False(vm.HasTextSelection);
    }

    [Fact]
    public void Switching_to_whole_lines_puts_the_marked_text_away()
    {
        var vm = Viewer("hello world");
        vm.Select(P(1, 0), P(1, 5));
        Assert.True(vm.HasTextSelection);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.LineSelection = true;

        Assert.False(vm.HasTextSelection);
        Assert.False(vm.FreeSelection);
        Assert.Contains(nameof(TextViewerViewModel.FreeSelection), changed);
    }

    [Fact]
    public void Marking_announces_the_selection_and_its_summary()
    {
        var vm = Viewer("hello world");
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Select(P(1, 0), P(1, 5));

        Assert.Contains(nameof(TextViewerViewModel.Selection), changed);
        Assert.Contains(nameof(TextViewerViewModel.HasTextSelection), changed);
        Assert.Contains(nameof(TextViewerViewModel.SelectionInfo), changed);
    }

    [Fact]
    public void Places_outside_the_file_are_brought_inside_it()
    {
        var vm = Viewer("one", "two", "three");

        vm.Select(P(-4, -9), P(99, 3));

        Assert.Equal(P(1, 0), vm.Selection.Anchor);
        Assert.Equal(P(3, 3), vm.Selection.Caret);
    }

    [Fact]
    public void The_marked_text_of_one_line_is_the_slice()
    {
        var vm = Viewer("2026-07-23 14:12:00 ERROR boom happened");

        vm.Select(P(1, 20), P(1, 25));

        Assert.Equal("ERROR", vm.SelectedText(out var truncated));
        Assert.False(truncated);
    }

    [Fact]
    public void The_marked_text_across_lines_is_joined_with_line_breaks()
    {
        var vm = Viewer("first line", "second line", "third line");

        vm.Select(P(1, 6), P(3, 5));

        Assert.Equal("line" + Environment.NewLine + "second line" + Environment.NewLine + "third", vm.SelectedText(out _));
    }

    [Fact]
    public void Dragging_backwards_marks_the_same_text()
    {
        var vm = Viewer("first line", "second line");

        vm.Select(P(2, 6), P(1, 6));

        Assert.Equal("line" + Environment.NewLine + "second", vm.SelectedText(out _));
    }

    [Fact]
    public void Nothing_marked_copies_nothing()
    {
        Assert.Equal(string.Empty, Viewer("a").SelectedText(out _));
    }

    [Fact]
    public void Select_all_marks_every_line_and_copies_the_whole_file()
    {
        var vm = Viewer("one", "two", "three");

        vm.SelectAll();

        Assert.Equal("one" + Environment.NewLine + "two" + Environment.NewLine + "three", vm.SelectedText(out _));
        Assert.Equal(P(3, 5), vm.Selection.Caret);
    }

    [Fact]
    public void Select_all_of_an_empty_file_marks_nothing()
    {
        var vm = Viewer();

        vm.SelectAll();

        Assert.False(vm.HasTextSelection);
    }

    [Fact]
    public void A_line_can_be_marked_whole_and_a_bad_line_is_ignored()
    {
        var vm = Viewer("one", "two words", "three");

        vm.SelectLineAt(2);
        Assert.Equal("two words", vm.SelectedText(out _));

        vm.SelectLineAt(99);
        vm.SelectLineAt(0);
        Assert.Equal("two words", vm.SelectedText(out _)); // unchanged
    }

    [Fact]
    public void Clearing_removes_the_marking()
    {
        var vm = Viewer("hello");
        vm.SelectAll();

        vm.ClearTextSelection();

        Assert.False(vm.HasTextSelection);
        Assert.Equal(string.Empty, vm.SelectionInfo);
    }

    // ---- words ----

    [Theory]
    [InlineData("2026-07-23 14:12:30.123 INFO start", 5, "2026-07-23")]
    [InlineData("2026-07-23 14:12:30.123 INFO start", 14, "14:12:30.123")]
    [InlineData("loaded C:\\Windows\\System32\\drivers\\x.sys now", 12, "C:\\Windows\\System32\\drivers\\x.sys")]
    [InlineData("id=0e5abd85-e2f4-4d07-be31-f1b33bb56dfd;next", 10, "0e5abd85-e2f4-4d07-be31-f1b33bb56dfd")]
    [InlineData("name=\"quoted value\" end", 8, "quoted")]
    [InlineData("[Component] message", 3, "Component")]
    [InlineData("a,b,c", 2, "b")]
    public void A_double_click_marks_the_word_between_the_characters_that_delimit_values(string line, int column, string expected)
    {
        var vm = Viewer(line);

        vm.SelectWordAt(1, column);

        Assert.Equal(expected, vm.SelectedText(out _));
    }

    [Fact]
    public void A_double_click_on_spaces_marks_the_run_of_spaces()
    {
        var vm = Viewer("a     b");

        vm.SelectWordAt(1, 3);

        Assert.Equal("     ", vm.SelectedText(out _));
    }

    [Fact]
    public void A_double_click_past_the_end_marks_the_last_word_and_on_an_empty_line_nothing()
    {
        var vm = Viewer("end of line", string.Empty);

        vm.SelectWordAt(1, 500);
        Assert.Equal("line", vm.SelectedText(out _));

        vm.ClearTextSelection();
        vm.SelectWordAt(2, 0);
        Assert.False(vm.HasTextSelection);
        vm.SelectWordAt(9, 0); // not a line
        Assert.False(vm.HasTextSelection);
    }

    // ---- size ----

    [Fact]
    public void A_selection_of_a_huge_file_is_cut_at_the_copy_limit_and_says_so()
    {
        var lines = Enumerable.Range(1, TextViewerViewModel.MaxCopyLines + 50).Select(i => "line " + i).ToArray();
        var vm = Viewer(lines);

        vm.SelectAll();
        var text = vm.SelectedText(out var truncated);

        Assert.True(truncated);
        Assert.Equal(TextViewerViewModel.MaxCopyLines, text.Split(Environment.NewLine).Length);
        Assert.EndsWith("line " + TextViewerViewModel.MaxCopyLines, text);
    }

    [Fact]
    public void Reading_a_large_selection_in_chunks_keeps_every_line_in_order()
    {
        var lines = Enumerable.Range(1, 7_000).Select(i => "line " + i).ToArray();
        var vm = Viewer(lines);

        vm.Select(P(1, 0), P(7_000, 9));
        var text = vm.SelectedText(out var truncated).Split(Environment.NewLine);

        Assert.False(truncated);
        Assert.Equal(lines, text);
    }

    // ---- the summary ----

    [Fact]
    public void The_summary_counts_lines_and_characters_for_a_small_selection()
    {
        var vm = Viewer("hello world", "second");

        vm.Select(P(1, 6), P(1, 11));
        Assert.Equal("1 line, 5 characters selected", vm.SelectionInfo);

        vm.Select(P(1, 6), P(2, 3));
        Assert.Equal("2 lines, " + (5 + Environment.NewLine.Length + 3) + " characters selected", vm.SelectionInfo);
    }

    [Fact]
    public void The_summary_of_a_huge_selection_does_not_read_the_whole_file()
    {
        var vm = Viewer(Enumerable.Range(1, 20_000).Select(i => "line " + i).ToArray());

        vm.SelectAll();

        Assert.Equal("20,000 lines selected", vm.SelectionInfo);
    }

    // ---- drawing it ----

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private static HighlightedTextBlock Block(string text, int lineNumber = 5)
    {
        var block = new HighlightedTextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Line = new LineViewModel(lineNumber, text),
        };
        return block;
    }

    /// <summary>Shows the block in a window off screen so that it is laid out for real.</summary>
    private static Window Host(HighlightedTextBlock block)
    {
        var window = new Window
        {
            Content = block,
            Width = 1200,
            Height = 200,
            Left = -20000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        block.UpdateLayout();
        return window;
    }

    private static string[] Pieces(TextBlock block) => block.Inlines.OfType<Run>().Select(r => r.Text).ToArray();

    private static bool[] Marked(TextBlock block) => block.Inlines.OfType<Run>()
        .Select(r => DependencyPropertyHelper.GetValueSource(r, TextElement.BackgroundProperty).IsExpression)
        .ToArray();

    [Fact]
    public void A_line_the_selection_does_not_touch_is_drawn_whole()
    {
        OnSta(() =>
        {
            var block = Block("plain text with no highlights", 5);
            block.Selection = new LogSelection(P(1, 0), P(3, 4));

            Assert.Equal(new[] { "plain text with no highlights" }, Pieces(block));
        });
    }

    [Fact]
    public void A_selection_on_part_of_a_line_splits_it_into_before_marked_and_after()
    {
        OnSta(() =>
        {
            var block = Block("plain text with no highlights", 5);

            block.Selection = new LogSelection(P(5, 6), P(5, 10));

            Assert.Equal(new[] { "plain ", "text", " with no highlights" }, Pieces(block));
            Assert.Equal(new[] { false, true, false }, Marked(block));
        });
    }

    [Fact]
    public void A_line_in_the_middle_of_a_selection_is_marked_whole_and_the_ends_from_and_to_their_columns()
    {
        OnSta(() =>
        {
            var selection = new LogSelection(P(4, 3), P(6, 5));

            var first = Block("0123456789", 4);
            first.Selection = selection;
            Assert.Equal(new[] { "012", "3456789" }, Pieces(first));
            Assert.Equal(new[] { false, true }, Marked(first));

            var middle = Block("0123456789", 5);
            middle.Selection = selection;
            Assert.Equal(new[] { "0123456789" }, Pieces(middle));
            Assert.Equal(new[] { true }, Marked(middle));

            var last = Block("0123456789", 6);
            last.Selection = selection;
            Assert.Equal(new[] { "01234", "56789" }, Pieces(last));
            Assert.Equal(new[] { true, false }, Marked(last));
        });
    }

    [Fact]
    public void Clearing_the_selection_draws_the_line_plain_again()
    {
        OnSta(() =>
        {
            var block = Block("0123456789", 5);
            block.Selection = new LogSelection(P(5, 2), P(5, 4));
            Assert.Equal(3, Pieces(block).Length);

            block.Selection = LogSelection.None;

            Assert.Equal(new[] { "0123456789" }, Pieces(block));
        });
    }

    [Fact]
    public void A_selection_and_a_found_word_and_a_time_stamp_can_overlap_on_one_line()
    {
        OnSta(() =>
        {
            var block = Block("2026-07-23 14:12:00 ERROR boom happened", 5);
            block.Highlight = "boom";

            block.Selection = new LogSelection(P(5, 0), P(5, 27)); // time stamp, ERROR and part of "boom"

            Assert.Equal("2026-07-23 14:12:00 ERROR boom happened", string.Concat(Pieces(block)));
            var pieces = Pieces(block);
            var marked = Marked(block);
            Assert.Contains("oom", pieces);          // the found word is cut where the selection ends inside it
            Assert.True(marked[0]);                  // the start of the line is marked
            Assert.False(marked[^1]);                // the end is not
        });
    }

    [Fact]
    public void Redrawing_leaves_the_text_exactly_as_it_was()
    {
        OnSta(() =>
        {
            var text = "  odd   spacing\ttabs and unicode \u00e9\u00fc\u4e2d ";
            var block = Block(text, 5);
            block.Selection = new LogSelection(P(5, 3), P(5, 20));
            block.Highlight = "spacing";

            Assert.Equal(text, string.Concat(Pieces(block)));
        });
    }

    [Fact]
    public void A_click_position_maps_to_a_character_of_the_line()
    {
        OnSta(() =>
        {
            var text = "abcdefghijklmnopqrstuvwxyz";
            var block = Block(text, 5);
            var window = Host(block);
            try
            {
                Assert.Equal(0, block.ColumnAt(new Point(0, 4)));
                Assert.Equal(text.Length, block.ColumnAt(new Point(5000, 4)));

                var half = block.ColumnAt(new Point(block.DesiredSize.Width / 2, 4));
                Assert.InRange(half, 8, 18);

                // Further right is never an earlier character.
                var previous = 0;
                for (var x = 0; x < block.DesiredSize.Width; x += 3)
                {
                    var column = block.ColumnAt(new Point(x, 4));
                    Assert.True(column >= previous);
                    previous = column;
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_click_position_on_a_highlighted_line_counts_across_the_pieces()
    {
        OnSta(() =>
        {
            var text = "2026-07-23 14:12:00 ERROR boom happened here";
            var block = Block(text, 5);
            block.Highlight = "boom";
            block.Selection = new LogSelection(P(5, 3), P(5, 9));   // several runs now
            var window = Host(block);
            try
            {
                Assert.True(Pieces(block).Length > 3);
                Assert.Equal(0, block.ColumnAt(new Point(0, 4)));
                Assert.Equal(text.Length, block.ColumnAt(new Point(5000, 4)));
                var quarter = block.ColumnAt(new Point(block.DesiredSize.Width / 4, 4));
                Assert.InRange(quarter, 6, 16);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
