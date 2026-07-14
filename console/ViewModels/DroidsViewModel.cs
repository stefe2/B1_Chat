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
    private readonly OtaService _ota;
    private readonly UpdateService _update = new();
    private Droid? _otaDroid;
    private string? _latestFwVersion;

    public ObservableCollection<Droid> Droids => _protocol.Droids;

    [ObservableProperty] private bool _anyOtaActive;

    // Le maitre se flashe toujours par USB (pas de cible OTA pour lui-meme) ; la fenetre
    // dediee (FirmwareWindow) vit dans MainWindow, hors de portee de cette vue -> evenement.
    public event Action? OpenFirmwareRequested;

    [RelayCommand]
    private void OpenFirmware() => OpenFirmwareRequested?.Invoke();

    public DroidsViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _ota = new OtaService(protocol);
        _ota.Progress += OnOtaProgress;
        _ota.Completed += OnOtaCompleted;
        // Sans ca, un chunk qui echoue a l'ecriture serie restait silencieux jusqu'a ce que le
        // maitre abandonne tout seul ~45s plus tard (timeout "console injoignable") : ici on le
        // sait tout de suite et on annule proprement des deux cotes plutot que d'attendre.
        _protocol.LinkError += OnLinkError;
        Droids.CollectionChanged += (_, e) =>
        {
            if (e.NewItems == null) return;
            foreach (Droid d in e.NewItems) d.LatestFwVersion = _latestFwVersion;
        };
        _ = RefreshLatestFwVersionAsync();
    }

    // Verifie la derniere release firmware GitHub (prefixe "fw-") au demarrage pour
    // colorer la colonne FW de chaque droide (vert = a jour, rouge = MAJ disponible).
    private async Task RefreshLatestFwVersionAsync()
    {
        var result = await _update.CheckUpdatesAsync();
        if (!result.Ok || string.IsNullOrEmpty(result.Fw.Latest)) return;
        _latestFwVersion = result.Fw.Latest;
        foreach (var d in Droids) d.LatestFwVersion = _latestFwVersion;
    }

    private void OnOtaProgress(int sent, int total)
    {
        if (_otaDroid == null) return;
        _otaDroid.OtaProgressPct = total > 0 ? (int)(100.0 * sent / total) : 0;
        _otaDroid.OtaStatusText = $"{sent}/{total} morceaux";
    }

    private void OnLinkError(string message)
    {
        if (_otaDroid == null) return;
        _flashToken = null;
        _ota.Abort(); // previent le maitre tout de suite au lieu de le laisser attendre le timeout
        var droid = _otaDroid;
        droid.OtaInProgress = false;
        droid.OtaStatusText = "Erreur d'écriture série : " + message;
        _otaDroid = null;
        AnyOtaActive = false;
    }

    private void OnOtaCompleted(bool ok, string message)
    {
        _flashToken = null;
        if (_otaDroid != null)
        {
            _otaDroid.OtaInProgress = false;
            _otaDroid.OtaStatusText = message;
        }
        _otaDroid = null;
        AnyOtaActive = false;
    }

    // Identifie la tentative de flash OTA en cours : si un appel async reprend (fin d'un
    // telechargement) alors que ce jeton a change entretemps (echec, annulation via
    // OnLinkError, etc.), on abandonne silencieusement plutot que de demarrer une session
    // OTA en double sur une tentative deja consideree terminee cote UI.
    private object? _flashToken;

    [RelayCommand]
    private async Task FlashOta(Droid? droid)
    {
        if (droid == null || AnyOtaActive) return;

        var confirm = MessageBox.Show(
            $"Mettre à jour le firmware de « {droid.Name} » vers la dernière version publiée par le mesh (sans USB) ?\n\n" +
            "Le droïde redémarrera automatiquement à la fin — ne pas l'éteindre pendant le transfert " +
            "(généralement 8 à 15 minutes, plus si la liaison est faible).",
            "Confirmer la mise à jour OTA", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var token = new object();
        _flashToken = token;
        _otaDroid = droid;
        droid.OtaInProgress = true;
        droid.OtaProgressPct = 0;
        droid.OtaStatusText = "vérification de la dernière version…";
        AnyOtaActive = true;

        var result = await _update.CheckUpdatesAsync();
        if (_flashToken != token) return;
        if (!result.Ok || string.IsNullOrEmpty(result.Fw.UrlSlave))
        {
            FailOta(droid, result.Ok ? "Aucun firmware esclave trouvé dans la dernière release GitHub." : result.Error);
            return;
        }

        droid.OtaStatusText = "téléchargement du firmware…";
        var (dlOk, path, _, _, dlError) = await _update.DownloadAssetAsync(result.Fw.UrlSlave, result.Fw.Sha256Slave);
        if (_flashToken != token) return;
        if (!dlOk || path == null)
        {
            FailOta(droid, dlError);
            return;
        }

        droid.OtaStatusText = "démarrage…";
        if (!_ota.Start(droid.Id, path, out var startError)) FailOta(droid, startError);
    }

    private void FailOta(Droid droid, string? error)
    {
        _flashToken = null;
        droid.OtaInProgress = false;
        droid.OtaStatusText = error ?? "erreur inconnue";
        _otaDroid = null;
        AnyOtaActive = false;
        MessageBox.Show(error ?? "Erreur inconnue", "OTA", MessageBoxButton.OK, MessageBoxImage.Error);
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

    [RelayCommand]
    private void AdoptDroid(Droid? droid)
    {
        if (droid == null) return;
        _protocol.Adopt(droid.Id);
    }

    [RelayCommand]
    private void ForgetDroid(Droid? droid)
    {
        if (droid == null) return;
        _protocol.Forget(droid.Id);
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
