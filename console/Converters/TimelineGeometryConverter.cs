using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using b1_chat_console.Models;

namespace b1_chat_console.Converters;

/// <summary>
/// Sequencer timeline clip/playhead geometry — one converter, three ConverterParameter modes
/// (same "one converter, several modes" shape as StrengthToBrushConverter's "Color" parameter):
///   "Left"  — {timeMs (int/double), pxPerMs (double)} -> double. Used for clip X and the
///             playhead line (both are just "a time in ms" at bind time).
///   "Width" — {resolvedDurationMs (int), pxPerMs (double)} -> double. The shared duration
///             provider owns all fallback policy; this converter only turns time into pixels.
///   "Top"   — {target (ushort), tracks (ObservableCollection&lt;TimelineTrack&gt;)} -> double.
///             Falls back to row 0 (the broadcast row) if the target isn't a currently-known
///             track (e.g. a droid that went offline since the sequence was authored).
///   "ClipTop" — same inputs as "Top", plus a vertical inset (ClipInsetY) so a clip floats
///             centered inside its contiguous 52px row (mockup's .clip top:5/bottom:5) while
///             the row background itself still starts at the raw row top.
///   "AudioWidth" — {durationMs (int), pxPerMs (double)} -> double. Audio clip width, with its
///             own floor so a clip whose duration is unknown (failed probe) or genuinely zero
///             stays visible, selectable and right-clickable instead of collapsing to nothing
///             (SEQ-F04). The floor is purely visual: the clip's DurationMs stays 0, so an
///             unreadable file never silently defines the end of the sequence.
/// </summary>
public class TimelineGeometryConverter : IMultiValueConverter
{
    private const double MinWidth = 18;
    // Wider than a gesture clip's floor: an audio clip in this state also shows a warning badge
    // next to its (trimmed) filename.
    public const double MinAudioWidth = 26;
    // Vertical breathing room of a clip inside its row (row height 52 - 2×5 = 42px clip).
    public const double ClipInsetY = 5;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return (parameter as string) switch
        {
            "Left" => ConvertLeft(values),
            "Width" => ConvertWidth(values),
            "Top" => ConvertTop(values),
            "ClipTop" => ConvertTop(values) + ClipInsetY,
            "AudioWidth" => ConvertAudioWidth(values),
            _ => 0.0,
        };
    }

    private static double ConvertLeft(object[] values)
    {
        if (values.Length < 2) return 0.0;
        var timeMs = ToDouble(values[0]);
        var pxPerMs = ToDouble(values[1]);
        return timeMs * pxPerMs;
    }

    private static double ConvertWidth(object[] values)
    {
        if (values.Length < 2) return MinWidth;
        var durationMs = ToDouble(values[0]);
        var pxPerMs = ToDouble(values[1]);
        return Math.Max(MinWidth, durationMs * pxPerMs);
    }

    private static double ConvertAudioWidth(object[] values)
    {
        if (values.Length < 2) return MinAudioWidth;
        return Math.Max(MinAudioWidth, ToDouble(values[0]) * ToDouble(values[1]));
    }

    private static double ConvertTop(object[] values)
    {
        if (values.Length < 2 || values[1] is not ObservableCollection<TimelineTrack> tracks || tracks.Count == 0)
            return 0.0;
        var target = values[0] is ushort u ? u : (ushort)0xFFFF;
        var track = tracks.FirstOrDefault(t => t.Id == target) ?? tracks[0];
        return track.RowIndex * (TimelineTrack.RowHeight + TimelineTrack.RowGap);
    }

    private static double ToDouble(object v) => v switch
    {
        double d => d,
        int i => i,
        _ => 0.0,
    };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
