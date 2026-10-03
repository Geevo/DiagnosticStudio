using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using DiagnosticStudio.App.Services;

namespace DiagnosticStudio.Tests.App;

public class AutoScrollTests
{
    // ---- speed ----

    [Fact]
    public void Nothing_scrolls_inside_the_dead_zone()
    {
        Assert.Equal(0, AutoScrollMath.Velocity(0, byItem: false));
        Assert.Equal(0, AutoScrollMath.Velocity(AutoScrollMath.DeadZone, byItem: false));
        Assert.Equal(0, AutoScrollMath.Velocity(-AutoScrollMath.DeadZone, byItem: true));
    }

    [Fact]
    public void Scrolling_follows_the_side_of_the_origin_the_pointer_is_on()
    {
        Assert.True(AutoScrollMath.Velocity(60, byItem: false) > 0);
        Assert.True(AutoScrollMath.Velocity(-60, byItem: false) < 0);
        Assert.Equal(AutoScrollMath.Velocity(60, byItem: false), -AutoScrollMath.Velocity(-60, byItem: false), 6);
    }

    [Fact]
    public void The_further_away_the_faster_up_to_a_limit()
    {
        var near = AutoScrollMath.Velocity(30, byItem: false);
        var mid = AutoScrollMath.Velocity(100, byItem: false);
        var far = AutoScrollMath.Velocity(300, byItem: false);
        var farther = AutoScrollMath.Velocity(900, byItem: false);

        Assert.True(near > 0 && near < mid && mid < far);
        Assert.Equal(AutoScrollMath.MaxPixelsPerTick, far, 6);
        Assert.Equal(far, farther, 6);
    }

    [Fact]
    public void A_list_that_scrolls_by_item_moves_in_smaller_steps_than_one_that_scrolls_by_pixel()
    {
        var pixels = AutoScrollMath.Velocity(120, byItem: false);
        var items = AutoScrollMath.Velocity(120, byItem: true);

        Assert.True(items > 0);
        Assert.True(items < pixels);
        Assert.True(items < 6); // a few items per tick at most, not a screenful
    }

    // ---- what scrolls ----

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

    private static ScrollViewer Viewer(int rows, double height)
    {
        var panel = new StackPanel();
        for (var i = 0; i < rows; i++)
        {
            panel.Children.Add(new TextBlock { Text = "row " + i, Height = 20 });
        }

        var viewer = new ScrollViewer { Content = panel, Height = height, Width = 200 };
        viewer.Measure(new Size(200, height));
        viewer.Arrange(new Rect(0, 0, 200, height));
        viewer.UpdateLayout();
        return viewer;
    }

    [Fact]
    public void A_pointer_over_the_content_of_a_scrollable_area_finds_that_area()
    {
        OnSta(() =>
        {
            var viewer = Viewer(100, 200);
            var child = ((StackPanel)viewer.Content).Children[40];

            Assert.Same(viewer, AutoScrollMath.FindScrollable(child));
            Assert.Same(viewer, AutoScrollMath.FindScrollable(viewer));
        });
    }

    [Fact]
    public void An_area_with_nothing_to_scroll_is_passed_over_for_one_that_has_something()
    {
        OnSta(() =>
        {
            var inner = Viewer(3, 200);      // fits: nothing to scroll
            Assert.Equal(0, inner.ScrollableHeight);
            var panel = new StackPanel { Height = 100 };
            panel.Children.Add(inner);
            var outer = new ScrollViewer { Content = new Border { Height = 5000, Child = null }, Height = 100, Width = 100 };
            outer.Measure(new Size(100, 100));
            outer.Arrange(new Rect(0, 0, 100, 100));
            outer.UpdateLayout();

            Assert.True(outer.ScrollableHeight > 0);
            Assert.Null(AutoScrollMath.FindScrollable(inner));          // nothing above it here
            Assert.Same(outer, AutoScrollMath.FindScrollable(outer.Content as DependencyObject));
        });
    }

    [Fact]
    public void Nothing_scrollable_means_no_auto_scroll()
    {
        OnSta(() =>
        {
            Assert.Null(AutoScrollMath.FindScrollable(null));
            Assert.Null(AutoScrollMath.FindScrollable(new TextBlock()));
            Assert.Null(AutoScrollMath.FindScrollable(Viewer(2, 300)));
        });
    }

    [Fact]
    public void A_scroll_area_scrolled_sideways_only_counts_too()
    {
        OnSta(() =>
        {
            var viewer = new ScrollViewer
            {
                Content = new Border { Width = 3000, Height = 20 },
                Width = 200,
                Height = 100,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            viewer.Measure(new Size(200, 100));
            viewer.Arrange(new Rect(0, 0, 200, 100));
            viewer.UpdateLayout();

            Assert.Same(viewer, AutoScrollMath.FindScrollable(viewer.Content as DependencyObject));
        });
    }

    [Fact]
    public void Text_inside_a_text_block_is_not_a_visual_and_still_finds_its_scroll_area()
    {
        // A click on log text lands on a Run, which is not a Visual; asking the visual tree about it threw and closed the app.
        OnSta(() =>
        {
            var run = new System.Windows.Documents.Run(string.Join("\n", Enumerable.Range(0, 200).Select(i => "row " + i)));
            var text = new TextBlock();
            text.Inlines.Add(run);
            var viewer = new ScrollViewer { Content = text, Height = 100, Width = 200 };
            viewer.Measure(new Size(200, 100));
            viewer.Arrange(new Rect(0, 0, 200, 100));
            viewer.UpdateLayout();

            Assert.Same(text, AutoScrollMath.Parent(run));
            Assert.Same(viewer, AutoScrollMath.FindScrollable(run));
        });
    }
}
