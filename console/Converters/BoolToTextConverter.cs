using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>ConverterParameter = "texteSiVrai|texteSiFaux".</summary>
public class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        var parts = (parameter as string ?? "").Split('|');
        return isTrue ? parts[0] : (parts.Length > 1 ? parts[1] : "");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
