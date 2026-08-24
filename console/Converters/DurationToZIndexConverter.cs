using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>
/// Gesture-clip ResolvedDurationMs -&gt; Panel.ZIndex, inverted: the shorter clip always renders
/// on top of a longer one it overlaps in time, deterministically (never depending on Steps
/// insertion/creation order, which is arbitrary and previously made a base pose invisible
/// whenever an unrelated wider clip happened to come later in the collection).
/// </summary>
public class DurationToZIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int durationMs ? -durationMs : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
