using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Text.RegularExpressions;
using b1_chat_console.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Markdig.Wpf;
using NAudio.Wave;

namespace b1_chat_console.Services;

/// <summary>
/// Verifies files that must remain outside the single-file executable. This is intentionally
/// callable without opening a window so both the release pipeline and NSIS can smoke-test the
/// exact payload that will run on the destination computer.
/// </summary>
public static class InstallationVerifier
{
    private static readonly Regex ImageLinkRegex =
        new(@"!\[[^\]]*\]\((?<src>[^)]+)\)", RegexOptions.Compiled);

    public static bool TryVerify(string baseDirectory, out string error)
    {
        try
        {
            // Resolving one public type from each packaged dependency makes the smoke test fail
            // immediately if a managed assembly was omitted from the single-file bundle.
            var bundledAssemblies = new[]
            {
                typeof(SerialPort).Assembly,
                typeof(ObservableObject).Assembly,
                typeof(Markdown).Assembly,
                typeof(AudioFileReader).Assembly,
            };
            if (bundledAssemblies.Any(assembly => string.IsNullOrWhiteSpace(assembly.GetName().Name)))
                throw new InvalidDataException("A bundled managed dependency could not be loaded.");

            var root = Path.GetFullPath(baseDirectory);
            var helpRoot = Path.Combine(root, "Help");
            var docsRoot = Path.Combine(helpRoot, "docs");
            var manifestPath = Path.Combine(helpRoot, "manifest.json");
            RequireFile(Path.Combine(root, "tools", "espflash.exe"), "flashing tool");
            RequireFile(manifestPath, "Help manifest");

            var manifest = JsonSerializer.Deserialize<HelpManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var pages = manifest?.Sections.SelectMany(section => section.Pages).ToList();
            if (pages is not { Count: > 0 }) throw new InvalidDataException("Help manifest contains no pages.");

            foreach (var page in pages)
            {
                var pagePath = SafeChildPath(docsRoot, page.File);
                RequireFile(pagePath, $"Help page '{page.File}'");

                var markdown = File.ReadAllText(pagePath);
                foreach (Match match in ImageLinkRegex.Matches(markdown))
                {
                    var source = match.Groups["src"].Value.Trim();
                    if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    source = Uri.UnescapeDataString(source);
                    var imagePath = SafeChildPath(docsRoot,
                        Path.GetRelativePath(docsRoot, Path.Combine(Path.GetDirectoryName(pagePath)!, source)));
                    RequireFile(imagePath, $"Help image '{source}' referenced by '{page.File}'");
                }
            }

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string SafeChildPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes its installation directory: '{relativePath}'.");
        return path;
    }

    private static void RequireFile(string path, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Missing {description}.", path);
    }
}
