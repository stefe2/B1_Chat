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
///   "Width" — {animId (int), pxPerMs (double), durationLookup (IReadOnlyDictionary&lt;int,int&gt;)}
///             -> double. Falls back to 800ms when the real gesture duration hasn't arrived yet
///             (getAnimDurations is fetched once at handshake); floors at 18px so a short/
///             zoomed-out clip stays clickable.
///   "Top"   — {target (ushort), tracks (ObservableCollection&lt;TimelineTrack&gt;)} -> double.
///             Falls back to row 0 (the broadcast row) if the target isn't a currently-known
///             track (e.g. a droid that went offline since the sequence was authored).
///   "Duration" — {durationMs (int), pxPerMs (double)} -> double. Same math as "Left" (a
///             duration is just "a span of ms" at bind time) — used for the audio bar's width.
/// </summary>
public class TimelineGeometryConverter : IMultiValueConverter
{
    private const int DefaultDurationMs = 800;
    private const double MinWidth = 18;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return (parameter as string) switch
        {
            "Left" => ConvertLeft(values),
            "Width" => ConvertWidth(values),
            "Top" => ConvertTop(values),
            "Duration" => ConvertLeft(values),
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
        if (values.Length < 3) return MinWidth;
        var animId = values[0] is int i ? i : -1;
        var pxPerMs = ToDouble(values[1]);
        var durationMs = values[2] is IReadOnlyDictionary<int, int> lookup && lookup.TryGetValue(animId, out var ms)
            ? ms : DefaultDurationMs;
        return Math.Max(MinWidth, durationMs * pxPerMs);
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
