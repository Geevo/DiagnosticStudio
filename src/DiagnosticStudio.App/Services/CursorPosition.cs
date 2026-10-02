using System.Runtime.InteropServices;
using System.Windows;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// Where the pointer is right now. <c>Mouse.GetPosition</c> reports where it was at the last mouse message, which is
/// stale for a timer that polls (auto-scroll, scrolling while dragging a selection) when the pointer has moved but no
/// message has been handled yet.
/// </summary>
internal static class CursorPosition
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>The pointer in the coordinates of <paramref name="element"/>.</summary>
    public static Point RelativeTo(UIElement element)
    {
        if (!GetCursorPos(out var screen) || PresentationSource.FromVisual(element) is null)
        {
            return System.Windows.Input.Mouse.GetPosition(element);
        }

        return element.PointFromScreen(new Point(screen.X, screen.Y));
    }
}
