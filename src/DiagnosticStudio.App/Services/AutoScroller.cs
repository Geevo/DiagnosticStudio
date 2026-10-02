using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DiagnosticStudio.App.Services;

/// <summary>The numbers behind auto-scroll, kept apart from the window so they can be tested.</summary>
public static class AutoScrollMath
{
    /// <summary>Distance from the starting point inside which nothing scrolls.</summary>
    public const double DeadZone = 12;

    /// <summary>The fastest scroll, in pixels per tick (at about 60 ticks a second).</summary>
    public const double MaxPixelsPerTick = 70;

    /// <summary>Scroll speed for a pointer <paramref name="distance"/> device-independent pixels from the origin (signed).</summary>
    /// <param name="itemsPerPixel">Scale used when the list scrolls by whole items rather than by pixels.</param>
    public static double Velocity(double distance, bool byItem)
    {
        var magnitude = Math.Abs(distance);
        if (magnitude <= DeadZone)
        {
            return 0;
        }

        // Past the dead zone speed grows with distance, a little faster than linearly so a short push is gentle and a
        // long one is quick.
        var beyond = magnitude - DeadZone;
        var pixels = Math.Min(MaxPixelsPerTick, (beyond * 0.12) + (beyond * beyond * 0.0015));
        var speed = byItem ? pixels / 20.0 : pixels;
        return Math.Sign(distance) * speed;
    }

    /// <summary>The nearest scroll viewer around <paramref name="source"/> that has something to scroll, or <c>null</c>.</summary>
    public static ScrollViewer? FindScrollable(DependencyObject? source)
    {
        for (var current = source; current is not null; current = Parent(current))
        {
            if (current is ScrollViewer viewer && (viewer.ScrollableHeight > 0 || viewer.ScrollableWidth > 0))
            {
                return viewer;
            }
        }

        return null;
    }

    /// <summary>
    /// The element around <paramref name="element"/>. Text inside a TextBlock (a <c>Run</c>) is not a visual, so its
    /// parent comes from the logical tree; asking the visual tree about it throws.
    /// </summary>
    public static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}

/// <summary>
/// Middle-button auto-scroll as in browsers and Windows Terminal: press the middle button on a scrollable area, then
/// move the pointer away from where you pressed; the further it is, the faster the area scrolls. A click, a key press
/// or switching away stops it. Holding the button and releasing it after dragging also stops it.
/// </summary>
public sealed class AutoScroller
{
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private ScrollViewer? _target;
    private Point _origin;
    private double _y;
    private double _x;
    private bool _byItem;
    private bool _moved;
    private OriginAdorner? _adorner;
    private AdornerLayer? _layer;

    public AutoScroller(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Step();

        window.PreviewMouseDown += OnMouseDown;
        window.PreviewMouseUp += OnMouseUp;
        window.PreviewKeyDown += (_, _) => Stop();
        window.Deactivated += (_, _) => Stop();
    }

    public bool IsActive => _target is not null;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsActive)
        {
            // Any click ends it, and the click that ended it does nothing else.
            Stop();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (AutoScrollMath.FindScrollable(source) is { } target)
        {
            Start(target, e.GetPosition(_window));
            e.Handled = true;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Pressed, dragged and released: that is a gesture, not a toggle.
        if (IsActive && e.ChangedButton == MouseButton.Middle && _moved)
        {
            Stop();
            e.Handled = true;
        }
    }

    private void Start(ScrollViewer target, Point origin)
    {
        _target = target;
        _origin = origin;
        _moved = false;
        _y = target.VerticalOffset;
        _x = target.HorizontalOffset;
        _byItem = target.CanContentScroll;

        var vertical = target.ScrollableHeight > 0;
        var horizontal = target.ScrollableWidth > 0;
        Mouse.OverrideCursor = vertical && horizontal ? Cursors.ScrollAll : vertical ? Cursors.ScrollNS : Cursors.ScrollWE;

        _layer = AdornerLayer.GetAdornerLayer(_window.Content as UIElement ?? target);
        if (_layer is not null)
        {
            _adorner = new OriginAdorner((UIElement)_window.Content, origin, vertical, horizontal, _window);
            _layer.Add(_adorner);
        }

        _timer.Start();
    }

    private void Stop()
    {
        if (_target is null)
        {
            return;
        }

        _timer.Stop();
        _target = null;
        Mouse.OverrideCursor = null;
        if (_adorner is not null)
        {
            _layer?.Remove(_adorner);
            _adorner = null;
        }
    }

    private void Step()
    {
        if (_target is not { } target)
        {
            return;
        }

        var now = CursorPosition.RelativeTo(_window);
        var dy = now.Y - _origin.Y;
        var dx = now.X - _origin.X;
        if (Math.Abs(dy) > AutoScrollMath.DeadZone || Math.Abs(dx) > AutoScrollMath.DeadZone)
        {
            _moved = true;
        }

        if (target.ScrollableHeight > 0)
        {
            // The offset is kept here, not read back, so that slow movement still adds up when the list scrolls by whole items.
            _y = Math.Clamp(_y + AutoScrollMath.Velocity(dy, _byItem), 0, target.ScrollableHeight);
            target.ScrollToVerticalOffset(_y);
        }

        if (target.ScrollableWidth > 0)
        {
            _x = Math.Clamp(_x + AutoScrollMath.Velocity(dx, byItem: false), 0, target.ScrollableWidth);
            target.ScrollToHorizontalOffset(_x);
        }
    }

    /// <summary>A small marker where the button was pressed, showing which directions scroll.</summary>
    private sealed class OriginAdorner : Adorner
    {
        private readonly Point _center;
        private readonly bool _vertical;
        private readonly bool _horizontal;
        private readonly Window _window;

        public OriginAdorner(UIElement adorned, Point center, bool vertical, bool horizontal, Window window)
            : base(adorned)
        {
            _center = center;
            _vertical = vertical;
            _horizontal = horizontal;
            _window = window;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var text = _window.TryFindResource("AccentBrush") as Brush ?? Brushes.Gray;
            var back = _window.TryFindResource("PanelBrush") as Brush ?? Brushes.Black;
            var pen = new Pen(text, 1.5);
            const double radius = 14;

            // The adorner sits on the window's content, whose origin is the window's client origin.
            drawingContext.DrawEllipse(back, pen, _center, radius, radius);
            drawingContext.DrawEllipse(text, null, _center, 2.2, 2.2);

            void Arrow(double dx, double dy)
            {
                var tip = new Point(_center.X + (dx * (radius - 4)), _center.Y + (dy * (radius - 4)));
                var a = new Point(tip.X - (dx * 4) + (dy * 3.5), tip.Y - (dy * 4) + (dx * 3.5));
                var b = new Point(tip.X - (dx * 4) - (dy * 3.5), tip.Y - (dy * 4) - (dx * 3.5));
                var figure = new PathFigure(tip, new[] { new LineSegment(a, true), new LineSegment(b, true) }, true);
                drawingContext.DrawGeometry(text, null, new PathGeometry(new[] { figure }));
            }

            if (_vertical)
            {
                Arrow(0, -1);
                Arrow(0, 1);
            }

            if (_horizontal)
            {
                Arrow(-1, 0);
                Arrow(1, 0);
            }
        }
    }
}
