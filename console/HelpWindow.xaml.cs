using System.Windows;
using System.Windows.Input;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = new HelpViewModel();
    }

    private void Hyperlink_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not string url) return;
        var vm = (HelpViewModel)DataContext;
        if (vm.TryNavigateInternalLink(url)) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
