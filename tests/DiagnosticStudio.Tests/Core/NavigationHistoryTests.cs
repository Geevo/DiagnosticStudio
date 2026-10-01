using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.Tests.Core;

public class NavigationHistoryTests
{
    private static DiagnosticLocation Loc(int n) => DiagnosticLocation.ForLine(Guid.Empty, n);

    [Fact]
    public void Empty_history_cannot_navigate()
    {
        var history = new NavigationHistory();

        Assert.Null(history.Current);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.GoBack());
        Assert.Null(history.GoForward());
    }

    [Fact]
    public void Back_and_forward_walk_the_recorded_entries()
    {
        var history = new NavigationHistory();
        history.Record(Loc(1));
        history.Record(Loc(2));
        history.Record(Loc(3));

        Assert.Equal(Loc(2), history.GoBack());
        Assert.Equal(Loc(1), history.GoBack());
        Assert.False(history.CanGoBack);
        Assert.Equal(Loc(2), history.GoForward());
        Assert.True(history.CanGoForward);
    }

    [Fact]
    public void Recording_after_going_back_discards_forward_entries()
    {
        var history = new NavigationHistory();
        history.Record(Loc(1));
        history.Record(Loc(2));
        history.GoBack();

        history.Record(Loc(3));

        Assert.False(history.CanGoForward);
        Assert.Equal(Loc(1), history.GoBack());
    }

    [Fact]
    public void Recording_the_current_location_again_is_ignored()
    {
        var history = new NavigationHistory();
        history.Record(Loc(1));
        var changes = 0;
        history.Changed += (_, _) => changes++;

        history.Record(Loc(1));

        Assert.Equal(0, changes);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Capacity_drops_the_oldest_entries()
    {
        var history = new NavigationHistory(capacity: 2);
        history.Record(Loc(1));
        history.Record(Loc(2));
        history.Record(Loc(3));

        Assert.Equal(Loc(2), history.GoBack());
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void Clear_resets_state_and_raises_changed()
    {
        var history = new NavigationHistory();
        history.Record(Loc(1));
        var changed = false;
        history.Changed += (_, _) => changed = true;

        history.Clear();

        Assert.True(changed);
        Assert.Null(history.Current);
    }
}
