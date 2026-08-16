using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace b1_chat_console.Converters;

/// <summary>
/// Colors the generated V2 gesture IDs by family for Sequencer clips and chips.
/// </summary>
public class AnimFamilyToBrushConverter : IValueConverter
{
    // steel = idle/rest, teal = look/curiosity, violet = affirmation, brass = scan/track,
    // rust = alert/glitch, accent = TALK (tied to the audio track's own accent color).
    private static readonly Color Steel = (Color)ColorConverter.ConvertFromString("#7C93B0")!;
    private static readonly Color Teal = (Color)ColorConverter.ConvertFromString("#4FBEB0")!;
    private static readonly Color Violet = (Color)ColorConverter.ConvertFromString("#9C87D6")!;
    private static readonly Color Brass = (Color)ColorConverter.ConvertFromString("#D6A94F")!;
    private static readonly Color Rust = (Color)ColorConverter.ConvertFromString("#D6673F")!;
    private static readonly Color Accent = (Color)ColorConverter.ConvertFromString("#FF9D2E")!;

    // Index = generated V2 gesture ID.
    private static readonly Color[] ByAnimId =
    {
        Steel,  // 0 idle.center
        Violet, // 1 communicate.nod
        Accent, // 2 dialogue.talk
    };

    // Retained as a catalog-color lookup for consumers outside the V2 view model.
    public static readonly (string Label, int[] AnimIds)[] Families =
    {
        ("REST", new[] { 0 }),
        ("COMMUNICATION", new[] { 1 }),
        ("DIALOGUE", new[] { 2 }),
    };

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
