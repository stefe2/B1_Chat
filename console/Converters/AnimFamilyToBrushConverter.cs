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

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value switch { int i => i, double d => (int)d, _ => -1 };
        var color = id >= 0 && id < ByAnimId.Length ? ByAnimId[id] : Steel;
        // ConverterParameter="Color" returns the raw Color (e.g. for a DropShadowEffect.Color,
        // which isn't a Brush) instead of the default SolidColorBrush.
        if (parameter as string == "Color") return color;
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
