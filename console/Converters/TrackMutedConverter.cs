using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Data;
using b1_chat_console.Models;

namespace b1_chat_console.Converters;

/// <summary>
/// {target (ushort), tracks (ObservableCollection&lt;TimelineTrack&gt;)} -> bool: whether the
/// clip's target track is currently muted. Same by-Id lookup shape as
/// TimelineGeometryConverter's "Top" mode — falls back to "not muted" if the target isn't a
/// currently-known track.
/// </summary>
public class TrackMutedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not ObservableCollection<TimelineTrack> tracks) return false;
        var target = values[0] is ushort u ? u : (ushort)0xFFFF;
        var track = tracks.FirstOrDefault(t => t.Id == target);
        return track?.Muted ?? false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
