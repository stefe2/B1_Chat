using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>ConverterParameter = "cleStyleSiVrai|cleStyleSiFaux" (recherchees dans Application.Resources).</summary>
public class BoolToStyleConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        var parts = (parameter as string ?? "").Split('|');
        var key = isTrue ? parts[0] : (parts.Length > 1 ? parts[1] : parts[0]);
        return Application.Current.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
