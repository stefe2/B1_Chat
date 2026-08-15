using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.ViewModels;

public enum FleetUpdateItemState
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

public partial class FleetUpdateItemViewModel : ObservableObject
{
    public FleetUpdateTarget Target { get; }
    public string DisplayName => Target.DisplayName;
    public string RoleAndId => $"{Target.RoleLabel} · {Target.DroidIdHex}";
    public string VersionChange => $"{Target.CurrentIdentity}  →  {Target.TargetIdentity}";

    [ObservableProperty] private FleetUpdateItemState _state;
    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _progressIndeterminate;

    public FleetUpdateItemViewModel(FleetUpdateTarget target) => Target = target;
}

public partial class FleetUpdateViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MasterReconnectTimeout = TimeSpan.FromSeconds(30);

    private readonly FleetUpdatePlan _plan;
    private readonly ProtocolClient _protocol;
    private readonly SerialLinkService _link;
    private readonly SequencerViewModel _sequencer;
    private readonly UpdateService _update = new();
    private readonly OtaService _ota;
    private readonly FlashService _flash = new();
    private TaskCompletionSource<OtaCompletion>? _otaCompletion;
    private TaskCompletionSource<(bool Ok, int? ExitCode, string? Error)>? _flashCompletion;
    private FleetUpdateItemViewModel? _activeItem;
    private int _activeIndex;
    private bool _disposed;

    public ObservableCollection<FleetUpdateItemViewModel> Items { get; }
    public string TargetVersion => _plan.TargetVersion;
    public string SummaryText => Items.Count == 1
        ? $"1 online droid can be updated to firmware v{TargetVersion}."
        : $"{Items.Count} online droids can be updated to firmware v{TargetVersion}.";
    public bool HasNotices => _plan.Notices.Count > 0;
    public string NoticesText => string.Join(Environment.NewLine, _plan.Notices);

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _hasFailure;
    [ObservableProperty] private int _overallProgress;
    [ObservableProperty] private bool _overallProgressIndeterminate;
    [ObservableProperty] private string _currentStatus = "Review the update plan, then confirm when the droids can safely reboot.";

    public bool ShowConfirmation => !IsRunning && !IsComplete;
    public bool CanClose => !IsRunning;
    public event Action? CloseRequested;

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowConfirmation));
        OnPropertyChanged(nameof(CanClose));
        StartUpdateCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCompleteChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowConfirmation));
        StartUpdateCommand.NotifyCanExecuteChanged();
    }

    public FleetUpdateViewModel(
        FleetUpdatePlan plan,
        ProtocolClient protocol,
        SerialLinkService link,
        SequencerViewModel sequencer)
    {
        _plan = plan;
        _protocol = protocol;
        _link = link;
        _sequencer = sequencer;
        Items = new ObservableCollection<FleetUpdateItemViewModel>(
            plan.Targets.Select(target => new FleetUpdateItemViewModel(target)));

        _ota = new OtaService(protocol);
        _ota.Progress += OnOtaProgress;
        _ota.Retrying += OnOtaRetrying;
        _ota.CompletedDetailed += OnOtaCompleted;
        _flash.Progress += OnFlashProgress;
        _flash.Completed += OnFlashCompleted;
        _protocol.LinkError += OnLinkError;
        _protocol.LinkClosed += OnLinkClosed;
    }

    private bool CanStartUpdate() => !IsRunning && !IsComplete && Items.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStartUpdate))]
    private async Task StartUpdate()
    {
        IsRunning = true;
        HasFailure = false;
        OverallProgress = 0;
        OverallProgressIndeterminate = true;

        // Stop the Sequencer and send the firmware's non-persistent centered hold before
        // rebooting nodes. This neither disables servos nor changes saved settings.
        _sequencer.SafeStopCommand.Execute(null);

        try
        {
            var images = await DownloadRequiredImagesAsync();
            if (images == null) return;

            OverallProgressIndeterminate = false;
            for (_activeIndex = 0; _activeIndex < Items.Count; _activeIndex++)
            {
                _activeItem = Items[_activeIndex];
                var target = _activeItem.Target;
                var image = images[target.IsMaster];
                var result = target.IsMaster
                    ? await UpdateMasterAsync(_activeItem, image)
                    : await UpdateSlaveAsync(_activeItem, image);

                if (!result.Ok)
                {
                    FailActiveItem(result.Error);
                    return;
                }

                _activeItem.State = FleetUpdateItemState.Succeeded;
                _activeItem.ProgressIndeterminate = false;
                _activeItem.Progress = 100;
                _activeItem.Status = "Updated and verified";
                OverallProgress = (int)Math.Round(100.0 * (_activeIndex + 1) / Items.Count);
            }

            IsComplete = true;
            CurrentStatus = "All selected droids were updated and verified successfully.";
        }
        catch (Exception ex)
        {
            FailActiveItem(ex.Message);
        }
        finally
        {
            OverallProgressIndeterminate = false;
            IsRunning = false;
            _activeItem = null;
        }
    }

    private async Task<Dictionary<bool, string>?> DownloadRequiredImagesAsync()
    {
        var images = new Dictionary<bool, string>();
        foreach (var isMaster in Items.Select(item => item.Target.IsMaster).Distinct())
        {
            var role = isMaster ? "MASTER" : "SLAVE";
            CurrentStatus = $"Downloading and verifying the {role} firmware…";
            var url = isMaster ? _plan.Firmware.UrlMaster : _plan.Firmware.UrlSlave;
            var sha = isMaster ? _plan.Firmware.Sha256Master : _plan.Firmware.Sha256Slave;
            if (string.IsNullOrWhiteSpace(url))
            {
                FailBeforeTransfer($"The release does not contain a {role} firmware image.");
                return null;
            }

            var download = await _update.DownloadAssetAsync(url, sha);
            if (!download.Ok || download.Path == null)
            {
                FailBeforeTransfer($"{role} firmware download failed: {download.Error}");
                return null;
            }
            images[isMaster] = download.Path;
        }
        return images;
    }

    private async Task<(bool Ok, string? Error)> UpdateSlaveAsync(FleetUpdateItemViewModel item, string imagePath)
    {
        var target = item.Target;
        var live = _protocol.Droids.FirstOrDefault(droid => droid.Id == target.DroidId);
        if (live is not { Online: true }) return (false, "The droid went offline before its OTA update started.");

        item.State = FleetUpdateItemState.Running;
        item.Progress = 0;
        item.ProgressIndeterminate = true;
        item.Status = "Starting OTA…";
        CurrentStatus = $"Updating {target.DisplayName} ({target.RoleLabel}) over the mesh…";

        _otaCompletion = new TaskCompletionSource<OtaCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ota.Start(target.DroidId, imagePath, out var startError))
        {
            _otaCompletion = null;
            return (false, startError);
        }

        var completion = await _otaCompletion.Task;
        _otaCompletion = null;
        if (!completion.Ok) return (false, completion.Message);
        return VerifyIdentity(target, completion.FirmwareVersion, completion.BuildId);
    }

    private async Task<(bool Ok, string? Error)> UpdateMasterAsync(FleetUpdateItemViewModel item, string imagePath)
    {
        var target = item.Target;
        var port = _link.PortName;
        if (string.IsNullOrWhiteSpace(port)) return (false, "The master's USB port is no longer connected.");

        item.State = FleetUpdateItemState.Running;
        item.Progress = 0;
        item.ProgressIndeterminate = true;
        item.Status = $"Flashing app on {port}…";
        CurrentStatus = $"Updating {target.DisplayName} (MASTER) by USB on {port}…";

        _flashCompletion = new TaskCompletionSource<(bool, int?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _link.PrepareForExternalClose();
        _flash.Start(imagePath, "0x10000", port, eraseFirst: false);
        var flash = await _flashCompletion.Task;
        _flashCompletion = null;
        if (!flash.Ok)
        {
            _link.Open(port);
            return (false, flash.Error ?? $"espflash returned code {flash.ExitCode}.");
        }

        item.Progress = 100;
        item.ProgressIndeterminate = true;
        item.Status = "Reconnecting and verifying…";
        CurrentStatus = $"The MASTER flash completed; waiting for {target.DisplayName} to reconnect…";
        return await ReconnectAndVerifyMasterAsync(target, port);
    }

    private async Task<(bool Ok, string? Error)> ReconnectAndVerifyMasterAsync(FleetUpdateTarget target, string port)
    {
        var identity = new TaskCompletionSource<(string? Version, string? Build)>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnHello()
        {
            if (_protocol.SessionReady)
                identity.TrySetResult((_protocol.FwVersion, _protocol.FwBuildId));
        }

        _protocol.HelloReceived += OnHello;
        try
        {
            var deadline = DateTime.UtcNow + MasterReconnectTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!_link.IsOpen && SerialLinkService.GetPortNames().Contains(port))
                    _link.Open(port);

                var wait = await Task.WhenAny(identity.Task, Task.Delay(TimeSpan.FromSeconds(2)));
                if (wait == identity.Task)
                {
                    var actual = await identity.Task;
                    return VerifyIdentity(target, actual.Version, actual.Build);
                }
            }
            return (false, $"The master did not reconnect on {port} within {MasterReconnectTimeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            _protocol.HelloReceived -= OnHello;
        }
    }

    internal static (bool Ok, string? Error) VerifyIdentity(
        FleetUpdateTarget target,
        string? actualVersion,
        string? actualBuild)
    {
        if (!string.Equals(actualVersion, target.TargetVersion, StringComparison.OrdinalIgnoreCase))
            return (false, $"Verification reported firmware v{actualVersion ?? "?"}; expected v{target.TargetVersion}.");
        if (!string.IsNullOrWhiteSpace(target.ExpectedBuildId) &&
            !string.Equals(actualBuild, target.ExpectedBuildId, StringComparison.OrdinalIgnoreCase))
            return (false, $"Verification reported build {actualBuild ?? "?"}; expected {target.ExpectedBuildId}.");
        return (true, null);
    }

    private void OnOtaProgress(int sent, int total) => RunOnUi(() =>
    {
        if (_activeItem == null) return;
        var pct = total > 0 ? (int)Math.Clamp(100.0 * sent / total, 0, 100) : 0;
        _activeItem.ProgressIndeterminate = false;
        _activeItem.Progress = pct;
        _activeItem.Status = $"OTA transfer {sent}/{total} chunks";
        OverallProgress = (int)Math.Round(100.0 * (_activeIndex + pct / 100.0) / Items.Count);
    });

    private void OnOtaRetrying(int index, int attempt) => RunOnUi(() =>
    {
        if (_activeItem != null) _activeItem.Status = $"Chunk {index}: retry {attempt}…";
    });

    private void OnOtaCompleted(OtaCompletion completion) => _otaCompletion?.TrySetResult(completion);

    private void OnFlashProgress(int pct) => RunOnUi(() =>
    {
        if (_activeItem == null) return;
        _activeItem.ProgressIndeterminate = false;
        _activeItem.Progress = pct;
        _activeItem.Status = $"USB flash {pct}%";
        OverallProgress = (int)Math.Round(100.0 * (_activeIndex + pct / 100.0) / Items.Count);
    });

    private void OnFlashCompleted(bool ok, int? exitCode, string? error) =>
        _flashCompletion?.TrySetResult((ok, exitCode, error));

    private void OnLinkError(string error)
    {
        if (_otaCompletion == null) return;
        _ota.Abort();
        _otaCompletion.TrySetResult(new OtaCompletion(false, "Serial link error: " + error, null, null, "serialError"));
    }

    private void OnLinkClosed(bool unexpected)
    {
        if (_otaCompletion == null) return;
        _ota.Abort();
        _otaCompletion.TrySetResult(new OtaCompletion(false, "The serial link closed during the OTA update.", null, null, "serialClosed"));
    }

    private void FailBeforeTransfer(string message)
    {
        HasFailure = true;
        IsComplete = true;
        CurrentStatus = message;
    }

    private void FailActiveItem(string? message)
    {
        var error = string.IsNullOrWhiteSpace(message) ? "Unknown update error." : message;
        if (_activeItem != null)
        {
            _activeItem.State = FleetUpdateItemState.Failed;
            _activeItem.ProgressIndeterminate = false;
            _activeItem.Status = error;
        }
        HasFailure = true;
        IsComplete = true;
        CurrentStatus = "Fleet update stopped: " + error;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private bool CanRequestClose() => CanClose;

    [RelayCommand(CanExecute = nameof(CanRequestClose))]
    private void Close() => CloseRequested?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ota.Progress -= OnOtaProgress;
        _ota.Retrying -= OnOtaRetrying;
        _ota.CompletedDetailed -= OnOtaCompleted;
        _ota.Dispose();
        _flash.Progress -= OnFlashProgress;
        _flash.Completed -= OnFlashCompleted;
        _protocol.LinkError -= OnLinkError;
        _protocol.LinkClosed -= OnLinkClosed;
    }
}
