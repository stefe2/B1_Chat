using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace b1_chat_console;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "B1ChatConsole", "settings.json");

    // "0.3.0+build.42" — VersionPrefix du csproj + numéro de build auto-incrémenté.
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    private SerialPort? _port;
    private CancellationTokenSource? _readCts;
    private string? _lastPort;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        Title = $"B1 Chat — Console de supervision  v{AppVersion.Replace("+build.", " (build ")}{(AppVersion.Contains("+build.") ? ")" : "")}";
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => ClosePort();
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var el = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(SettingsFile));
            if (el.TryGetProperty("lastPort", out var p)) _lastPort = p.GetString();
        }
        catch { /* réglages illisibles : repartir à neuf */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { lastPort = _lastPort }));
        }
        catch { /* disque plein/verrouillé : tant pis pour la mémoire du port */ }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await Browser.EnsureCoreWebView2Async();

        Browser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

        var indexPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        Browser.CoreWebView2.Navigate(new Uri(indexPath).AbsoluteUri);
    }

    // Transport-level messages from the page (distinct from the firmware's own
    // cmd/evt JSON protocol, which travels inside "write"/"line" as opaque strings).
    private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement msg;
        try
        {
            msg = JsonSerializer.Deserialize<JsonElement>(e.WebMessageAsJson);
        }
        catch
        {
            return;
        }

        var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;

        switch (type)
        {
            case "getAppInfo":
                SendToPage(new { type = "appInfo", version = AppVersion });
                break;

            // Pont fichiers : la page ne peut pas ouvrir de dialogues natifs.
            case "saveFile":
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = msg.TryGetProperty("suggestedName", out var sn) ? (sn.GetString() ?? "export.json") : "export.json",
                    Filter = "JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*",
                };
                if (dlg.ShowDialog(this) == true)
                {
                    try
                    {
                        File.WriteAllText(dlg.FileName, msg.GetProperty("content").GetString() ?? "");
                        SendToPage(new { type = "fileSaved", ok = true, path = dlg.FileName });
                    }
                    catch (Exception ex)
                    {
                        SendToPage(new { type = "fileSaved", ok = false, error = ex.Message });
                    }
                }
                else SendToPage(new { type = "fileSaved", ok = false, cancelled = true });
                break;
            }

            case "libList":
                SendLibrary();
                break;

            case "libSave":
            {
                try
                {
                    var id = SafeLibId(msg.GetProperty("id").GetString());
                    Directory.CreateDirectory(LibraryDir);
                    File.WriteAllText(Path.Combine(LibraryDir, id + ".json"), msg.GetProperty("item").GetRawText());
                    SendToPage(new { type = "libSaved", ok = true, id });
                }
                catch (Exception ex) { SendToPage(new { type = "libSaved", ok = false, error = ex.Message }); }
                SendLibrary();
                break;
            }

            case "libDelete":
            {
                try
                {
                    var id = SafeLibId(msg.GetProperty("id").GetString());
                    var p = Path.Combine(LibraryDir, id + ".json");
                    if (File.Exists(p)) File.Delete(p);
                    SendToPage(new { type = "libDeleted", ok = true, id });
                }
                catch (Exception ex) { SendToPage(new { type = "libDeleted", ok = false, error = ex.Message }); }
                SendLibrary();
                break;
            }

            case "pickBin":
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Image firmware (*.bin)|*.bin|Tous les fichiers (*.*)|*.*" };
                if (dlg.ShowDialog(this) == true)
                {
                    var fi = new FileInfo(dlg.FileName);
                    SendToPage(new { type = "binPicked", ok = true, path = fi.FullName, name = fi.Name, size = fi.Length });
                }
                else SendToPage(new { type = "binPicked", ok = false });
                break;
            }

            case "flash":
                StartFlash(
                    msg.GetProperty("path").GetString() ?? "",
                    msg.TryGetProperty("address", out var ad) ? (ad.GetString() ?? "0x0") : "0x0",
                    msg.GetProperty("port").GetString() ?? "");
                break;

            case "openFile":
            {
                var purpose = msg.TryGetProperty("purpose", out var pu) ? pu.GetString() : "";
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json|Tous les fichiers (*.*)|*.*" };
                if (dlg.ShowDialog(this) == true)
                {
                    try
                    {
                        SendToPage(new { type = "fileOpened", ok = true, purpose, name = Path.GetFileName(dlg.FileName), content = File.ReadAllText(dlg.FileName) });
                    }
                    catch (Exception ex)
                    {
                        SendToPage(new { type = "fileOpened", ok = false, purpose, error = ex.Message });
                    }
                }
                else SendToPage(new { type = "fileOpened", ok = false, purpose, cancelled = true });
                break;
            }

            case "listPorts":
                SendToPage(new { type = "ports", list = SerialPort.GetPortNames(), lastPort = _lastPort });
                break;

            case "open":
                OpenPort(msg.GetProperty("port").GetString() ?? "");
                break;

            case "close":
                ClosePort();
                SendToPage(new { type = "closed" });
                break;

            case "write":
                var data = msg.GetProperty("data").GetString() ?? "";
                try
                {
                    _port?.Write(data);
                }
                catch (Exception ex)
                {
                    SendToPage(new { type = "error", message = ex.Message });
                }
                break;

            case "checkUpdates":
                _ = CheckUpdatesAsync();
                break;

            case "downloadAsset":
                _ = DownloadAssetAsync(
                    msg.GetProperty("url").GetString() ?? "",
                    msg.TryGetProperty("sha256", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null);
                break;

            case "installAppUpdate":
                _ = InstallAppUpdateAsync(msg.GetProperty("url").GetString() ?? "");
                break;
        }
    }

    private void OpenPort(string portName)
    {
        ClosePort();

        try
        {
            _port = new SerialPort(portName, 115200)
            {
                NewLine = "\n",
                Encoding = System.Text.Encoding.UTF8,
                ReadTimeout = 500,
            };
            _port.Open();

            _readCts = new CancellationTokenSource();
            var token = _readCts.Token;
            var port = _port;
            Task.Run(() => ReadLoop(port, token), token);

            _lastPort = portName;
            SaveSettings();
            SendToPage(new { type = "opened", ok = true, port = portName });
        }
        catch (Exception ex)
        {
            _port = null;
            SendToPage(new { type = "opened", ok = false, port = portName, error = ex.Message });
        }
    }

    private void ReadLoop(SerialPort port, CancellationToken token)
    {
        while (!token.IsCancellationRequested && port.IsOpen)
        {
            string line;
            try
            {
                line = port.ReadLine();
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch
            {
                break;
            }

            Dispatcher.Invoke(() => SendToPage(new { type = "line", data = line }));
        }

        // Sortie de boucle sans annulation = le port est mort (câble débranché,
        // erreur d'E/S). Sans cette notification, la page croirait le lien
        // toujours actif ; "unexpected" lui permet de tenter une reconnexion.
        if (!token.IsCancellationRequested)
        {
            Dispatcher.Invoke(() =>
            {
                ClosePort();
                SendToPage(new { type = "closed", unexpected = true });
            });
        }
    }

    private void ClosePort()
    {
        _readCts?.Cancel();
        _readCts = null;

        if (_port != null)
        {
            try { if (_port.IsOpen) _port.Close(); } catch { /* ignore */ }
            _port.Dispose();
            _port = null;
        }
    }

    private void SendToPage(object message)
    {
        if (Browser.CoreWebView2 == null) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    // --- Bibliothèque locale de séquences ------------------------------------
    // Un fichier JSON par séquence dans %LOCALAPPDATA%\B1ChatConsole\library —
    // illimitée, contrairement aux 8 slots NVS du maître. La page pousse/rapatrie
    // les séquences entre cette bibliothèque et l'ESP32.

    private static readonly string LibraryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "B1ChatConsole", "library");

    private static string SafeLibId(string? id)
    {
        var s = string.IsNullOrWhiteSpace(id) ? "sequence" : id;
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    private void SendLibrary()
    {
        var items = new List<JsonElement>();
        try
        {
            if (Directory.Exists(LibraryDir))
            {
                foreach (var f in Directory.GetFiles(LibraryDir, "*.json").OrderBy(x => x))
                {
                    try { items.Add(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(f))); }
                    catch { /* fichier corrompu : ignoré, pas de quoi bloquer la liste */ }
                }
            }
        }
        catch { /* répertoire illisible : liste vide */ }
        SendToPage(new { type = "libList", list = items });
    }

    // --- Mises à jour GitHub (app + firmware) ---------------------------------
    // La page envoie checkUpdates ; app et firmware partagent le même dépôt
    // GitHub (fusion du dépôt console dedans) mais sur deux trains de tags
    // distincts ("vX.Y.Z" pour l'app, "fw-vX.Y.Z" pour le firmware) — donc pas
    // moyen d'utiliser /releases/latest (global au dépôt, toutes releases
    // confondues) : on liste les releases et on prend la plus récente qui
    // correspond au préfixe voulu (l'API les retourne déjà triées par date).
    // C'est la page qui compare les versions (elle connaît la sienne via
    // appInfo et celle du firmware via hello).

    private const string Repo = "stefe2/B1_Chat";

    private static readonly string UpdatesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "B1ChatConsole", "updates");

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // L'API GitHub exige un User-Agent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("b1-chat-console");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    // Dernière release du dépôt dont le tag correspond au prédicat, ou null si aucune.
    private static async Task<JsonElement?> GetLatestReleaseAsync(Func<string, bool> tagMatches)
    {
        var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=50");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        var list = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        foreach (var rel in list.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? "";
            if (tagMatches(tag)) return rel;
        }
        return null;
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

    private async Task CheckUpdatesAsync()
    {
        try
        {
            var appRel = await GetLatestReleaseAsync(tag => tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) && !tag.StartsWith("fw-", StringComparison.OrdinalIgnoreCase));
            var fwRel = await GetLatestReleaseAsync(tag => tag.StartsWith("fw-", StringComparison.OrdinalIgnoreCase));

            object app = appRel is JsonElement ar
                ? new
                {
                    latest = (string?)(ar.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V'),
                    url = AssetUrl(ar, n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
                    notes = ar.TryGetProperty("body", out var ab) ? ab.GetString() : null,
                }
                : new { latest = (string?)null, url = (string?)null, notes = (string?)null };

            object fw;
            if (fwRel is JsonElement fr)
            {
                // Tag "fw-v1.2.0" ou "v1.2.0" -> "1.2.0"
                var tag = fr.GetProperty("tag_name").GetString() ?? "";
                var latest = tag.Replace("fw-", "").TrimStart('v', 'V');
                string? shaMaster = null, shaSlave = null;
                // Le manifeste (modèle KyberEditor) porte les SHA-256 des .bin.
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
                            if (role == "master") shaMaster = sha;
                            else if (role == "slave") shaSlave = sha;
                        }
                    }
                    catch { /* manifeste absent/illisible : téléchargement sans vérification */ }
                }
                fw = new
                {
                    latest,
                    urlMaster = AssetUrl(fr, n => n.Contains("master") && n.EndsWith(".bin")),
                    urlSlave = AssetUrl(fr, n => n.Contains("slave") && n.EndsWith(".bin")),
                    sha256Master = shaMaster,
                    sha256Slave = shaSlave,
                    notes = fr.TryGetProperty("body", out var fb) ? fb.GetString() : null,
                };
            }
            else fw = new { latest = (string?)null };

            SendToPage(new { type = "updateInfo", ok = true, app, fw });
        }
        catch (Exception ex)
        {
            SendToPage(new { type = "updateInfo", ok = false, error = ex.Message });
        }
    }

    // Télécharge un asset (firmware .bin) dans updates\ et vérifie son SHA-256
    // si le manifeste l'a fourni — un transfert tronqué/corrompu est rejeté.
    private async Task DownloadAssetAsync(string url, string? sha256)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL manquante");
            Directory.CreateDirectory(UpdatesDir);
            var name = Path.GetFileName(new Uri(url).LocalPath);
            var path = Path.Combine(UpdatesDir, name);
            var bytes = await Http.GetByteArrayAsync(url);

            if (!string.IsNullOrEmpty(sha256))
            {
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, sha256.ToLowerInvariant(), StringComparison.Ordinal))
                    throw new InvalidOperationException($"SHA-256 invalide (attendu {sha256[..12]}…, obtenu {actual[..12]}…) — fichier rejeté");
            }

            await File.WriteAllBytesAsync(path, bytes);
            SendToPage(new { type = "assetDownloaded", ok = true, path, name, size = bytes.LongLength });
        }
        catch (Exception ex)
        {
            SendToPage(new { type = "assetDownloaded", ok = false, error = ex.Message });
        }
    }

    // Télécharge l'installeur de l'app, le lance, et ferme l'application
    // (installation par-utilisateur, sans admin ; l'utilisateur relance ensuite).
    private async Task InstallAppUpdateAsync(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL manquante");
            Directory.CreateDirectory(UpdatesDir);
            var path = Path.Combine(UpdatesDir, Path.GetFileName(new Uri(url).LocalPath));
            await File.WriteAllBytesAsync(path, await Http.GetByteArrayAsync(url));
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SendToPage(new { type = "error", message = "Mise à jour de l'app impossible : " + ex.Message });
        }
    }

    // --- Flashage firmware (espflash) ---------------------------------------

    // Le binaire espflash n'est pas versionné avec le projet : on le cherche à
    // côté de l'app (tools\), puis dans l'installation KyberEditor, puis le PATH.
    private static string? FindEspflash()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "espflash.exe"),
            @"C:\Program Files\KyberEditor\Tools\espflash.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var p = Path.Combine(dir.Trim(), "espflash.exe");
            try { if (dir.Length > 0 && File.Exists(p)) return p; } catch { /* segment de PATH invalide */ }
        }
        return null;
    }

    private void StartFlash(string binPath, string address, string portName)
    {
        var tool = FindEspflash();
        if (tool == null)
        {
            SendToPage(new { type = "flashDone", ok = false, error = "espflash.exe introuvable — dépose-le dans le dossier tools\\ à côté de l'application (voir CLAUDE.md)." });
            return;
        }
        if (!File.Exists(binPath))
        {
            SendToPage(new { type = "flashDone", ok = false, error = "fichier .bin introuvable : " + binPath });
            return;
        }
        if (string.IsNullOrWhiteSpace(portName))
        {
            SendToPage(new { type = "flashDone", ok = false, error = "aucun port série choisi." });
            return;
        }

        // espflash exige le port pour lui seul ; la page sait que cette
        // fermeture est volontaire (elle a mis manualClose avant d'envoyer "flash").
        ClosePort();
        SendToPage(new { type = "closed" });

        var psi = new ProcessStartInfo
        {
            FileName = tool,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("write-bin");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(portName);
        psi.ArgumentList.Add("-B"); psi.ArgumentList.Add("460800");
        psi.ArgumentList.Add(address);
        psi.ArgumentList.Add(binPath);

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void Line(string? s) { if (s != null) Dispatcher.Invoke(() => SendToPage(new { type = "flashLog", line = s })); }
            proc.OutputDataReceived += (_, a) => Line(a.Data);
            proc.ErrorDataReceived += (_, a) => Line(a.Data);
            proc.Exited += (_, _) => Dispatcher.Invoke(() =>
            {
                SendToPage(new { type = "flashDone", ok = proc.ExitCode == 0, exitCode = proc.ExitCode });
                proc.Dispose();
            });
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            SendToPage(new { type = "flashLog", line = $"» espflash write-bin --port {portName} -B 460800 {address} {Path.GetFileName(binPath)}" });
        }
        catch (Exception ex)
        {
            SendToPage(new { type = "flashDone", ok = false, error = ex.Message });
        }
    }
}
