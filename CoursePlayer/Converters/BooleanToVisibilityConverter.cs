using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CoursePlayer.Converters;

/// <summary>
/// Collapses the element when the bound boolean is false, and — unlike the built-in
/// converter — shows it when false if <c>Invert</c> is passed as the parameter.
/// </summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
