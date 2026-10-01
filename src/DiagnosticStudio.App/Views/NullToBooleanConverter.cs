using System.Globalization;
using System.Windows.Data;

namespace DiagnosticStudio.App.Views;

/// <summary>Converts a reference to <c>true</c> when it is non-null.</summary>
public sealed class NullToBooleanConverter : IValueConverter
{
    public static NullToBooleanConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
