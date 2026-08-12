using System.Windows;
using b1_chat_console.Services;

namespace b1_chat_console;

public enum SceneDecisionResult
{
    Cancel,
    Primary,
    Secondary,
}

public partial class SceneDecisionWindow : Window
{
    public string Heading { get; }
    public string Message { get; }
    public string PrimaryLabel { get; }
    public string SecondaryLabel { get; }
    public bool ShowSecondary { get; }
    public SceneDecisionResult Selection { get; private set; } = SceneDecisionResult.Cancel;

    public SceneDecisionWindow(
        string title,
        string heading,
        string message,
        string primaryLabel,
        string? secondaryLabel = null)
    {
        Title = title;
        Heading = heading;
        Message = message;
        PrimaryLabel = primaryLabel;
        SecondaryLabel = secondaryLabel ?? "";
        ShowSecondary = secondaryLabel != null;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = this;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Selection = SceneDecisionResult.Primary;
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Selection = SceneDecisionResult.Secondary;
        DialogResult = true;
    }
}
