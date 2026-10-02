using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace DiagnosticStudio.App.Services;

/// <summary>Which colours the application uses.</summary>
public enum ThemePreference
{
    /// <summary>Whatever Windows is set to, including a change made while the application runs.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies the user's light/dark choice. WPF's Fluent theme (<see cref="Application.ThemeMode"/>) restyles the
/// standard controls and title bar; this also swaps the application's own palette (panels, severity colours,
/// banners) to match, at start-up, when the choice changes, and, for <see cref="ThemePreference.System"/>, whenever
/// Windows changes its setting while the application is running.
/// </summary>
public sealed class ThemeService : ObservableObject, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightValue = "AppsUseLightTheme";

    private static readonly Uri LightPalette = new("pack://application:,,,/DiagnosticStudio.App;component/Views/Palette.Light.xaml");
    private static readonly Uri DarkPalette = new("pack://application:,,,/DiagnosticStudio.App;component/Views/Palette.Dark.xaml");

    private readonly Application _application;
    private readonly ISettingsStore? _settings;
    private ResourceDictionary? _current;
    private ThemePreference _preference;

    public ThemeService(Application application, ISettingsStore? settings = null)
    {
        _application = application;
        _settings = settings;
        _preference = settings?.Theme ?? ThemePreference.System;
    }

    public bool IsDark { get; private set; }

    public ThemePreference Preference => _preference;

    // One flag per choice, for the check marks in the menu: checking one chooses it, and clicking the one already
    // checked leaves it checked.
    public bool IsSystem
    {
        get => _preference == ThemePreference.System;
        set => Check(ThemePreference.System, value);
    }

    public bool IsLight
    {
        get => _preference == ThemePreference.Light;
        set => Check(ThemePreference.Light, value);
    }

    public bool IsDarkChosen
    {
        get => _preference == ThemePreference.Dark;
        set => Check(ThemePreference.Dark, value);
    }

    private void Check(ThemePreference preference, bool on)
    {
        if (on)
        {
            Choose(preference);
        }
        else
        {
            // The menu item unticked itself; show the choice that is still in force.
            _application.Dispatcher.BeginInvoke(NotifyChoice);
        }
    }

    private void NotifyChoice()
    {
        OnPropertyChanged(nameof(Preference));
        OnPropertyChanged(nameof(IsSystem));
        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDarkChosen));
    }

    /// <summary>True when Windows is set to dark app mode. Unreadable or missing settings mean light.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(LightValue) is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void Start()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Chooses light, dark or the Windows setting, applies it at once and remembers it.</summary>
    public void Choose(ThemePreference preference)
    {
        if (preference == _preference)
        {
            return;
        }

        _preference = preference;
        if (_settings is not null)
        {
            _settings.Theme = preference;
        }

        Apply();
        NotifyChoice();
    }

    /// <summary>Applies the palette, and the Fluent theme for the standard controls, for the current choice.</summary>
    private void Apply()
    {
        _application.ThemeMode = _preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

        Apply(_preference == ThemePreference.Dark || (_preference == ThemePreference.System && SystemPrefersDark()));
    }

    public void Apply(bool dark)
    {
        IsDark = dark;
        var palette = new ResourceDictionary { Source = dark ? DarkPalette : LightPalette };
        var merged = _application.Resources.MergedDictionaries;

        if (_current is not null && merged.IndexOf(_current) is var index and >= 0)
        {
            merged[index] = palette;
        }
        else
        {
            merged.Insert(0, palette);
        }

        _current = palette;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference != ThemePreference.System
            || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color))
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(() =>
        {
            var dark = SystemPrefersDark();
            if (_preference == ThemePreference.System && dark != IsDark)
            {
                Apply(dark);
            }
        });
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
