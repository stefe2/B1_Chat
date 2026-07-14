using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

public class FwStatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        true => "À jour",
        false => "Mise à jour disponible",
        _ => "Version la plus récente inconnue",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
