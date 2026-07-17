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

public partial class SequencerViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;
    private readonly LibraryService _library = new();
    private readonly AudioPlaybackService _audioPlayer = new();
    private const int HistoryMax = 50;
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
    [ObservableProperty] private bool _isLiveTracking;

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
    private DateTime _liveAnchorUtc;
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

    // NotifyCanExecuteChangedFor is load-bearing: a [RelayCommand(CanExecute=...)] never
    // re-evaluates on its own — without these, Pause stayed permanently disabled (the
    // condition was checked once at startup, while nothing played, and never again).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedoCommand))]
    private bool _canRedo;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _isPlaying;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _isPaused;
    [ObservableProperty] private SequenceStep? _selectedStep;

    // SelectedStep.Target as a TimelineTrack, for the inspector's Target ComboBox.
    // ComboBox.SelectedValue/SelectedValuePath was unreliable against DarkComboBoxStyle's
    // fully-replaced ControlTemplate (rendered a validation-error border with no item
    // resolved) — SelectedItem against this ushort<->TimelineTrack wrapper is more robust.
    public TimelineTrack? SelectedStepTrack
    {
        get => SelectedStep == null ? null : Tracks.FirstOrDefault(t => t.Id == SelectedStep.Target);
        set { if (SelectedStep != null && value != null) SelectedStep.Target = value.Id; }
    }

    partial void OnSelectedStepChanged(SequenceStep? value) => OnPropertyChanged(nameof(SelectedStepTrack));

    private readonly Stack<SequenceSnapshot> _history = new();
    private readonly Stack<SequenceSnapshot> _future = new();
    private readonly List<System.Threading.Timer> _playbackTimers = new();
    private int _elapsedAtPauseMs;

    public SequencerViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.DroidsChanged += RebuildTracks;
        _protocol.AnimDurationsReceived += () =>
        {
            OnPropertyChanged(nameof(AnimDurationMsLookup));
            // Real durations change TotalDurationMs (clip tails) — refresh ruler extent too.
            RebuildRulerTicks();
        };
        Steps.CollectionChanged += (_, _) => RebuildRulerTicks();
        RebuildTracks();
        ApplyAudioLanesFromDto(null);
        RebuildRulerTicks();
        RefreshLibrary();
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
        var currentLane = AudioLanes.FirstOrDefault(l => l.Clips.Contains(clip));
        if (currentLane == null || ReferenceEquals(currentLane, targetLane)) return;
        currentLane.Clips.Remove(clip);
        targetLane.Clips.Add(clip);
        Dirty = true;
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

    // Called by the view once at drag-end (not per MouseMove — recomputing ticks under the
    // cursor mid-drag would just make the ruler jitter).
    public void RefreshTimelineExtent() => RebuildRulerTicks();

    public int RoundToGrid(double ms) => SnapToGrid ? (int)(Math.Round(ms / 100.0) * 100) : (int)ms;

    // Public wrappers: the view's drag handlers call these once per drag gesture (mouse-down),
    // not per pixel, so Undo restores the pre-drag position in a single step.
    public void BeginStepDrag() => PushHistory();
    public void BeginAudioClipDrag() => PushHistory();

    [RelayCommand]
    private void InsertGesture(int animId) =>
        InsertGestureAt(animId, ArmedTrack ?? Tracks.FirstOrDefault(), Math.Max(0, RoundToGrid(PlayheadMs)));

    // Called directly from code-behind (not bound in XAML) when a gesture-library chip is
    // dropped on a specific track+time cell instead of just clicked.
    public void InsertGestureAt(int animId, TimelineTrack? track, int startMs)
    {
        PushHistory();
        var step = new SequenceStep { AnimId = animId, Target = track?.Id ?? 0xFFFF, StartMs = Math.Max(0, startMs) };
        Steps.Add(step);
        SelectedStep = step;
        Dirty = true;
    }

    [RelayCommand]
    private void NudgeStartForward()
    {
        if (SelectedStep == null) return;
        PushHistory();
        SelectedStep.StartMs += 100;
        Dirty = true;
    }

    [RelayCommand]
    private void NudgeStartBackward()
    {
        if (SelectedStep == null) return;
        PushHistory();
        SelectedStep.StartMs = Math.Max(0, SelectedStep.StartMs - 100);
        Dirty = true;
    }

    // --- Audio lanes/clips -------------------------------------------------------

    [RelayCommand]
    private void AddAudioLane()
    {
        PushHistory();
        AudioLanes.Add(new AudioLane { Label = $"AUDIO {AudioLanes.Count + 1}", RowIndex = AudioLanes.Count });
        Dirty = true;
    }

    // Any lane can be deleted, the two seeded ones (AMBIENT/AUDIO) included — but a lane
    // that still holds clips asks first (direct user request). Undo restores lane + clips.
    [RelayCommand]
    private void DeleteAudioLane(AudioLane? lane)
    {
        if (lane == null || !AudioLanes.Contains(lane)) return;
        if (lane.Clips.Count > 0)
        {
            var res = MessageBox.Show(
                $"Lane \"{lane.Label}\" still holds {lane.Clips.Count} audio clip(s) — delete the lane and its clips?",
                "Delete audio lane", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }
        PushHistory();
        AudioLanes.Remove(lane);
        for (var i = 0; i < AudioLanes.Count; i++) AudioLanes[i].RowIndex = i;
        Dirty = true;
    }

    // Empties the timeline (all gestures + all audio clips; the lanes themselves stay).
    // Asks first when there are unsaved changes; still one Undo away either way.
    [RelayCommand]
    private void ClearTimeline()
    {
        if (Steps.Count == 0 && AudioLanes.All(l => l.Clips.Count == 0)) return;
        if (Dirty)
        {
            var res = MessageBox.Show(
                "The current sequence has unsaved changes — clear the whole timeline anyway?",
                "Clear timeline", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
        }
        PushHistory();
        Steps.Clear();
        foreach (var lane in AudioLanes) lane.Clips.Clear();
        SelectedStep = null;
        Dirty = true;
        RefreshTimelineExtent();
    }

    [RelayCommand]
    private async Task AddAudioClip(AudioLane? lane)
    {
        lane ??= AudioLanes.FirstOrDefault();
        if (lane == null) return;
        var dlg = new OpenFileDialog { Filter = AudioFileFilter };
        if (dlg.ShowDialog() != true) return;
        PushHistory();
        var durationMs = await AudioPlaybackService.ProbeDurationMsAsync(dlg.FileName);
        var start = Math.Max(0, RoundToGrid(PlayheadMs));
        var clip = new AudioClip { FilePath = dlg.FileName, DurationMs = durationMs, StartMs = start };
        lane.Clips.Add(clip);
        Dirty = true;
        RebuildRulerTicks();
        _ = LoadWaveformAsync(clip);
    }

    [RelayCommand]
    private async Task ReplaceAudioClip(AudioClip? clip)
    {
        if (clip == null) return;
        var dlg = new OpenFileDialog { Filter = AudioFileFilter };
        if (dlg.ShowDialog() != true) return;
        PushHistory();
        clip.FilePath = dlg.FileName;
        clip.Peaks = null; // stale for the new file until the fresh decode below completes
        clip.DurationMs = await AudioPlaybackService.ProbeDurationMsAsync(dlg.FileName);
        Dirty = true;
        RebuildRulerTicks();
        _ = LoadWaveformAsync(clip);
    }

    // Fire-and-forget from every clip-creation path (Add/Replace/load) — decoding happens off
    // the UI thread in WaveformService; only the final property write is marshalled back.
    private async Task LoadWaveformAsync(AudioClip clip)
    {
        var peaks = await WaveformService.GetPeaksAsync(clip.FilePath);
        RunOnUiThread(() => clip.Peaks = peaks);
    }

    [RelayCommand]
    private void DeleteAudioClip(AudioClip? clip)
    {
        if (clip == null) return;
        var lane = AudioLanes.FirstOrDefault(l => l.Clips.Contains(clip));
        if (lane == null) return;
        PushHistory();
        lane.Clips.Remove(clip);
        Dirty = true;
        RebuildRulerTicks();
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
        IsLiveTracking = true;
        _liveAnchorUtc = DateTime.UtcNow;
        _liveAnchorElapsedMs = fromElapsedMs;
        PlayheadMs = fromElapsedMs;
        if (_playheadTimer == null)
        {
            _playheadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _playheadTimer.Tick += (_, _) =>
                PlayheadMs = _liveAnchorElapsedMs + (DateTime.UtcNow - _liveAnchorUtc).TotalMilliseconds;
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

    private void Apply(SequenceSnapshot snap)
    {
        Name = snap.Name;
        Loop = snap.Loop;
        ApplyAudioLanesFromDto(snap.AudioLanes);
        Steps.Clear();
        foreach (var s in snap.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
        RebuildTracks(); // step targets may have changed — offline rows must follow
        SelectedStep = null;
        Dirty = true;
        UpdateUndoButtons();
    }

    private void PushHistory()
    {
        _history.Push(Snapshot());
        while (_history.Count > HistoryMax) { /* Stack has no RemoveAt: > 50 is tolerated on this minimal port */ break; }
        _future.Clear();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        CanUndo = _history.Count > 0;
        CanRedo = _future.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_history.Count == 0) return;
        _future.Push(Snapshot());
        Apply(_history.Pop());
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_future.Count == 0) return;
        _history.Push(Snapshot());
        Apply(_future.Pop());
    }

    private void ClearHistory()
    {
        _history.Clear();
        _future.Clear();
        UpdateUndoButtons();
    }

    // --- Editing ----------------------------------------------------------------

    [RelayCommand]
    private void DeleteStep(SequenceStep? step)
    {
        if (step == null) return;
        PushHistory();
        Steps.Remove(step);
        if (SelectedStep == step) SelectedStep = null;
        Dirty = true;
    }

    [RelayCommand]
    private void DuplicateStep(SequenceStep? step)
    {
        if (step == null) return;
        PushHistory();
        var clone = step.Clone();
        // Nudged right and selected so the new clip is visibly a new arrival instead of
        // landing invisibly right on top of the original (direct user request).
        clone.StartMs += 200;
        var idx = Steps.IndexOf(step);
        Steps.Insert(idx + 1, clone);
        SelectedStep = clone;
        Dirty = true;
    }

    // (The whole "Firmware: 8 NVS slots" region — LoadSlot/SaveToSlot/DeleteSlot/PushToMaster/
    // PullFromMaster and the slot-audio store — was removed 2026-07-16: sequences are
    // console-only now, and fw 1.7.0 dropped the slot machinery too.)

    // --- Local library ------------------------------------------------------

    [RelayCommand]
    private void LoadFromLibrary(SequenceLibraryItem? item)
    {
        if (item == null) return;
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

    [RelayCommand]
    private void DeleteFromLibrary(SequenceLibraryItem? item)
    {
        if (item == null) return;
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
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "B1 Sequence (*.b1seq.json)|*.b1seq.json|JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var obj = JsonNode.Parse(File.ReadAllText(dlg.FileName)) as JsonObject;
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
        catch (Exception ex)
        {
            MessageBox.Show("Import failed: " + ex.Message, "Sequencer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
            _audioPlayer.ResumeAll(); // continues from each clip's retained position, no seek math
            ScheduleTimers(_elapsedAtPauseMs);
            StartPlayheadTicker(_elapsedAtPauseMs);
            IsPaused = false;
            return;
        }
        if (Steps.Count == 0 && AudioLanes.All(l => l.Clips.Count == 0)) return;
        _audioPlayer.StopAll(); // Play pressed mid-playback restarts clean, no overlapped audio
        IsPlaying = true;
        IsPaused = false;
        _elapsedAtPauseMs = 0;
        ScheduleTimers(0);
        StartPlayheadTicker(0);
    }

    [RelayCommand]
    private void Stop()
    {
        IsPlaying = false;
        IsPaused = false;
        DisposePlaybackTimers();
        _audioPlayer.StopAll();
        StopPlayheadTimer();
        PlayheadMs = 0;
        IsLiveTracking = false;
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (!CanPause()) return;
        var elapsed = _liveAnchorElapsedMs + (DateTime.UtcNow - _liveAnchorUtc).TotalMilliseconds;
        DisposePlaybackTimers();
        _audioPlayer.PauseAll(); // clips already mid-playback keep their position natively
        _elapsedAtPauseMs = (int)elapsed;
        StopPlayheadTimer();
        PlayheadMs = elapsed;
        IsPaused = true;
        IsLiveTracking = false;
    }

    private bool CanPause() => IsPlaying && !IsPaused;

    // Absolute-time model (FIRMWARE-CONTRACT.md §6): one one-shot timer per step and per audio
    // clip, armed at its own StartMs relative to fromMs — steps/clips sharing a StartMs fire
    // together, matching what the firmware's own player does. Only items whose StartMs >=
    // fromMs are armed, so resuming (Play while paused — fromMs = elapsed at the moment of
    // pause) never replays
    // anything that already fired before the pause. Muted droids' steps are simply skipped (see
    // ToggleMute) — unconditional now, since Play is the only playback path there is.
    private void ScheduleTimers(int fromMs)
    {
        DisposePlaybackTimers();
        foreach (var step in Steps)
        {
            if (step.StartMs < fromMs || IsTrackMuted(step.Target)) continue;
            var timer = new System.Threading.Timer(_ => RunOnUiThread(() =>
            {
                if (!IsPlaying || IsPaused) return;
                _protocol.PlayAnim(step.Target, step.AnimId, (uint)Random.Shared.Next());
            }), null, step.StartMs - fromMs, System.Threading.Timeout.Infinite);
            _playbackTimers.Add(timer);
        }
        foreach (var clip in AudioLanes.SelectMany(l => l.Clips))
        {
            if (clip.StartMs < fromMs) continue;
            var timer = new System.Threading.Timer(_ => RunOnUiThread(() =>
            {
                if (!IsPlaying || IsPaused) return;
                _audioPlayer.Play(clip.FilePath, clip.Loop);
            }), null, clip.StartMs - fromMs, System.Threading.Timeout.Infinite);
            _playbackTimers.Add(timer);
        }
        var totalMs = (int)TotalDurationMs();
        var endDelay = Math.Max(0, totalMs - fromMs);
        var endTimer = new System.Threading.Timer(_ => RunOnUiThread(() =>
        {
            if (!IsPlaying || IsPaused) return;
            if (Loop)
            {
                // Stop every player (including a looping ambient clip) before rearming the next
                // pass, or it would keep stacking a fresh MediaPlayer on top of the still-running
                // one every time the sequence loops.
                _audioPlayer.StopAll();
                _elapsedAtPauseMs = 0;
                ScheduleTimers(0);
                StartPlayheadTicker(0);
            }
            else Stop();
        }), null, endDelay, System.Threading.Timeout.Infinite);
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
