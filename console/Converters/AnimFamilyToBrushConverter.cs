using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace b1_chat_console.Converters;

/// <summary>
/// Colors a gesture (by animId, 0-17, matching AnimationViewModel.AnimNames) by family, for
/// Sequencer timeline clips and gesture-library chips — same grouping/palette as the approved
/// HTML mockup, so a gesture reads as the same color everywhere in the app.
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

    // Index = animId (0-17), matching AnimationViewModel.AnimNames order.
    private static readonly Color[] ByAnimId =
    {
        Steel,  // 0  IDLE
        Teal,   // 1  LOOK_AROUND
        Violet, // 2  NOD_YES
        Violet, // 3  SHAKE_NO
        Teal,   // 4  CURIOUS_TILT
        Brass,  // 5  SCAN_SLOW
        Rust,   // 6  ALERT_SNAP
        Brass,  // 7  TRACK
        Rust,   // 8  GLITCH_STUTTER
        Teal,   // 9  CONFUSED_TILT
        Teal,   // 10 DOUBLE_TAKE
        Steel,  // 11 SLEEPY_DROOP
        Brass,  // 12 TARGET_LOCK
        Brass,  // 13 WHIRR_SEARCH
        Rust,   // 14 SIGNAL_GLITCH
        Violet, // 15 GREETING_NOD
        Steel,  // 16 POWER_DOWN
        Accent, // 17 TALK
    };

    // Family grouping/labels, single source of truth reused by SequencerViewModel.GestureFamilies
    // (mockup-matched "GESTURE LIBRARY" rows) so the two never drift apart.
    public static readonly (string Label, int[] AnimIds)[] Families =
    {
        ("IDLE & REST", new[] { 0, 11, 16 }),
        ("LOOK & CURIOSITY", new[] { 1, 4, 9, 10 }),
        ("AFFIRMATION", new[] { 2, 3, 15 }),
        ("SCAN & TRACK", new[] { 5, 7, 12, 13 }),
        ("ALERT & GLITCH", new[] { 6, 8, 14 }),
        ("TALK (AUDIO-SYNCED, LOOPS)", new[] { 17 }),
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
