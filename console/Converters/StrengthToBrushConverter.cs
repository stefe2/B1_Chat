using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace b1_chat_console.Converters;

/// <summary>Interpolates Bad->Warn->Ok based on a 0..1 signal strength (mesh links).</summary>
public class StrengthToBrushConverter : IValueConverter
{
    private static readonly Color Bad = (Color)ColorConverter.ConvertFromString("#FF5B52")!;
    private static readonly Color Warn = (Color)ColorConverter.ConvertFromString("#FFCC4D")!;
    private static readonly Color Ok = (Color)ColorConverter.ConvertFromString("#3DDC84")!;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var t = value is double d ? Math.Clamp(d, 0, 1) : 0;
        var color = t < 0.5 ? Lerp(Bad, Warn, t / 0.5) : Lerp(Warn, Ok, (t - 0.5) / 0.5);
        // ConverterParameter="Color" returns the raw Color (e.g. for a DropShadowEffect.Color,
        // which isn't a Brush) instead of the default SolidColorBrush.
        if (parameter as string == "Color") return color;
        return new SolidColorBrush(color);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
