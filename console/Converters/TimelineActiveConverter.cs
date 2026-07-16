using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>
/// Whether the Sequencer timeline's playhead (local scrub or live hardware position) currently
/// falls inside a clip's [StartMs, StartMs+duration) span — feeds the existing
/// BoolToBrushConverter for the active-clip highlight instead of a dedicated brush converter.
/// Inputs: {startMs (int), animId (int), playheadMs (double), durationLookup
/// (IReadOnlyDictionary&lt;int,int&gt;)}.
/// </summary>
public class TimelineActiveConverter : IMultiValueConverter
{
    private const int DefaultDurationMs = 800;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4) return false;
        if (values[0] is not int startMs) return false;
        var animId = values[1] is int i ? i : -1;
        var playheadMs = values[2] switch { double d => d, int pi => pi, _ => -1.0 };
        var durationMs = values[3] is IReadOnlyDictionary<int, int> lookup && lookup.TryGetValue(animId, out var ms)
            ? ms : DefaultDurationMs;
        return playheadMs >= startMs && playheadMs < startMs + durationMs;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
