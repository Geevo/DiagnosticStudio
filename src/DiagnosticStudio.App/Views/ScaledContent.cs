using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DiagnosticStudio.App.ViewModels;

namespace DiagnosticStudio.App.Views;

/// <summary>
/// Scales what it holds by its own zoom level, apart from the interface zoom the window applies. Ctrl and the mouse
/// wheel over it change that level; anywhere else they change the interface's.
/// </summary>
public sealed class ScaledContent : Border
{
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(ZoomViewModel), typeof(ScaledContent));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(
        nameof(Scale), typeof(double), typeof(ScaledContent),
        new PropertyMetadata(1.0, (d, e) => ((ScaledContent)d).Apply((double)e.NewValue)));

    public ScaledContent()
    {
        // Every scaled area follows the one document zoom of the window it is in.
        const string path = "DataContext.ContentZoom";
        var window = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Window), 1);
        SetBinding(ZoomProperty, new Binding(path) { RelativeSource = window });
        SetBinding(ScaleProperty, new Binding(path + ".Scale") { RelativeSource = window });
    }

    /// <summary>The level the wheel changes.</summary>
    public ZoomViewModel? Zoom
    {
        get => (ZoomViewModel?)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>The factor applied: 1.0 is normal.</summary>
    public double Scale
    {
        get => (double)GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    /// <summary>Whether the element is inside a scaled area, which handles the Ctrl+wheel gesture itself.</summary>
    public static bool Contains(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ScaledContent)
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Zoom is { } zoom && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            zoom.Wheel(e.Delta);
            e.Handled = true;
            return;
        }

        base.OnPreviewMouseWheel(e);
    }

    private void Apply(double scale) =>
        LayoutTransform = scale is > 0 and not 1.0 ? new ScaleTransform(scale, scale) : Transform.Identity;
}
