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
    [ObservableProperty] private DateTime _lastSeen = DateTime.MinValue;

    public string RssiLabel => IsMaster ? "local" : $"{Rssi} dBm";
    public string IdHex => Id.ToString("X4");
    public string DisplayLabel => $"{Name} ({IdHex})";

    partial void OnRssiChanged(int value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnIsMasterChanged(bool value) => OnPropertyChanged(nameof(RssiLabel));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));
}
