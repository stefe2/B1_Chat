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

    // The master is always flashed over USB (no OTA target for itself); the dedicated
    // window (FirmwareWindow) lives in MainWindow, out of this view's reach -> event.
    public event Action? OpenFirmwareRequested;

    [RelayCommand]
    private void OpenFirmware() => OpenFirmwareRequested?.Invoke();

    public DroidsViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _ota = new OtaService(protocol);
        _ota.Progress += OnOtaProgress;
        _ota.Completed += OnOtaCompleted;
        _ota.Retrying += (index, attempt) =>
        {
            if (_otaDroid != null)
                _otaDroid.OtaStatusText = $"chunk {index} no response, attempt {attempt}…";
        };
        // Without this, a chunk that fails to write to the serial port stayed silent until the
        // master gave up on its own ~45s later (the "console unreachable" timeout): here we know
        // right away and cancel cleanly on both sides instead of waiting it out.
        _protocol.LinkError += OnLinkError;
        _protocol.LinkClosed += unexpected =>
        {
            if (_otaDroid != null) OnLinkError(unexpected ? "serial link dropped" : "serial link closed");
        };
        Droids.CollectionChanged += (_, e) =>
        {
            if (e.NewItems == null) return;
            foreach (Droid d in e.NewItems) d.LatestFwVersion = _latestFwVersion;
        };
    }

    // Called by MainViewModel whenever the shared FirmwareViewModel learns of a new
    // GitHub release (at startup, or after a manual refresh in the Firmware window) —
    // colors each droid's version column (green = up to date, red = update available).
    public void UpdateLatestFwVersion(string? latest)
    {
        _latestFwVersion = latest;
        foreach (var d in Droids) d.LatestFwVersion = latest;
    }

    private void OnOtaProgress(int sent, int total)
    {
        if (_otaDroid == null) return;
        _otaDroid.OtaProgressPct = total > 0 ? (int)(100.0 * sent / total) : 0;
        _otaDroid.OtaStatusText = $"{sent}/{total} chunks";
    }

    private void OnLinkError(string message)
    {
        if (_otaDroid == null) return;
        _flashToken = null;
        _ota.Abort(); // notifies the master right away instead of letting it wait for the timeout
        var droid = _otaDroid;
        droid.OtaInProgress = false;
        droid.OtaStatusText = "Serial link error: " + message;
        _otaDroid = null;
        AnyOtaActive = false;
        MessageBox.Show("OTA update interrupted — " + message, "OTA — " + droid.Name,
                        MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnOtaCompleted(bool ok, string message)
    {
        _flashToken = null;
        var droid = _otaDroid;
        if (droid != null)
        {
            droid.OtaInProgress = false;
            droid.OtaStatusText = message;
        }
        _otaDroid = null;
        AnyOtaActive = false;
        // The status text is hidden as soon as OtaInProgress drops: without this dialog,
        // the failure reason pushed by the master (timeout, chunk, rolledBack...)
        // disappears before it can be read.
        if (!ok)
            MessageBox.Show(message, "OTA — " + (droid?.Name ?? "?"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // Identifies the in-flight OTA flash attempt: if an async call resumes (a download
    // finishes) after this token has changed in the meantime (failure, cancellation via
    // OnLinkError, etc.), it bails out silently instead of starting a duplicate OTA
    // session on an attempt the UI already considers finished.
    private object? _flashToken;

    [RelayCommand]
    private async Task FlashOta(Droid? droid)
    {
        if (droid == null || AnyOtaActive) return;

        var confirm = MessageBox.Show(
            $"Update the firmware of « {droid.Name} » to the latest version published over the mesh (no USB)?\n\n" +
            "The droid will reboot automatically once done — do not power it off during the transfer " +
            "(typically 8 to 15 minutes, longer over a weak link).",
            "Confirm OTA update", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var token = new object();
        _flashToken = token;
        _otaDroid = droid;
        droid.OtaInProgress = true;
        droid.OtaProgressPct = 0;
        droid.OtaStatusText = "checking the latest version…";
        AnyOtaActive = true;

        var result = await _update.CheckUpdatesAsync();
        if (_flashToken != token) return;
        if (!result.Ok || string.IsNullOrEmpty(result.Fw.UrlSlave))
        {
            FailOta(droid, result.Ok ? "No slave firmware found in the latest GitHub release." : result.Error);
            return;
        }

        droid.OtaStatusText = "downloading firmware…";
        var (dlOk, path, _, _, dlError) = await _update.DownloadAssetAsync(result.Fw.UrlSlave, result.Fw.Sha256Slave);
        if (_flashToken != token) return;
        if (!dlOk || path == null)
        {
            FailOta(droid, dlError);
            return;
        }

        droid.OtaStatusText = "starting…";
        if (!_ota.Start(droid.Id, path, out var startError)) FailOta(droid, startError);
    }

    private void FailOta(Droid droid, string? error)
    {
        _flashToken = null;
        droid.OtaInProgress = false;
        droid.OtaStatusText = error ?? "unknown error";
        _otaDroid = null;
        AnyOtaActive = false;
        MessageBox.Show(error ?? "Unknown error", "OTA", MessageBoxButton.OK, MessageBoxImage.Error);
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
    private void ToggleLocate(Droid? droid)
    {
        if (droid == null) return;
        droid.LocateOn = !droid.LocateOn;
        _protocol.SetLocate(droid.Id, droid.LocateOn);
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

    // --- Backup / restore --------------------------------------------
    // Simplified compared to index.html: no dedicated diff window, just a
    // confirmation MessageBox before restoring (the roadmap emphasized a
    // functional port of the 8 cards, not a pixel-perfect reproduction of
    // every secondary dialog).

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
            ["freq"] = _protocol.LastFreq,
            ["amp"] = _protocol.LastAmp,
            ["speed"] = _protocol.LastSpeed,
            ["names"] = namesObj,
        };
        File.WriteAllText(dlg.FileName, backup.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        MessageBox.Show($"Backup saved: {dlg.FileName}", "Backup", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show("Unreadable file: " + ex.Message, "Restore", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (backup == null) return;

        var confirm = MessageBox.Show(
            $"Restore the configuration from « {Path.GetFileName(dlg.FileName)} »?\n\nNames and animation settings will be overwritten.",
            "Confirm restore", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var ops = new JsonArray();
        if (backup["names"] is JsonObject names)
            foreach (var (idStr, nameNode) in names)
                if (ushort.TryParse(idStr, out var id) && nameNode != null)
                    ops.Add(new JsonObject { ["cmd"] = "name", ["id"] = id, ["name"] = nameNode.GetValue<string>() });

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
            // Older firmware: send one by one instead of an atomic batch.
            foreach (var op in ops)
            {
                if (op is not JsonObject o) continue;
                switch (o["cmd"]?.GetValue<string>())
                {
                    case "name": _protocol.SetName((ushort)o["id"]!.GetValue<int>(), o["name"]!.GetValue<string>()); break;
                    case "config": _protocol.SetConfig(0xFFFF, o["freq"]!.GetValue<int>(), o["amp"]!.GetValue<int>(), o["speed"]!.GetValue<int>()); break;
                }
            }
        }
        if (_protocol.HasCap("commit")) _protocol.Commit();
    }
}
