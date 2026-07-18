using System.Windows;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class MainWindow : Window
{
    private FirmwareWindow? _firmwareWindow;
    private HelpWindow? _helpWindow;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        Title = "B1 Chat — Supervision Console";
        vm.Droids.OpenFirmwareRequested += OpenFirmwareWindow;
    }

    private void OpenFirmwareWindow_Click(object sender, RoutedEventArgs e) => OpenFirmwareWindow();

    private void OpenFirmwareWindow()
    {
        if (_firmwareWindow is { IsVisible: true })
        {
            _firmwareWindow.Activate();
            return;
        }

        var vm = (MainViewModel)DataContext;
        vm.Firmware.RefreshFlashPortsCommand.Execute(null);
        vm.Firmware.CheckUpdatesCommand.Execute(null);
        _firmwareWindow = new FirmwareWindow { Owner = this, DataContext = vm.Firmware };
        _firmwareWindow.Show();
    }

    private void OpenHelpWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_helpWindow is { IsVisible: true })
        {
            _helpWindow.Activate();
            return;
        }

        _helpWindow = new HelpWindow { Owner = this };
        _helpWindow.Show();
    }
}
