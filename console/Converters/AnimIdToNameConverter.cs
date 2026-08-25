using System.Globalization;
using System.Windows.Data;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.Converters;

/// <summary>Generated V2 gesture ID -> the active catalog's display name.</summary>
public class AnimIdToNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value is int i ? i : -1;
        var ordered = GestureSceneV2Persistence.Catalog.Ordered;
        return id >= 0 && id < ordered.Count ? ordered[id].DisplayName : "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
