using System.Xml.Linq;

namespace b1_chat_console.Tests;

public class MainWindowInteractionTests
{
    [Fact]
    public void UpdateAvailableBadge_IsAButtonWithFleetClickHandler()
    {
        var xamlPath = FindRepositoryFile("console", "MainWindow.xaml");
        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var button = document.Descendants(presentation + "Button")
            .SingleOrDefault(element =>
                string.Equals((string?)element.Attribute("Click"), "OpenAvailableUpdate_Click",
                    StringComparison.Ordinal));

        Assert.NotNull(button);
        var label = button.Descendants(presentation + "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .FirstOrDefault(text => text != null);
        Assert.Contains("update available", label!, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException($"Repository file not found: {Path.Combine(segments)}");
    }
}
