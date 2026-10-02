using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiagnosticStudio.App.Services;

namespace DiagnosticStudio.App.ViewModels;

/// <summary>Scales the whole interface, for small text on high-resolution screens and for projecting.</summary>
public sealed partial class ZoomViewModel : ObservableObject
{
    public const int Minimum = 50;
    public const int Maximum = 300;
    public const int Step = 10;
    public const int Normal = 100;

    private readonly ISettingsStore _settings;
    private CancellationTokenSource? _lingering;
    private bool _recentlyChanged;

    public ZoomViewModel(ISettingsStore settings)
    {
        _settings = settings;
        _percent = Clamp(settings.ZoomPercent);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(Text), nameof(Label), nameof(IsScaled), nameof(IsIndicatorVisible))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(ResetCommand))]
    private int _percent;

    /// <summary>The factor the interface is scaled by: 1.0 at 100%.</summary>
    public double Scale => Percent / 100.0;

    public string Text => string.Create(CultureInfo.CurrentCulture, $"{Percent}%");

    /// <summary>The status bar text.</summary>
    public string Label => "Zoom " + Text;

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
        _settings.ZoomPercent = value;
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
}
