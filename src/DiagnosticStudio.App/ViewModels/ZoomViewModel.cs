using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.Services;

namespace DiagnosticStudio.App.ViewModels;

/// <summary>Scales the whole interface, for small text on high-resolution screens and for projecting.</summary>
public partial class ZoomViewModel : ObservableObject
{
    public const int Minimum = 50;
    public const int Maximum = 300;
    public const int Step = 10;
    public const int Normal = 100;

    private readonly Action<int> _save;
    private CancellationTokenSource? _lingering;
    private bool _recentlyChanged;

    public ZoomViewModel(ISettingsStore settings)
        : this(settings.ZoomPercent, percent => settings.ZoomPercent = percent)
    {
    }

    protected ZoomViewModel(int saved, Action<int> save)
    {
        _save = save;
        _percent = Clamp(saved);
    }

    /// <summary>The levels offered in a list; any level from <see cref="Minimum"/> to <see cref="Maximum"/> can be typed.</summary>
    public static IReadOnlyList<string> Presets { get; } =
        new[] { 50, 75, 100, 125, 150, 200, 300 }.Select(Format).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(Text), nameof(Label), nameof(Entry), nameof(IsScaled), nameof(IsIndicatorVisible))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(ResetCommand))]
    private int _percent;

    /// <summary>The factor the interface is scaled by: 1.0 at 100%.</summary>
    public double Scale => Percent / 100.0;

    public string Text => string.Create(CultureInfo.CurrentCulture, $"{Percent}%");

    /// <summary>The status bar text.</summary>
    public string Label => "Zoom " + Text;

    /// <summary>
    /// The level as shown in an editable list ("125 %"). Setting it accepts what a person types: "125", "125%" or
    /// "125 %"; text that holds no number is dropped and the box shows the current level again.
    /// </summary>
    public string Entry
    {
        get => Format(Percent);
        set
        {
            var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).Take(4).ToArray());
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var typed))
            {
                Percent = Clamp(typed);
            }

            // Also when nothing changed: "9999" is shown as the 300 % it became, and "abc" as the level it was.
            OnPropertyChanged(nameof(Entry));
        }
    }

    /// <summary>Not at 100%.</summary>
    public bool IsScaled => Percent != Normal;

    /// <summary>
    /// Whether the status bar shows the level: always while scaled, and for a moment after any change, so that
    /// returning to 100% is confirmed on screen before the indicator goes away again.
    /// </summary>
    public bool IsIndicatorVisible => IsScaled || _recentlyChanged;

    /// <summary>How long the indicator stays after the last change when the level is 100%.</summary>
    internal TimeSpan Linger { get; set; } = TimeSpan.FromSeconds(2);

    partial void OnPercentChanged(int value)
    {
        _save(value);
        ShowBriefly();
    }

    // Each change restarts the wait, so a run of wheel notches keeps the indicator up until the last one.
    private void ShowBriefly()
    {
        _lingering?.Cancel();
        var source = new CancellationTokenSource();
        _lingering = source;
        SetRecentlyChanged(true);
        _ = HideAfterLingerAsync(source.Token);
    }

    private async Task HideAfterLingerAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Linger, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SetRecentlyChanged(false);
    }

    private void SetRecentlyChanged(bool value)
    {
        _recentlyChanged = value;
        OnPropertyChanged(nameof(IsIndicatorVisible));
    }

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => Percent = Clamp(Percent + Step);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => Percent = Clamp(Percent - Step);

    [RelayCommand(CanExecute = nameof(IsScaled))]
    private void Reset() => Percent = Normal;

    private bool CanZoomIn() => Percent < Maximum;

    private bool CanZoomOut() => Percent > Minimum;

    /// <summary>One notch of the mouse wheel with Ctrl held: up zooms in, down zooms out.</summary>
    public void Wheel(int delta)
    {
        if (delta > 0)
        {
            ZoomIn();
        }
        else if (delta < 0)
        {
            ZoomOut();
        }
    }

    private static int Clamp(int percent) => Math.Clamp(percent, Minimum, Maximum);

    private static string Format(int percent) => string.Create(CultureInfo.InvariantCulture, $"{percent} %");
}

/// <summary>
/// Scales what is shown in the document pane (log text, tables, trees, detail), apart from the interface zoom, for
/// people who want large log text but normal-sized menus. The two multiply.
/// </summary>
public sealed class ContentZoomViewModel : ZoomViewModel
{
    public ContentZoomViewModel(ISettingsStore settings)
        : base(settings.ContentZoomPercent, percent => settings.ContentZoomPercent = percent)
    {
    }
}
