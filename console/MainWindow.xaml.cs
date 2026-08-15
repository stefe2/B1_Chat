using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class MainWindow : Window
{
    private FirmwareWindow? _firmwareWindow;
    private FleetUpdateWindow? _fleetUpdateWindow;
    private HelpWindow? _helpWindow;
    private CalibrationWindow? _calibrationWindow;

    public MainWindow()
    {
        InitializeComponent();

        WindowPlacement.FitMainWindowToCurrentMonitor(this);

        DarkTitleBar.Apply(this);
        var vm = new MainViewModel();
        DataContext = vm;
        Title = "B1 Chat — Supervision Console";
        vm.Droids.OpenFirmwareRequested += OpenFirmwareWindow;
        vm.Droids.OpenCalibrationRequested += OpenCalibrationWindow;
        vm.FleetUpdatePromptRequested += OpenFleetUpdateWindow;
    }

    private void OpenFleetUpdateWindow(FleetUpdateViewModel viewModel)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_fleetUpdateWindow is { IsVisible: true })
            {
                viewModel.Dispose();
                _fleetUpdateWindow.Activate();
                return;
            }

            _fleetUpdateWindow = new FleetUpdateWindow(viewModel) { Owner = this };
            _fleetUpdateWindow.Closed += (_, _) => _fleetUpdateWindow = null;
            _fleetUpdateWindow.ShowDialog();
        });
    }

    private void OpenFirmwareWindow_Click(object sender, RoutedEventArgs e) => OpenFirmwareWindow();

    private void OpenAvailableUpdate_Click(object sender, RoutedEventArgs e)
    {
        var vm = (MainViewModel)DataContext;
        // The shared badge also reports console application updates. Prefer the supervised
        // fleet plan when eligible droids exist; otherwise retain access to the Firmware
        // window where a console-only update can be installed.
        if (!vm.TryRequestFleetUpdateWindow()) OpenFirmwareWindow();
    }

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
        try
        {
            if (_helpWindow is { IsVisible: true })
            {
                _helpWindow.Activate();
                return;
            }

            _helpWindow = new HelpWindow { Owner = this };
            _helpWindow.Show();
        }
        catch (Exception ex)
        {
            _helpWindow = null;
            TraceLog.Write("ERR", $"Open Help: {ex.GetType().Name} — {ex.Message}");
            MessageBox.Show(
                "Help could not be opened. Reinstall B1 Chat Console.\n\n" +
                "Diagnostic details were written to:\n%LOCALAPPDATA%\\B1ChatConsole\\serial-trace.log",
                "B1 Chat Help", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Handled at the tunneling (Preview) stage, before the event can reach whichever
    // ComboBox/Slider/etc. happens to be under the cursor: in a dense card (e.g. Animation's
    // stacked Target/Gesture/idle-tuning row), a child control can otherwise intercept the
    // wheel first and the page scroll gets stuck or jerky depending on exact cursor position.
    // Page scroll is authoritative for an unmodified wheel. Ctrl/Shift wheel over the
    // Sequencer viewport is deliberately yielded to its nested horizontal ScrollViewer so
    // pointer zoom and horizontal pan can receive the routed event.
    private void MainScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ShouldYieldWheelToTimeline(
                Keyboard.Modifiers,
                IsInsideNamedScrollViewer(e.OriginalSource as DependencyObject, "ScrollArea")))
            return;

        MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    internal static bool ShouldYieldWheelToTimeline(ModifierKeys modifiers, bool insideTimelineViewport) =>
        insideTimelineViewport && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

    private static bool IsInsideNamedScrollViewer(DependencyObject? source, string name)
    {
        while (source != null)
        {
            if (source is ScrollViewer viewer && string.Equals(viewer.Name, name, StringComparison.Ordinal))
                return true;
            source = GetRoutedParent(source);
        }
        return false;
    }

    private static DependencyObject? GetRoutedParent(DependencyObject source)
    {
        if (source is ContentElement content)
            return ContentOperations.GetParent(content) ??
                   (content as FrameworkContentElement)?.Parent;
        return VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Explicitly invalidate Sequencer callbacks and close its audio players. Relying on
        // process teardown leaves a window where queued timer callbacks can still run while
        // WPF is closing.
        if (DataContext is MainViewModel vm) vm.Sequencer.Dispose();
        base.OnClosed(e);
    }
}
