using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>
/// {animId (int), durationLookup (IReadOnlyDictionary&lt;int,int&gt;)} -> "2.00s" — the small
/// duration line under a timeline clip's name (mockup's .clip .len). Same 800ms fallback as
/// TimelineGeometryConverter's "Width" mode, so the label always matches the drawn width.
/// </summary>
public class AnimDurationTextConverter : IMultiValueConverter
{
    private const int DefaultDurationMs = 800;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var animId = values.Length > 0 && values[0] is int i ? i : -1;
        var ms = values.Length > 1 && values[1] is IReadOnlyDictionary<int, int> lookup && lookup.TryGetValue(animId, out var v)
            ? v : DefaultDurationMs;
        return (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
