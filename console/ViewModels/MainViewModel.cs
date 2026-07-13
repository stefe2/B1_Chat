using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ProtocolClient Protocol { get; }
    private readonly SerialLinkService _link;
    private readonly SettingsService _settings;

    public ObservableCollection<string> AvailablePorts { get; } = new();
    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    private const int LogMax = 300;

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _connectionStatusText = "Déconnecté";

    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public string VersionSubtitle =>
        $"Console de supervision — v{AppVersion.Replace("+build.", " (build ")}{(AppVersion.Contains("+build.") ? ")" : "")}";

    public DroidsViewModel Droids { get; }
    public CalibrationViewModel Calibration { get; }
    public AnimationViewModel Animation { get; }
    public AudioViewModel Audio { get; }
    public FirmwareViewModel Firmware { get; }
    public MeshTopologyViewModel Topology { get; }
    public SequencerViewModel Sequencer { get; }

    public bool ShowCommitUi => Protocol.Dirty && Protocol.HasCap("commit");

    public MainViewModel()
    {
        _settings = new SettingsService();
        _settings.Load();

        _link = new SerialLinkService();
        Protocol = new ProtocolClient(_link);

        Droids = new DroidsViewModel(Protocol);
        Calibration = new CalibrationViewModel(Protocol);
        Animation = new AnimationViewModel(Protocol);
        Audio = new AudioViewModel(Protocol);
        Firmware = new FirmwareViewModel(Protocol, _link);
        Topology = new MeshTopologyViewModel(Protocol);
        Sequencer = new SequencerViewModel(Protocol);

        _link.Opened += () => { Connected = true; ConnectionStatusText = "Connecté — handshake…"; };
        _link.Closed += unexpected => { Connected = false; ConnectionStatusText = unexpected ? "Déconnecté (inattendu) — reconnexion…" : "Déconnecté"; };
        _link.OpenFailed += err => ConnectionStatusText = "Échec de connexion : " + err;

        Protocol.HelloReceived += () =>
        {
            ConnectionStatusText = Protocol.SessionReady ? $"Connecté — fw {Protocol.FwVersion ?? "?"}" : "Handshake échoué";
            OnPropertyChanged(nameof(ShowCommitUi));
        };
        Protocol.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtocolClient.Dirty)) OnPropertyChanged(nameof(ShowCommitUi));
        };

        Protocol.LogTx += line => AddLog(LogKind.Tx, "→ " + line);
        Protocol.LogRx += line => AddLog(LogKind.Rx, "← " + line);
        Protocol.LogSys += line => AddLog(LogKind.Sys, line);
        Protocol.LogErr += line => AddLog(LogKind.Err, line);

        RefreshPorts();
        if (!string.IsNullOrEmpty(_settings.LastPort)) SelectedPort = _settings.LastPort;
    }

    private void AddLog(LogKind kind, string text)
    {
        LogEntries.Add(new LogEntry(kind, text));
        while (LogEntries.Count > LogMax) LogEntries.RemoveAt(0);
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var p in SerialLinkService.GetPortNames()) AvailablePorts.Add(p);
        if (SelectedPort == null && AvailablePorts.Count > 0) SelectedPort = AvailablePorts[0];
    }

    [RelayCommand]
    private void Connect()
    {
        if (string.IsNullOrEmpty(SelectedPort)) return;
        _settings.SetLastPort(SelectedPort);
        _link.Open(SelectedPort);
    }

    [RelayCommand]
    private void Disconnect() => _link.Close();

    [RelayCommand]
    private void CommitChanges() => Protocol.Commit();

    [RelayCommand]
    private void RevertChanges() => Protocol.Revert();
}
