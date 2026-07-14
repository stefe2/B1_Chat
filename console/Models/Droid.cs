using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

public partial class Droid : ObservableObject
{
    public ushort Id { get; init; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _editingName = "";
    [ObservableProperty] private int _rssi;
    [ObservableProperty] private bool _isMaster;
    [ObservableProperty] private bool _online;
    [ObservableProperty] private bool _servosOn;
    [ObservableProperty] private bool _autoAnimOn;
    [ObservableProperty] private bool _adopted = true;
    [ObservableProperty] private string? _portName;
    [ObservableProperty] private string _fwVersion = "";
    [ObservableProperty] private string? _latestFwVersion;
    [ObservableProperty] private DateTime _lastSeen = DateTime.MinValue;
    [ObservableProperty] private bool _otaInProgress;
    [ObservableProperty] private int _otaProgressPct;
    [ObservableProperty] private string _otaStatusText = "";

    public string RssiLabel => IsMaster ? (PortName ?? "local") : (Online ? $"{Rssi} dBm" : "-");
    public string IdHex => Id.ToString("X4");
    public string DisplayLabel => $"{Name} ({IdHex})";

    // Droïde jamais adopté : en attente de décision (Adopter/Ignorer) côté UI.
    public bool IsPending => !IsMaster && !Adopted;

    // Droïde adopté (esclave) : peut être retiré manuellement du registre à tout moment.
    public bool CanForget => !IsMaster && Adopted;

    // Idem, mais masqué pendant qu'une mise à jour OTA est en cours sur ce droïde
    // (remplacé par la barre de progression).
    public bool CanFlashOta => CanForget && !OtaInProgress;

    // null = pas encore verifie (ou version pas encore rapportee) -> couleur neutre.
    public bool? FwUpToDate => string.IsNullOrEmpty(LatestFwVersion) || string.IsNullOrEmpty(FwVersion)
        ? null
        : FwVersion == LatestFwVersion;

    partial void OnFwVersionChanged(string value) => OnPropertyChanged(nameof(FwUpToDate));
    partial void OnLatestFwVersionChanged(string? value) => OnPropertyChanged(nameof(FwUpToDate));

    partial void OnRssiChanged(int value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnPortNameChanged(string? value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnOnlineChanged(bool value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnIsMasterChanged(bool value)
    {
        OnPropertyChanged(nameof(RssiLabel));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(CanForget));
        OnPropertyChanged(nameof(CanFlashOta));
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));
    partial void OnAdoptedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(CanForget));
        OnPropertyChanged(nameof(CanFlashOta));
    }
    partial void OnOtaInProgressChanged(bool value) => OnPropertyChanged(nameof(CanFlashOta));
}
