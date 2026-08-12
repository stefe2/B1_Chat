using System.Windows;
using b1_chat_console.Services;

namespace b1_chat_console;

public partial class SceneNameWindow : Window
{
    public string SceneName { get; set; }

    public SceneNameWindow(string initialName, string title)
    {
        SceneName = initialName;
        Title = title;
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = this;
    }

    private void Window_ContentRendered(object sender, EventArgs e)
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SceneName = NameBox.Text;
        DialogResult = true;
    }
}
