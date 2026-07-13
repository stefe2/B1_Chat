using System.Collections.ObjectModel;
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

    public ObservableCollection<string> FlashLog { get; } = new();

    [ObservableProperty] private string? _binPath;
    [ObservableProperty] private string _address = "0x10000";
    [ObservableProperty] private bool _flashing;
    [ObservableProperty] private bool _canFlash;

    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string? _appLatest;
    [ObservableProperty] private string? _appDownloadUrl;
    [ObservableProperty] private string? _fwLatest;
    [ObservableProperty] private string? _fwUrlMaster;
    [ObservableProperty] private string? _fwUrlSlave;
    [ObservableProperty] private string? _fwShaMaster;
    [ObservableProperty] private string? _fwShaSlave;

    private static void RunOnUi(Action a)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) a(); else dispatcher.Invoke(a);
    }

    public FirmwareViewModel(ProtocolClient protocol, SerialLinkService link)
    {
        _protocol = protocol;
        _link = link;
        // FlashService leve ses evenements depuis les callbacks async de Process (thread pool) :
        // remarshalage sur l'UI avant de toucher les ObservableCollection/proprietes liees.
        _flash.LogLine += line => RunOnUi(() => FlashLog.Add(line));
        _flash.Completed += (ok, code, err) => RunOnUi(() => OnFlashCompleted(ok, code, err));
    }

    [RelayCommand]
    private void PickBin()
    {
        var dlg = new OpenFileDialog { Filter = "Image firmware (*.bin)|*.bin|Tous les fichiers (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        BinPath = dlg.FileName;
        CanFlash = true;
    }

    [RelayCommand]
    private void Flash()
    {
        if (string.IsNullOrEmpty(BinPath) || _link.PortName == null || Flashing) return;
        if (System.Windows.MessageBox.Show(
                $"Écrire « {System.IO.Path.GetFileName(BinPath)} » à {Address} sur {_link.PortName} ?\n\nLe firmware actuel sera remplacé. Ne débranche rien pendant l'opération.",
                "Confirmer le flash", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        Flashing = true;
        CanFlash = false;
        var port = _link.PortName;
        _link.PrepareForExternalClose();
        FlashLog.Add("— Flash démarré —");
        _flash.Start(BinPath, Address, port);
    }

    private void OnFlashCompleted(bool ok, int? exitCode, string? error)
    {
        Flashing = false;
        CanFlash = BinPath != null;
        FlashLog.Add(ok ? "— Flash terminé avec succès —" : $"— Échec du flash{(error != null ? " : " + error : $" (code {exitCode})")} —");
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        UpdateStatus = "Vérification en cours…";
        var result = await _update.CheckUpdatesAsync();
        if (!result.Ok)
        {
            UpdateStatus = "Erreur : " + result.Error;
            return;
        }
        UpdateStatus = "Vérifié.";
        AppLatest = result.App.Latest;
        AppDownloadUrl = result.App.Url;
        FwLatest = result.Fw.Latest;
        FwUrlMaster = result.Fw.UrlMaster;
        FwUrlSlave = result.Fw.UrlSlave;
        FwShaMaster = result.Fw.Sha256Master;
        FwShaSlave = result.Fw.Sha256Slave;
    }

    [RelayCommand]
    private async Task InstallAppUpdate()
    {
        if (string.IsNullOrEmpty(AppDownloadUrl)) return;
        var (ok, path, error) = await _update.DownloadAppInstallerAsync(AppDownloadUrl);
        if (!ok || path == null)
        {
            UpdateStatus = "Échec du téléchargement : " + error;
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    private async Task PrepareFirmwareAsync(string? url, string? sha256)
    {
        if (string.IsNullOrEmpty(url)) return;
        UpdateStatus = "Téléchargement du firmware…";
        var (ok, path, name, size, error) = await _update.DownloadAssetAsync(url, sha256);
        if (!ok || path == null)
        {
            UpdateStatus = "Échec : " + error;
            return;
        }
        BinPath = path;
        Address = "0x10000";
        CanFlash = true;
        UpdateStatus = $"{name} téléchargé, SHA-256 vérifié — prêt à flasher.";
    }

    [RelayCommand] private Task PrepareFirmwareMaster() => PrepareFirmwareAsync(FwUrlMaster, FwShaMaster);
    [RelayCommand] private Task PrepareFirmwareSlave() => PrepareFirmwareAsync(FwUrlSlave, FwShaSlave);
}
