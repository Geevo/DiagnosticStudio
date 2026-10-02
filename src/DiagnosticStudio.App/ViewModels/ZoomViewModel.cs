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

    public ZoomViewModel(ISettingsStore settings)
    {
        _settings = settings;
        _percent = Clamp(settings.ZoomPercent);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Scale), nameof(Text), nameof(IsScaled))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand), nameof(ZoomOutCommand), nameof(ResetCommand))]
    private int _percent;

    /// <summary>The factor the interface is scaled by: 1.0 at 100%.</summary>
    public double Scale => Percent / 100.0;

    public string Text => string.Create(CultureInfo.CurrentCulture, $"{Percent}%");

    /// <summary>Not at 100%, so the status bar shows it.</summary>
    public bool IsScaled => Percent != Normal;

    partial void OnPercentChanged(int value) => _settings.ZoomPercent = value;

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
