using System.Globalization;
using System.Windows.Data;

namespace b1_chat_console.Converters;

/// <summary>
/// {values[0] == values[1]} -> bool. Used by the Sequencer timeline to highlight the selected
/// clip (compares a clip's SequenceStep against SequencerViewModel.SelectedStep) without a
/// dedicated "IsSelected" flag on the model itself.
/// </summary>
public class ReferenceEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
