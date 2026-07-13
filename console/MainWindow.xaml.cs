using System.Windows;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class MainWindow : Window
{
    private FirmwareWindow? _firmwareWindow;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        Title = "B1 Chat — Console de supervision";
    }

    private void OpenFirmwareWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareWindow is { IsVisible: true })
        {
            _firmwareWindow.Activate();
            return;
        }

        var vm = (MainViewModel)DataContext;
        _firmwareWindow = new FirmwareWindow { Owner = this, DataContext = vm.Firmware };
        _firmwareWindow.Show();
    }
}
