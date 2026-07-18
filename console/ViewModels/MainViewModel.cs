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
    [ObservableProperty] private string _connectionStatusText = "Disconnected";

    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public string VersionSubtitle =>
        $"Supervision Console — v{AppVersion.Replace("+build.", " (build ")}{(AppVersion.Contains("+build.") ? ")" : "")}";

    public DroidsViewModel Droids { get; }
    public CalibrationViewModel Calibration { get; }
    public AnimationViewModel Animation { get; }
    public FirmwareViewModel Firmware { get; }
    public MeshTopologyViewModel Topology { get; }
    public SequencerViewModel Sequencer { get; }

    // Always visible once the firmware supports the commit/dirty model (regardless of
    // Dirty itself) — the badge now doubles as a passive "synced" status indicator
    // instead of only appearing to flag a pending auto-commit.
    public bool ShowSyncBadge => Protocol.HasCap("commit");

    // Console update (real version comparison) OR at least one droid actually behind the
    // latest published firmware (real per-droid comparison) — see FirmwareViewModel.HasAppUpdate
    // and DroidsViewModel.AnyFwUpdateAvailable for why neither can just be "a release exists".
    public bool HasAnyUpdateAvailable => Firmware.HasAppUpdate || Droids.AnyFwUpdateAvailable;

    public MainViewModel()
    {
        _settings = new SettingsService();
        _settings.Load();

        _link = new SerialLinkService();
        Protocol = new ProtocolClient(_link);

        Droids = new DroidsViewModel(Protocol);
        Calibration = new CalibrationViewModel(Protocol);
        Animation = new AnimationViewModel(Protocol);
        Firmware = new FirmwareViewModel(Protocol, _link);
        Topology = new MeshTopologyViewModel(Protocol);
        Sequencer = new SequencerViewModel(Protocol);

        _link.Opened += () => { Connected = true; ConnectionStatusText = "Connected — handshake…"; };
        _link.Closed += unexpected => { Connected = false; ConnectionStatusText = unexpected ? "Disconnected (unexpected) — reconnecting…" : "Disconnected"; };
        _link.OpenFailed += err => ConnectionStatusText = "Connection failed: " + err;
        Protocol.LinkError += err => ConnectionStatusText = "Serial port error: " + err;

        Protocol.HelloReceived += () =>
        {
            ConnectionStatusText = Protocol.SessionReady ? $"Connected — fw {Protocol.FwVersion ?? "?"}" : "Handshake failed";
            OnPropertyChanged(nameof(ShowSyncBadge));
        };

        Protocol.LogTx += line => AddLog(LogKind.Tx, "→ " + line);
        Protocol.LogRx += line => AddLog(LogKind.Rx, "← " + line);
        Protocol.LogSys += line => AddLog(LogKind.Sys, line);
        Protocol.LogErr += line => AddLog(LogKind.Err, line);

        // Single source of truth for "what's the latest GitHub firmware release":
        // whenever the shared Firmware view-model learns of one (at startup, or after a
        // manual refresh in the Firmware window), push it into the Droids card so its
        // per-droid version column/badge reflects it too, instead of each doing its own check.
        Firmware.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FirmwareViewModel.FwLatest)) Droids.UpdateLatestFwVersion(Firmware.FwLatest);
            if (e.PropertyName is nameof(FirmwareViewModel.HasAppUpdate)) OnPropertyChanged(nameof(HasAnyUpdateAvailable));
        };
        Droids.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DroidsViewModel.AnyFwUpdateAvailable)) OnPropertyChanged(nameof(HasAnyUpdateAvailable));
        };
        Firmware.CheckUpdatesCommand.Execute(null);

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
}
