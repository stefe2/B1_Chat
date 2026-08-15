using System.Windows;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class FirmwareWindow : Window
{
    private FirmwareViewModel? _viewModel;

    public FirmwareWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContextChanged += FirmwareWindow_DataContextChanged;
    }

    private void FirmwareWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
            _viewModel.CloseFirmwareWindowRequested -= CloseFirmwareWindow;

        _viewModel = e.NewValue as FirmwareViewModel;
        if (_viewModel != null)
            _viewModel.CloseFirmwareWindowRequested += CloseFirmwareWindow;
    }

    private void CloseFirmwareWindow() => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.CloseFirmwareWindowRequested -= CloseFirmwareWindow;
            _viewModel.DismissFlashResult();
            _viewModel = null;
        }
        base.OnClosed(e);
    }
}
