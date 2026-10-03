using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace DiagnosticStudio.App.Views;

/// <summary>
/// A date and time typed as UTC text (<c>2026-07-23 14:12:00</c>) with a calendar to pick the day. The text keeps
/// the time; the popup has a time of day to go with the picked day. It is applied when the box loses focus or on Enter.
/// </summary>
public partial class DateTimeBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DateTimeBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>The time of day given to a picked date when the box has no time yet.</summary>
    public static readonly DependencyProperty DefaultTimeProperty = DependencyProperty.Register(
        nameof(DefaultTime), typeof(TimeSpan), typeof(DateTimeBox), new PropertyMetadata(TimeSpan.Zero));

    /// <summary>A date and time to open the calendar at when this box is empty, such as the earliest entry.</summary>
    public static readonly DependencyProperty HintTextProperty = DependencyProperty.Register(
        nameof(HintText), typeof(string), typeof(DateTimeBox), new PropertyMetadata(string.Empty));

    private const string Format = "yyyy-MM-dd HH:mm:ss";

    // The click that closes the popup (by landing outside it) must not open it again.
    private long _closedAt;
    private bool _opening;

    public DateTimeBox()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TimeSpan DefaultTime
    {
        get => (TimeSpan)GetValue(DefaultTimeProperty);
        set => SetValue(DefaultTimeProperty, value);
    }

    public string HintText
    {
        get => (string)GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }

    private static bool TryParse(string? text, out DateTime value) =>
        DateTime.TryParse(
            text?.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
        else if (e.Key == Key.F4 || (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            Pop.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnCalendarClick(object sender, RoutedEventArgs e)
    {
        if (Environment.TickCount64 - _closedAt > 250)
        {
            Pop.IsOpen = true;
        }
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        _opening = true;
        try
        {
            // What the box says wins; failing that, its other end; failing that, today.
            var hasOwn = TryParse(Box.Text, out var own);
            var shown = hasOwn ? own : TryParse(HintText, out var hint) ? hint : DateTime.UtcNow;
            Picker.SelectedDate = hasOwn ? own.Date : null;
            Picker.DisplayDate = shown.Date;
            TimeBox.Text = (hasOwn ? own.TimeOfDay : DefaultTime).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }
        finally
        {
            _opening = false;
        }
    }

    private void OnPopupClosed(object? sender, EventArgs e) => _closedAt = Environment.TickCount64;

    private void OnDatePicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_opening || Picker.SelectedDate is not { } day)
        {
            return;
        }

        Text = (day.Date + PickedTime()).ToString(Format, CultureInfo.InvariantCulture);
        Pop.IsOpen = false;
        Box.Focus();
    }

    // What the time box says; a time that cannot be read gives the box's default time of day.
    private TimeSpan PickedTime() =>
        DateTime.TryParseExact(
            TimeBox.Text?.Trim(),
            new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" },
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var time)
            ? time.TimeOfDay
            : DefaultTime;

    // Enter applies the time to the day the box already holds; a day is picked from the calendar otherwise.
    private void OnTimeBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (TryParse(Box.Text, out var current))
        {
            Text = (current.Date + PickedTime()).ToString(Format, CultureInfo.InvariantCulture);
            Pop.IsOpen = false;
            Box.Focus();
        }
    }

    // A calendar in a popup keeps the mouse captured after a click, which would swallow the next one.
    private void OnCalendarMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured is CalendarItem)
        {
            Mouse.Capture(null);
        }
    }
}
