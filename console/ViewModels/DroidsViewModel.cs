using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;
using Microsoft.Win32;

namespace b1_chat_console.ViewModels;

public partial class DroidsViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;

    public ObservableCollection<Droid> Droids => _protocol.Droids;

    public DroidsViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
    }

    [RelayCommand]
    private void CommitName(Droid? droid)
    {
        if (droid == null) return;
        var name = (droid.EditingName ?? "").Trim();
        if (name.Length == 0 || name == droid.Name) return;
        droid.Name = name;
        _protocol.SetName(droid.Id, name);
    }

    [RelayCommand]
    private void ToggleServos(Droid? droid)
    {
        if (droid == null) return;
        droid.ServosOn = !droid.ServosOn;
        _protocol.SetServo(droid.Id, droid.ServosOn);
    }

    [RelayCommand]
    private void ToggleAutoAnim(Droid? droid)
    {
        if (droid == null) return;
        droid.AutoAnimOn = !droid.AutoAnimOn;
        _protocol.SetAutoAnim(droid.Id, droid.AutoAnimOn);
    }

    // --- Sauvegarde / restauration --------------------------------------------
    // Simplifie par rapport a index.html : pas de fenetre de diff dediee, une
    // confirmation MessageBox avant restauration (le contenu du plan met l'accent
    // sur le portage fonctionnel des 8 cartes, pas la reproduction pixel-pres de
    // chaque boite de dialogue annexe).

    [RelayCommand]
    private void Backup()
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"b1-config-{DateTime.Now:yyyy-MM-dd}.json",
            Filter = "JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;

        var namesObj = new JsonObject();
        foreach (var d in Droids) namesObj[d.Id.ToString()] = d.Name;

        var backup = new JsonObject
        {
            ["type"] = "b1-config-backup",
            ["version"] = 1,
            ["savedAt"] = DateTime.UtcNow.ToString("O"),
            ["volume"] = _protocol.LastVolume,
            ["freq"] = _protocol.LastFreq,
            ["amp"] = _protocol.LastAmp,
            ["speed"] = _protocol.LastSpeed,
            ["names"] = namesObj,
        };
        File.WriteAllText(dlg.FileName, backup.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show($"Sauvegarde enregistree : {dlg.FileName}", "Sauvegarde", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void Restore()
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;

        JsonObject? backup;
        try { backup = JsonNode.Parse(File.ReadAllText(dlg.FileName)) as JsonObject; }
        catch (Exception ex)
        {
            MessageBox.Show("Fichier illisible : " + ex.Message, "Restauration", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (backup == null) return;

        var confirm = MessageBox.Show(
            $"Restaurer la configuration depuis « {Path.GetFileName(dlg.FileName)} » ?\n\nLes noms, le volume et les parametres d'animation seront ecrases.",
            "Confirmer la restauration", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var ops = new JsonArray();
        if (backup["names"] is JsonObject names)
            foreach (var (idStr, nameNode) in names)
                if (ushort.TryParse(idStr, out var id) && nameNode != null)
                    ops.Add(new JsonObject { ["cmd"] = "name", ["id"] = id, ["name"] = nameNode.GetValue<string>() });

        if (backup.TryGetPropertyValue("volume", out var vol) && vol != null)
            ops.Add(new JsonObject { ["cmd"] = "volume", ["value"] = vol.GetValue<int>() });

        if (backup.TryGetPropertyValue("freq", out var freq) && backup.TryGetPropertyValue("amp", out var amp) && backup.TryGetPropertyValue("speed", out var speed))
            ops.Add(new JsonObject
            {
                ["cmd"] = "config", ["target"] = 0xFFFF,
                ["freq"] = freq!.GetValue<int>(), ["amp"] = amp!.GetValue<int>(), ["speed"] = speed!.GetValue<int>(),
            });

        if (_protocol.HasCap("setMulti"))
        {
            _protocol.SetMulti(ops);
        }
        else
        {
            // Ancien firmware : envoi espace au lieu d'un lot atomique.
            foreach (var op in ops)
            {
                if (op is not JsonObject o) continue;
                switch (o["cmd"]?.GetValue<string>())
                {
                    case "name": _protocol.SetName((ushort)o["id"]!.GetValue<int>(), o["name"]!.GetValue<string>()); break;
                    case "volume": _protocol.SetVolume(o["value"]!.GetValue<int>()); break;
                    case "config": _protocol.SetConfig(0xFFFF, o["freq"]!.GetValue<int>(), o["amp"]!.GetValue<int>(), o["speed"]!.GetValue<int>()); break;
                }
            }
        }
        if (_protocol.HasCap("commit")) _protocol.Commit();
    }
}
