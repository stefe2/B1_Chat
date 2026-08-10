using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

public partial class HelpViewModel : ObservableObject
{
    private static readonly string HelpRoot = Path.Combine(AppContext.BaseDirectory, "Help");
    private static readonly string DocsRoot = Path.Combine(HelpRoot, "docs");

    private readonly Dictionary<string, HelpPage> _pagesByFile = new(StringComparer.OrdinalIgnoreCase);

    public List<HelpSection> Sections { get; private set; } = new();

    [ObservableProperty] private string _currentTitle = "";
    [ObservableProperty] private string _currentMarkdown = "";
    [ObservableProperty] private HelpPage? _currentPage;

    public HelpViewModel()
    {
        LoadManifest();
        var first = Sections.FirstOrDefault()?.Pages.FirstOrDefault();
        if (first != null)
            SelectPage(first);
        else
            CurrentMarkdown = "# Help unavailable\n\nThe Help manifest could not be loaded. Reinstall B1 Chat Console.";
    }

    private void LoadManifest()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(HelpRoot, "manifest.json"));
            var manifest = JsonSerializer.Deserialize<HelpManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Sections = manifest?.Sections ?? new List<HelpSection>();
        }
        catch (Exception ex)
        {
            TraceLog.Write("ERR", $"Help manifest: {ex.GetType().Name} — {ex.Message}");
            Sections = new List<HelpSection>();
        }

        foreach (var page in Sections.SelectMany(s => s.Pages))
            _pagesByFile[Normalize(page.File)] = page;
    }

    [RelayCommand]
    private void SelectPage(HelpPage page)
    {
        CurrentPage = page;
        CurrentTitle = page.Title;
        try
        {
            var raw = File.ReadAllText(Path.Combine(DocsRoot, page.File.Replace('/', Path.DirectorySeparatorChar)));
            CurrentMarkdown = ResolveImagePaths(raw, page.File);
        }
        catch (Exception ex)
        {
            TraceLog.Write("ERR", $"Help page {page.File}: {ex.GetType().Name} — {ex.Message}");
            CurrentMarkdown = $"# Page not found\n\n`{page.File}`";
        }
    }

    private static readonly Regex ImageLinkRegex = new(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled);

    // Markdig.Wpf's image renderer builds a BitmapImage straight from the Markdown src with no
    // base URI (new Uri(url, UriKind.RelativeOrAbsolute)) — a plain "images/x.png" wouldn't
    // resolve to anything. Rewriting it to the image's real path on disk here, relative to the
    // displaying page's own folder (same rule TryNavigateInternalLink uses for page links), is
    // what lets the same "images/x.png" markdown work regardless of which page it's written from.
    // Emitted as a "file://" URI (Uri.AbsoluteUri), not a raw Windows path: this repo's own path
    // contains a space ("B1 Chat"), which an un-encoded path would break Markdown link syntax on.
    private string ResolveImagePaths(string markdown, string pageFile) =>
        ImageLinkRegex.Replace(markdown, m =>
        {
            var (alt, src) = (m.Groups[1].Value, m.Groups[2].Value);
            // FlowDocument loads images synchronously on the UI thread. Never let a remote or
            // absent image stall the entire console; show a link/placeholder instead.
            if (src.StartsWith("http://") || src.StartsWith("https://"))
                return $"[Image: {SafeAlt(alt)}]({src})";

            var relative = Normalize(DirOf(pageFile) + src).Replace('/', Path.DirectorySeparatorChar);
            var imagePath = Path.Combine(DocsRoot, relative);
            if (!File.Exists(imagePath))
            {
                TraceLog.Write("ERR", $"Help image missing: {imagePath}");
                return $"**Image unavailable:** {SafeAlt(alt)}";
            }

            var uri = new Uri(imagePath).AbsoluteUri;
            return $"![{alt}]({uri})";
        });

    private static string SafeAlt(string alt) =>
        string.IsNullOrWhiteSpace(alt) ? "unnamed image" : alt.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Resolves a link href from inside the currently displayed page (relative to its own
    /// folder, "../" allowed) against the manifest, and navigates to it if it matches a known page.
    /// Returns false for an absolute http(s)/mailto URI, so the caller opens it externally instead.</summary>
    public bool TryNavigateInternalLink(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == "mailto"))
        {
            return false;
        }

        var baseDir = DirOf(CurrentPage?.File ?? "");
        var target = Normalize(baseDir + href);
        if (_pagesByFile.TryGetValue(target, out var page)) SelectPage(page);
        return true;
    }

    private static string DirOf(string file)
    {
        var i = file.LastIndexOf('/');
        return i >= 0 ? file[..(i + 1)] : "";
    }

    private static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var seg in path.Split('/'))
        {
            if (seg == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else if (seg != "." && seg.Length > 0) segments.Add(seg);
        }
        return string.Join('/', segments);
    }
}
