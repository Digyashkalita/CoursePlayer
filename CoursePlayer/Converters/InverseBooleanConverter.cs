using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CoursePlayer.Converters;

/// <summary>Inverts a boolean. Used for "show when NOT busy" style bindings.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag ? !flag : DependencyProperty.UnsetValue;
}
