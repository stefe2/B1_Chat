using System.Windows;
using System.Windows.Controls;
using b1_chat_console.Models;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Views;

public partial class DroidsCardView : UserControl
{
    public DroidsCardView() => InitializeComponent();

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is DroidsViewModel vm && sender is TextBox { DataContext: Droid droid })
            vm.CommitNameCommand.Execute(droid);
    }
}
