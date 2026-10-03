using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiagnosticStudio.App.ViewModels.TextViewer;

namespace DiagnosticStudio.App.Views;

/// <summary>Marking text with the mouse across the (virtualised) lines of the viewer.</summary>
public partial class TextViewerView
{
    private const int DragScrollIntervalMilliseconds = 40;

    private bool _dragging;
    private DispatcherTimer? _dragScroll;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is not { FreeSelection: true } vm
            || e.ChangedButton != MouseButton.Left
            || (e.OriginalSource is DependencyObject source && IsOnScrollBar(source)))
        {
            return;
        }

        if (HitPosition(e.GetPosition(LineList), out var gutter) is not { } hit)
        {
            return;
        }

        LineList.Focus();
        LineList.UnselectAll();
        e.Handled = true;
        vm.SetCurrentLineFromSelection(hit.Line);

        if (gutter || e.ClickCount >= 3)
        {
            vm.SelectLineAt(hit.Line);
        }
        else if (e.ClickCount == 2)
        {
            vm.SelectWordAt(hit.Line, hit.Column);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && vm.HasTextSelection)
        {
            vm.Select(vm.Selection.Anchor, hit);
            _dragging = true;
            LineList.CaptureMouse();
        }
        else
        {
            vm.Select(hit, hit);
            _dragging = true;
            LineList.CaptureMouse();
        }
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _viewModel is not { FreeSelection: true })
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        ExtendTo(e.GetPosition(LineList));
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            EndDrag();
            e.Handled = true;
        }
    }

    private void OnListLostCapture(object sender, MouseEventArgs e) => StopDragScroll();

    private void EndDrag()
    {
        _dragging = false;
        StopDragScroll();
        if (ReferenceEquals(Mouse.Captured, LineList))
        {
            LineList.ReleaseMouseCapture();
        }
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { FreeSelection: true } vm)
        {
            vm.SelectAll();
        }
        else
        {
            LineList.SelectAll();
        }
    }

    // ---- extending the selection ----

    private void ExtendTo(Point point)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        var inside = HitPosition(point, out _);
        var scroll = FindScrollViewer(LineList);
        if (inside is { } here)
        {
            vm.Select(vm.Selection.Anchor, here);
            StopDragScroll();
            return;
        }

        // Past the top or bottom edge: the selection follows to the first or last visible line, and the list scrolls.
        if (scroll is null)
        {
            return;
        }

        if (point.Y < 0)
        {
            vm.Select(vm.Selection.Anchor, new TextPosition(LineAt((int)scroll.VerticalOffset), 0));
            StartDragScroll(-1);
        }
        else if (point.Y > LineList.ActualHeight)
        {
            var last = LineAt((int)(scroll.VerticalOffset + scroll.ViewportHeight) - 1);
            vm.Select(vm.Selection.Anchor, new TextPosition(last, vm.LineLength(last)));
            StartDragScroll(1);
        }
    }

    private void StartDragScroll(int direction)
    {
        _dragScroll ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(DragScrollIntervalMilliseconds) };
        _dragScroll.Tick -= OnDragScrollTick;
        _dragScroll.Tick += OnDragScrollTick;
        _dragScroll.Tag = direction;
        _dragScroll.Start();
    }

    private void StopDragScroll() => _dragScroll?.Stop();

    private void OnDragScrollTick(object? sender, EventArgs e)
    {
        if (_viewModel is not { } vm || _dragScroll?.Tag is not int direction || FindScrollViewer(LineList) is not { } scroll)
        {
            return;
        }

        // The further past the edge the pointer is, the more lines per step.
        var point = DiagnosticStudio.App.Services.CursorPosition.RelativeTo(LineList);
        var distance = direction < 0 ? -point.Y : point.Y - LineList.ActualHeight;
        var lines = 1 + (int)Math.Max(0, distance / 25);
        scroll.ScrollToVerticalOffset(Math.Clamp(scroll.VerticalOffset + (direction * lines), 0, scroll.ScrollableHeight));

        if (direction < 0)
        {
            vm.Select(vm.Selection.Anchor, new TextPosition(LineAt((int)scroll.VerticalOffset), 0));
        }
        else
        {
            var last = LineAt((int)(scroll.VerticalOffset + scroll.ViewportHeight) - 1);
            vm.Select(vm.Selection.Anchor, new TextPosition(last, vm.LineLength(last)));
        }
    }

    // ---- finding what is under the pointer ----

    /// <summary>The line and character under a point of the list, or <c>null</c> when it is not over a line.</summary>
    private TextPosition? HitPosition(Point point, out bool inGutter)
    {
        inGutter = false;
        if (VisualTreeHelper.HitTest(LineList, point)?.VisualHit is not { } visual
            || ItemsControl.ContainerFromElement(LineList, visual) is not ListBoxItem container
            || container.DataContext is not LineViewModel line)
        {
            return null;
        }

        var inContainer = LineList.TranslatePoint(point, container);
        inGutter = _viewModel is not null && inContainer.X < _viewModel.GutterWidth;
        if (FindDescendant<HighlightedTextBlock>(container) is not { } block)
        {
            return new TextPosition(line.LineNumber, 0);
        }

        return new TextPosition(line.LineNumber, block.ColumnAt(LineList.TranslatePoint(point, block)));
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool IsOnScrollBar(DependencyObject source)
    {
        for (var current = source; current is not null; current = DiagnosticStudio.App.Services.AutoScrollMath.Parent(current))
        {
            if (current is ScrollBar or Thumb)
            {
                return true;
            }
        }

        return false;
    }

    // ---- keys ----

    /// <summary>Keys for the list in text-selection mode: no row is selected, so the arrows scroll.</summary>
    private bool HandleFreeSelectionKey(TextViewerViewModel vm, KeyEventArgs e, bool ctrl)
    {
        var scroll = FindScrollViewer(LineList);
        switch (e.Key)
        {
            case Key.A when ctrl:
                vm.SelectAll();
                break;
            case Key.Escape when vm.HasTextSelection:
                vm.ClearTextSelection();
                break;
            case Key.Down when scroll is not null:
                scroll.LineDown();
                break;
            case Key.Up when scroll is not null:
                scroll.LineUp();
                break;
            case Key.PageDown when scroll is not null:
                scroll.PageDown();
                break;
            case Key.PageUp when scroll is not null:
                scroll.PageUp();
                break;
            case Key.Home when ctrl && scroll is not null:
                scroll.ScrollToTop();
                break;
            case Key.End when ctrl && scroll is not null:
                scroll.ScrollToBottom();
                break;
            default:
                return false;
        }

        e.Handled = true;
        return true;
    }
}
