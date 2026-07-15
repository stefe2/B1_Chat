using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace b1_chat_console.Converters;

/// <summary>Colors mesh-topology packet dots by frame kind — see MeshTopologyViewModel.</summary>
public class PacketKindToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Color> Colors = new()
    {
        ["anim"] = (Color)ColorConverter.ConvertFromString("#FF9D2E")!,
        ["servo"] = (Color)ColorConverter.ConvertFromString("#3DDC84")!,
        ["autoAnim"] = (Color)ColorConverter.ConvertFromString("#4DD0E1")!,
        ["config"] = (Color)ColorConverter.ConvertFromString("#FFCC4D")!,
        ["calib"] = (Color)ColorConverter.ConvertFromString("#B39DDB")!,
        ["preview"] = (Color)ColorConverter.ConvertFromString("#7E9CFF")!,
        ["locate"] = (Color)ColorConverter.ConvertFromString("#C6FF4D")!,
        ["ota"] = (Color)ColorConverter.ConvertFromString("#FF5B52")!,
        ["heartbeat"] = (Color)ColorConverter.ConvertFromString("#A9ADB3")!,
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value as string ?? "";
        var color = Colors.TryGetValue(kind, out var c) ? c : Colors["anim"];
        // ConverterParameter="Color" returns the raw Color (e.g. for a DropShadowEffect.Color,
        // which isn't a Brush) instead of the default SolidColorBrush.
        if (parameter as string == "Color") return color;
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
