using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// A scroll bar thumb is as long as the view is of the whole, so over hundreds of thousands of log lines it shrinks
/// to a few pixels that cannot be grabbed. Every scroll bar in the application keeps its thumb at least this long.
/// </summary>
/// <remarks>
/// The track sizes the thumb as <c>track × viewport / (viewport + range)</c> and ignores the thumb's own minimum
/// length, so the viewport it is given is raised just far enough for that to come to <see cref="Length"/>. The track
/// maps a drag to a scroll value with the same figures, so dragging stays exact.
/// </remarks>
internal static class ScrollBarThumbMinimum
{
    /// <summary>The shortest thumb, in device-independent pixels.</summary>
    public const double Length = 32;

    private static readonly ConditionalWeakTable<Track, object> Patched = new();
    private static readonly object Marker = new();

    // A class handler on ScrollBar itself is never called (WPF has already built that type's handler list), so the
    // scroll viewers, which own every scroll bar in the application, are the ones watched. Their templates can be
    // applied again (a theme change), which gives them new tracks, so this is checked on every scroll.
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer viewer)
        {
            Limit(viewer, "PART_VerticalScrollBar");
            Limit(viewer, "PART_HorizontalScrollBar");
        }
    }

    private static void Limit(ScrollViewer viewer, string name)
    {
        if (viewer.Template?.FindName(name, viewer) is not ScrollBar bar
            || bar.Template?.FindName("PART_Track", bar) is not Track track
            || Patched.TryGetValue(track, out _))
        {
            return;
        }

        Patched.Add(track, Marker);
        var vertical = bar.Orientation == Orientation.Vertical;
        var binding = new MultiBinding { Converter = ViewportConverter.Instance, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding(nameof(ScrollBar.ViewportSize)) { Source = bar });
        binding.Bindings.Add(new Binding(nameof(RangeBase.Minimum)) { Source = bar });
        binding.Bindings.Add(new Binding(nameof(RangeBase.Maximum)) { Source = bar });
        binding.Bindings.Add(new Binding(vertical ? nameof(FrameworkElement.ActualHeight) : nameof(FrameworkElement.ActualWidth)) { Source = track });
        track.SetBinding(Track.ViewportSizeProperty, binding);
    }

    /// <summary>The smallest viewport that gives a thumb of <see cref="Length"/> on this track, or the real one if larger.</summary>
    internal static double EffectiveViewport(double viewport, double range, double trackLength)
    {
        if (double.IsNaN(viewport) || double.IsNaN(range) || double.IsNaN(trackLength) || range <= 0 || trackLength <= Length)
        {
            return viewport;
        }

        return Math.Max(viewport, Length * range / (trackLength - Length));
    }

    private sealed class ViewportConverter : IMultiValueConverter
    {
        public static readonly ViewportConverter Instance = new();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [double viewport, double minimum, double maximum, double trackLength]
                ? EffectiveViewport(viewport, maximum - minimum, trackLength)
                : Binding.DoNothing;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
