using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using b1_chat_console.Services;

namespace b1_chat_console.Converters;

/// <summary>
/// Colors the active catalog's gesture IDs by family for Sequencer clips and chips. The
/// per-family palette below is the only hand-picked part; which gesture belongs to which
/// family, and how many gestures/families exist, comes from the catalog file.
/// </summary>
public class AnimFamilyToBrushConverter : IValueConverter
{
    // steel = rest, teal = attention, violet = communication, rose = emotion, rust = reaction,
    // brass = mechanical, accent = dialogue (tied to the audio track's own accent color).
    private static readonly Color Steel = (Color)ColorConverter.ConvertFromString("#7C93B0")!;
    private static readonly Color Teal = (Color)ColorConverter.ConvertFromString("#4FBEB0")!;
    private static readonly Color Violet = (Color)ColorConverter.ConvertFromString("#9C87D6")!;
    private static readonly Color Brass = (Color)ColorConverter.ConvertFromString("#D6A94F")!;
    private static readonly Color Rust = (Color)ColorConverter.ConvertFromString("#D6673F")!;
    private static readonly Color Accent = (Color)ColorConverter.ConvertFromString("#FF9D2E")!;
    private static readonly Color Rose = (Color)ColorConverter.ConvertFromString("#D67FA0")!;

    private static readonly IReadOnlyDictionary<string, Color> ColorByFamily =
        new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["rest"] = Steel,
            ["attention"] = Teal,
            ["communication"] = Violet,
            ["dialogue"] = Accent,
            ["emotion"] = Rose,
            ["reaction"] = Rust,
            ["mechanical"] = Brass,
        };

    // Index = catalog-file order (matches generated V2 gesture ID). Computed once from the
    // active catalog instead of hand-typed, so a family unknown to ColorByFamily falls back to
    // Steel rather than failing.
    private static readonly Color[] ByAnimId = GestureSceneV2Persistence.Catalog.Ordered
        .Select(gesture => ColorByFamily.GetValueOrDefault(gesture.Family, Steel))
        .ToArray();

    // Retained as a catalog-color lookup for consumers outside the V2 view model.
    public static readonly (string Label, int[] AnimIds)[] Families = GestureSceneV2Persistence.Catalog.Ordered
        .Select((gesture, id) => (gesture.Family, Id: id))
        .GroupBy(entry => entry.Family)
        .Select(group => (group.Key.ToUpperInvariant(), group.Select(entry => entry.Id).ToArray()))
        .ToArray();

    // One frozen gradient per family color — clips re-render often (drag, zoom), no point
    // allocating a fresh brush every time.
    private static readonly Dictionary<Color, LinearGradientBrush> GradientCache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value switch { int i => i, double d => (int)d, _ => -1 };
        var color = id >= 0 && id < ByAnimId.Length ? ByAnimId[id] : Steel;
        // ConverterParameter="Color" returns the raw Color (e.g. for a DropShadowEffect.Color,
        // which isn't a Brush) instead of the default SolidColorBrush.
        if (parameter as string == "Color") return color;
        // ConverterParameter="Gradient" returns the mockup's clip fill: lightened tone at the
        // top fading to the base family color (reads as a beveled top highlight for free).
        if (parameter as string == "Gradient")
        {
            if (!GradientCache.TryGetValue(color, out var grad))
            {
                var top = Color.FromRgb(Lift(color.R), Lift(color.G), Lift(color.B));
                grad = new LinearGradientBrush(top, color, 90);
                grad.Freeze();
                GradientCache[color] = grad;
            }
            return grad;
        }
        return new SolidColorBrush(color);
    }

    // Mirrors the mockup's lighten(color, 18) helper (each channel +18*2.4, clamped).
    private static byte Lift(byte c) => (byte)Math.Min(255, c + 43);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
