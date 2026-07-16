using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace b1_chat_console.Converters;

/// <summary>
/// AudioClip.Peaks (float[0..1], fixed resolution) -> a filled Geometry in the domain
/// x:[0, N-1], y:[0, 2] (peak 0 = flat line at y=1, peak 1 = full y:[0, 2] band). Hosted in a
/// Viewbox Stretch="Fill" in the view, so the fixed-domain geometry scales to whatever pixel
/// size the clip currently has (zoom, drag) without recomputing peaks.
/// </summary>
public class WaveformToGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not float[] { Length: > 0 } peaks) return Geometry.Empty;

        var figure = new PathFigure { StartPoint = new System.Windows.Point(0, 1), IsClosed = true, IsFilled = true };
        for (var i = 0; i < peaks.Length; i++)
            figure.Segments.Add(new LineSegment(new System.Windows.Point(i, 1 - Math.Clamp(peaks[i], 0f, 1f)), true));
        for (var i = peaks.Length - 1; i >= 0; i--)
            figure.Segments.Add(new LineSegment(new System.Windows.Point(i, 1 + Math.Clamp(peaks[i], 0f, 1f)), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
