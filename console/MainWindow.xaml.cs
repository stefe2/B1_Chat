using System.Windows;
using System.Windows.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class MainWindow : Window
{
    private FirmwareWindow? _firmwareWindow;
    private HelpWindow? _helpWindow;
    private CalibrationWindow? _calibrationWindow;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        var vm = new MainViewModel();
        DataContext = vm;
        Title = "B1 Chat — Supervision Console";
        vm.Droids.OpenFirmwareRequested += OpenFirmwareWindow;
        vm.Droids.OpenCalibrationRequested += OpenCalibrationWindow;
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

    private void OpenCalibrationWindow(Droid target)
    {
        var vm = (MainViewModel)DataContext;
        vm.Calibration.SelectedTarget = target;

        if (_calibrationWindow is { IsVisible: true })
        {
            _calibrationWindow.Activate();
            return;
        }

        _calibrationWindow = new CalibrationWindow { Owner = this, DataContext = vm.Calibration };
        _calibrationWindow.Show();
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

    // Handled at the tunneling (Preview) stage, before the event can reach whichever
    // ComboBox/Slider/etc. happens to be under the cursor: in a dense card (e.g. Animation's
    // stacked Target/Gesture/idle-tuning row), a child control can otherwise intercept the
    // wheel first and the page scroll gets stuck or jerky depending on exact cursor position.
    // Page scroll is made authoritative everywhere in the main window — ComboBox popups are
    // separate top-level windows (unaffected) and the Sequencer timeline has no vertical
    // scroll of its own to compete with.
    private void MainScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }
}
