using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CoursePlayer.Converters;

/// <summary>
/// Collapses the element when the bound value is null or an empty string.
/// Pass <c>Invert</c> as the parameter to flip the result.
/// </summary>
public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            _ => true,
        };

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
