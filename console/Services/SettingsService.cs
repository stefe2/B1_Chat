using System.IO;
using System.Text.Json;

namespace b1_chat_console.Services;

/// <summary>Porte de LoadSettings/SaveSettings (ex-MainWindow.xaml.cs) : meme fichier, meme forme.</summary>
public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B1ChatConsole");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public string? LastPort { get; private set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(SettingsFile));
            if (doc.TryGetProperty("lastPort", out var p) && p.ValueKind == JsonValueKind.String)
                LastPort = p.GetString();
        }
        catch { /* fichier absent/corrompu : on repart sans dernier port connu */ }
    }

    public void SetLastPort(string? port)
    {
        LastPort = port;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(new { lastPort = LastPort });
            File.WriteAllText(SettingsFile, json);
        }
        catch { /* disque plein/verrouille : non bloquant */ }
    }
}
