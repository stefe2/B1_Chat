using System.Globalization;
using System.Windows.Data;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Converters;

/// <summary>animId (int) -> short gesture name, for Sequencer timeline clip labels. Reuses
/// AnimationViewModel.AnimNames, the app's one canonical gesture-name source.</summary>
public class AnimIdToNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value is int i ? i : -1;
        return id >= 0 && id < AnimationViewModel.AnimNames.Length ? AnimationViewModel.AnimNames[id] : "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
