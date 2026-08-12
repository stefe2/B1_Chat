using System.Windows.Controls;
using System.Windows.Input;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Views;

public partial class SequencerCardView : UserControl
{
    public SequencerCardView() => InitializeComponent();

    private void SceneMoreButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (SceneMoreButton.ContextMenu == null) return;
        SceneMoreButton.ContextMenu.PlacementTarget = SceneMoreButton;
        SceneMoreButton.ContextMenu.IsOpen = true;
    }

    private void RenameSceneMenuItem_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        SceneNameBox.Focus();
        SceneNameBox.SelectAll();
    }

    private void SequencerCardView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SequencerViewModel vm) return;

        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (control && e.Key == Key.N)
            e.Handled = Execute(vm.NewSceneCommand);
        else if (control && e.Key == Key.O)
            e.Handled = Execute(vm.OpenSceneLibraryCommand);
        else if (control && e.Key == Key.S && shift)
            e.Handled = Execute(vm.SaveSceneAsCommand);
        else if (control && e.Key == Key.S)
            e.Handled = Execute(vm.SaveSceneCommand);
        else if (e.Key == Key.F2 && vm.CanEditSequence)
        {
            SceneNameBox.Focus();
            SceneNameBox.SelectAll();
            e.Handled = true;
        }
    }

    private static bool Execute(ICommand command)
    {
        if (!command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }
}
