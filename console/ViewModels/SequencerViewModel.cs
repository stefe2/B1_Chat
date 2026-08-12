using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Converters;
using b1_chat_console.Models;
using b1_chat_console.Services;
using Microsoft.Win32;

namespace b1_chat_console.ViewModels;

public enum SequencerTransportState
{
    Stopped,
    Playing,
    Paused,
}

public partial class SequencerViewModel : ObservableObject, IDisposable
{
    private readonly ISequencerProtocol _protocol;
    private readonly SettingsService _settings;
    private readonly LibraryService _library = new();
    private readonly ISequencerAudioPlayer _audioPlayer;
    private readonly IPlaybackTimerScheduler _timerScheduler;
    private readonly IPlaybackTimerScheduler _executionTimerScheduler;
    private readonly IPlaybackClock _playbackClock;
    private const int HistoryMax = 50;
    private const int ExecutionStartTimeoutMs = 1500;
    private const int ExecutionCompletionGraceMs = 1500;
    private const ushort InfiniteAnimLeaseMs = 5000;
    private const int InfiniteAnimLeaseRenewMs = 2000;
    private const string AudioFileFilter = "Audio files (*.mp3;*.wav;*.wma;*.ogg)|*.mp3;*.wav;*.wma;*.ogg|All files (*.*)|*.*";

    public ObservableCollection<SequenceLibraryItem> Library { get; } = new();
    public ObservableCollection<SequenceStep> Steps { get; } = new();
    public ObservableCollection<Droid> Targets => _protocol.Droids;

    // --- Timeline (Views/SequenceTimelineView) --------------------------------

    public ObservableCollection<TimelineTrack> Tracks { get; } = new();
    public ObservableCollection<TimelineTick> RulerTicks { get; } = new();

    // Console-side audio (DFPlayer set aside "for now", see CLAUDE.md): one or more named
    // lanes (default "AUDIO"/"AMBIENT"), each holding independently-placeable clips that may
    // overlap within their own lane. Never sent to the master — console-side only.
    public ObservableCollection<AudioLane> AudioLanes { get; } = new();

    // The 18 built-in gestures, reused as-is from AnimationViewModel — never redefined here.
    public IReadOnlyList<string> GestureNames { get; } =
        AnimationViewModel.AnimNames.Select((n, i) => $"{i} — {n}").ToList();
    public IReadOnlyList<GestureLibraryEntry> GestureLibrary { get; } =
        AnimationViewModel.AnimNames.Select((n, i) => new GestureLibraryEntry { Id = i, Name = n }).ToList();

    // Same 18 gestures, grouped into labeled rows (mockup-matched "GESTURE LIBRARY" layout) —
    // grouping/labels come from AnimFamilyToBrushConverter.Families, the single source of truth
    // also used to color every clip/chip, so the two can't drift apart.
    public IReadOnlyList<GestureFamily> GestureFamilies { get; } = AnimFamilyToBrushConverter.Families
        .Select(f => new GestureFamily
        {
            Label = f.Label,
            ColorAnimId = f.AnimIds[0],
            Gestures = f.AnimIds.Select(id => new GestureLibraryEntry { Id = id, Name = AnimationViewModel.AnimNames[id] }).ToList(),
        }).ToList();

    public IReadOnlyDictionary<int, int> AnimDurationMsLookup => _protocol.AnimDurationMs;

    [ObservableProperty] private TimelineTrack? _armedTrack;
    [ObservableProperty] private double _pxPerSecond = 80;
    [ObservableProperty] private bool _snapToGrid = true;
    [ObservableProperty] private double _playheadMs;

    private SequencerTransportState _transportState = SequencerTransportState.Stopped;
    public SequencerTransportState TransportState => _transportState;
    public bool IsPlaying => TransportState == SequencerTransportState.Playing;
    public bool IsPaused => TransportState == SequencerTransportState.Paused;
    public bool IsLiveTracking => TransportState == SequencerTransportState.Playing;

