using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DiagnosticStudio.App.Views;

/// <summary>Visible when the bound value is a non-empty reference; collapsed otherwise.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static NullToVisibilityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
