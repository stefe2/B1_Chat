using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>ConverterParameter = "Invert" to flip it (true -> Collapsed).</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
