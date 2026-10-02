using DiagnosticStudio.App.Services;

namespace DiagnosticStudio.Tests.App;

public sealed class ScrollBarThumbMinimumTests
{
    // The track sizes the thumb as track * viewport / (viewport + range).
    private static double ThumbLength(double viewport, double range, double track) => track * viewport / (viewport + range);

    [Theory]
    [InlineData(18, 599_982, 234)]
    [InlineData(40, 5_000_000, 800)]
    [InlineData(10, 100_000, 100)]
    public void Long_documents_keep_the_thumb_at_the_minimum(double viewport, double range, double track)
    {
        var effective = ScrollBarThumbMinimum.EffectiveViewport(viewport, range, track);

        Assert.Equal(ScrollBarThumbMinimum.Length, ThumbLength(effective, range, track), precision: 6);
    }

    [Theory]
    [InlineData(18, 40, 234)]
    [InlineData(500, 1_000, 400)]
    public void Short_documents_are_left_alone(double viewport, double range, double track)
    {
        Assert.Equal(viewport, ScrollBarThumbMinimum.EffectiveViewport(viewport, range, track));
    }

    [Theory]
    [InlineData(18, 0, 234)]
    [InlineData(18, 1_000, 0)]
    [InlineData(18, 1_000, 32)]
    [InlineData(double.NaN, 1_000, 234)]
    public void Nothing_to_scroll_or_no_room_leaves_the_viewport_unchanged(double viewport, double range, double track)
    {
        var effective = ScrollBarThumbMinimum.EffectiveViewport(viewport, range, track);

        Assert.True(double.IsNaN(viewport) ? double.IsNaN(effective) : effective == viewport);
    }
}
