using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace b1_chat_console.Converters;

public class FwStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            true => "OkBrush",
            false => "BadBrush",
            _ => "MutedBrush",
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
