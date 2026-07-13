using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace b1_chat_console.Converters;

public class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        var parts = (parameter as string)?.Split('|');
        var key = parts is { Length: > 0 }
            ? (isTrue ? parts[0] : (parts.Length > 1 ? parts[1] : parts[0]))
            : (isTrue ? "AccentBrush" : "PanelHiBrush");
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
