using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>
/// Whether the Sequencer timeline's playhead (local scrub or live hardware position) currently
/// falls inside a clip's [StartMs, StartMs+duration) span — feeds the existing
/// BoolToBrushConverter for the active-clip highlight instead of a dedicated brush converter.
/// Inputs: {startMs (int), resolvedDurationMs (int), playheadMs (double)}.
/// </summary>
public class TimelineActiveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return false;
        if (values[0] is not int startMs) return false;
        var durationMs = values[1] is int i ? i : 0;
        var playheadMs = values[2] switch { double d => d, int pi => pi, _ => -1.0 };
        return playheadMs >= startMs && playheadMs < startMs + durationMs;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
