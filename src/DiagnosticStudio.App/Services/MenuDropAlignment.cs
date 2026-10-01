using System.Reflection;
using System.Windows;

namespace DiagnosticStudio.App.Services;

/// <summary>
/// Windows reports a "menus open to the left" preference (set for left-handed tablet/pen use, and sometimes by
/// touch drivers on ordinary desktops). WPF honours it, so a menu bar's drop-downs open leftwards and end up
/// partly off the window. Menu bars are always read left to right here, so the preference is overridden.
/// </summary>
internal static class MenuDropAlignment
{
    private const string FieldName = "_menuDropAlignment";

    public static void KeepRight()
    {
        Apply();

        // WPF refreshes the cached value when the system parameters change.
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SystemParameters.MenuDropAlignment) or null)
            {
                Apply();
            }
        };
    }

    private static void Apply()
    {
        if (!SystemParameters.MenuDropAlignment)
        {
            return;
        }

        // There is no public setter; the cached field is the only switch. If a future WPF renames it the menus
        // simply keep the system behaviour.
        typeof(SystemParameters)
            .GetField(FieldName, BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, false);
    }
}
