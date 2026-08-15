using System.ComponentModel;
using System.Windows;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class FleetUpdateWindow : Window
{
    private readonly FleetUpdateViewModel _viewModel;

    public FleetUpdateWindow(FleetUpdateViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = viewModel;
        viewModel.CloseRequested += CloseFromViewModel;
    }

    private void CloseFromViewModel() => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Closing the process in the middle of an OTA/USB write can strand a board. Keep the
        // supervised progress window open until the current batch has a definitive result.
        if (_viewModel.IsRunning) e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= CloseFromViewModel;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
