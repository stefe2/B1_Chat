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

    public ObservableCollection<string> FlashLog { get; } = new();
    public ObservableCollection<string> AvailablePorts { get; } = new();

    [ObservableProperty] private string? _selectedFlashPort;
    [ObservableProperty] private string? _binPath;
    [ObservableProperty] private bool _binVerified;
    [ObservableProperty] private string _address = "0x10000";
    [ObservableProperty] private bool _flashing;
    [ObservableProperty] private bool _canFlash;
    [ObservableProperty] private bool _isMasterRole = true;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private bool _eraseChipFirst;

    public bool IsSlaveRole => !IsMasterRole;
    public string RoleLabel => IsMasterRole ? "MAÎTRE" : "ESCLAVE";
    public string FlashLabel => $"Flasher {RoleLabel}";
    public string ReadyStatus => BinPath == null
        ? "Aucun binaire chargé."
        : System.IO.Path.GetFileName(BinPath) + (BinVerified ? " — SHA-256 vérifié ✓" : " — non vérifié (fichier local)");

    partial void OnIsMasterRoleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSlaveRole));
        OnPropertyChanged(nameof(RoleLabel));
        OnPropertyChanged(nameof(FlashLabel));
    }

    partial void OnBinPathChanged(string? value) => OnPropertyChanged(nameof(ReadyStatus));
    partial void OnBinVerifiedChanged(bool value) => OnPropertyChanged(nameof(ReadyStatus));

    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string? _appLatest;
    [ObservableProperty] private string? _appDownloadUrl;
    [ObservableProperty] private string? _fwLatest;
    [ObservableProperty] private string? _fwUrlMaster;
    [ObservableProperty] private string? _fwUrlSlave;
    [ObservableProperty] private string? _fwShaMaster;
    [ObservableProperty] private string? _fwShaSlave;

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
        // FlashService leve ses evenements depuis les callbacks async de Process (thread pool) :
        // remarshalage sur l'UI avant de toucher les ObservableCollection/proprietes liees.
        _flash.LogLine += line => RunOnUi(() => FlashLog.Add(line));
        _flash.Completed += (ok, code, err) => RunOnUi(() => OnFlashCompleted(ok, code, err));
        RefreshFlashPorts();
        // Detecte une carte branchee/debranchee (ex. un deuxieme droide) sans action manuelle ;
        // diff-based dans RefreshFlashPorts pour ne pas perturber le ComboBox si rien ne change.
        _portScanTimer = new System.Threading.Timer(_ => RunOnUi(RefreshFlashPorts), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    [RelayCommand] private void SelectMasterRole() => IsMasterRole = true;
    [RelayCommand] private void SelectSlaveRole() => IsMasterRole = false;
    [RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    /// <summary>
    /// Scan de ports independant de SerialLinkService : la fenetre Firmware n'a pas besoin
    /// d'etre connectee (ni meme d'une carte qui parle deja le protocole JSON) pour flasher.
    /// Presélectionne le port actuellement connecte dans la fenetre principale par confort,
    /// sans jamais en dependre.
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
        var dlg = new OpenFileDialog { Filter = "Image firmware (*.bin)|*.bin|Tous les fichiers (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return;
        BinPath = dlg.FileName;
        BinVerified = false;
        CanFlash = true;
    }

    [RelayCommand]
    private void Flash()
    {
        if (string.IsNullOrEmpty(BinPath) || Flashing) return;
        if (string.IsNullOrEmpty(SelectedFlashPort))
        {
            System.Windows.MessageBox.Show("Choisis d'abord un port série pour le flash (liste « Port de flash » ci-dessus).",
                "Aucun port sélectionné", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var port = SelectedFlashPort;
        var message = $"Écrire le firmware {RoleLabel} « {System.IO.Path.GetFileName(BinPath)} » à {Address} sur {port} ?\n\nLe firmware actuel sera remplacé. Ne débranche rien pendant l'opération.";
        if (EraseChipFirst)
            message = $"⚠ EFFACEMENT COMPLET DE LA PUCE, puis écriture du firmware {RoleLabel} « {System.IO.Path.GetFileName(BinPath)} » sur {port}.\n\n" +
                       "Tous les réglages sauvegardés sur CE droïde (nom, calibration servo, et si maître : volume, séquences, paramètres d'anim) seront perdus définitivement — pas seulement le firmware.\n\n" +
                       "Continuer ?";

        if (System.Windows.MessageBox.Show(message, "Confirmer le flash", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        Flashing = true;
        CanFlash = false;
        // Le port de flash est independant du lien serie principal ; on ne le libere que s'il
        // se trouve etre exactement celui deja ouvert par SerialLinkService (meme carte).
        if (_link.IsOpen && _link.PortName == port) _link.PrepareForExternalClose();
        FlashLog.Add(EraseChipFirst ? $"— Effacement complet puis flash démarrés sur {port} —" : $"— Flash démarré sur {port} —");
        _flash.Start(BinPath, Address, port, EraseChipFirst);
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
        var (ok, error) = await RunCheckUpdatesAsync();
        UpdateStatus = ok ? "Vérifié." : "Erreur : " + error;
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
        return (true, null);
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
        BinVerified = true;
        Address = "0x10000";
        CanFlash = true;
        UpdateStatus = $"{name} téléchargé, SHA-256 vérifié — prêt à flasher.";
    }

    [RelayCommand]
    private async Task PrepareFromGithub()
    {
        UpdateStatus = "Vérification des mises à jour…";
        var (ok, error) = await RunCheckUpdatesAsync();
        if (!ok)
        {
            UpdateStatus = "Erreur : " + error;
            return;
        }

        var url = IsMasterRole ? FwUrlMaster : FwUrlSlave;
        var sha = IsMasterRole ? FwShaMaster : FwShaSlave;
        if (string.IsNullOrEmpty(url))
        {
            UpdateStatus = $"Aucun firmware {RoleLabel} trouvé dans la dernière release GitHub.";
            return;
        }
        await PrepareFirmwareAsync(url, sha);
    }
}
