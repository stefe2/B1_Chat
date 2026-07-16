using System.IO;
using System.Text.Json;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>
/// Local-only association between one of the master's 8 NVS sequence slots and its
/// console-side audio lanes (each a label + a list of clips). The firmware's NVS blob has
/// no room for filesystem paths (and doesn't need any — DFPlayer set aside "for now", see
/// CLAUDE.md), so this lives entirely client-side, keyed by slot number, same folder
/// pattern as SettingsService. A slot deleted or re-saved from a different console install
/// simply has no entry (Get returns null), same as a slot that never had audio attached.
/// </summary>
public class SequenceAudioStore
{
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "B1ChatConsole", "slot-audio.json");

    private Dictionary<int, List<AudioLaneDto>> _map = new();

    public SequenceAudioStore() => Load();

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            _map = JsonSerializer.Deserialize<Dictionary<int, List<AudioLaneDto>>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { _map = new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map));
        }
        catch { /* disk full/locked: non-blocking */ }
    }

    public List<AudioLaneDto>? Get(int slot) => _map.TryGetValue(slot, out var v) ? v : null;

    public void Set(int slot, List<AudioLaneDto> lanes)
    {
        var hasAnyClip = lanes.Any(l => l.Clips.Count > 0);
        if (!hasAnyClip) _map.Remove(slot);
        else _map[slot] = lanes;
        Save();
    }

    public void Delete(int slot)
    {
        _map.Remove(slot);
        Save();
    }
}
