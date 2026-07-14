using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// Port of GetLatestReleaseAsync/CheckUpdatesAsync/DownloadAssetAsync/InstallAppUpdateAsync
/// (formerly MainWindow.xaml.cs): same single repo with tag-prefix filtering ("v*" excluding
/// "fw-*" for the app, "fw-*" for the firmware, never /releases/latest which would mix the
/// two release trains), same SHA-256 verification via firmware_manifest.json.
/// </summary>
public class UpdateService
{
    private const string Repo = "stefe2/B1_Chat";

    public static readonly string UpdatesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B1ChatConsole", "updates");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("b1-chat-console");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static async Task<JsonElement?> GetLatestReleaseAsync(Func<string, bool> tagMatches)
    {
        var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=50");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var list = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());

        // NEVER trust the /releases list order: observed in practice to be sorted
        // LEXICOGRAPHICALLY by tag ("fw-v1.3.9" before "fw-v1.3.11"/"fw-v1.3.10"), not
        // chronologically — the console already flashed a 1.3.9 thinking it was the
        // latest. Parse the versions and keep the semantic maximum.
        JsonElement? best = null;
        Version? bestVersion = null;
        foreach (var rel in list.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? "";
            if (!tagMatches(tag)) continue;
            var v = ParseTagVersion(tag);
            if (best == null || (v != null && (bestVersion == null || v > bestVersion)))
            {
                best = rel;
                bestVersion = v;
            }
        }
        return best;
    }

    private static Version? ParseTagVersion(string tag)
    {
        var s = tag;
        if (s.StartsWith("fw-", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        s = s.TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : null;
    }

    private static string? AssetUrl(JsonElement release, Func<string, bool> match)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (match(name)) return a.GetProperty("browser_download_url").GetString();
        }
        return null;
    }

    public async Task<UpdateCheckResult> CheckUpdatesAsync()
    {
        try
        {
            var appRel = await GetLatestReleaseAsync(tag => tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("fw-", StringComparison.OrdinalIgnoreCase));
            var fwRel = await GetLatestReleaseAsync(tag => tag.StartsWith("fw-", StringComparison.OrdinalIgnoreCase));

            var app = new AppUpdateInfo();
            if (appRel is JsonElement ar)
            {
                app.Latest = (ar.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                app.Url = AssetUrl(ar, n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                app.Notes = ar.TryGetProperty("body", out var ab) ? ab.GetString() : null;
            }

            var fw = new FirmwareUpdateInfo();
            if (fwRel is JsonElement fr)
            {
                var tag = fr.GetProperty("tag_name").GetString() ?? "";
                fw.Latest = tag.Replace("fw-", "").TrimStart('v', 'V');
                var manifestUrl = AssetUrl(fr, n => n == "firmware_manifest.json");
                if (manifestUrl != null)
                {
                    try
                    {
                        var man = JsonSerializer.Deserialize<JsonElement>(await Http.GetStringAsync(manifestUrl));
                        foreach (var f in man.GetProperty("files").EnumerateArray())
                        {
                            var role = f.GetProperty("role").GetString();
                            var sha = f.GetProperty("sha256").GetString();
                            if (role == "master") fw.Sha256Master = sha;
                            else if (role == "slave") fw.Sha256Slave = sha;
                        }
                    }
                    catch { /* manifest missing/unreadable: download without verification */ }
                }
                fw.UrlMaster = AssetUrl(fr, n => n.Contains("master") && n.EndsWith(".bin"));
                fw.UrlSlave = AssetUrl(fr, n => n.Contains("slave") && n.EndsWith(".bin"));
                fw.Notes = fr.TryGetProperty("body", out var fb) ? fb.GetString() : null;
            }

            return new UpdateCheckResult { Ok = true, App = app, Fw = fw };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult { Ok = false, Error = ex.Message };
        }
    }

    public async Task<(bool Ok, string? Path, string? Name, long Size, string? Error)> DownloadAssetAsync(string url, string? sha256)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Missing URL");
            Directory.CreateDirectory(UpdatesDir);
            var name = Path.GetFileName(new Uri(url).LocalPath);
            var path = Path.Combine(UpdatesDir, name);
            var bytes = await Http.GetByteArrayAsync(url);

            if (!string.IsNullOrEmpty(sha256))
            {
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, sha256.ToLowerInvariant(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Invalid SHA-256 (expected {sha256[..12]}…, got {actual[..12]}…) — file rejected");
            }

            await File.WriteAllBytesAsync(path, bytes);
            return (true, path, name, bytes.LongLength, null);
        }
        catch (Exception ex)
        {
            return (false, null, null, 0, ex.Message);
        }
    }

    /// <summary>Downloads the installer; it's up to the caller to launch the process and close the app.</summary>
    public async Task<(bool Ok, string? Path, string? Error)> DownloadAppInstallerAsync(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Missing URL");
            Directory.CreateDirectory(UpdatesDir);
            var path = Path.Combine(UpdatesDir, Path.GetFileName(new Uri(url).LocalPath));
            await File.WriteAllBytesAsync(path, await Http.GetByteArrayAsync(url));
            return (true, path, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
