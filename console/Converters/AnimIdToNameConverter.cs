using System.Globalization;
using System.Windows.Data;
using b1_chat_console.Models;

namespace b1_chat_console.Converters;

/// <summary>Generated V2 gesture ID -> the small catalog's display name.</summary>
public class AnimIdToNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value is int i ? i : -1;
        return id switch
        {
            0 => "Center",
            1 => "Nod",
            2 => "Talk",
            3 => "Look right",
            4 => "Look left",
            5 => "Look up",
            6 => "Look down",
            _ => "?",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