    public double PxPerMs => PxPerSecond / 1000.0;
    partial void OnPxPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(PxPerMs));
        RebuildRulerTicks();
    }

    // Transport bar readout ("00:03.400" + " / 00:15.800") — mirrors the mockup's timecode
    // pill, split in two so the view renders the current position in accent and the total in
    // muted gray (the mockup's .timecode/.tot duo).
    public string TimecodeNowText => FormatTimecode(PlayheadMs);
    public string TimecodeTotalText => $" / {FormatTimecode(TotalDurationMs())}";
    partial void OnPlayheadMsChanged(double value)
    {
        OnPropertyChanged(nameof(TimecodeNowText));
        OnPropertyChanged(nameof(TimecodeTotalText));
    }

    private static string FormatTimecode(double ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private DispatcherTimer? _playheadTimer;
    private double _liveAnchorElapsedMs;

    // --- /Timeline -------------------------------------------------------------

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _dirty;

    // Card header badge — name only, now that the ESP32 slot concept is gone from the console.
    public string SequenceBadgeText => string.IsNullOrWhiteSpace(Name)
        ? "UNSAVED · NEW SEQUENCE"
        : $"\"{Name.ToUpperInvariant()}\"";
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(SequenceBadgeText));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;
    [ObservableProperty] private SequenceStep? _selectedStep;

    // SelectedStep.Target as a TimelineTrack, for the inspector's Target ComboBox.
    // ComboBox.SelectedValue/SelectedValuePath was unreliable against DarkComboBoxStyle's
    // fully-replaced ControlTemplate (rendered a validation-error border with no item
    // resolved) — SelectedItem against this ushort<->TimelineTrack wrapper is more robust.
    public TimelineTrack? SelectedStepTrack
    {
        get => SelectedStep == null ? null : Tracks.FirstOrDefault(t => t.Id == SelectedStep.Target);
        set { if (CanEditSequence && SelectedStep != null && value != null) SelectedStep.Target = value.Id; }
    }

    partial void OnSelectedStepChanged(SequenceStep? value) => OnPropertyChanged(nameof(SelectedStepTrack));

    private readonly Stack<SequenceSnapshot> _history = new();
    private readonly Stack<SequenceSnapshot> _future = new();
    private readonly List<IDisposable> _playbackTimers = new();
    private readonly HashSet<int> _dispatchedPlaybackEvents = new();
    private readonly Dictionary<uint, ExecutionTracker> _executionTrackers = new();
    private readonly Dictionary<ushort, GestureTargetState> _latestGestureByDroid = new();
    private readonly Dictionary<uint, ActiveAnimLease> _activeAnimLeases = new();
    private readonly PlaybackGeneration _playbackGeneration = new();
    private SequencerPlaybackPlan? _activePlaybackPlan;
    private SequenceSnapshot? _activeEditBefore;
    private bool _suppressTimelineRefresh;
    private int _elapsedAtPauseMs;
    private bool _disposed;

    private readonly record struct GestureTargetState(uint RequestId, int AnimId)
    {
        public bool IsInfinite => AnimId is 16 or 17;
    }

    private sealed class ExecutionTracker
    {
        public required uint RequestId { get; init; }
        public required SequenceStep Step { get; init; }
        public required HashSet<ushort> ExpectedDroids { get; init; }
        public required int AnimId { get; init; }
        public required bool IsBroadcast { get; init; }
        public Dictionary<ushort, GestureTargetState?> PreviousTargetStates { get; } = new();
        public AnimMasterReceipt? MasterReceipt { get; set; }
        public Dictionary<ushort, AnimExecutionReport> Reports { get; } = new();
        public IDisposable? StartDeadline { get; set; }
        public IDisposable? CompletionDeadline { get; set; }
        public bool StartDeadlineExpired { get; set; }
        public bool CompletionDeadlineExpired { get; set; }
    }

    private sealed class ActiveAnimLease
    {
        public required uint RequestId { get; init; }
        public required ushort Target { get; init; }
        public required int AnimId { get; init; }
        public int MeshSeq { get; set; }
        public IDisposable? RenewalTimer { get; set; }
    }

    // Persistent sequence edits are locked for the whole active pass, including Pause. The
    // playback plan is a snapshot, so allowing the editor to show one document while another
    // keeps running would be misleading even though the snapshot itself is now race-free.
    public bool CanEditSequence => TransportState == SequencerTransportState.Stopped;

    private void TransitionTransportTo(SequencerTransportState next)
    {
        if (_transportState == next) return;
        var allowed = (_transportState, next) switch
        {
            (SequencerTransportState.Stopped, SequencerTransportState.Playing) => true,
            (SequencerTransportState.Playing, SequencerTransportState.Paused) => true,
            (SequencerTransportState.Playing, SequencerTransportState.Stopped) => true,
            (SequencerTransportState.Paused, SequencerTransportState.Playing) => true,
            (SequencerTransportState.Paused, SequencerTransportState.Stopped) => true,
            _ => false,
        };
        if (!allowed)
            throw new InvalidOperationException($"Invalid Sequencer transport transition: {_transportState} -> {next}.");

        SetProperty(ref _transportState, next, nameof(TransportState));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsLiveTracking));
        // Relay commands do not re-evaluate CanExecute automatically when a derived property
        // changes. Keep every transport-dependent command synchronized from this one source.
        PauseCommand.NotifyCanExecuteChanged();
        RefreshEditAvailability();
    }

    private void RefreshEditAvailability()
    {
        OnPropertyChanged(nameof(CanEditSequence));
        InsertGestureCommand.NotifyCanExecuteChanged();
        NudgeStartForwardCommand.NotifyCanExecuteChanged();
        NudgeStartBackwardCommand.NotifyCanExecuteChanged();
        AddAudioLaneCommand.NotifyCanExecuteChanged();
        DeleteAudioLaneCommand.NotifyCanExecuteChanged();
        ClearTimelineCommand.NotifyCanExecuteChanged();
        AddAudioClipCommand.NotifyCanExecuteChanged();
        ReplaceAudioClipCommand.NotifyCanExecuteChanged();
        DeleteAudioClipCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        DeleteStepCommand.NotifyCanExecuteChanged();
        DuplicateStepCommand.NotifyCanExecuteChanged();
        LoadFromLibraryCommand.NotifyCanExecuteChanged();
        DeleteFromLibraryCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }

    public SequencerViewModel(
        ISequencerProtocol protocol,
        SettingsService settings,
        ISequencerAudioPlayer? audioPlayer = null,
        IPlaybackTimerScheduler? timerScheduler = null,
        IPlaybackClock? playbackClock = null,
        IPlaybackTimerScheduler? executionTimerScheduler = null)
    {
        _protocol = protocol;
        _settings = settings;
        _audioPlayer = audioPlayer ?? new AudioPlaybackService();
        _timerScheduler = timerScheduler ?? new ThreadPoolPlaybackTimerScheduler();
        _executionTimerScheduler = executionTimerScheduler ?? new ThreadPoolPlaybackTimerScheduler();
        _playbackClock = playbackClock ?? new StopwatchPlaybackClock();
        _protocol.DroidsChanged += RebuildTracks;
        _protocol.AnimDurationsReceived += OnAnimDurationsReceived;
        _protocol.AnimMasterAccepted += OnAnimMasterAccepted;
        _protocol.AnimExecutionReceived += OnAnimExecutionReceived;
        _protocol.LinkClosed += OnProtocolLinkClosed;
        Steps.CollectionChanged += (_, _) =>
        {
            if (_activeEditBefore == null && !_suppressTimelineRefresh)
                RebuildRulerTicks();
        };
        RebuildTracks();
        ApplyAudioLanesFromDto(null);
        RebuildRulerTicks();
        RefreshLibrary();
    }

    private void OnAnimDurationsReceived()
    {
        OnPropertyChanged(nameof(AnimDurationMsLookup));
        // Real durations change TotalDurationMs (clip tails) — refresh ruler extent too.
        RebuildRulerTicks();
    }

    private void ResetExecutionTracking()
    {
        foreach (var tracker in _executionTrackers.Values)
            DisposeExecutionDeadlines(tracker);
        _executionTrackers.Clear();
        foreach (var step in Steps)
        {
            step.ExecutionSummary = "";
            step.ExecutionDetail = "";
            step.ExecutionTone = "none";
        }
    }

    private void TrackExecution(AnimDispatchResult dispatch, GesturePlaybackEvent gesture)
    {
        if (gesture.SourceOrder < 0 || gesture.SourceOrder >= Steps.Count) return;
        var step = Steps[gesture.SourceOrder];
        if (!dispatch.Written)
        {
            step.ExecutionSummary = dispatch.State switch
            {
                AnimDispatchState.NotConnected => "NO LINK",
                AnimDispatchState.HandshakePending => "NOT READY",
                _ => "WRITE FAIL",
            };
            step.ExecutionDetail = $"Request {dispatch.RequestId}: serial dispatch failed ({DispatchStateName(dispatch.State)}).";
            step.ExecutionTone = "rejected";
            return;
        }

        var isBroadcast = gesture.Target == ushort.MaxValue;
        var expected = isBroadcast
            ? _protocol.Droids.Where(d => d.Online).Select(d => d.Id).ToHashSet()
            : new HashSet<ushort> { gesture.Target };
        var tracker = new ExecutionTracker
        {
            RequestId = dispatch.RequestId,
            Step = step,
            ExpectedDroids = expected,
            AnimId = gesture.AnimId,
            IsBroadcast = isBroadcast,
        };
        _executionTrackers[dispatch.RequestId] = tracker;
        RecordGestureDispatch(tracker);
        step.ExecutionSummary = "WRITE";
        step.ExecutionDetail = $"Request {dispatch.RequestId}: serial write completed; awaiting master acceptance.";
        step.ExecutionTone = "sent";

        tracker.StartDeadline = _executionTimerScheduler.Schedule(
            ExecutionStartTimeoutMs,
            () => RunOnUiThread(() => ExpireExecutionDeadline(dispatch.RequestId, completion: false)));

        // POWER_DOWN and TALK deliberately loop until another gesture interrupts them. Once
        // their START arrives, absence of COMPLETED is therefore healthy rather than a timeout.
        if (gesture.AnimId is not (16 or 17))
        {
            var completionDueMs = (int)Math.Min(
                int.MaxValue, Math.Max(ExecutionStartTimeoutMs,
                    (long)Math.Max(0, gesture.DurationMs) + ExecutionCompletionGraceMs));
            tracker.CompletionDeadline = _executionTimerScheduler.Schedule(
                completionDueMs,
                () => RunOnUiThread(() => ExpireExecutionDeadline(dispatch.RequestId, completion: true)));
        }
    }

    private static string DispatchStateName(AnimDispatchState state) => state switch
    {
        AnimDispatchState.NotConnected => "not connected",
        AnimDispatchState.HandshakePending => "handshake pending",
        AnimDispatchState.WriteFailed => "serial write error",
        _ => "written",
    };

    private void RecordGestureDispatch(ExecutionTracker tracker)
    {
        foreach (var droidId in tracker.ExpectedDroids)
        {
            tracker.PreviousTargetStates[droidId] =
                _latestGestureByDroid.TryGetValue(droidId, out var previous) ? previous : null;
            _latestGestureByDroid[droidId] = new GestureTargetState(
                tracker.RequestId, tracker.AnimId);
        }
    }

    private void RollBackFailedMeshDispatch(ExecutionTracker tracker, AnimMasterReceipt receipt)
    {
        if (receipt.MeshQueued)
        {
            PruneInactiveAnimLeases();
            return;
        }
        var localMasterIds = receipt.LocalHandled
            ? _protocol.Droids.Where(d => d.IsMaster).Select(d => d.Id).ToHashSet()
            : new HashSet<ushort>();

        foreach (var droidId in tracker.ExpectedDroids)
        {
            // A local master target does not depend on the failed ESP-NOW queue.
            if (localMasterIds.Contains(droidId)) continue;
            if (!_latestGestureByDroid.TryGetValue(droidId, out var current)
                || current.RequestId != tracker.RequestId) continue;
            if (tracker.PreviousTargetStates.TryGetValue(droidId, out var previous)
                && previous.HasValue)
                _latestGestureByDroid[droidId] = previous.Value;
            else
                _latestGestureByDroid.Remove(droidId);
        }
        PruneInactiveAnimLeases();
    }

    private void UpdateGestureStateFromExecution(ExecutionTracker tracker, AnimExecutionReport report)
    {
        if (!_latestGestureByDroid.TryGetValue(report.DroidId, out var current)
            || current.RequestId != tracker.RequestId) return;
        // A terminal result for an infinite command proves it is no longer active. Finite
        // commands remain recorded as the latest state but are never selected for cleanup.
        if (current.IsInfinite && IsTerminalExecutionPhase(report.Phase))
            _latestGestureByDroid.Remove(report.DroidId);
        PruneInactiveAnimLeases();
    }

    private void TrackAnimLease(AnimDispatchResult dispatch, GesturePlaybackEvent gesture,
                                ushort leaseMs)
    {
        if (!dispatch.Written || leaseMs == 0) return;
        _activeAnimLeases[dispatch.RequestId] = new ActiveAnimLease
        {
            RequestId = dispatch.RequestId,
            Target = gesture.Target,
            AnimId = gesture.AnimId,
        };
        PruneInactiveAnimLeases();
    }

    private void AcceptAnimLease(AnimMasterReceipt receipt)
    {
        if (!_activeAnimLeases.TryGetValue(receipt.RequestId, out var lease)) return;
        if (receipt.Target != lease.Target || receipt.AnimId != lease.AnimId ||
            receipt.LeaseMs != InfiniteAnimLeaseMs) return;
        if (!receipt.MeshQueued && !receipt.LocalHandled)
        {
            CancelAnimLease(lease.RequestId);
            return;
        }
        lease.MeshSeq = receipt.MeshSeq;
        ScheduleAnimLeaseRenewal(lease);
    }

    private void ScheduleAnimLeaseRenewal(ActiveAnimLease lease)
    {
        lease.RenewalTimer?.Dispose();
        lease.RenewalTimer = _executionTimerScheduler.Schedule(
            InfiniteAnimLeaseRenewMs,
            () => RunOnUiThread(() => RenewAnimLease(lease)));
    }

    private void RenewAnimLease(ActiveAnimLease lease)
    {
        if (!_activeAnimLeases.TryGetValue(lease.RequestId, out var current) ||
            !ReferenceEquals(current, lease)) return;
        if (!IsAnimLeaseOwned(lease.RequestId))
        {
            CancelAnimLease(lease.RequestId);
            return;
        }
        _protocol.RenewAnimLease(lease.Target, lease.MeshSeq, InfiniteAnimLeaseMs);
        ScheduleAnimLeaseRenewal(lease);
    }

    private bool IsAnimLeaseOwned(uint requestId) =>
        _latestGestureByDroid.Values.Any(state => state.RequestId == requestId && state.IsInfinite);

    private void PruneInactiveAnimLeases()
    {
        foreach (var requestId in _activeAnimLeases.Keys
                     .Where(requestId => !IsAnimLeaseOwned(requestId)).ToArray())
            CancelAnimLease(requestId);
    }

    private void CancelAnimLease(uint requestId)
    {
        if (!_activeAnimLeases.Remove(requestId, out var lease)) return;
        lease.RenewalTimer?.Dispose();
        lease.RenewalTimer = null;
    }

    private void CancelAllAnimLeases()
    {
        foreach (var lease in _activeAnimLeases.Values)
            lease.RenewalTimer?.Dispose();
        _activeAnimLeases.Clear();
    }

    private void StopInfiniteGestures()
    {
        // The explicit IDLE is immediate cleanup; cancelling first guarantees that a timer
        // already queued on the UI thread cannot revive the command. If the write fails,
        // the firmware-side lease still expires to IDLE on its own.
        CancelAllAnimLeases();
        var active = _latestGestureByDroid
            .Where(pair => pair.Value.IsInfinite)
            .OrderBy(pair => pair.Key)
            .ToArray();
        foreach (var (droidId, previous) in active)
        {
            var seed = (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1);
            var dispatch = _protocol.PlayAnim(droidId, 0, seed);
            if (!dispatch.Written) continue;
            if (_latestGestureByDroid.TryGetValue(droidId, out var current)
                && current == previous)
                _latestGestureByDroid[droidId] = new GestureTargetState(dispatch.RequestId, 0);
        }
    }

    private void OnAnimMasterAccepted(AnimMasterReceipt receipt) => RunOnUiThread(() =>
    {
        AcceptAnimLease(receipt);
        if (!_executionTrackers.TryGetValue(receipt.RequestId, out var tracker)) return;
        if (receipt.AnimId != tracker.AnimId) return;
        if (receipt.Target != (tracker.IsBroadcast ? ushort.MaxValue : tracker.ExpectedDroids.Single())) return;
        tracker.MasterReceipt = receipt;
        RollBackFailedMeshDispatch(tracker, receipt);
        UpdateExecutionSummary(tracker);
    });

    private void OnAnimExecutionReceived(AnimExecutionReport report) => RunOnUiThread(() =>
    {
        if (!_executionTrackers.TryGetValue(report.RequestId, out var tracker)) return;
        if (report.AnimId != tracker.AnimId || !IsKnownExecutionPhase(report.Phase)) return;
        if (!tracker.IsBroadcast && !tracker.ExpectedDroids.Contains(report.DroidId)) return;
        // A broadcast can discover a droid that came online after dispatch.
        if (tracker.IsBroadcast) tracker.ExpectedDroids.Add(report.DroidId);

        // A delayed duplicate START must never replace COMPLETED/INTERRUPTED/REJECTED and make
        // a finished clip look active again. The first terminal report is authoritative.
        if (tracker.Reports.TryGetValue(report.DroidId, out var previous)
            && IsTerminalExecutionPhase(previous.Phase))
            return;
        tracker.Reports[report.DroidId] = report;
        UpdateGestureStateFromExecution(tracker, report);
        UpdateExecutionSummary(tracker);
        if (AllExpectedDroidsTerminal(tracker)) DisposeExecutionDeadlines(tracker);
    });

    private void ExpireExecutionDeadline(uint requestId, bool completion)
    {
        if (!_executionTrackers.TryGetValue(requestId, out var tracker)) return;
        if (completion)
        {
            tracker.CompletionDeadlineExpired = true;
            tracker.CompletionDeadline?.Dispose();
            tracker.CompletionDeadline = null;
        }
        else
        {
            tracker.StartDeadlineExpired = true;
            tracker.StartDeadline?.Dispose();
            tracker.StartDeadline = null;
        }
        UpdateExecutionSummary(tracker);
    }

    private static bool IsKnownExecutionPhase(string phase) =>
        phase is "started" or "completed" or "interrupted" or "rejected";

    private static bool IsTerminalExecutionPhase(string phase) =>
        phase is "completed" or "interrupted" or "rejected";

    private static bool AllExpectedDroidsTerminal(ExecutionTracker tracker) =>
        tracker.ExpectedDroids.Count > 0
        && tracker.ExpectedDroids.All(id => tracker.Reports.TryGetValue(id, out var report)
            && IsTerminalExecutionPhase(report.Phase));

    private static void DisposeExecutionDeadlines(ExecutionTracker tracker)
    {
        tracker.StartDeadline?.Dispose();
        tracker.CompletionDeadline?.Dispose();
        tracker.StartDeadline = null;
        tracker.CompletionDeadline = null;
    }

    private static void UpdateExecutionSummary(ExecutionTracker tracker)
    {
        var reports = tracker.Reports.Values.ToArray();
        var expected = Math.Max(1, tracker.ExpectedDroids.Count);
        var rejected = reports.Count(r => r.Phase == "rejected");
        var interrupted = reports.Count(r => r.Phase == "interrupted");
        var completed = reports.Count(r => r.Phase == "completed");
        var confirmed = reports.Count(r => r.Phase is "started" or "completed" or "interrupted");
        var missing = tracker.ExpectedDroids
            .Where(id => !tracker.Reports.ContainsKey(id))
            .OrderBy(id => id)
            .ToArray();
        var awaitingCompletion = tracker.ExpectedDroids
            .Where(id => tracker.Reports.TryGetValue(id, out var report) && report.Phase == "started")
            .OrderBy(id => id)
            .ToArray();
        var meshDispatchFailed = tracker.MasterReceipt is { MeshQueued: false } receipt
            && (!receipt.LocalHandled || receipt.Target == ushort.MaxValue);

        if (rejected > 0)
        {
            tracker.Step.ExecutionSummary = expected == 1 ? "REJECT" : $"REJ {rejected}/{expected}";
            tracker.Step.ExecutionTone = "rejected";
        }
        else if (completed >= expected)
        {
            tracker.Step.ExecutionSummary = expected == 1 ? "DONE" : $"DONE {completed}/{expected}";
            tracker.Step.ExecutionTone = "completed";
        }
        else if (interrupted > 0)
        {
            tracker.Step.ExecutionSummary = expected == 1 ? "STOP" : $"STOP {interrupted}/{expected}";
            tracker.Step.ExecutionTone = "interrupted";
        }
        else if (reports.Length == 0 && meshDispatchFailed)
        {
            tracker.Step.ExecutionSummary = "MESH FAIL";
            tracker.Step.ExecutionTone = "rejected";
        }
        else if (tracker.CompletionDeadlineExpired && awaitingCompletion.Length > 0)
        {
            var unresolved = awaitingCompletion.Length + missing.Length;
            tracker.Step.ExecutionSummary = expected == 1 ? "TIMEOUT" : $"TIMEOUT {unresolved}/{expected}";
            tracker.Step.ExecutionTone = "timeout";
        }
        else if ((tracker.StartDeadlineExpired || tracker.CompletionDeadlineExpired)
                 && (missing.Length > 0 || tracker.ExpectedDroids.Count == 0))
        {
            tracker.Step.ExecutionSummary = expected == 1 ? "UNCONF" : $"MISS {missing.Length}/{expected}";
            tracker.Step.ExecutionTone = "timeout";
        }
        else
        {
            tracker.Step.ExecutionSummary = reports.Length == 0
                ? (tracker.MasterReceipt.HasValue ? "MASTER" : "WRITE")
                : (expected == 1 ? "START" : $"ACK {confirmed}/{expected}");
            tracker.Step.ExecutionTone = reports.Length == 0 ? "sent" : "started";
        }

        var details = new List<string> { "serial: written" };
        if (tracker.MasterReceipt is { } master)
        {
            var meshState = master.MeshQueued ? "mesh queued" : "mesh queue failed";
            var localState = master.LocalHandled ? ", local target handled" : "";
            details.Add($"master: accepted (meshSeq {master.MeshSeq}, {meshState}{localState})");
        }
        details.AddRange(reports
            .OrderBy(r => r.DroidId)
            .Select(r => $"{r.DroidId}: {r.Phase}{(string.IsNullOrWhiteSpace(r.Reason) ? "" : $" ({r.Reason})")}")
            .ToList());
        if (tracker.StartDeadlineExpired || tracker.CompletionDeadlineExpired)
            details.AddRange(missing.Select(id => $"{id}: no start report"));
        if (tracker.CompletionDeadlineExpired)
            details.AddRange(awaitingCompletion.Select(id => $"{id}: completion timeout"));
        if (tracker.ExpectedDroids.Count == 0 && tracker.StartDeadlineExpired)
            details.Add("no online target reported execution");
        tracker.Step.ExecutionDetail = $"Request {tracker.RequestId}: {string.Join("; ", details)}.";
    }

    private void OnProtocolLinkClosed(bool unexpected) => RunOnUiThread(() =>
    {
        if (TransportState != SequencerTransportState.Stopped) Stop();
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        foreach (var tracker in _executionTrackers.Values)
            DisposeExecutionDeadlines(tracker);
        _executionTrackers.Clear();
        _protocol.DroidsChanged -= RebuildTracks;
        _protocol.AnimDurationsReceived -= OnAnimDurationsReceived;
        _protocol.AnimMasterAccepted -= OnAnimMasterAccepted;
        _protocol.AnimExecutionReceived -= OnAnimExecutionReceived;
        _protocol.LinkClosed -= OnProtocolLinkClosed;
    }

    // --- Timeline: tracks, ruler, zoom, playhead --------------------------------

    // Explicit Canvas extents for the ScrollViewer — a WPF Canvas doesn't auto-size to its
    // children's positions, so the scrollable width/height must be computed and bound.
    // Floored at the viewport width (mockup: width = max(content, viewport)) so the row
    // backgrounds/gridlines fill the whole visible body even for a short/empty sequence,
    // instead of stopping in a stub partway across.
    public double TimelineWidthPx => Math.Max(Math.Max(400, ViewportWidthPx), (TotalDurationMs() + 2000) * PxPerMs);

    // Pushed by the view on ScrollViewer.SizeChanged — a pure layout input, not sequence data.
    private double _viewportWidthPx;
    public double ViewportWidthPx
    {
        get => _viewportWidthPx;
        set
        {
            if (Math.Abs(value - _viewportWidthPx) < 0.5) return;
            _viewportWidthPx = value;
            OnPropertyChanged(nameof(TimelineWidthPx));
            RebuildRulerTicks(); // ticks/gridlines span the drawn width, which just changed
        }
    }
    public double TracksHeightPx => Math.Max(TimelineTrack.RowHeight, Tracks.Count * (TimelineTrack.RowHeight + TimelineTrack.RowGap));

    // Droid roster carried by the loaded/imported sequence file (id → name, in saved row
    // order) — lets a sequence authored against the full fleet keep one row per droid even
    // when nothing is plugged in, instead of collapsing every step onto the broadcast row.
    private readonly List<SequenceTrackDto> _fileTracks = new();

    private void RebuildTracks()
    {
        var armedId = ArmedTrack?.Id;
        // Muted is a live per-track toggle, not sequence data (see CLAUDE.md) — it must
        // survive a heartbeat-driven rebuild instead of silently resetting, same reasoning
        // that already applies to ArmedTrack below.
        var mutedIds = Tracks.Where(t => t.Muted).Select(t => t.Id).ToHashSet();
        Tracks.Clear();
        Tracks.Add(new TimelineTrack { Id = 0xFFFF, Label = "All droids", Role = "BROADCAST", IsBroadcast = true, RowIndex = 0, Muted = mutedIds.Contains(0xFFFF) });
        var row = 1;
        foreach (var d in Targets.OrderByDescending(d => d.IsMaster).ThenBy(d => d.Id))
            Tracks.Add(new TimelineTrack
            {
                Id = d.Id, Label = d.Name.Length > 0 ? d.Name : d.IdHex, Role = d.IsMaster ? "MASTER" : "SLAVE",
                RowIndex = row++, Muted = mutedIds.Contains(d.Id),
            });
        // Offline rows: first the sequence file's saved roster (name + order preserved),
        // then any step target still unaccounted for (e.g. a pre-roster file), labeled by
        // its hex id. A droid that comes online later simply takes over its row as live.
        foreach (var ft in _fileTracks)
            if (ft.Id != 0xFFFF && Tracks.All(t => t.Id != ft.Id))
                Tracks.Add(new TimelineTrack
                {
                    Id = ft.Id, Label = ft.Name.Length > 0 ? ft.Name : $"{ft.Id:X4}", Role = "OFFLINE",
                    RowIndex = row++, Muted = mutedIds.Contains(ft.Id),
                });
        foreach (var target in Steps.Select(s => s.Target).Distinct())
            if (target != 0xFFFF && Tracks.All(t => t.Id != target))
                Tracks.Add(new TimelineTrack
                {
                    Id = target, Label = $"{target:X4}", Role = "OFFLINE",
                    RowIndex = row++, Muted = mutedIds.Contains(target),
                });
        ArmedTrack = armedId.HasValue ? Tracks.FirstOrDefault(t => t.Id == armedId.Value) : null;
        OnPropertyChanged(nameof(TracksHeightPx));
        // Tracks are wholesale-replaced (new instances) — the inspector's Target combo holds a
        // reference into the old generation via SelectedStepTrack and must re-resolve against
        // the new one, or it silently shows nothing selected even though Target itself is fine.
        OnPropertyChanged(nameof(SelectedStepTrack));
    }

    [RelayCommand]
    private void ArmTrack(TimelineTrack? track) => ArmedTrack = track;

    // Play only affects this — there's no other playback path left. (Historical note: when a
    // separate hardware-`seqRun`-backed Play still existed, mute couldn't touch it — the master
    // replayed its own NVS-stored steps from its own loop() with no per-step veto from the
    // console. That path is gone; Play is now entirely console-driven, so mute applies cleanly.)
    [RelayCommand]
    private void ToggleMute(TimelineTrack? track)
    {
        if (track == null) return;
        track.Muted = !track.Muted;
    }

    private bool IsTrackMuted(ushort targetId) => Tracks.FirstOrDefault(t => t.Id == targetId)?.Muted ?? false;

    // Maps a Y pixel inside TracksCanvas to the track row under it — used both for dragging a
    // gesture clip onto another droid's row and for dropping one from the gesture library.
    public TimelineTrack? TrackAtY(double y)
    {
        if (Tracks.Count == 0) return null;
        // Rows are contiguous now (RowGap 0) — Floor maps [rowTop, rowBottom) to the row,
        // where the old Round only made sense with a gap between rows.
        var idx = (int)Math.Floor(y / (TimelineTrack.RowHeight + TimelineTrack.RowGap));
        idx = Math.Clamp(idx, 0, Tracks.Count - 1);
        return Tracks.ElementAtOrDefault(idx) ?? Tracks.FirstOrDefault();
    }

    // Maps a Y pixel inside the audio-lanes ItemsControl to the lane under it — used for
    // dragging an audio clip from one lane to another (SequenceTimelineView.xaml.cs).
    public AudioLane? AudioLaneAtY(double y)
    {
        if (AudioLanes.Count == 0) return null;
        var idx = (int)Math.Floor(y / (AudioLane.RowHeight + AudioLane.RowGap));
        idx = Math.Clamp(idx, 0, AudioLanes.Count - 1);
        return AudioLanes.ElementAtOrDefault(idx) ?? AudioLanes.FirstOrDefault();
    }

    // Moves a clip from whichever lane currently holds it to targetLane, preserving its StartMs
    // (already live-updated by the drag) — a no-op if it's already there. Called once at
    // drag-end, not per MouseMove: each lane's clips render in that lane's own Canvas, so a
    // mid-drag move would mean re-parenting the visual element every pixel.
    public void MoveAudioClipToLane(AudioClip clip, AudioLane targetLane)
    {
        if (!CanEditSequence) return;
        var currentLane = AudioLanes.FirstOrDefault(l => l.Clips.Contains(clip));
        if (currentLane == null || ReferenceEquals(currentLane, targetLane)) return;
        ExecuteSequenceEdit(() =>
        {
            currentLane.Clips.Remove(clip);
            targetLane.Clips.Add(clip);
        });
    }

    // Public: also read by the view's "Fit" zoom handler (SequenceTimelineView.xaml.cs).
    // Uses each step's REAL gesture duration (getAnimDurations) rather than a fixed 1.5s
    // tail — the old flat tail under-measured long gestures (TALK ~4s), which is why "Fit"
    // kept cutting a sliver off the right edge of the last clip.
    public double TotalDurationMs()
    {
        var stepsEnd = Steps.Count == 0 ? 0 : Steps.Max(s =>
            s.StartMs + (AnimDurationMsLookup.TryGetValue(s.AnimId, out var d) ? d : 1500));
        var audioEnd = AudioLanes.SelectMany(l => l.Clips)
            .Select(c => (double)(c.StartMs + c.DurationMs)).DefaultIfEmpty(0).Max();
        return Math.Max(stepsEnd, audioEnd);
    }

    private void RebuildRulerTicks()
    {
        RulerTicks.Clear();
        if (PxPerMs > 0)
        {
            // Ticks (and therefore the gridlines bound to them) cover the whole DRAWN width —
            // viewport floor included — not just the sequence's own duration, so the grid
            // never stops in a stub partway across ("la trame reste en pleine longueur").
            var endMs = Math.Max(TotalDurationMs(), TimelineWidthPx / PxPerMs);
            int[] niceIntervals = { 100, 200, 500, 1000, 2000, 5000, 10000 };
            var interval = niceIntervals.FirstOrDefault(i => i * PxPerMs >= 50, niceIntervals[^1]);
            for (double t = 0; t <= endMs; t += interval)
            {
                var major = (long)t % (interval * 5) == 0;
                RulerTicks.Add(new TimelineTick
                {
                    Left = t * PxPerMs,
                    Major = major,
                    Label = major ? (t / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s" : "",
                });
            }
        }
        OnPropertyChanged(nameof(TimelineWidthPx));
        OnPropertyChanged(nameof(TimecodeNowText));
        OnPropertyChanged(nameof(TimecodeTotalText));
    }

    public int RoundToGrid(double ms) => SnapToGrid ? (int)(Math.Round(ms / 100.0) * 100) : (int)ms;

    // A drag spans multiple mouse events, so it owns a long-lived edit transaction. Transient
    // Dragging/DragOffsetY fields are absent from snapshots; a click or a move that returns to
    // its origin therefore commits as a true no-op instead of polluting Undo.
    public bool BeginStepDrag() => BeginSequenceEdit();
    public bool BeginAudioClipDrag() => BeginSequenceEdit();
    public bool CompleteDragEdit() => CommitSequenceEdit();

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void InsertGesture(int animId) =>
        InsertGestureAt(animId, ArmedTrack ?? Tracks.FirstOrDefault(), Math.Max(0, RoundToGrid(PlayheadMs)));

    // Called directly from code-behind (not bound in XAML) when a gesture-library chip is
    // dropped on a specific track+time cell instead of just clicked.
    public void InsertGestureAt(int animId, TimelineTrack? track, int startMs)
    {
        if (!CanEditSequence) return;
        ExecuteSequenceEdit(() =>
        {
            var step = new SequenceStep { AnimId = animId, Target = track?.Id ?? 0xFFFF, StartMs = Math.Max(0, startMs) };
            Steps.Add(step);
            SelectedStep = step;
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void NudgeStartForward()
    {
        if (!CanEditSequence || SelectedStep == null) return;
        ExecuteSequenceEdit(() => SelectedStep.StartMs += 100);
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void NudgeStartBackward()
    {
        if (!CanEditSequence || SelectedStep == null) return;
        ExecuteSequenceEdit(() => SelectedStep.StartMs = Math.Max(0, SelectedStep.StartMs - 100));
    }

    // --- Audio lanes/clips -------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void AddAudioLane()
    {
        if (!CanEditSequence) return;
        ExecuteSequenceEdit(() =>
            AudioLanes.Add(new AudioLane { Label = $"AUDIO {AudioLanes.Count + 1}", RowIndex = AudioLanes.Count }));
    }

    // Any lane can be deleted, the two seeded ones (AMBIENT/AUDIO) included — but a lane
    // that still holds clips asks first (direct user request). Undo restores lane + clips.
    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DeleteAudioLane(AudioLane? lane)
    {
        if (!CanEditSequence || lane == null || !AudioLanes.Contains(lane)) return;
        if (lane.Clips.Count > 0)
        {
            var res = MessageBox.Show(
                $"Lane \"{lane.Label}\" still holds {lane.Clips.Count} audio clip(s) — delete the lane and its clips?",
                "Delete audio lane", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }
        ExecuteSequenceEdit(() =>
        {
            AudioLanes.Remove(lane);
            for (var i = 0; i < AudioLanes.Count; i++) AudioLanes[i].RowIndex = i;
        });
    }

    // Empties the timeline (all gestures + all audio clips; the lanes themselves stay).
    // Asks first when there are unsaved changes; still one Undo away either way.
    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void ClearTimeline()
    {
        if (!CanEditSequence) return;
        if (Steps.Count == 0 && AudioLanes.All(l => l.Clips.Count == 0)) return;
        if (Dirty)
        {
            var res = MessageBox.Show(
                "The current sequence has unsaved changes — clear the whole timeline anyway?",
                "Clear timeline", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }
        ExecuteSequenceEdit(() =>
        {
            Steps.Clear();
            foreach (var lane in AudioLanes) lane.Clips.Clear();
            SelectedStep = null;
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private async Task AddAudioClip(AudioLane? lane)
    {
        if (!CanEditSequence) return;
        lane ??= AudioLanes.FirstOrDefault();
        if (lane == null) return;
        var dlg = new OpenFileDialog { Filter = AudioFileFilter };
        if (dlg.ShowDialog() != true) return;
        var durationMs = await AudioPlaybackService.ProbeDurationMsAsync(dlg.FileName);
        // The duration probe yields to WPF. Playback may have started while it was open; in that
        // case the edit lock wins and the picked file is simply not inserted into the active pass.
        if (!CanEditSequence || !AudioLanes.Contains(lane)) return;
        var clip = new AudioClip
        {
            FilePath = dlg.FileName,
            DurationMs = durationMs,
            StartMs = Math.Max(0, RoundToGrid(PlayheadMs)),
        };
        InsertAudioClip(lane, clip);
        _ = LoadWaveformAsync(clip);
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private async Task ReplaceAudioClip(AudioClip? clip)
    {
        if (!CanEditSequence || clip == null) return;
        var dlg = new OpenFileDialog { Filter = AudioFileFilter };
        if (dlg.ShowDialog() != true) return;
        var replacementPath = dlg.FileName;
        var replacementDurationMs = await AudioPlaybackService.ProbeDurationMsAsync(replacementPath);
        if (!CanEditSequence || !AudioLanes.Any(l => l.Clips.Contains(clip))) return;
        ReplaceAudioClipSource(clip, replacementPath, replacementDurationMs);
        _ = LoadWaveformAsync(clip);
    }

    internal bool InsertAudioClip(AudioLane lane, AudioClip clip)
    {
        if (!CanEditSequence || !AudioLanes.Contains(lane) ||
            AudioLanes.Any(existing => existing.Clips.Contains(clip))) return false;
        return ExecuteSequenceEdit(() => lane.Clips.Add(clip));
    }

    internal bool ReplaceAudioClipSource(AudioClip clip, string path, int durationMs)
    {
        if (!CanEditSequence || !AudioLanes.Any(lane => lane.Clips.Contains(clip))) return false;
        return ExecuteSequenceEdit(() =>
        {
            clip.FilePath = path;
            clip.Peaks = null; // stale for the new file until the fresh decode below completes
            clip.DurationMs = durationMs;
        });
    }

    // Fire-and-forget from every clip-creation path (Add/Replace/load) — decoding happens off
    // the UI thread in WaveformService; only the final property write is marshalled back.
    private async Task LoadWaveformAsync(AudioClip clip)
    {
        var peaks = await WaveformService.GetPeaksAsync(clip.FilePath);
        RunOnUiThread(() => clip.Peaks = peaks);
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DeleteAudioClip(AudioClip? clip)
    {
        if (!CanEditSequence || clip == null) return;
        var lane = AudioLanes.FirstOrDefault(l => l.Clips.Contains(clip));
        if (lane == null) return;
        ExecuteSequenceEdit(() => lane.Clips.Remove(clip));
    }

    private List<AudioLaneDto> AudioLanesToDto() => AudioLanes.Select(l => new AudioLaneDto
    {
        Label = l.Label,
        Clips = l.Clips.Select(c => new AudioClipDto { FilePath = c.FilePath, DurationMs = c.DurationMs, StartMs = c.StartMs, Loop = c.Loop }).ToList(),
    }).ToList();

    // Null/empty falls back to the default two lanes — used both for a brand-new sequence and
    // for a slot/library item that predates this feature (or simply never had audio attached).
    private void ApplyAudioLanesFromDto(List<AudioLaneDto>? dtos)
    {
        AudioLanes.Clear();
        if (dtos == null || dtos.Count == 0)
        {
            AudioLanes.Add(new AudioLane { Label = "AMBIENT", RowIndex = 0 });
            AudioLanes.Add(new AudioLane { Label = "AUDIO", RowIndex = 1 });
            return;
        }
        var row = 0;
        foreach (var dto in dtos)
        {
            var lane = new AudioLane { Label = dto.Label, RowIndex = row++ };
            foreach (var c in dto.Clips)
            {
                var clip = new AudioClip { FilePath = c.FilePath, DurationMs = c.DurationMs, StartMs = c.StartMs, Loop = c.Loop };
                lane.Clips.Add(clip);
                _ = LoadWaveformAsync(clip);
            }
            AudioLanes.Add(lane);
        }
    }

    // --- Playhead: local scrub + live hardware sync -----------------------------

    public void SetPlayheadFromPixel(double x)
    {
        if (IsLiveTracking || PxPerMs <= 0) return;
        PlayheadMs = Math.Max(0, x / PxPerMs);
    }

    // Anchors the playhead ticker at fromElapsedMs and starts advancing it — used by the
    // console-driven Play/Resume path below. (The old `seqState` hardware reflection was
    // removed with the rest of the ESP32 slot machinery, fw 1.7.0.)
    private void StartPlayheadTicker(double fromElapsedMs)
    {
        _playbackClock.Restart();
        _liveAnchorElapsedMs = fromElapsedMs;
        PlayheadMs = fromElapsedMs;
        if (_playheadTimer == null)
        {
            _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _playheadTimer.Tick += (_, _) =>
                PlayheadMs = _liveAnchorElapsedMs + _playbackClock.Elapsed.TotalMilliseconds;
        }
        _playheadTimer.Start();
    }

    private void StopPlayheadTimer() => _playheadTimer?.Stop();

    // --- /Timeline ---------------------------------------------------------------

    private void RefreshLibrary()
    {
        Library.Clear();
        foreach (var item in _library.List()) Library.Add(item);
    }

    // --- Snapshot / undo-redo --------------------------------------------------

    private SequenceSnapshot Snapshot() => new(Name, Loop, AudioLanesToDto(),
        Steps.Select(s => new SequenceStepDto { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs }).ToList());

    private static bool SnapshotsEqual(SequenceSnapshot left, SequenceSnapshot right)
    {
        if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
            left.Loop != right.Loop ||
            left.Steps.Count != right.Steps.Count ||
            left.AudioLanes.Count != right.AudioLanes.Count)
            return false;

        for (var i = 0; i < left.Steps.Count; i++)
        {
            var a = left.Steps[i];
            var b = right.Steps[i];
            if (a.AnimId != b.AnimId || a.Target != b.Target || a.StartMs != b.StartMs)
                return false;
        }

        for (var laneIndex = 0; laneIndex < left.AudioLanes.Count; laneIndex++)
        {
            var aLane = left.AudioLanes[laneIndex];
            var bLane = right.AudioLanes[laneIndex];
            if (!string.Equals(aLane.Label, bLane.Label, StringComparison.Ordinal) ||
                aLane.Clips.Count != bLane.Clips.Count)
                return false;
            for (var clipIndex = 0; clipIndex < aLane.Clips.Count; clipIndex++)
            {
                var a = aLane.Clips[clipIndex];
                var b = bLane.Clips[clipIndex];
                if (!string.Equals(a.FilePath, b.FilePath, StringComparison.Ordinal) ||
                    a.DurationMs != b.DurationMs || a.StartMs != b.StartMs || a.Loop != b.Loop)
                    return false;
            }
        }
        return true;
    }

    private bool BeginSequenceEdit()
    {
        if (!CanEditSequence || _activeEditBefore != null) return false;
        _activeEditBefore = Snapshot();
        return true;
    }

    private bool CommitSequenceEdit()
    {
        if (_activeEditBefore == null) return false;
        var before = _activeEditBefore;
        _activeEditBefore = null;
        var after = Snapshot();
        if (SnapshotsEqual(before, after)) return false;

        PushHistory(before);
        Dirty = true;
        RefreshDerivedTimelineState();
        return true;
    }

    private bool ExecuteSequenceEdit(Action mutation)
    {
        if (!CanEditSequence) return false;
        var ownsTransaction = _activeEditBefore == null;
        if (ownsTransaction && !BeginSequenceEdit()) return false;
        var committed = false;
        try
        {
            mutation();
        }
        finally
        {
            if (ownsTransaction) committed = CommitSequenceEdit();
        }
        return !ownsTransaction || committed;
    }

    private void RefreshDerivedTimelineState()
    {
        RebuildTracks();
        RebuildRulerTicks();
    }

    private void Apply(SequenceSnapshot snap)
    {
        _suppressTimelineRefresh = true;
        try
        {
            Name = snap.Name;
            Loop = snap.Loop;
            ApplyAudioLanesFromDto(snap.AudioLanes);
            Steps.Clear();
            foreach (var s in snap.Steps)
                Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
            SelectedStep = null;
        }
        finally
        {
            _suppressTimelineRefresh = false;
        }
        Dirty = true;
        RefreshDerivedTimelineState();
    }

    private void PushHistory(SequenceSnapshot before)
    {
        _history.Push(before);
        while (_history.Count > HistoryMax) { /* Stack has no RemoveAt: > 50 is tolerated on this minimal port */ break; }
        _future.Clear();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        CanUndo = _history.Count > 0;
        CanRedo = _future.Count > 0;
    }

    private bool CanUndoEdit() => CanEditSequence && CanUndo;

    [RelayCommand(CanExecute = nameof(CanUndoEdit))]
    private void Undo()
    {
        if (!CanEditSequence || _history.Count == 0) return;
        _future.Push(Snapshot());
        Apply(_history.Pop());
        UpdateUndoButtons();
    }

    private bool CanRedoEdit() => CanEditSequence && CanRedo;

    [RelayCommand(CanExecute = nameof(CanRedoEdit))]
    private void Redo()
    {
        if (!CanEditSequence || _future.Count == 0) return;
        _history.Push(Snapshot());
        Apply(_future.Pop());
        UpdateUndoButtons();
    }

    private void ClearHistory()
    {
        _history.Clear();
        _future.Clear();
        UpdateUndoButtons();
    }

    // --- Editing ----------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DeleteStep(SequenceStep? step)
    {
        if (!CanEditSequence || step == null || !Steps.Contains(step)) return;
        ExecuteSequenceEdit(() =>
        {
            Steps.Remove(step);
            if (SelectedStep == step) SelectedStep = null;
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DuplicateStep(SequenceStep? step)
    {
        if (!CanEditSequence || step == null || !Steps.Contains(step)) return;
        ExecuteSequenceEdit(() =>
        {
            var clone = step.Clone();
            // Nudged right and selected so the new clip is visibly a new arrival instead of
            // landing invisibly right on top of the original (direct user request).
            clone.StartMs += 200;
            var idx = Steps.IndexOf(step);
            Steps.Insert(idx + 1, clone);
            SelectedStep = clone;
        });
    }

    // (The whole "Firmware: 8 NVS slots" region — LoadSlot/SaveToSlot/DeleteSlot/PushToMaster/
    // PullFromMaster and the slot-audio store — was removed 2026-07-16: sequences are
    // console-only now, and fw 1.7.0 dropped the slot machinery too.)

    // --- Local library ------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void LoadFromLibrary(SequenceLibraryItem? item)
    {
        if (!CanEditSequence || item == null) return;
        Name = item.Name;
        Loop = item.Loop;
        _fileTracks.Clear();
        _fileTracks.AddRange(item.Tracks);
        ApplyAudioLanesFromDto(item.AudioLanes);
        Steps.Clear();
        foreach (var s in item.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
        RebuildTracks();
        SelectedStep = null;
        ClearHistory();
        Dirty = false;
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DeleteFromLibrary(SequenceLibraryItem? item)
    {
        if (!CanEditSequence || item == null) return;
        _library.Delete(item.Id);
        RefreshLibrary();
    }

    // --- Export / import ----------------------------------------------------------

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog { FileName = $"{(string.IsNullOrEmpty(Name) ? "sequence" : Name)}.b1seq.json", Filter = "B1 Sequence (*.b1seq.json)|*.b1seq.json" };
        if (dlg.ShowDialog() != true) return;
        var obj = new JsonObject
        {
            ["type"] = "b1-sequence", ["version"] = 4, ["name"] = Name, ["loop"] = Loop,
            // Droid roster (id + name, row order): re-imported on a console with the fleet
            // unplugged, every step still gets its own named row instead of one flat line.
            ["tracks"] = new JsonArray(Tracks.Where(t => !t.IsBroadcast)
                .Select(t => (JsonNode)new JsonObject { ["id"] = t.Id, ["name"] = t.Label }).ToArray()),
            // Local-machine paths only (no audio bytes travel with the export) — a reasonable
            // best-effort round-trip on the same console install, per CLAUDE.md's console-side
            // audio decision; harmless dangling reference if imported elsewhere.
            ["audioLanes"] = new JsonArray(AudioLanes.Select(l => (JsonNode)new JsonObject
            {
                ["label"] = l.Label,
                ["clips"] = new JsonArray(l.Clips.Select(c => (JsonNode)new JsonObject
                {
                    ["filePath"] = c.FilePath, ["durationMs"] = c.DurationMs, ["startMs"] = c.StartMs, ["loop"] = c.Loop,
                }).ToArray()),
            }).ToArray()),
            ["steps"] = new JsonArray(Steps.Select(s => (JsonNode)new JsonObject { ["animId"] = s.AnimId, ["target"] = s.Target, ["startMs"] = s.StartMs }).ToArray()),
        };
        File.WriteAllText(dlg.FileName, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _settings.SetLastSequencePath(dlg.FileName);
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void Import()
    {
        if (!CanEditSequence) return;
        var dlg = new OpenFileDialog { Filter = "B1 Sequence (*.b1seq.json)|*.b1seq.json|JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ImportFrom(dlg.FileName);
            _settings.SetLastSequencePath(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Import failed: " + ex.Message, "Sequencer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Restores whatever sequence was last exported/imported, so the console resumes exactly
    // where the previous session left off instead of starting blank. Silent on failure (a
    // missing/corrupt file at startup shouldn't pop a dialog before the app has even settled).
    public void TryLoadLastSequence()
    {
        var path = _settings.LastSequencePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { ImportFrom(path); }
        catch { /* stale/corrupt last-sequence file: start with an empty sequence instead */ }
    }

    private void ImportFrom(string path)
    {
        var obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (obj == null) return;
        Name = obj["name"]?.GetValue<string>() ?? "";
        Loop = obj["loop"]?.GetValue<bool>() ?? false;
        _fileTracks.Clear();
        if (obj["tracks"] is JsonArray trackArr)
            foreach (var tn in trackArr)
                if (tn is JsonObject to)
                    _fileTracks.Add(new SequenceTrackDto
                    {
                        Id = to["id"]?.GetValue<ushort>() ?? 0xFFFF,
                        Name = to["name"]?.GetValue<string>() ?? "",
                    });
        List<AudioLaneDto>? lanes = null;
        if (obj["audioLanes"] is JsonArray laneArr)
        {
            lanes = new List<AudioLaneDto>();
            foreach (var ln in laneArr)
                if (ln is JsonObject lo)
                {
                    var laneDto = new AudioLaneDto { Label = lo["label"]?.GetValue<string>() ?? "AUDIO" };
                    if (lo["clips"] is JsonArray clipArr)
                        foreach (var cl in clipArr)
                            if (cl is JsonObject co)
                                laneDto.Clips.Add(new AudioClipDto
                                {
                                    FilePath = co["filePath"]?.GetValue<string>() ?? "",
                                    DurationMs = co["durationMs"]?.GetValue<int>() ?? 0,
                                    StartMs = co["startMs"]?.GetValue<int>() ?? 0,
                                    Loop = co["loop"]?.GetValue<bool>() ?? false,
                                });
                    lanes.Add(laneDto);
                }
        }
        ApplyAudioLanesFromDto(lanes);
        Steps.Clear();
        if (obj["steps"] is JsonArray arr)
            foreach (var st in arr)
                if (st is JsonObject so)
                    // "delayMs": pre-timeline export (schema version 1) — read back as a
                    // start offset, not a relative delay; not equivalent, but a reasonable
                    // best-effort rather than silently dropping the step.
                    Steps.Add(new SequenceStep
                    {
                        AnimId = so["animId"]?.GetValue<int>() ?? 0,
                        Target = so["target"]?.GetValue<ushort>() ?? 0xFFFF,
                        StartMs = so["startMs"]?.GetValue<int>() ?? so["delayMs"]?.GetValue<int>() ?? 0,
                    });
        RebuildTracks();
        SelectedStep = null;
        ClearHistory();
        Dirty = false;
    }

    // --- Playback (client-side: real anim/audio commands, nothing stored) --------
    //
    // Unifies what used to be two separate paths — a hardware-`seqRun`-backed Play/Stop/
    // Pause/Resume (told the master to replay its own NVS-stored sequence) and a separate
    // "Rehearse (local)" toggle (the console scheduled its own timers, no NVS save needed,
    // firing real per-step `anim` mesh commands plus local audio, but no pause/resume and no
    // playhead feedback). Play now works directly on whatever's in the editor (no CurrentSlot/
    // save required, like Rehearse did) and drives the exact same real commands + audio, with
    // genuine pause/resume on top.

    // Play doubles as Resume (the dedicated ⏵ button was removed on request): pressed while
    // paused it picks up exactly where Pause left off; otherwise it (re)starts from t=0.
    [RelayCommand]
    private void Play()
    {
        if (IsPaused)
        {
            if (_activePlaybackPlan == null) { Stop(); return; }
            StartPlaybackPass(_elapsedAtPauseMs, resumeAudio: true);
            return;
        }
        if (Steps.Count == 0 && AudioLanes.All(l => l.Clips.Count == 0)) return;
        _playbackGeneration.Cancel();
        DisposePlaybackTimers();
        _audioPlayer.StopAll(); // Play pressed mid-playback restarts clean, no overlapped audio
        StopInfiniteGestures(); // a restart must not inherit TALK/POWER_DOWN from the old pass
        ResetExecutionTracking();
        _activePlaybackPlan = SequencerPlaybackPlan.Capture(
            Steps, AudioLanes, AnimDurationMsLookup, Loop);
        _dispatchedPlaybackEvents.Clear();
        _elapsedAtPauseMs = 0;
        StartPlaybackPass(0, resumeAudio: false);
    }

    [RelayCommand]
    private void Stop()
    {
        StopTransportCore();
        StopInfiniteGestures();
    }

    [RelayCommand]
    private void SafeStop()
    {
        StopTransportCore();
        CancelAllAnimLeases();
        var state = _protocol.SupportsSafeStop
            ? _protocol.SafeStop(ushort.MaxValue)
            : _protocol.PlayAnim(ushort.MaxValue, 0,
                (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1)).State;
        if (state == AnimDispatchState.Written) _latestGestureByDroid.Clear();
    }

    [RelayCommand]
    private void EmergencyStop()
    {
        StopTransportCore();
        CancelAllAnimLeases();
        _latestGestureByDroid.Clear();
        // Deliberately no confirmation dialog: an emergency control must act on
        // the first click. Servo OFF is persisted by each droid's firmware.
        _protocol.SetServo(ushort.MaxValue, false);
    }

    private void StopTransportCore()
    {
        _playbackGeneration.Cancel();
        TransitionTransportTo(SequencerTransportState.Stopped);
        DisposePlaybackTimers();
        _audioPlayer.StopAll();
        _activePlaybackPlan = null;
        _dispatchedPlaybackEvents.Clear();
        StopPlayheadTimer();
        PlayheadMs = 0;
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (!CanPause()) return;
        var elapsed = _liveAnchorElapsedMs + _playbackClock.Elapsed.TotalMilliseconds;
        _playbackGeneration.Cancel();
        TransitionTransportTo(SequencerTransportState.Paused);
        DisposePlaybackTimers();
        _audioPlayer.PauseAll(); // clips already mid-playback keep their position natively
        _elapsedAtPauseMs = (int)elapsed;
        StopPlayheadTimer();
        PlayheadMs = elapsed;
    }

    private bool CanPause() => TransportState == SequencerTransportState.Playing;

    private void StartPlaybackPass(int fromMs, bool resumeAudio)
    {
        var plan = _activePlaybackPlan
            ?? throw new InvalidOperationException("Cannot start Sequencer transport without a playback plan.");
        var generation = _playbackGeneration.Begin();
        try
        {
            TransitionTransportTo(SequencerTransportState.Playing);
            if (resumeAudio)
                _audioPlayer.ResumeAll(); // continues from each clip's retained position, no seek math
            ScheduleTimers(plan, fromMs, generation);
            StartPlayheadTicker(fromMs);
        }
        catch
        {
            // A partial timer/audio start must never leave the UI claiming that transport is live.
            StopTransportCore();
            throw;
        }
    }

    // Absolute-time model (FIRMWARE-CONTRACT.md §6): the immutable pass plan schedules each
    // event at StartMs relative to fromMs. Every callback checks its generation after reaching
    // the UI thread, because disposing a timer cannot retract a callback already queued there.
    // Track mute is deliberately evaluated at dispatch time so it remains useful mid-pass.
    private void ScheduleTimers(SequencerPlaybackPlan plan, int fromMs, long generation)
    {
        DisposePlaybackTimers();
        foreach (var playbackEvent in plan.Events)
        {
            // At an exact Pause boundary, the timer may already have dispatched while its
            // StartMs still equals fromMs. Keep consumed source IDs across Resume so that event
            // is not sent twice; muted events count as consumed as well because their moment has
            // elapsed. A fresh Play or whole-pass Loop clears the set.
            if (_dispatchedPlaybackEvents.Contains(playbackEvent.SourceOrder)) continue;
            // A callback can still be waiting for the UI dispatcher even after its nominal
            // StartMs. If Pause cancels it first, it remains undispatched and must run
            // immediately on Resume instead of being lost merely because time moved past it.
            var dueTimeMs = Math.Max(0, playbackEvent.StartMs - fromMs);
            var timer = _timerScheduler.Schedule(dueTimeMs, () => RunOnUiThread(() =>
            {
                if (!_playbackGeneration.IsCurrent(generation) ||
                    TransportState != SequencerTransportState.Playing) return;
                _dispatchedPlaybackEvents.Add(playbackEvent.SourceOrder);
                switch (playbackEvent)
                {
                    case GesturePlaybackEvent gesture when !IsTrackMuted(gesture.Target):
                        var leaseMs = _protocol.SupportsAnimLease && gesture.AnimId is 16 or 17
                            ? InfiniteAnimLeaseMs
                            : (ushort)0;
                        var dispatch = _protocol.PlayAnim(
                            gesture.Target, gesture.AnimId, gesture.Seed, leaseMs);
                        TrackExecution(dispatch, gesture);
                        TrackAnimLease(dispatch, gesture, leaseMs);
                        break;
                    case AudioPlaybackEvent audio:
                        _audioPlayer.Play(audio.FilePath, audio.Loop);
                        break;
                }
            }));
            _playbackTimers.Add(timer);
        }
        var totalMs = plan.TotalDurationMs;
        var endDelay = Math.Max(0, totalMs - fromMs);
        var endTimer = _timerScheduler.Schedule(endDelay, () => RunOnUiThread(() =>
        {
            if (!_playbackGeneration.IsCurrent(generation) ||
                TransportState != SequencerTransportState.Playing) return;
            if (plan.Loop)
            {
                // Stop every player (including a looping ambient clip) before rearming the next
                // pass, or it would keep stacking a fresh MediaPlayer on top of the still-running
                // one every time the sequence loops.
                _audioPlayer.StopAll();
                _elapsedAtPauseMs = 0;
                _dispatchedPlaybackEvents.Clear();
                ResetExecutionTracking();
                StartPlaybackPass(0, resumeAudio: false);
            }
            else Stop();
        }));
        _playbackTimers.Add(endTimer);
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action);
    }

    private void DisposePlaybackTimers()
    {
        foreach (var t in _playbackTimers) t.Dispose();
        _playbackTimers.Clear();
    }
}
