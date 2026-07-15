using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Services;
using Microsoft.Win32;

namespace b1_chat_console.ViewModels;

public partial class FirmwareViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;
    private readonly SerialLinkService _link;
    private readonly FlashService _flash = new();
    private readonly UpdateService _update = new();
    private readonly System.Threading.Timer _portScanTimer;
    private string? _reconnectPortAfterFlash;

    public ObservableCollection<string> FlashLog { get; } = new();
    public ObservableCollection<string> AvailablePorts { get; } = new();

    [ObservableProperty] private string? _selectedFlashPort;
    [ObservableProperty] private string? _binPath;
    [ObservableProperty] private bool _binVerified;
    // Role-independent support images (bootloader at 0x1000, partition table at 0x8000), used
    // ONLY when EraseChipFirst is checked (see SupportImagesAvailable/Flash()) — a full chip
    // erase wipes the bootloader/partition table too, so without these the board would be left
    // unable to boot at all. Otherwise the flash is always app-only, which is what keeps a
    // board's saved settings (NVS) intact — see CLAUDE.md, "Full flash" pitfall (2026-07-15):
    // silently rewriting the partition table on a board that DIDN'T need it once corrupted a
    // droid's NVS (a partition-table generation mismatch shifted the NVS window, resurrecting a
    // years-old droid name). Tying the two together removes that failure mode entirely: the
    // only path that touches the partition table is the one that already discards NVS anyway.
    [ObservableProperty] private string? _bootloaderPath;
    [ObservableProperty] private string? _partitionsPath;
    [ObservableProperty] private string _address = "0x10000";
    [ObservableProperty] private bool _flashing;
    [ObservableProperty] private bool _canFlash;
    [ObservableProperty] private int _flashProgressPct;
    // espflash only prints its progress bar (%) if it detects a real terminal; here its
    // output is redirected (Process.RedirectStandardOutput/Error), so in practice no
    // progress line ever arrives -> indeterminate bar by default, unless a percentage can
    // still be read (see FlashService.Progress).
    [ObservableProperty] private bool _flashProgressIndeterminate = true;
    [ObservableProperty] private bool _isMasterRole = true;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private bool _eraseChipFirst;

    public bool IsSlaveRole => !IsMasterRole;
    public string RoleLabel => IsMasterRole ? "MASTER" : "SLAVE";
    public string FlashLabel => $"Flash {RoleLabel}";
    // Whether the bootloader/partition-table images are on hand — availability only, NOT a
    // decision to write them (that's EraseChipFirst's job, see Flash()).
    public bool SupportImagesAvailable => BootloaderPath != null && PartitionsPath != null;
    // Checking "erase" without the support images would wipe the board's bootloader/partition
    // table with nothing to replace them — shown as an inline warning, blocked at Flash() time.
    public bool NeedsSupportImagesWarning => EraseChipFirst && !SupportImagesAvailable;
    public string ReadyStatus => BinPath == null
        ? "No binary loaded."
        : System.IO.Path.GetFileName(BinPath)
          + (BinVerified ? " — SHA-256 verified ✓" : " — unverified (local file)")
          + (SupportImagesAvailable ? " · bootloader + partitions available (for a full erase+flash)" : " · app-only");

    partial void OnIsMasterRoleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSlaveRole));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(FlashLabel));
    }

    partial void OnBinPathChanged(string? value) => OnPropertyChanged(nameof(ReadyStatus));
    partial void OnBinVerifiedChanged(bool value) => OnPropertyChanged(nameof(ReadyStatus));
    partial void OnBootloaderPathChanged(string? value)
    {
        OnPropertyChanged(nameof(SupportImagesAvailable));
        OnPropertyChanged(nameof(NeedsSupportImagesWarning));
        OnPropertyChanged(nameof(ReadyStatus));
    }
    partial void OnPartitionsPathChanged(string? value)
    {
        OnPropertyChanged(nameof(SupportImagesAvailable));
        OnPropertyChanged(nameof(NeedsSupportImagesWarning));
        OnPropertyChanged(nameof(ReadyStatus));
    }
    partial void OnEraseChipFirstChanged(bool value) => OnPropertyChanged(nameof(NeedsSupportImagesWarning));

    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string? _appLatest;
    [ObservableProperty] private string? _appDownloadUrl;
    [ObservableProperty] private string? _fwLatest;
    [ObservableProperty] private string? _fwUrlMaster;
    [ObservableProperty] private string? _fwUrlSlave;
    [ObservableProperty] private string? _fwShaMaster;
    [ObservableProperty] private string? _fwShaSlave;
    [ObservableProperty] private string? _fwUrlBootloader;
    [ObservableProperty] private string? _fwUrlPartitions;
    [ObservableProperty] private string? _fwShaBootloader;
    [ObservableProperty] private string? _fwShaPartitions;

    public bool HasAppUpdate => !string.IsNullOrEmpty(AppLatest);
    public bool HasFwUpdate => !string.IsNullOrEmpty(FwLatest);
    partial void OnAppLatestChanged(string? value) => OnPropertyChanged(nameof(HasAppUpdate));
    partial void OnFwLatestChanged(string? value) => OnPropertyChanged(nameof(HasFwUpdate));

    private static void RunOnUi(Action a)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) a(); else dispatcher.Invoke(a);
    }

    public FirmwareViewModel(ProtocolClient protocol, SerialLinkService link)
    {
        _protocol = protocol;
        _link = link;
        // FlashService raises its events from Process's async callbacks (thread pool):
        // remarshal onto the UI before touching bound ObservableCollections/properties.
        _flash.LogLine += line => RunOnUi(() => FlashLog.Add(line));
        _flash.Progress += pct => RunOnUi(() =>
        {
            FlashProgressIndeterminate = false;
            FlashProgressPct = pct;
        });
        _flash.Completed += (ok, code, err) => RunOnUi(() => OnFlashCompleted(ok, code, err));
        RefreshFlashPorts();
        // Detects a board being plugged/unplugged (e.g. a second droid) with no manual action;
        // diff-based in RefreshFlashPorts so the ComboBox isn't disturbed if nothing changed.
        _portScanTimer = new System.Threading.Timer(_ => RunOnUi(RefreshFlashPorts), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    [RelayCommand] private void SelectMasterRole() => IsMasterRole = true;
    [RelayCommand] private void SelectSlaveRole() => IsMasterRole = false;
    [RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    /// <summary>
    /// Port scan independent of SerialLinkService: the Firmware window doesn't need to be
    /// connected (nor even to a board that already speaks the JSON protocol) to flash.
    /// Preselects the port currently connected in the main window for convenience,
    /// without ever depending on it.
    /// </summary>
    [RelayCommand]
    private void RefreshFlashPorts()
    {
        var ports = SerialLinkService.GetPortNames();
        if (!ports.OrderBy(p => p).SequenceEqual(AvailablePorts.OrderBy(p => p)))
        {
            AvailablePorts.Clear();
            foreach (var p in ports) AvailablePorts.Add(p);
        }
        if (string.IsNullOrEmpty(SelectedFlashPort) || !AvailablePorts.Contains(SelectedFlashPort))
            SelectedFlashPort = _link.PortName ?? (AvailablePorts.Count > 0 ? AvailablePorts[0] : null);
    }

    [RelayCommand]
    private void PickBin()
    {
        var dlg = new OpenFileDialog { Filter = "Firmware image (*.bin)|*.bin|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        BinPath = dlg.FileName;
        BinVerified = false;
        CanFlash = true;
        DetectSupportImagesBeside(dlg.FileName);
    }

    // A pio build drops bootloader.bin + partitions.bin next to firmware.bin: if both are there,
    // make them available for a full erase+flash (see EraseChipFirst/Flash()) — not armed on
    // their own, since writing them on a board that didn't need it risks the NVS-corruption
    // pitfall documented in CLAUDE.md.
    private void DetectSupportImagesBeside(string binPath)
    {
        var dir = System.IO.Path.GetDirectoryName(binPath);
        var boot = dir != null ? System.IO.Path.Combine(dir, "bootloader.bin") : null;
        var part = dir != null ? System.IO.Path.Combine(dir, "partitions.bin") : null;
        var found = boot != null && part != null && System.IO.File.Exists(boot) && System.IO.File.Exists(part);
        BootloaderPath = found ? boot : null;
        PartitionsPath = found ? part : null;
        if (found) FlashLog.Add("Found bootloader.bin + partitions.bin beside the binary — check \"New/erased board\" below for a full flash.");
    }

    [RelayCommand]
    private void Flash()
    {
        if (string.IsNullOrEmpty(BinPath) || Flashing) return;
        if (string.IsNullOrEmpty(SelectedFlashPort))
        {
            System.Windows.MessageBox.Show("First choose a serial port for the flash (the \"Flash port\" list above).",
                "No port selected", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // Erase without the support images would wipe the bootloader/partition table with
        // nothing to replace them — the board would never boot again. Block before confirming.
        if (EraseChipFirst && !SupportImagesAvailable)
        {
            System.Windows.MessageBox.Show(
                "A full chip erase also wipes the bootloader and partition table — without " +
                "bootloader.bin + partitions.bin to rewrite them, the board would be left unable to boot at all.\n\n" +
                "Pick a firmware.bin from a folder that also has bootloader.bin + partitions.bin " +
                "(a plain \"pio run\" build output, e.g. .pio\\build\\b1\\, has all three), or use " +
                "\"From GitHub\" — recent releases ship them.",
                "Missing bootloader/partitions", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }

        var port = SelectedFlashPort;
        var what = EraseChipFirst
            ? $"bootloader + partition table + {RoleLabel} firmware « {System.IO.Path.GetFileName(BinPath)} » (full flash)"
            : $"the {RoleLabel} firmware « {System.IO.Path.GetFileName(BinPath)} » at {Address}";
        var message = $"Write {what} on {port}?\n\nThe current firmware will be replaced. Do not unplug anything during the operation.";
        if (EraseChipFirst)
            message = $"⚠ FULL CHIP ERASE, then writing {what} on {port}.\n\n" +
                       "All settings saved on THIS droid (name, servo calibration, and if master: volume, sequences, anim parameters) will be permanently lost — not just the firmware.\n\n" +
                       "Continue?";

        if (System.Windows.MessageBox.Show(message, "Confirm flash", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        Flashing = true;
        CanFlash = false;
        FlashProgressPct = 0;
        FlashProgressIndeterminate = true;
        // The flash port is independent of the main serial link; it's only released if it
        // happens to be exactly the one already opened by SerialLinkService (same board). It
        // will be reopened ourselves once the flash finishes (PrepareForExternalClose doesn't
        // raise the Closed event, so MainViewModel.Connected would otherwise stay stuck at true).
        _reconnectPortAfterFlash = null;
        if (_link.IsOpen && _link.PortName == port)
        {
            _reconnectPortAfterFlash = port;
            _link.PrepareForExternalClose();
        }
        FlashLog.Add(EraseChipFirst ? $"— Full erase then flash started on {port} —" : $"— Flash started on {port} —");
        if (EraseChipFirst)
        {
            // Guaranteed SupportImagesAvailable at this point (checked above). App always at
            // 0x10000 for a full flash — the advanced Address field only applies to app-only.
            _flash.Start(new[]
            {
                new FlashService.FlashImage("0x1000", BootloaderPath!),
                new FlashService.FlashImage("0x8000", PartitionsPath!),
                new FlashService.FlashImage("0x10000", BinPath),
            }, port, EraseChipFirst);
        }
        else
        {
            _flash.Start(BinPath, Address, port, EraseChipFirst);
        }
    }

    private void OnFlashCompleted(bool ok, int? exitCode, string? error)
    {
        Flashing = false;
        CanFlash = BinPath != null;
        if (ok) { FlashProgressPct = 100; FlashProgressIndeterminate = false; }
        FlashLog.Add(ok ? "— Flash completed successfully —" : $"— Flash failed{(error != null ? ": " + error : $" (code {exitCode})")} —");

        if (_reconnectPortAfterFlash != null)
        {
            var port = _reconnectPortAfterFlash;
            _reconnectPortAfterFlash = null;
            FlashLog.Add($"— Reconnecting to {port} —");
            _link.Open(port);
        }
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        UpdateStatus = "Checking…";
        var (ok, error) = await RunCheckUpdatesAsync();
        UpdateStatus = ok ? "Checked." : "Error: " + error;
    }

    private async Task<(bool Ok, string? Error)> RunCheckUpdatesAsync()
    {
        var result = await _update.CheckUpdatesAsync();
        if (!result.Ok) return (false, result.Error);
        AppLatest = result.App.Latest;
        AppDownloadUrl = result.App.Url;
        FwLatest = result.Fw.Latest;
        FwUrlMaster = result.Fw.UrlMaster;
        FwUrlSlave = result.Fw.UrlSlave;
        FwShaMaster = result.Fw.Sha256Master;
        FwShaSlave = result.Fw.Sha256Slave;
        FwUrlBootloader = result.Fw.UrlBootloader;
        FwUrlPartitions = result.Fw.UrlPartitions;
        FwShaBootloader = result.Fw.Sha256Bootloader;
        FwShaPartitions = result.Fw.Sha256Partitions;
        return (true, null);
    }

    [RelayCommand]
    private async Task InstallAppUpdate()
    {
        if (string.IsNullOrEmpty(AppDownloadUrl)) return;
        var (ok, path, error) = await _update.DownloadAppInstallerAsync(AppDownloadUrl);
        if (!ok || path == null)
        {
            UpdateStatus = "Download failed: " + error;
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    private async Task PrepareFirmwareAsync(string? url, string? sha256)
    {
        if (string.IsNullOrEmpty(url)) return;
        UpdateStatus = "Downloading firmware…";
        var (ok, path, name, size, error) = await _update.DownloadAssetAsync(url, sha256);
        if (!ok || path == null)
        {
            UpdateStatus = "Failed: " + error;
            return;
        }
        BinPath = path;
        BinVerified = true;
        Address = "0x10000";
        CanFlash = true;

        // Also fetch the shared bootloader + partition table, made available for a full
        // erase+flash (see EraseChipFirst/Flash()) if the user needs to bring up a virgin
        // board. Absent from older releases → app-only remains the only option.
        BootloaderPath = null;
        PartitionsPath = null;
        if (!string.IsNullOrEmpty(FwUrlBootloader) && !string.IsNullOrEmpty(FwUrlPartitions))
        {
            UpdateStatus = "Downloading bootloader + partition table…";
            var (bok, bpath, _, _, berr) = await _update.DownloadAssetAsync(FwUrlBootloader, FwShaBootloader);
            var (pok, ppath, _, _, perr) = await _update.DownloadAssetAsync(FwUrlPartitions, FwShaPartitions);
            if (bok && pok) { BootloaderPath = bpath; PartitionsPath = ppath; }
            else { UpdateStatus = $"Bootloader/partitions download failed ({berr ?? perr}) — app-only flash."; return; }
        }

        UpdateStatus = SupportImagesAvailable
            ? $"{name} + bootloader + partitions downloaded, verified — check \"New/erased board\" below for a virgin-board flash."
            : $"{name} downloaded, SHA-256 verified — ready to flash (app-only).";
    }

    [RelayCommand]
    private async Task PrepareFromGithub()
    {
        UpdateStatus = "Checking for updates…";
        var (ok, error) = await RunCheckUpdatesAsync();
        if (!ok)
        {
            UpdateStatus = "Error: " + error;
            return;
        }

        var url = IsMasterRole ? FwUrlMaster : FwUrlSlave;
        var sha = IsMasterRole ? FwShaMaster : FwShaSlave;
        if (string.IsNullOrEmpty(url))
        {
            UpdateStatus = $"No {RoleLabel} firmware found in the latest GitHub release.";
            return;
        }
        await PrepareFirmwareAsync(url, sha);
    }
}
