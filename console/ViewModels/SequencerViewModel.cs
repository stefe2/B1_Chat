using System.Collections.ObjectModel;
using System.IO;
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
    private readonly ISequencerSettings _settings;
    private readonly ISequenceLibraryService _library;
    private readonly ISequencerAudioPlayer _audioPlayer;
    private readonly IAudioProbe _audioProbe;
    private readonly IWaveformDecoder _waveformDecoder;
    private readonly IPlaybackWakeScheduler _timerScheduler;
    private readonly IPlaybackTimerScheduler _executionTimerScheduler;
    private readonly IPlaybackClock _playbackClock;
    private readonly ISequencerPersistenceDialogs _persistenceDialogs;
    private readonly IAtomicTextFileWriter _atomicFileWriter;
    private const int ExecutionStartTimeoutMs = 1500;
    private const int ExecutionCompletionGraceMs = 1500;
    private const ushort InfiniteAnimLeaseMs = 5000;
    private const int InfiniteAnimLeaseRenewMs = 2000;
    internal const int MaxRulerTickCount = 600;
    private const double MinimumRulerTickSpacingPx = 50;
    private static readonly int[] RulerIntervalsMs =
    {
        100, 200, 500,
        1_000, 2_000, 5_000, 10_000, 15_000, 30_000,
        60_000, 120_000, 300_000, 600_000, 900_000, 1_800_000,
        3_600_000, 7_200_000, 14_400_000, 21_600_000, 43_200_000, 86_400_000,
    };
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
    private double _totalDurationMs;
    public double TotalDurationMsValue => _totalDurationMs;
    private int _calculatedContentEndMs;
    public int CalculatedContentEndMs => _calculatedContentEndMs;

    [ObservableProperty] private TimelineTrack? _armedTrack;
    [ObservableProperty] private double _pxPerSecond = 80;
    [ObservableProperty] private bool _snapToGrid = true;
    [ObservableProperty] private bool _followPlayhead = true;
    [ObservableProperty] private double _playheadMs;
    [ObservableProperty] private int? _sequenceEndMs;

    public bool HasManualSequenceEnd => SequenceEndMs.HasValue;
    public string SequenceEndModeText => HasManualSequenceEnd ? "END SET" : "END AUTO";
    public string SequenceEndToolTip => HasManualSequenceEnd
        ? $"Manual Scene endpoint: {FormatTimecode(TotalDurationMsValue)}. Content is never truncated; Auto returns to the calculated tail."
        : $"Automatic Scene endpoint follows the calculated content tail: {FormatTimecode(TotalDurationMsValue)}.";

    private SequencerTransportState _transportState = SequencerTransportState.Stopped;
    public SequencerTransportState TransportState => _transportState;
    public bool IsPlaying => TransportState == SequencerTransportState.Playing;
    public bool IsPaused => TransportState == SequencerTransportState.Paused;
    public bool IsLiveTracking => TransportState == SequencerTransportState.Playing;
    public string PrimaryTransportGlyph => IsPlaying ? "⏸" : "▶";
    public string PrimaryTransportToolTip => TransportState switch
    {
        SequencerTransportState.Playing => "Pause playback (Space). Droid motion already dispatched continues.",
        SequencerTransportState.Paused => "Resume playback from the paused position (Space).",
        _ => "Play from the current playhead (Space). Use Restart to play explicitly from the beginning.",
    };

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
    public string TimecodeTotalText => $" / {FormatTimecode(TotalDurationMsValue)}";
    partial void OnPlayheadMsChanged(double value)
    {
        OnPropertyChanged(nameof(TimecodeNowText));
        OnPropertyChanged(nameof(TimecodeTotalText));
        ReturnToStartCommand.NotifyCanExecuteChanged();
        SetSequenceEndAtPlayheadCommand.NotifyCanExecuteChanged();
    }

    partial void OnSequenceEndMsChanged(int? value)
    {
        OnPropertyChanged(nameof(HasManualSequenceEnd));
        OnPropertyChanged(nameof(SequenceEndModeText));
        OnPropertyChanged(nameof(SequenceEndToolTip));
        UseAutomaticSequenceEndCommand.NotifyCanExecuteChanged();
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
    private bool _dirty;
    public bool Dirty
    {
        get => _dirty;
        private set
        {
            if (!SetProperty(ref _dirty, value)) return;
            OnPropertyChanged(nameof(SequenceBadgeText));
            OnPropertyChanged(nameof(SceneDisplayName));
            OnPropertyChanged(nameof(SceneDocumentStateText));
        }
    }

    private SequencerDocumentOrigin _documentOrigin = SequencerDocumentOrigin.New;
    private string? _currentSceneId;
    private int _libraryIssueCount;
    private IReadOnlyList<SequenceLibraryIssue> _libraryIssues = Array.Empty<SequenceLibraryIssue>();
    public SequencerDocumentOrigin DocumentOrigin => _documentOrigin;
    public string? CurrentSceneId => _currentSceneId;
    public string SceneOriginText => DocumentOrigin switch
    {
        SequencerDocumentOrigin.LocalLibrary => "LOCAL LIBRARY",
        SequencerDocumentOrigin.ExternalFile => "IMPORTED / EXTERNAL FILE",
        _ => "NEW",
    };
    public string LibraryStatusText => _libraryIssueCount == 0
        ? $"{Library.Count} scene{(Library.Count == 1 ? "" : "s")}"
        : $"{Library.Count} scene{(Library.Count == 1 ? "" : "s")} · {_libraryIssueCount} file issue{(_libraryIssueCount == 1 ? "" : "s")}";
    public string LibraryIssueText => _libraryIssueCount == 0
        ? "All Local Library files are readable."
        : string.Join(Environment.NewLine, _libraryIssues.Select(issue => $"{issue.FileName}: {issue.Message}"));
    public string SequenceBadgeText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "UNTITLED" : $"\"{Name.ToUpperInvariant()}\"";
            var state = Dirty ? "MODIFIED" : DocumentOrigin == SequencerDocumentOrigin.New ? "CLEAN" : "SAVED";
            return $"{name} · {SceneOriginText} · {state}";
        }
    }
    public string SceneDisplayName => string.IsNullOrWhiteSpace(Name) ? "Untitled Scene" : Name.Trim();
    public string SceneDocumentStateText => Dirty
        ? "MODIFIED"
        : DocumentOrigin == SequencerDocumentOrigin.New ? "NEW" : "SAVED";
    public string EditableName
    {
        get => Name;
        set => SetSequenceName(value);
    }
    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(EditableName));
        OnPropertyChanged(nameof(SequenceBadgeText));
        OnPropertyChanged(nameof(SceneDisplayName));
    }
    public bool EditableLoop
    {
        get => Loop;
        set => SetSequenceLoop(value);
    }
    partial void OnLoopChanged(bool value) => OnPropertyChanged(nameof(EditableLoop));

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
        set { if (value != null && SelectedStep != null) SetStepTarget(SelectedStep, value.Id); }
    }

    public int SelectedStepAnimId
    {
        get => SelectedStep?.AnimId ?? 0;
        set { if (SelectedStep != null) SetStepAnimId(SelectedStep, value); }
    }

    public int SelectedStepEndAfterMs => SelectedStep?.EndAfterMs ?? 0;

    partial void OnSelectedStepChanged(SequenceStep? value)
    {
        OnPropertyChanged(nameof(SelectedStepTrack));
        OnPropertyChanged(nameof(SelectedStepAnimId));
        OnPropertyChanged(nameof(SelectedStepEndAfterMs));
    }

    private readonly SequencerEditHistory _editHistory = new();
    private IPlaybackWakeTimer? _playbackWakeTimer;
    private int _scheduledWakeAbsoluteMs;
    private int _nextPlaybackBatchIndex;
    private readonly HashSet<int> _dispatchedPlaybackEvents = new();
    private readonly Dictionary<uint, ExecutionTracker> _executionTrackers = new();
    private readonly Dictionary<ushort, GestureTargetState> _latestGestureByDroid = new();
    private readonly Dictionary<uint, ActiveAnimLease> _activeAnimLeases = new();
    private readonly Dictionary<int, uint> _infiniteRequestBySourceOrder = new();
    private readonly PlaybackGeneration _playbackGeneration = new();
    private SequencerPlaybackPlan? _activePlaybackPlan;
    private SequenceSnapshot? _savedCheckpoint;
    private bool _suppressTimelineRefresh;
    private int _elapsedAtPauseMs;
    private bool _disposed;
    private string _trackRosterSignature = "";
    private string _durationTargetSignature = "";
    private IReadOnlyList<SequencerScheduleWarning> _scheduleWarnings = Array.Empty<SequencerScheduleWarning>();
    public bool HasScheduleWarnings => _scheduleWarnings.Count > 0;
    public string ScheduleWarningText => string.Join(
        Environment.NewLine,
        _scheduleWarnings.Select(warning => $"{FormatTimecode(warning.StartMs)} — {warning.Message}"));

    // Audio clips that failed to play during the current pass (SEQ-F07). A failure no longer
    // passes in silence; it names the clip and survives until the next pass starts.
    private readonly List<string> _audioFailures = new();
    private readonly HashSet<(int ClipId, string FilePath)> _audioFailureKeys = new();
    private CancellationTokenSource? _audioAssetValidationCancellation;
    private int _audioAssetValidationGeneration;
    public bool HasAudioFailures => _audioFailures.Count > 0;
    public string AudioFailureText => string.Join(Environment.NewLine, _audioFailures);

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
        OnPropertyChanged(nameof(PrimaryTransportGlyph));
        OnPropertyChanged(nameof(PrimaryTransportToolTip));
        // Relay commands do not re-evaluate CanExecute automatically when a derived property
        // changes. Keep every transport-dependent command synchronized from this one source.
        PauseCommand.NotifyCanExecuteChanged();
        ReturnToStartCommand.NotifyCanExecuteChanged();
        RefreshEditAvailability();
    }

    private void RefreshEditAvailability()
    {
        OnPropertyChanged(nameof(CanEditSequence));
        InsertGestureCommand.NotifyCanExecuteChanged();
        NudgeStartForwardCommand.NotifyCanExecuteChanged();
        NudgeStartBackwardCommand.NotifyCanExecuteChanged();
        NudgeEndLongerCommand.NotifyCanExecuteChanged();
        NudgeEndShorterCommand.NotifyCanExecuteChanged();
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
        DeleteFromLibraryCommand.NotifyCanExecuteChanged();
        DeleteCurrentSceneCommand.NotifyCanExecuteChanged();
        SaveSceneCommand.NotifyCanExecuteChanged();
        SaveSceneAsCommand.NotifyCanExecuteChanged();
        ToggleAudioLoopCommand.NotifyCanExecuteChanged();
        SetSequenceEndAtPlayheadCommand.NotifyCanExecuteChanged();
        UseAutomaticSequenceEndCommand.NotifyCanExecuteChanged();
    }

    public SequencerViewModel(
        ISequencerProtocol protocol,
        ISequencerSettings settings,
        ISequencerAudioPlayer? audioPlayer = null,
        IPlaybackWakeScheduler? timerScheduler = null,
        IPlaybackClock? playbackClock = null,
        IPlaybackTimerScheduler? executionTimerScheduler = null,
        ISequencerPersistenceDialogs? persistenceDialogs = null,
        IAtomicTextFileWriter? atomicFileWriter = null,
        ISequenceLibraryService? library = null,
        IAudioProbe? audioProbe = null,
        IWaveformDecoder? waveformDecoder = null)
    {
        _protocol = protocol;
        _settings = settings;
        _audioPlayer = audioPlayer ?? new AudioPlaybackService();
        _audioProbe = audioProbe ?? new AudioProbe();
        _waveformDecoder = waveformDecoder ?? WaveformService.Shared;
        _audioPlayer.PlaybackFailed += OnAudioPlaybackFailed;
        _timerScheduler = timerScheduler ?? new ThreadPoolPlaybackTimerScheduler();
        _executionTimerScheduler = executionTimerScheduler ?? new ThreadPoolPlaybackTimerScheduler();
        _playbackClock = playbackClock ?? new StopwatchPlaybackClock();
        _persistenceDialogs = persistenceDialogs ?? new WpfSequencerPersistenceDialogs();
        _atomicFileWriter = atomicFileWriter ?? new AtomicTextFileWriter();
        _library = library ?? new LibraryService();
        _protocol.DroidsChanged += OnDroidsChanged;
        _protocol.AnimDurationsReceived += OnAnimDurationsReceived;
        _protocol.AnimConfigurationChanged += OnAnimConfigurationChanged;
        _protocol.AnimMasterAccepted += OnAnimMasterAccepted;
        _protocol.AnimExecutionReceived += OnAnimExecutionReceived;
        _protocol.LinkClosed += OnProtocolLinkClosed;
        Steps.CollectionChanged += (_, _) =>
        {
            if (!_editHistory.HasActiveEdit && !_suppressTimelineRefresh)
                RefreshDerivedTimelineState();
        };
        RebuildTracks();
        ApplyAudioLanesFromDto(null);
        RefreshDerivedTimelineState();
        RefreshLibrary();
        EstablishSavedCheckpoint();
    }

    private void OnDroidsChanged()
    {
        // Firmware publishes the complete inventory every ~1.5 s even when nothing relevant
        // changed. Rebuilding Tracks and thousands of ruler bindings on every heartbeat stalls
        // every UI animation (radar sweep and playhead) at that cadence. Track layout only
        // depends on identity/name/role; broadcast timing only depends on online membership.
        // Compare those two projections independently and leave the visual tree untouched when
        // a heartbeat merely refreshes age/RSSI/state already represented elsewhere.
        if (!string.Equals(_trackRosterSignature, TrackRosterSignature(), StringComparison.Ordinal))
            RebuildTracks();
        if (!string.Equals(_durationTargetSignature, DurationTargetSignature(), StringComparison.Ordinal))
            RefreshDurationDerivedState();
    }

    private void OnAnimDurationsReceived()
    {
        OnPropertyChanged(nameof(AnimDurationMsLookup));
        RefreshDurationDerivedState();
    }

    private void OnAnimConfigurationChanged() => RefreshDurationDerivedState();

    private void RefreshDurationDerivedState()
    {
        ResolveGestureDurationsAndExtent();
        RebuildRulerTicks();
    }

    private void ResetExecutionTracking()
    {
        foreach (var tracker in _executionTrackers.Values)
            DisposeExecutionDeadlines(tracker);
        _executionTrackers.Clear();
        _infiniteRequestBySourceOrder.Clear();
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
        CancelAudioAssetValidation();
        Stop();
        foreach (var tracker in _executionTrackers.Values)
            DisposeExecutionDeadlines(tracker);
        _executionTrackers.Clear();
        _protocol.DroidsChanged -= OnDroidsChanged;
        _protocol.AnimDurationsReceived -= OnAnimDurationsReceived;
        _protocol.AnimConfigurationChanged -= OnAnimConfigurationChanged;
        _protocol.AnimMasterAccepted -= OnAnimMasterAccepted;
        _protocol.AnimExecutionReceived -= OnAnimExecutionReceived;
        _protocol.LinkClosed -= OnProtocolLinkClosed;
        _audioPlayer.PlaybackFailed -= OnAudioPlaybackFailed;
        (_audioPlayer as IDisposable)?.Dispose();
    }

    // --- Timeline: tracks, ruler, zoom, playhead --------------------------------

    // Explicit Canvas extents for the ScrollViewer — a WPF Canvas doesn't auto-size to its
    // children's positions, so the scrollable width/height must be computed and bound.
    // Floored at the viewport width (mockup: width = max(content, viewport)) so the row
    // backgrounds/gridlines fill the whole visible body even for a short/empty sequence,
    // instead of stopping in a stub partway across.
    public double TimelineWidthPx => Math.Max(Math.Max(400, ViewportWidthPx), (TotalDurationMsValue + 2000) * PxPerMs);

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
        _trackRosterSignature = TrackRosterSignature();
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

    private string TrackRosterSignature() => string.Join(
        '|',
        Targets.OrderBy(droid => droid.Id).Select(droid =>
            $"{droid.Id:X4}:{droid.Name}:{(droid.IsMaster ? 'M' : 'S')}"));

    private string DurationTargetSignature() => string.Join(
        '|',
        Targets.Where(droid => droid.Online || droid.IsMaster)
            .OrderBy(droid => droid.Id)
            .Select(droid => droid.Id.ToString("X4")));

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

    // Public compatibility seam used by the view's Fit handler. The value is cached at the
    // document/metadata transaction boundary; playhead ticks never rescan clip collections.
    public double TotalDurationMs() => TotalDurationMsValue;

    private void ResolveGestureDurationsAndExtent()
    {
        _durationTargetSignature = DurationTargetSignature();
        var provider = new AnimationDurationProvider(
            _protocol.AnimDurationMetadata,
            _protocol.AnimDurationMs,
            _protocol.AnimSpeedPct,
            _protocol.Droids);
        long stepsEnd = 0;
        foreach (var step in Steps)
        {
            var resolved = provider.Resolve(step);
            step.DurationKind = resolved.Kind;
            step.ResolvedDurationMs = resolved.EffectiveMs;
            step.DurationSummary = resolved.Summary;
            step.DurationDetail = resolved.Detail;
            step.DurationProvisional = resolved.Provisional;
            stepsEnd = Math.Max(stepsEnd, (long)Math.Max(0, step.StartMs) + resolved.EffectiveMs);
        }
        var audioEnd = AudioLanes.SelectMany(lane => lane.Clips)
            .Select(clip => (long)Math.Max(0, clip.StartMs) + Math.Max(0, clip.EffectiveDurationMs))
            .DefaultIfEmpty(0).Max();
        _calculatedContentEndMs = (int)Math.Min(int.MaxValue, Math.Max(stepsEnd, audioEnd));
        _totalDurationMs = Math.Max(_calculatedContentEndMs, SequenceEndMs ?? 0);
        OnPropertyChanged(nameof(CalculatedContentEndMs));
        OnPropertyChanged(nameof(TotalDurationMsValue));
        OnPropertyChanged(nameof(SequenceEndToolTip));
    }

    private void RebuildRulerTicks()
    {
        RulerTicks.Clear();
        if (PxPerMs > 0)
        {
            // Ticks (and therefore the gridlines bound to them) cover the whole DRAWN width —
            // viewport floor included — not just the sequence's own duration, so the grid
            // never stops in a stub partway across ("la trame reste en pleine longueur").
            var endMs = Math.Max(TotalDurationMsValue, TimelineWidthPx / PxPerMs);
            var interval = SelectRulerIntervalMs(endMs, PxPerMs);
            for (var index = 0; index < MaxRulerTickCount; index++)
            {
                var t = (double)index * interval;
                if (t > endMs) break;
                var major = index % 5 == 0;
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

    internal static int SelectRulerIntervalMs(double endMs, double pxPerMs)
    {
        if (!double.IsFinite(endMs) || endMs < 0)
            throw new ArgumentOutOfRangeException(nameof(endMs));
        if (!double.IsFinite(pxPerMs) || pxPerMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(pxPerMs));

        // Satisfy both constraints at once: labels remain readable at the current zoom and
        // the three WPF ItemsControls bound to this collection can never materialize an
        // unbounded number of ruler/gridline elements on a long Scene.
        var requiredMs = Math.Max(
            MinimumRulerTickSpacingPx / pxPerMs,
            endMs / (MaxRulerTickCount - 1));
        return RulerIntervalsMs.FirstOrDefault(
            candidate => candidate >= requiredMs,
            RulerIntervalsMs[^1]);
    }

    public int RoundToGrid(double ms) => SnapToGrid ? (int)(Math.Round(ms / 100.0) * 100) : (int)ms;

    // A drag spans multiple mouse events, so it owns a long-lived edit transaction. Transient
    // Dragging/DragOffsetY fields are absent from snapshots; a click or a move that returns to
    // its origin therefore commits as a true no-op instead of polluting Undo.
    public bool BeginStepDrag() => BeginSequenceEdit();
    public bool BeginAudioClipDrag() => BeginSequenceEdit();
    public bool BeginLaneRename() => BeginSequenceEdit();
    public bool CompleteEditTransaction() => CommitSequenceEdit();
    public bool CancelEditTransaction() => CancelSequenceEdit();

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

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void NudgeEndLonger()
    {
        if (!CanEditSequence || SelectedStep is not { IsInfinite: true }) return;
        ExecuteSequenceEdit(() => SelectedStep.EndAfterMs = Math.Min(
            SequenceImportService.MaxTimelineMs - Math.Max(0, SelectedStep.StartMs),
            SelectedStep.EndAfterMs + 100));
        OnPropertyChanged(nameof(SelectedStepEndAfterMs));
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void NudgeEndShorter()
    {
        if (!CanEditSequence || SelectedStep is not { IsInfinite: true }) return;
        ExecuteSequenceEdit(() => SelectedStep.EndAfterMs = Math.Max(100, SelectedStep.EndAfterMs - 100));
        OnPropertyChanged(nameof(SelectedStepEndAfterMs));
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
        var probe = await _audioProbe.ProbeAsync(dlg.FileName);
        // The duration probe yields to WPF. Playback may have started while it was open; in that
        // case the edit lock wins and the picked file is simply not inserted into the active pass.
        if (!CanEditSequence || !AudioLanes.Contains(lane)) return;
        // A failed probe still inserts the clip: the operator sees it, badged with the reason,
        // and can replace the file. Dropping it silently was the old behavior (SEQ-F04/F05).
        var clip = new AudioClip
        {
            FilePath = dlg.FileName,
            DurationMs = probe.DurationMs,
            StartMs = Math.Max(0, RoundToGrid(PlayheadMs)),
            ProbeStatus = probe.Status,
            ProbeMessage = probe.Ok ? null : probe.Describe(),
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
        var probe = await _audioProbe.ProbeAsync(replacementPath);
        if (!CanEditSequence || !AudioLanes.Any(l => l.Clips.Contains(clip))) return;
        ReplaceAudioClipSource(clip, replacementPath, probe.DurationMs, probe);
        _ = LoadWaveformAsync(clip);
    }

    internal bool InsertAudioClip(AudioLane lane, AudioClip clip)
    {
        if (!CanEditSequence || !AudioLanes.Contains(lane) ||
            AudioLanes.Any(existing => existing.Clips.Contains(clip))) return false;
        return ExecuteSequenceEdit(() => lane.Clips.Add(clip));
    }

    internal bool ReplaceAudioClipSource(
        AudioClip clip, string path, int durationMs, AudioProbeResult? probe = null)
    {
        if (!CanEditSequence || !AudioLanes.Any(lane => lane.Clips.Contains(clip))) return false;
        return ExecuteSequenceEdit(() =>
        {
            clip.FilePath = path;
            clip.Peaks = null; // stale for the new file until the fresh decode below completes
            clip.DurationMs = durationMs;
            clip.ProbeStatus = probe?.Status ?? AudioProbeStatus.Ok;
            clip.ProbeMessage = probe is { Ok: false } result ? result.Describe() : null;
            // Invalidate any decode still in flight for the previous file (SEQ-F06).
            clip.NextWaveformToken();
        });
    }

    // Fire-and-forget from every clip-creation path (Add/Replace/load) — decoding happens off
    // the UI thread in the waveform decoder; only the final property write is marshalled back.
    // The token check is what stops a slow decode of a replaced file from overwriting the new
    // clip's envelope, including when both files share the same path (SEQ-F06).
    internal async Task LoadWaveformAsync(AudioClip clip)
    {
        var token = clip.WaveformToken;
        var peaks = await _waveformDecoder.GetPeaksAsync(clip.FilePath);
        RunOnUiThread(() =>
        {
            if (clip.WaveformToken != token) return;
            clip.Peaks = peaks;
        });
    }

    private void OnAudioPlaybackFailed(AudioPlaybackFailure failure) => RunOnUiThread(() =>
    {
        var key = (failure.ClipId, failure.FilePath);
        if (!_audioFailureKeys.Add(key)) return;
        var message = $"{failure.FileName} — {failure.Message}";
        _audioFailures.Add(message);
        OnPropertyChanged(nameof(HasAudioFailures));
        OnPropertyChanged(nameof(AudioFailureText));
    });

    private void ClearAudioFailures()
    {
        if (_audioFailures.Count == 0) return;
        _audioFailures.Clear();
        _audioFailureKeys.Clear();
        OnPropertyChanged(nameof(HasAudioFailures));
        OnPropertyChanged(nameof(AudioFailureText));
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

    // Null and legacy-library empty lists seed the two default lanes. Current document
    // snapshots/imports pass seedDefaultsWhenEmpty=false so an explicitly empty lane list
    // round-trips exactly through Undo/Redo and schema v3/v4 import.
    private void ApplyAudioLanesFromDto(
        List<AudioLaneDto>? dtos,
        bool seedDefaultsWhenEmpty = true)
    {
        CancelAudioAssetValidation();
        AudioLanes.Clear();
        if (dtos == null || (seedDefaultsWhenEmpty && dtos.Count == 0))
        {
            AudioLanes.Add(new AudioLane { Label = "AMBIENT", RowIndex = 0 });
            AudioLanes.Add(new AudioLane { Label = "AUDIO", RowIndex = 1 });
            return;
        }
        var row = 0;
        var clipsToValidate = new List<AudioClip>();
        foreach (var dto in dtos)
        {
            var lane = new AudioLane { Label = dto.Label, RowIndex = row++ };
            foreach (var c in dto.Clips)
            {
                var clip = new AudioClip { FilePath = c.FilePath, DurationMs = c.DurationMs, StartMs = c.StartMs, Loop = c.Loop };
                // A Scene stores paths, not audio. Flag a file that has since moved or been
                // deleted right away, rather than letting the operator discover it at Play
                // time — a cheap existence check, no decoding (SEQ-F04).
                if (string.IsNullOrWhiteSpace(c.FilePath))
                {
                    clip.ProbeStatus = AudioProbeStatus.FileMissing;
                    clip.ProbeMessage = "No audio file selected.";
                }
                else if (!File.Exists(c.FilePath))
                {
                    clip.ProbeStatus = AudioProbeStatus.FileMissing;
                    clip.ProbeMessage = $"File not found: {clip.FileName}";
                }
                else
                {
                    clip.ValidationPending = true;
                    clipsToValidate.Add(clip);
                }
                lane.Clips.Add(clip);
                _ = LoadWaveformAsync(clip);
            }
            AudioLanes.Add(lane);
        }
        QueueAudioAssetValidation(clipsToValidate);
    }

    private void CancelAudioAssetValidation()
    {
        _audioAssetValidationGeneration++;
        _audioAssetValidationCancellation?.Cancel();
        _audioAssetValidationCancellation?.Dispose();
        _audioAssetValidationCancellation = null;
    }

    private void QueueAudioAssetValidation(IReadOnlyCollection<AudioClip> clips)
    {
        if (clips.Count == 0 || _disposed) return;
        var cancellation = new CancellationTokenSource();
        _audioAssetValidationCancellation = cancellation;
        var generation = _audioAssetValidationGeneration;
        _ = RevalidateAudioAssetsAsync(clips, generation, cancellation.Token);
    }

    private async Task RevalidateAudioAssetsAsync(
        IReadOnlyCollection<AudioClip> clips,
        int generation,
        CancellationToken cancellationToken)
    {
        // Probe each distinct asset once. Sequential probing avoids opening an unbounded number
        // of Media Foundation handles when a large Scene reuses many files; it never blocks WPF.
        var groups = clips
            .GroupBy(clip => clip.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var group in groups)
        {
            AudioProbeResult result;
            try
            {
                result = await _audioProbe.ProbeAsync(group.Key, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                result = AudioProbeResult.Failure(AudioProbeStatus.DecodeFailed, ex.Message);
            }

            if (cancellationToken.IsCancellationRequested ||
                result.Status == AudioProbeStatus.Cancelled)
                return;

            RunOnUiThread(() =>
            {
                if (_disposed || generation != _audioAssetValidationGeneration) return;
                var durationChanged = false;
                var runtimeChanged = false;
                foreach (var clip in group)
                {
                    if (!AudioLanes.Any(lane => lane.Clips.Contains(clip)) ||
                        !string.Equals(clip.FilePath, group.Key, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var message = result.Ok ? null : result.Describe();
                    runtimeChanged |= clip.ValidationPending ||
                        clip.ProbeStatus != result.Status || clip.ProbeMessage != message;
                    clip.ProbeStatus = result.Status;
                    clip.ProbeMessage = message;
                    if (result.Ok && clip.DurationMs != result.DurationMs)
                    {
                        clip.DurationMs = result.DurationMs;
                        durationChanged = true;
                    }
                    clip.ValidationPending = false;
                }

                if (runtimeChanged || durationChanged)
                    RefreshDerivedTimelineState();
                if (durationChanged)
                    RefreshDirtyFromCheckpoint();
            });
        }
    }

    // --- Playhead: local scrub + live hardware sync -----------------------------

    public void SetPlayheadFromPixel(double x)
    {
        if (IsLiveTracking || PxPerMs <= 0) return;
        var requestedMs = Math.Max(0, x / PxPerMs);
        if (IsPaused && Math.Abs(requestedMs - PlayheadMs) > 0.5)
        {
            // A paused pass retains live audio handles, scheduler consumption state and
            // potentially leased infinite gestures at the old time. Once the operator seeks,
            // that state no longer represents the visible cursor. Abandon it through the normal
            // Stop path before moving; the next Play creates a fresh play-from-cursor pass.
            Stop();
        }
        PlayheadMs = requestedMs;
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
        var scan = _library.Scan();
        Library.Clear();
        foreach (var item in scan.Items) Library.Add(item);
        _libraryIssues = scan.Issues;
        _libraryIssueCount = scan.Issues.Count;
        OnPropertyChanged(nameof(LibraryStatusText));
        OnPropertyChanged(nameof(LibraryIssueText));
    }

    private void SetDocumentOrigin(SequencerDocumentOrigin origin, string? sceneId)
    {
        _documentOrigin = origin;
        _currentSceneId = sceneId;
        OnPropertyChanged(nameof(DocumentOrigin));
        OnPropertyChanged(nameof(CurrentSceneId));
        OnPropertyChanged(nameof(SceneOriginText));
        OnPropertyChanged(nameof(SequenceBadgeText));
        OnPropertyChanged(nameof(SceneDocumentStateText));
        DeleteCurrentSceneCommand.NotifyCanExecuteChanged();
    }

    // --- Snapshot / undo-redo --------------------------------------------------

    private SequenceSnapshot Snapshot() => new(Name, Loop, AudioLanesToDto(),
        Steps.Select(s => new SequenceStepDto
        {
            AnimId = s.AnimId,
            Target = s.Target,
            StartMs = s.StartMs,
            EndAfterMs = s.EndAfterMs,
        }).ToList(), SequenceEndMs);

    private bool BeginSequenceEdit()
    {
        return CanEditSequence && _editHistory.Begin(Snapshot());
    }

    private bool CommitSequenceEdit()
    {
        // A fixed endpoint may extend a Scene but must never cut off content. Resolve the
        // current tails before the history snapshot so moving content later advances the
        // endpoint inside the same Undo transaction.
        ResolveGestureDurationsAndExtent();
        if (SequenceEndMs.HasValue && SequenceEndMs.Value < CalculatedContentEndMs)
            SequenceEndMs = CalculatedContentEndMs;
        if (!_editHistory.Commit(Snapshot())) return false;

        RefreshDirtyFromCheckpoint();
        RefreshDerivedTimelineState();
        UpdateUndoButtons();
        return true;
    }

    private bool CancelSequenceEdit()
    {
        var cancellation = _editHistory.Cancel(Snapshot());
        if (cancellation is not { DocumentChanged: true }) return false;
        Apply(cancellation.Value.Snapshot);
        return true;
    }

    private bool ExecuteSequenceEdit(Action mutation)
    {
        if (!CanEditSequence) return false;
        var ownsTransaction = !_editHistory.HasActiveEdit;
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
        SetScheduleWarnings(Array.Empty<SequencerScheduleWarning>());
        RebuildTracks();
        ResolveGestureDurationsAndExtent();
        RebuildRulerTicks();
    }

    private void SetScheduleWarnings(IReadOnlyList<SequencerScheduleWarning> warnings)
    {
        _scheduleWarnings = warnings;
        OnPropertyChanged(nameof(HasScheduleWarnings));
        OnPropertyChanged(nameof(ScheduleWarningText));
    }

    private void Apply(SequenceSnapshot snap)
    {
        _suppressTimelineRefresh = true;
        try
        {
            Name = snap.Name;
            Loop = snap.Loop;
            SequenceEndMs = snap.EndMs;
            ApplyAudioLanesFromDto(snap.AudioLanes, seedDefaultsWhenEmpty: false);
            Steps.Clear();
            foreach (var s in snap.Steps)
                Steps.Add(new SequenceStep
                {
                    AnimId = s.AnimId,
                    Target = s.Target,
                    StartMs = s.StartMs,
                    EndAfterMs = s.EndAfterMs,
                });
            SelectedStep = null;
        }
        finally
        {
            _suppressTimelineRefresh = false;
        }
        RefreshDirtyFromCheckpoint();
        RefreshDerivedTimelineState();
    }

    internal void EstablishSavedCheckpoint(SequenceSnapshot? checkpoint = null)
    {
        _savedCheckpoint = checkpoint ?? Snapshot();
        RefreshDirtyFromCheckpoint();
    }

    private void RefreshDirtyFromCheckpoint()
    {
        Dirty = _savedCheckpoint == null || !_savedCheckpoint.DocumentEquals(Snapshot());
    }

    private void UpdateUndoButtons()
    {
        CanUndo = _editHistory.CanUndo;
        CanRedo = _editHistory.CanRedo;
    }

    private bool CanUndoEdit() => CanEditSequence && CanUndo;

    [RelayCommand(CanExecute = nameof(CanUndoEdit))]
    private void Undo()
    {
        if (!CanEditSequence) return;
        var previous = _editHistory.Undo(Snapshot());
        if (previous == null) return;
        Apply(previous);
        UpdateUndoButtons();
    }

    private bool CanRedoEdit() => CanEditSequence && CanRedo;

    [RelayCommand(CanExecute = nameof(CanRedoEdit))]
    private void Redo()
    {
        if (!CanEditSequence) return;
        var next = _editHistory.Redo(Snapshot());
        if (next == null) return;
        Apply(next);
        UpdateUndoButtons();
    }

    private void ClearHistory()
    {
        _editHistory.Clear();
        UpdateUndoButtons();
    }

    // --- Editing ----------------------------------------------------------------

    internal bool SetSequenceName(string value) =>
        ExecuteSequenceEdit(() => Name = value);

    internal bool SetSequenceLoop(bool value) =>
        ExecuteSequenceEdit(() => Loop = value);

    private bool CanSetSequenceEndAtPlayhead() =>
        CanEditSequence && PlayheadMs >= 0 && PlayheadMs <= SequenceImportService.MaxTimelineMs;

    [RelayCommand(CanExecute = nameof(CanSetSequenceEndAtPlayhead))]
    private void SetSequenceEndAtPlayhead()
    {
        var requested = (int)Math.Clamp(
            Math.Round(PlayheadMs), 0, SequenceImportService.MaxTimelineMs);
        ExecuteSequenceEdit(() => SequenceEndMs = Math.Max(requested, CalculatedContentEndMs));
    }

    private bool CanUseAutomaticSequenceEnd() => CanEditSequence && SequenceEndMs.HasValue;

    [RelayCommand(CanExecute = nameof(CanUseAutomaticSequenceEnd))]
    private void UseAutomaticSequenceEnd() => ExecuteSequenceEdit(() => SequenceEndMs = null);

    internal bool SetStepAnimId(SequenceStep step, int value)
    {
        if (!Steps.Contains(step)) return false;
        var changed = ExecuteSequenceEdit(() => step.AnimId = value);
        if (changed && ReferenceEquals(SelectedStep, step))
        {
            OnPropertyChanged(nameof(SelectedStepAnimId));
            OnPropertyChanged(nameof(SelectedStepEndAfterMs));
        }
        return changed;
    }

    internal bool SetStepTarget(SequenceStep step, ushort value)
    {
        if (!Steps.Contains(step)) return false;
        var changed = ExecuteSequenceEdit(() => step.Target = value);
        if (changed && ReferenceEquals(SelectedStep, step))
            OnPropertyChanged(nameof(SelectedStepTrack));
        return changed;
    }

    internal bool SetAudioLaneLabel(AudioLane lane, string value)
    {
        if (!AudioLanes.Contains(lane)) return false;
        return ExecuteSequenceEdit(() => lane.Label = value);
    }

    internal bool MoveAudioLane(AudioLane lane, int destinationIndex)
    {
        var sourceIndex = AudioLanes.IndexOf(lane);
        if (sourceIndex < 0 || destinationIndex < 0 || destinationIndex >= AudioLanes.Count ||
            sourceIndex == destinationIndex) return false;
        return ExecuteSequenceEdit(() =>
        {
            AudioLanes.Move(sourceIndex, destinationIndex);
            for (var i = 0; i < AudioLanes.Count; i++) AudioLanes[i].RowIndex = i;
        });
    }

    internal bool SetAudioClipLoop(AudioClip clip, bool value)
    {
        if (!AudioLanes.Any(lane => lane.Clips.Contains(clip))) return false;
        return ExecuteSequenceEdit(() => clip.Loop = value);
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void ToggleAudioLoop(AudioClip? clip)
    {
        if (clip != null) SetAudioClipLoop(clip, !clip.Loop);
    }

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

    [RelayCommand]
    private void OpenSceneLibrary()
    {
        RefreshLibrary();
        var result = _persistenceDialogs.ChooseSceneToOpen(
            Library.ToArray(), CurrentSceneId, LibraryStatusText, LibraryIssueText);
        if (result == null) return;
        if (result.CreateNew)
        {
            NewScene();
            return;
        }
        if (result.Scene != null) LoadFromLibrary(result.Scene);
    }

    [RelayCommand]
    private void NewScene()
    {
        if (!PrepareDocumentReplacement("create a new Scene")) return;

        _suppressTimelineRefresh = true;
        try
        {
            Name = "";
            Loop = false;
            SequenceEndMs = null;
            _fileTracks.Clear();
            ApplyAudioLanesFromDto(null);
            Steps.Clear();
            SelectedStep = null;
            PlayheadMs = 0;
        }
        finally
        {
            _suppressTimelineRefresh = false;
        }
        ClearHistory();
        SetDocumentOrigin(SequencerDocumentOrigin.New, null);
        EstablishSavedCheckpoint();
        _settings.SetLastSceneId(null);
        _settings.SetLastSequencePath(null);
        RefreshDerivedTimelineState();
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void SaveScene()
    {
        if (!CanEditSequence) return;
        if (DocumentOrigin != SequencerDocumentOrigin.LocalLibrary ||
            string.IsNullOrWhiteSpace(CurrentSceneId))
        {
            SaveAsNewScene(promptAlways: string.IsNullOrWhiteSpace(Name));
            return;
        }

        SaveSceneCore(CurrentSceneId, Name);
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void SaveSceneAs()
    {
        if (!CanEditSequence) return;
        SaveAsNewScene(promptAlways: true);
    }

    private void SaveAsNewScene(bool promptAlways)
    {
        var suggestedName = string.IsNullOrWhiteSpace(Name)
            ? "New Scene"
            : DocumentOrigin == SequencerDocumentOrigin.LocalLibrary
                ? $"{Name.Trim()} Copy"
                : Name.Trim();
        var chosenName = promptAlways
            ? _persistenceDialogs.PromptForSceneName(suggestedName, "Save Scene As")
            : Name;
        if (chosenName == null) return;
        SaveSceneCore(Guid.NewGuid().ToString("N"), chosenName);
    }

    private void SaveSceneCore(string id, string requestedName)
    {
        var sceneName = requestedName.Trim();
        if (sceneName.Length == 0)
        {
            _persistenceDialogs.ShowError("Scene save failed", "Enter a scene name before saving.");
            return;
        }
        if (sceneName.Length > SequenceImportService.MaxSequenceNameLength)
        {
            _persistenceDialogs.ShowError(
                "Scene save failed",
                $"Scene names are limited to {SequenceImportService.MaxSequenceNameLength} characters.");
            return;
        }

        var conflict = Library.FirstOrDefault(item =>
            !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name.Trim(), sceneName, StringComparison.CurrentCultureIgnoreCase));
        if (conflict != null)
        {
            _persistenceDialogs.ShowError(
                "Scene name already exists",
                $"The Local Library already contains \"{conflict.Name}\". Choose a different name; Save never overwrites another scene identity.");
            return;
        }

        try
        {
            var document = Snapshot() with { Name = sceneName };
            var item = new SequenceLibraryItem
            {
                Id = id,
                Name = sceneName,
                Loop = document.Loop,
                EndMs = document.EndMs,
                Tracks = Tracks.Where(track => !track.IsBroadcast)
                    .Select(track => new SequenceTrackDto { Id = track.Id, Name = track.Label })
                    .ToList(),
                AudioLanes = document.AudioLanes,
                Steps = document.Steps,
                SavedAt = DateTime.UtcNow,
            };
            _library.Save(item);

            Name = sceneName;
            SetDocumentOrigin(SequencerDocumentOrigin.LocalLibrary, id);
            EstablishSavedCheckpoint(document);
            _settings.SetLastSequencePath(null);
            _settings.SetLastSceneId(id);
            RefreshLibrary();
        }
        catch (Exception ex)
        {
            _persistenceDialogs.ShowError("Scene save failed", ex.Message);
        }
    }

    [RelayCommand]
    private void LoadFromLibrary(SequenceLibraryItem? item)
    {
        if (item == null) return;
        if (!PrepareDocumentReplacement($"open \"{item.Name}\"")) return;
        ApplyLibraryItem(item);
        _settings.SetLastSequencePath(null);
        _settings.SetLastSceneId(item.Id);
    }

    private void ApplyLibraryItem(SequenceLibraryItem item)
    {
        Name = item.Name;
        Loop = item.Loop;
        SequenceEndMs = item.EndMs;
        _fileTracks.Clear();
        _fileTracks.AddRange(item.Tracks);
        ApplyAudioLanesFromDto(item.AudioLanes);
        Steps.Clear();
        foreach (var s in item.Steps) Steps.Add(new SequenceStep
        {
            AnimId = s.AnimId,
            Target = s.Target,
            StartMs = s.StartMs,
            EndAfterMs = s.EndAfterMs,
        });
        RefreshDerivedTimelineState();
        SelectedStep = null;
        ClearHistory();
        SetDocumentOrigin(SequencerDocumentOrigin.LocalLibrary, item.Id);
        EstablishSavedCheckpoint();
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DeleteFromLibrary(SequenceLibraryItem? item)
    {
        if (!CanEditSequence || item == null) return;
        if (!_persistenceDialogs.ConfirmMoveSceneToTrash(item.Name)) return;
        try
        {
            _library.MoveToTrash(item.Id);
            if (DocumentOrigin == SequencerDocumentOrigin.LocalLibrary &&
                string.Equals(CurrentSceneId, item.Id, StringComparison.OrdinalIgnoreCase))
            {
                SetDocumentOrigin(SequencerDocumentOrigin.New, null);
                _savedCheckpoint = null;
                RefreshDirtyFromCheckpoint();
                _settings.SetLastSceneId(null);
            }
            RefreshLibrary();
        }
        catch (Exception ex)
        {
            _persistenceDialogs.ShowError("Scene removal failed", ex.Message);
        }
    }

    private bool CanDeleteCurrentScene() =>
        CanEditSequence &&
        DocumentOrigin == SequencerDocumentOrigin.LocalLibrary &&
        !string.IsNullOrWhiteSpace(CurrentSceneId);

    [RelayCommand(CanExecute = nameof(CanDeleteCurrentScene))]
    private void DeleteCurrentScene()
    {
        if (!CanDeleteCurrentScene()) return;
        var item = Library.FirstOrDefault(scene =>
            string.Equals(scene.Id, CurrentSceneId, StringComparison.OrdinalIgnoreCase));
        if (item != null) DeleteFromLibrary(item);
    }

    // --- Export / import ----------------------------------------------------------

    [RelayCommand]
    private void Export()
    {
        var path = _persistenceDialogs.ChooseExportPath(
            $"{(string.IsNullOrEmpty(Name) ? "sequence" : Name)}.b1seq.json");
        if (path == null) return;
        try
        {
            ExportTo(path);
        }
        catch (Exception ex)
        {
            _persistenceDialogs.ShowError("Sequencer export failed", ex.Message);
        }
    }

    internal void ExportTo(string path)
    {
        var document = Snapshot();
        var fileTracks = Tracks.Where(track => !track.IsBroadcast)
            .Select(track => new SequenceTrackDto { Id = track.Id, Name = track.Label })
            .ToList();
        var contents = SequenceExportSerializer.Serialize(document, fileTracks);
        // Never write a file our own strict importer would reject. This also catches editor
        // values created by direct bindings (for example a blank lane label) before touching
        // the previous destination or saved checkpoint.
        _ = SequenceImportService.Parse(contents);

        _atomicFileWriter.WriteAllText(path, contents);
        // Export is an external-copy escape hatch. For a library-backed Scene it must not
        // claim that edits were saved back to the library or change startup restoration.
        if (DocumentOrigin != SequencerDocumentOrigin.LocalLibrary)
        {
            SetDocumentOrigin(SequencerDocumentOrigin.ExternalFile, null);
            _settings.SetLastSceneId(null);
            _settings.SetLastSequencePath(path);
            EstablishSavedCheckpoint(document);
        }
    }

    [RelayCommand]
    private void Import()
    {
        var path = _persistenceDialogs.ChooseImportPath();
        if (path == null) return;
        if (!PrepareDocumentReplacement($"import \"{Path.GetFileName(path)}\"")) return;
        try
        {
            ImportFrom(path);
            _settings.SetLastSceneId(null);
            _settings.SetLastSequencePath(path);
        }
        catch (Exception ex)
        {
            _persistenceDialogs.ShowError("Sequencer import failed", ex.Message);
        }
    }

    private bool PrepareDocumentReplacement(string replacementDescription)
    {
        var mustStopPlayback = !CanEditSequence;
        if (mustStopPlayback && !_persistenceDialogs.ConfirmStopPlayback(replacementDescription))
            return false;

        var unsavedChoice = Dirty
            ? _persistenceDialogs.ConfirmUnsavedSceneChanges(SceneDisplayName, replacementDescription)
            : UnsavedSceneChoice.Discard;
        if (unsavedChoice == UnsavedSceneChoice.Cancel) return false;

        // Defer the accepted Stop until every cancel-capable question has completed. Cancelling
        // the unsaved-changes prompt therefore leaves an active rehearsal running untouched.
        if (mustStopPlayback) Stop();

        switch (unsavedChoice)
        {
            case UnsavedSceneChoice.Save:
                SaveScene();
                return !Dirty;
            case UnsavedSceneChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    // Restores whatever sequence was last exported/imported, so the console resumes exactly
    // where the previous session left off instead of starting blank. Silent on failure (a
    // missing/corrupt file at startup shouldn't pop a dialog before the app has even settled).
    public void TryLoadLastSequence()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastSceneId))
        {
            try
            {
                var scene = _library.Get(_settings.LastSceneId);
                if (scene != null)
                {
                    ApplyLibraryItem(scene);
                    return;
                }
            }
            catch
            {
                // Fall back to the last external path or an empty document.
            }
        }
        var path = _settings.LastSequencePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { ImportFrom(path); }
        catch { /* stale/corrupt last-sequence file: start with an empty sequence instead */ }
    }

    internal void ImportFrom(string path)
    {
        // Parsing, schema migration and validation are deliberately side-effect free. Nothing
        // below runs unless the complete source document has already passed every check.
        var imported = SequenceImportService.ParseFile(path);
        ApplyImportedDocument(imported);
        SetDocumentOrigin(SequencerDocumentOrigin.ExternalFile, null);
    }

    private void ApplyImportedDocument(ImportedSequenceDocument imported)
    {
        Name = imported.Name;
        Loop = imported.Loop;
        SequenceEndMs = imported.EndMs;
        _fileTracks.Clear();
        _fileTracks.AddRange(imported.Tracks);
        ApplyAudioLanesFromDto(
            imported.AudioLanes,
            seedDefaultsWhenEmpty: imported.SourceVersion < 3);
        Steps.Clear();
        foreach (var step in imported.Steps)
            Steps.Add(new SequenceStep
            {
                AnimId = step.AnimId,
                Target = step.Target,
                StartMs = step.StartMs,
                EndAfterMs = step.EndAfterMs,
            });
        RefreshDerivedTimelineState();
        SelectedStep = null;
        ClearHistory();
        EstablishSavedCheckpoint();
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

    // The primary transport is an ordinary Play/Pause/Resume toggle. Restart is deliberately
    // separate so a double-click can never resend the beginning of a physical choreography.
    [RelayCommand]
    private void Play()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (IsPaused)
        {
            if (_activePlaybackPlan == null) { Stop(); return; }
            StartPlaybackPass(_elapsedAtPauseMs, resumeAudio: true, skipEventsBeforeStart: false);
            return;
        }
        StartNewPlaybackPass((int)Math.Clamp(PlayheadMs, 0, int.MaxValue));
    }

    [RelayCommand]
    private void Restart() => StartNewPlaybackPass(0);

    private void StartNewPlaybackPass(int requestedFromMs)
    {
        if (Steps.Count == 0 && AudioLanes.All(l => l.Clips.Count == 0) &&
            (SequenceEndMs ?? 0) == 0) return;
        _playbackGeneration.Cancel();
        DisposePlaybackScheduler();
        _audioPlayer.StopAll();
        StopInfiniteGestures();
        ResetExecutionTracking();
        _activePlaybackPlan = SequencerPlaybackPlan.Capture(
            Steps, AudioLanes, AnimDurationMsLookup, Loop,
            resolveDurationMs: step => step.ResolvedDurationMs,
            sequenceEndMs: SequenceEndMs);
        SetScheduleWarnings(_activePlaybackPlan.Warnings);
        ClearAudioFailures(); // a new pass starts with a clean audio report
        _dispatchedPlaybackEvents.Clear();
        // At the natural end, Play behaves like a conventional transport and starts a new pass.
        // At every other retained cursor position it is an explicit play-from-cursor rehearsal.
        var fromMs = requestedFromMs >= _activePlaybackPlan.TotalDurationMs ? 0 : requestedFromMs;
        _elapsedAtPauseMs = fromMs;
        FollowPlayhead = true;
        StartPlaybackPass(fromMs, resumeAudio: false, skipEventsBeforeStart: true);
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
        if (TransportState == SequencerTransportState.Playing)
            PlayheadMs = _liveAnchorElapsedMs + _playbackClock.Elapsed.TotalMilliseconds;
        _playbackGeneration.Cancel();
        TransitionTransportTo(SequencerTransportState.Stopped);
        DisposePlaybackScheduler();
        _audioPlayer.StopAll();
        _activePlaybackPlan = null;
        _dispatchedPlaybackEvents.Clear();
        StopPlayheadTimer();
    }

    [RelayCommand(CanExecute = nameof(CanReturnToStart))]
    private void ReturnToStart() => PlayheadMs = 0;

    private bool CanReturnToStart() =>
        TransportState == SequencerTransportState.Stopped && PlayheadMs > 0;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (!CanPause()) return;
        var elapsed = _liveAnchorElapsedMs + _playbackClock.Elapsed.TotalMilliseconds;
        _playbackGeneration.Cancel();
        TransitionTransportTo(SequencerTransportState.Paused);
        DisposePlaybackScheduler();
        _audioPlayer.PauseAll(); // clips already mid-playback keep their position natively
        _elapsedAtPauseMs = (int)elapsed;
        StopPlayheadTimer();
        PlayheadMs = elapsed;
    }

    private bool CanPause() => TransportState == SequencerTransportState.Playing;

    private void StartPlaybackPass(int fromMs, bool resumeAudio, bool skipEventsBeforeStart)
    {
        var plan = _activePlaybackPlan
            ?? throw new InvalidOperationException("Cannot start Sequencer transport without a playback plan.");
        var generation = _playbackGeneration.Begin();
        try
        {
            TransitionTransportTo(SequencerTransportState.Playing);
            if (resumeAudio)
                _audioPlayer.ResumeAll(); // continues from each clip's retained position, no seek math
            else if (skipEventsBeforeStart && fromMs > 0)
                StartAudioOverlappingCursor(plan, fromMs);
            StartPlayheadTicker(fromMs);
            StartPlaybackScheduler(plan, fromMs, generation, skipEventsBeforeStart);
        }
        catch
        {
            // A partial timer/audio start must never leave the UI claiming that transport is live.
            StopTransportCore();
            throw;
        }
    }

    private void StartAudioOverlappingCursor(SequencerPlaybackPlan plan, int fromMs)
    {
        foreach (var audio in plan.Events.OfType<AudioPlaybackEvent>())
        {
            if (audio.StartMs >= fromMs || audio.DurationMs <= 0) continue;
            var elapsedMs = fromMs - audio.StartMs;
            if (!audio.Loop && elapsedMs >= audio.DurationMs) continue;
            if (!_dispatchedPlaybackEvents.Add(audio.SourceOrder)) continue;

            var offsetMs = audio.Loop ? elapsedMs % audio.DurationMs : elapsedMs;
            _audioPlayer.Play(
                audio.FilePath, audio.Loop, audio.SourceOrder, startOffsetMs: offsetMs);
        }
    }

    // The immutable plan is owned by one rearmable wake timer. A wake drains every batch whose
    // absolute timestamp is due according to monotonic elapsed time, preserving plan/source
    // order inside each batch. This eliminates per-event timers and catches up deterministically
    // after host scheduling drift. Muted events still count as consumed at their due instant.
    private void StartPlaybackScheduler(
        SequencerPlaybackPlan plan, int fromMs, long generation, bool skipEventsBeforeStart)
    {
        DisposePlaybackScheduler();
        _nextPlaybackBatchIndex = 0;
        while (_nextPlaybackBatchIndex < plan.Batches.Count &&
               ((skipEventsBeforeStart && plan.Batches[_nextPlaybackBatchIndex].StartMs < fromMs) ||
                plan.Batches[_nextPlaybackBatchIndex].Events.All(playbackEvent =>
                    _dispatchedPlaybackEvents.Contains(playbackEvent.SourceOrder))))
            _nextPlaybackBatchIndex++;
        _playbackWakeTimer = _timerScheduler.Create(() => RunOnUiThread(() =>
            ProcessPlaybackWake(plan, fromMs, generation)));
        ArmNextPlaybackWake(plan, logicalNowMs: fromMs);
    }

    private void ProcessPlaybackWake(SequencerPlaybackPlan plan, int fromMs, long generation)
    {
        if (!_playbackGeneration.IsCurrent(generation) ||
            TransportState != SequencerTransportState.Playing) return;

        var monotonicNow = fromMs + (int)Math.Min(
            int.MaxValue - (long)fromMs,
            Math.Max(0, _playbackClock.Elapsed.TotalMilliseconds));
        var logicalNow = Math.Max(_scheduledWakeAbsoluteMs, monotonicNow);

        while (_nextPlaybackBatchIndex < plan.Batches.Count)
        {
            var batch = plan.Batches[_nextPlaybackBatchIndex];
            if (batch.StartMs > logicalNow) break;
            foreach (var playbackEvent in batch.Events)
            {
                if (!_dispatchedPlaybackEvents.Add(playbackEvent.SourceOrder)) continue;
                DispatchPlaybackEvent(playbackEvent);
            }
            _nextPlaybackBatchIndex++;
        }

        if (logicalNow >= plan.TotalDurationMs)
        {
            CompletePlaybackPass(plan);
            return;
        }

        ArmNextPlaybackWake(plan, logicalNow);
    }

    private void DispatchPlaybackEvent(SequencerPlaybackEvent playbackEvent)
    {
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
                if (dispatch.Written && gesture.AnimId is 16 or 17)
                    _infiniteRequestBySourceOrder[gesture.SourceOrder] = dispatch.RequestId;
                break;
            case GestureTerminationPlaybackEvent termination:
                TerminateInfiniteGesture(termination);
                break;
            case AudioPlaybackEvent audio:
                // SourceOrder is the clip's identity in this plan, so a playback failure can name
                // the offending clip instead of reporting an anonymous audio error (SEQ-F07).
                _audioPlayer.Play(audio.FilePath, audio.Loop, audio.SourceOrder);
                break;
        }
    }

    private void TerminateInfiniteGesture(GestureTerminationPlaybackEvent termination)
    {
        if (!_infiniteRequestBySourceOrder.Remove(termination.GestureSourceOrder, out var requestId)) return;
        CancelAnimLease(requestId);
        var ownedTargets = _latestGestureByDroid
            .Where(pair => pair.Value.RequestId == requestId && pair.Value.IsInfinite)
            .Select(pair => pair.Key)
            .OrderBy(id => id)
            .ToArray();
        foreach (var droidId in ownedTargets)
        {
            var dispatch = _protocol.PlayAnim(
                droidId, 0, (uint)Random.Shared.NextInt64(1, (long)uint.MaxValue + 1));
            if (!dispatch.Written) continue;
            if (_latestGestureByDroid.TryGetValue(droidId, out var current) &&
                current.RequestId == requestId)
                _latestGestureByDroid[droidId] = new GestureTargetState(dispatch.RequestId, 0);
        }
    }

    private void ArmNextPlaybackWake(SequencerPlaybackPlan plan, int logicalNowMs)
    {
        _scheduledWakeAbsoluteMs = _nextPlaybackBatchIndex < plan.Batches.Count
            ? Math.Min(plan.Batches[_nextPlaybackBatchIndex].StartMs, plan.TotalDurationMs)
            : plan.TotalDurationMs;
        var dueTimeMs = Math.Max(0, _scheduledWakeAbsoluteMs - logicalNowMs);
        _playbackWakeTimer?.Rearm(dueTimeMs);
    }

    private void CompletePlaybackPass(SequencerPlaybackPlan plan)
    {
        if (plan.Loop)
        {
            // Stop every player (including a looping ambient clip) before rearming the next
            // pass, or it would keep stacking a fresh MediaPlayer on top of the still-running
            // one every time the sequence loops.
            _audioPlayer.StopAll();
            _elapsedAtPauseMs = 0;
            _dispatchedPlaybackEvents.Clear();
            ResetExecutionTracking();
            StartPlaybackPass(0, resumeAudio: false, skipEventsBeforeStart: false);
        }
        else
        {
            Stop();
            PlayheadMs = plan.TotalDurationMs;
        }
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action);
    }

    private void DisposePlaybackScheduler()
    {
        _playbackWakeTimer?.Dispose();
        _playbackWakeTimer = null;
    }
}
