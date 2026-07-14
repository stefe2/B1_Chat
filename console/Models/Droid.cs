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

    // Never-adopted droid: waiting on a UI decision (Adopt/Ignore).
    public bool IsPending => !IsMaster && !Adopted;

    // Adopted droid (slave): can be manually removed from the registry at any time.
    public bool CanForget => !IsMaster && Adopted;

    // null = not checked yet (or version not reported yet) -> neutral color.
    public bool? FwUpToDate => string.IsNullOrEmpty(LatestFwVersion) || string.IsNullOrEmpty(FwVersion)
        ? null
        : FwVersion == LatestFwVersion;

    // USB flash entry point (master only) : hidden once the master's own firmware is
    // confirmed up to date, since the header's "Firmware…" button remains available
    // as the manual/dev fallback regardless.
    public bool CanFlashUsb => IsMaster && FwUpToDate != true;

    // Also hidden while an OTA is already running (replaced by the progress bar) or
    // once the droid is confirmed up to date.
    public bool CanFlashOta => CanForget && !OtaInProgress && FwUpToDate != true;

    // Fills the flash-actions column with a status badge once both flash entry points
    // above are hidden because the droid is confirmed up to date.
    public bool ShowFwUpToDateBadge => !IsPending && !OtaInProgress && FwUpToDate == true;

    partial void OnFwVersionChanged(string value)
    {
        OnPropertyChanged(nameof(FwUpToDate));
        OnPropertyChanged(nameof(CanFlashUsb));
        OnPropertyChanged(nameof(CanFlashOta));
        OnPropertyChanged(nameof(ShowFwUpToDateBadge));
    }
    partial void OnLatestFwVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(FwUpToDate));
        OnPropertyChanged(nameof(CanFlashUsb));
        OnPropertyChanged(nameof(CanFlashOta));
        OnPropertyChanged(nameof(ShowFwUpToDateBadge));
    }

    partial void OnRssiChanged(int value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnPortNameChanged(string? value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnOnlineChanged(bool value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnIsMasterChanged(bool value)
    {
        OnPropertyChanged(nameof(RssiLabel));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(CanForget));
        OnPropertyChanged(nameof(CanFlashUsb));
        OnPropertyChanged(nameof(CanFlashOta));
        OnPropertyChanged(nameof(ShowFwUpToDateBadge));
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));
    partial void OnAdoptedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(CanForget));
        OnPropertyChanged(nameof(CanFlashOta));
        OnPropertyChanged(nameof(ShowFwUpToDateBadge));
    }
    partial void OnOtaInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(CanFlashOta));
        OnPropertyChanged(nameof(ShowFwUpToDateBadge));
    }
}
