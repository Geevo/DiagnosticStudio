using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// Follows the Windows light/dark app setting. WPF's Fluent theme (<c>ThemeMode="System"</c>) restyles the
/// standard controls and title bar; this swaps the application's own palette (panels, severity colours, banners)
/// to match, at start-up and whenever the user changes the setting while the app is running.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightValue = "AppsUseLightTheme";

    private static readonly Uri LightPalette = new("pack://application:,,,/DiagnosticStudio.App;component/Views/Palette.Light.xaml");
    private static readonly Uri DarkPalette = new("pack://application:,,,/DiagnosticStudio.App;component/Views/Palette.Dark.xaml");

    private readonly Application _application;
    private ResourceDictionary? _current;

    public ThemeService(Application application)
    {
        _application = application;
    }

    public bool IsDark { get; private set; }

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
        Apply(SystemPrefersDark());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
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
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color))
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(() =>
        {
            var dark = SystemPrefersDark();
            if (dark != IsDark)
            {
                Apply(dark);
            }
        });
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
