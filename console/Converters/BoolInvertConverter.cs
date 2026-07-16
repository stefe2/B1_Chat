using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>Plain boolean negation — e.g. showing a "Muted" flag as an "Audible" ON/green toggle.</summary>
public class BoolInvertConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}
