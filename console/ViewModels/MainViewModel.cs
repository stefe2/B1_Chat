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

    // Retries the last known port every 3s until it reappears (droid fleet powered on after
    // the console itself) — stopped as soon as any connection succeeds or the user manually
    // disconnects, same "don't fight the user" reflex as SerialLinkService's own reconnect loop.
    private System.Threading.Timer? _startupConnectTimer;
    private CancellationTokenSource? _fleetUpdateOfferDebounce;
    private string? _pendingFleetUpdateFingerprint;
    private string? _evaluatedFleetUpdateFingerprint;
    private bool _fleetUpdateOfferShown;

    [ObservableProperty] private string? _selectedPort;
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _connectionStatusText = "Disconnected";

    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public string VersionSubtitle =>
        $"Supervision Console — v{AppVersion.Replace("+build.", " (build ")}{(AppVersion.Contains("+build.") ? ")" : "")}";

    public DroidsViewModel Droids { get; }
    public CalibrationViewModel Calibration { get; }
    public FirmwareViewModel Firmware { get; }
    public MeshTopologyViewModel Topology { get; }
    public SequencerViewModel Sequencer { get; }

    public event Action<FleetUpdateViewModel>? FleetUpdatePromptRequested;

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
        Firmware = new FirmwareViewModel(Protocol, _link);
        Topology = new MeshTopologyViewModel(Protocol);
        Sequencer = new SequencerViewModel(Protocol, _settings);
        Sequencer.TryLoadLastSequence();

        _link.Opened += () => { Connected = true; ConnectionStatusText = "Connected — handshake…"; StopStartupConnectTimer(); };
        _link.Closed += unexpected => { Connected = false; ConnectionStatusText = unexpected ? "Disconnected (unexpected) — reconnecting…" : "Disconnected"; };
        _link.OpenFailed += err => ConnectionStatusText = "Connection failed: " + err;
        Protocol.LinkError += err => ConnectionStatusText = "Serial port error: " + err;

        Protocol.HelloReceived += () =>
        {
            var build = string.IsNullOrWhiteSpace(Protocol.FwBuildId) ? "" : $" · build {Protocol.FwBuildId}";
            ConnectionStatusText = Protocol.SessionReady ? $"Connected — fw {Protocol.FwVersion ?? "?"}{build}" : "Handshake failed";
            OnPropertyChanged(nameof(ShowSyncBadge));
            ScheduleFleetUpdateOffer();
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
            if (e.PropertyName is nameof(FirmwareViewModel.Flashing)) ScheduleFleetUpdateOffer();
        };
        Firmware.FirmwareCatalogUpdated += ScheduleFleetUpdateOffer;
        Protocol.DroidsChanged += ScheduleFleetUpdateOffer;
        Droids.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DroidsViewModel.AnyFwUpdateAvailable))
            {
                OnPropertyChanged(nameof(HasAnyUpdateAvailable));
                ScheduleFleetUpdateOffer();
            }
        };
        Sequencer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SequencerViewModel.TransportState)) ScheduleFleetUpdateOffer();
        };
        Firmware.CheckUpdatesCommand.Execute(null);

        RefreshPorts();
        if (!string.IsNullOrEmpty(_settings.LastPort))
        {
            SelectedPort = _settings.LastPort;
            TryAutoConnect();
            _startupConnectTimer = new System.Threading.Timer(_ =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(TryAutoConnect),
                null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }
    }

    private void ScheduleFleetUpdateOffer()
    {
        if (_fleetUpdateOfferShown || !Protocol.SessionReady ||
            Firmware.LatestFirmwareInfo is not { Latest.Length: > 0 } ||
            Droids.AnyOtaActive || Firmware.Flashing ||
            Sequencer.TransportState != SequencerTransportState.Stopped)
        {
            CancelPendingFleetUpdateOffer();
            return;
        }

        var fingerprint = BuildFleetUpdateFingerprint(Protocol.Droids, Firmware.LatestFirmwareInfo);
        // The master republishes the roster about every 1.5 s. Do not restart the 2.5 s
        // stabilization delay for an identical semantic snapshot (RSSI/age telemetry is
        // intentionally absent from the fingerprint), or the prompt can never appear.
        if (string.Equals(_pendingFleetUpdateFingerprint, fingerprint, StringComparison.Ordinal) ||
            string.Equals(_evaluatedFleetUpdateFingerprint, fingerprint, StringComparison.Ordinal))
            return;

        CancelPendingFleetUpdateOffer();

        var debounce = new CancellationTokenSource();
        _fleetUpdateOfferDebounce = debounce;
        _pendingFleetUpdateFingerprint = fingerprint;
        _ = OfferFleetUpdateAfterRosterSettlesAsync(debounce, fingerprint);
    }

    private void CancelPendingFleetUpdateOffer()
    {
        _fleetUpdateOfferDebounce?.Cancel();
        _fleetUpdateOfferDebounce?.Dispose();
        _fleetUpdateOfferDebounce = null;
        _pendingFleetUpdateFingerprint = null;
    }

    internal static string BuildFleetUpdateFingerprint(
        IEnumerable<Droid> droids,
        FirmwareUpdateInfo firmware) =>
        $"{firmware.Latest}|{firmware.BuildIdMaster}|{firmware.BuildIdSlave}|" +
        string.Join(";", droids
            .OrderBy(droid => droid.Id)
            .Select(droid =>
                $"{droid.Id}:{droid.IsMaster}:{droid.Online}:{droid.Adopted}:{droid.FwVersion}:{droid.BuildId}"));

    public bool TryRequestFleetUpdateWindow()
    {
        if (Firmware.LatestFirmwareInfo is not { } firmware) return false;

        var plan = FleetUpdatePlanner.Create(Protocol.Droids, firmware);
        if (!plan.HasTargets) return false;

        CancelPendingFleetUpdateOffer();
        _fleetUpdateOfferShown = true;
        _evaluatedFleetUpdateFingerprint = BuildFleetUpdateFingerprint(Protocol.Droids, firmware);
        FleetUpdatePromptRequested?.Invoke(new FleetUpdateViewModel(plan, Protocol, _link, Sequencer));
        return true;
    }

    private async Task OfferFleetUpdateAfterRosterSettlesAsync(
        CancellationTokenSource debounce,
        string fingerprint)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), debounce.Token);
            if (debounce.IsCancellationRequested || _fleetUpdateOfferShown ||
                !Protocol.SessionReady || Droids.AnyOtaActive || Firmware.Flashing ||
                Sequencer.TransportState != SequencerTransportState.Stopped ||
                Firmware.LatestFirmwareInfo is not { } firmware)
                return;

            if (!string.Equals(BuildFleetUpdateFingerprint(Protocol.Droids, firmware), fingerprint,
                    StringComparison.Ordinal))
            {
                ScheduleFleetUpdateOffer();
                return;
            }

            var plan = FleetUpdatePlanner.Create(Protocol.Droids, firmware);
            _evaluatedFleetUpdateFingerprint = fingerprint;
            if (!plan.HasTargets) return;

            TryRequestFleetUpdateWindow();
        }
        catch (OperationCanceledException)
        {
            // A newer roster/catalog signal restarted the short stabilization delay.
        }
        finally
        {
            if (ReferenceEquals(_fleetUpdateOfferDebounce, debounce))
            {
                _fleetUpdateOfferDebounce.Dispose();
                _fleetUpdateOfferDebounce = null;
                _pendingFleetUpdateFingerprint = null;
            }
        }
    }

    // Re-scans ports and, if the last known one has (re)appeared and we're not already
    // connected, opens it — called once immediately at startup, then every 3s by
    // _startupConnectTimer until it succeeds or the user takes over manually.
    private void TryAutoConnect()
    {
        if (Connected) { StopStartupConnectTimer(); return; }
        RefreshPorts();
        var last = _settings.LastPort;
        if (!string.IsNullOrEmpty(last) && AvailablePorts.Contains(last))
        {
            SelectedPort = last;
            _link.Open(last);
        }
    }

    private void StopStartupConnectTimer()
    {
        _startupConnectTimer?.Dispose();
        _startupConnectTimer = null;
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
    private void Disconnect()
    {
        StopStartupConnectTimer();
        _link.Close();
    }
}
