using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

public class FwStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        true => "Up to date",
        false => "Update available",
        _ => "Latest version unknown",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
