using System.IO;
using System.Text.Json;

namespace b1_chat_console.Services;

public interface ISequencerSettings
{
    string? LastSequencePath { get; }
    string? LastSceneId { get; }
    void SetLastSequencePath(string? path);
    void SetLastSceneId(string? sceneId);
}

/// <summary>Port of LoadSettings/SaveSettings (formerly MainWindow.xaml.cs): same file, same shape.</summary>
public class SettingsService : ISequencerSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B1ChatConsole");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public string? LastPort { get; private set; }
    public string? LastSequencePath { get; private set; }
    public string? LastSceneId { get; private set; }

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return;
            var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(SettingsFile));
            if (doc.TryGetProperty("lastPort", out var p) && p.ValueKind == JsonValueKind.String)
                LastPort = p.GetString();
            if (doc.TryGetProperty("lastSequencePath", out var s) && s.ValueKind == JsonValueKind.String)
                LastSequencePath = s.GetString();
            if (doc.TryGetProperty("lastSceneId", out var scene) && scene.ValueKind == JsonValueKind.String)
                LastSceneId = scene.GetString();
        }
        catch { /* file missing/corrupt: start over without a known last port */ }
    }

    public void SetLastPort(string? port)
    {
        LastPort = port;
        Save();
    }

    public void SetLastSequencePath(string? path)
    {
        LastSequencePath = path;
        Save();
    }

    public void SetLastSceneId(string? sceneId)
    {
        LastSceneId = sceneId;
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(new
            {
                lastPort = LastPort,
                lastSequencePath = LastSequencePath,
                lastSceneId = LastSceneId,
            });
            File.WriteAllText(SettingsFile, json);
        }
        catch { /* disk full/locked: non-blocking */ }
    }
}
