using System.Xml;
using System.Xml.Linq;

namespace b1_chat_console.Tests;

public class TooltipCoverageTests
{
    private static readonly HashSet<string> InteractiveElements = new(StringComparer.Ordinal)
    {
        "Button",
        "CheckBox",
        "ComboBox",
        "ListBox",
        "MenuItem",
        "Slider",
        "TextBox",
        "ToggleButton",
    };

    [Fact]
    public void InteractiveControls_ExposeActionTooltips()
    {
        var consoleDirectory = FindRepositoryDirectory("console");
        var missing = new List<string>();

        foreach (var path in Directory.EnumerateFiles(consoleDirectory, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !string.Equals(Path.GetFileName(path), "App.xaml", StringComparison.OrdinalIgnoreCase)))
        {
            var document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants().Where(element => InteractiveElements.Contains(element.Name.LocalName)))
            {
                var attributeTooltip = (string?)element.Attribute("ToolTip");
                var elementTooltip = element.Elements()
                    .FirstOrDefault(child => child.Name.LocalName == $"{element.Name.LocalName}.ToolTip");
                if (!string.IsNullOrWhiteSpace(attributeTooltip) || elementTooltip != null) continue;

                var line = (element as IXmlLineInfo)?.LineNumber ?? 0;
                var label = (string?)element.Attribute("Content")
                            ?? (string?)element.Attribute("Header")
                            ?? (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                            ?? "unnamed";
                missing.Add($"{Path.GetRelativePath(consoleDirectory, path)}:{line} {element.Name.LocalName} '{label}'");
            }
        }

        Assert.True(missing.Count == 0,
            "Interactive controls without a tooltip:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void SharedInteractiveStyles_ShowTooltipsWhileDisabled()
    {
        var path = FindRepositoryFile("console", "Themes", "Effects.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expectedStyles = new[]
        {
            "BeveledButtonStyle",
            "DangerButtonStyle",
            "DarkMenuItemStyle",
            "AccentButtonStyle",
            "HaloBadgeButtonStyle",
            "HaloToggleButtonStyle",
            "LoopToggleButtonStyle",
            "OnOffSwitchStyle",
            "RecessedTextBoxStyle",
            "DangerCheckBoxStyle",
            "MetalSliderStyle",
            "DarkComboBoxStyle",
        };

        foreach (var key in expectedStyles)
        {
            var style = document.Descendants(presentation + "Style")
                .Single(element => string.Equals((string?)element.Attribute(xaml + "Key"), key, StringComparison.Ordinal));
            Assert.Contains(style.Elements(presentation + "Setter"), setter =>
                string.Equals((string?)setter.Attribute("Property"), "ToolTipService.ShowOnDisabled", StringComparison.Ordinal) &&
                string.Equals((string?)setter.Attribute("Value"), "True", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SafetyAndTimingTooltips_MatchCurrentBehavior()
    {
        var calibration = File.ReadAllText(FindRepositoryFile("console", "Views", "CalibrationCardView.xaml"));
        var droids = File.ReadAllText(FindRepositoryFile("console", "Views", "DroidsCardView.xaml"));
        var firmware = File.ReadAllText(FindRepositoryFile("console", "Views", "FirmwareCardView.xaml"));
        var sequencer = File.ReadAllText(FindRepositoryFile("console", "Views", "SequenceTimelineView.xaml"));

        Assert.Contains("saved after 1.2 seconds", calibration, StringComparison.Ordinal);
        Assert.Contains("identification LED with solid on", droids, StringComparison.Ordinal);
        Assert.Contains("do not select it automatically", firmware, StringComparison.Ordinal);
        Assert.Contains("clamped to the current content tail", sequencer, StringComparison.Ordinal);
        Assert.Contains("0.2 seconds after the original", sequencer, StringComparison.Ordinal);
    }

    private static string FindRepositoryDirectory(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (Directory.Exists(path)) return path;
        }

        throw new DirectoryNotFoundException($"Repository directory not found: {Path.Combine(segments)}");
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
