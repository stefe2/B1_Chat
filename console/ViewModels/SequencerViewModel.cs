using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using b1_chat_console.Models;
using b1_chat_console.Services;
using Microsoft.Win32;

namespace b1_chat_console.ViewModels;

public partial class SequencerViewModel : ObservableObject
{
    private readonly ProtocolClient _protocol;
    private readonly LibraryService _library = new();
    private readonly SequenceAudioStore _audioStore = new();
    private readonly AudioPlaybackService _audioPlayer = new();
    private const int HistoryMax = 50;
    private const string AudioFileFilter = "Audio files (*.mp3;*.wav;*.wma;*.ogg)|*.mp3;*.wav;*.wma;*.ogg|All files (*.*)|*.*";

    public ObservableCollection<SequenceSlotMeta> Catalog => _protocol.SeqCatalog;
    public ObservableCollection<SequenceLibraryItem> Library { get; } = new();
    public ObservableCollection<SequenceStep> Steps { get; } = new();
    public ObservableCollection<Droid> Targets => _protocol.Droids;

    // --- Timeline (Views/SequenceTimelineView) --------------------------------

    public ObservableCollection<TimelineTrack> Tracks { get; } = new();
    public ObservableCollection<TimelineTick> RulerTicks { get; } = new();

    // Console-side audio (DFPlayer set aside "for now", see CLAUDE.md): one or more named
    // lanes (default "AUDIO"/"AMBIENT"), each holding independently-placeable clips that may
    // overlap within their own lane. Never sent to the master — see SequenceAudioStore.
    public ObservableCollection<AudioLane> AudioLanes { get; } = new();

    // The 18 built-in gestures, reused as-is from AnimationViewModel — never redefined here.
    public IReadOnlyList<string> GestureNames { get; } =
        AnimationViewModel.AnimNames.Select((n, i) => $"{i} — {n}").ToList();
    public IReadOnlyList<GestureLibraryEntry> GestureLibrary { get; } =
        AnimationViewModel.AnimNames.Select((n, i) => new GestureLibraryEntry { Id = i, Name = n }).ToList();

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

    private DispatcherTimer? _playheadTimer;
    private DateTime _liveAnchorUtc;
    private double _liveAnchorElapsedMs;

    // --- /Timeline -------------------------------------------------------------

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private int _audioTrack;
    [ObservableProperty] private int? _currentSlot;
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _isRehearsing;
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
    private readonly List<System.Threading.Timer> _rehearsalTimers = new();

    public SequencerViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.SeqDataReceived += OnSeqData;
        _protocol.DroidsChanged += RebuildTracks;
        _protocol.AnimDurationsReceived += () => OnPropertyChanged(nameof(AnimDurationMsLookup));
        _protocol.SeqStateReceived += OnSeqState;
        Steps.CollectionChanged += (_, _) => RebuildRulerTicks();
        RebuildTracks();
        ApplyAudioLanesFromDto(null);
        RebuildRulerTicks();
        RefreshLibrary();
        _protocol.RequestSeqList();
    }

    // --- Timeline: tracks, ruler, zoom, playhead --------------------------------

    // Explicit Canvas extents for the ScrollViewer — a WPF Canvas doesn't auto-size to its
    // children's positions, so the scrollable width/height must be computed and bound.
    public double TimelineWidthPx => Math.Max(400, (TotalDurationMs() + 2000) * PxPerMs);
    public double TracksHeightPx => Math.Max(TimelineTrack.RowHeight, Tracks.Count * (TimelineTrack.RowHeight + TimelineTrack.RowGap));

    private void RebuildTracks()
    {
        var armedId = ArmedTrack?.Id;
        // Muted is a live per-track toggle, not sequence data (see CLAUDE.md) — it must
        // survive a heartbeat-driven rebuild instead of silently resetting, same reasoning
        // that already applies to ArmedTrack below.
        var mutedIds = Tracks.Where(t => t.Muted).Select(t => t.Id).ToHashSet();
        Tracks.Clear();
        Tracks.Add(new TimelineTrack { Id = 0xFFFF, Label = "All droids", IsBroadcast = true, RowIndex = 0, Muted = mutedIds.Contains(0xFFFF) });
        var row = 1;
        foreach (var d in Targets.OrderByDescending(d => d.IsMaster).ThenBy(d => d.Id))
            Tracks.Add(new TimelineTrack { Id = d.Id, Label = d.Name.Length > 0 ? d.Name : d.IdHex, RowIndex = row++, Muted = mutedIds.Contains(d.Id) });
        ArmedTrack = armedId.HasValue ? Tracks.FirstOrDefault(t => t.Id == armedId.Value) : null;
        OnPropertyChanged(nameof(TracksHeightPx));
        // Tracks are wholesale-replaced (new instances) — the inspector's Target combo holds a
        // reference into the old generation via SelectedStepTrack and must re-resolve against
        // the new one, or it silently shows nothing selected even though Target itself is fine.
        OnPropertyChanged(nameof(SelectedStepTrack));
    }

    [RelayCommand]
    private void ArmTrack(TimelineTrack? track) => ArmedTrack = track;

    // Rehearse (local) only — a live hardware Play cannot honor a console-only mute: seqRun
    // just tells the master to start, the master replays its own NVS-stored steps from its
    // own loop(), the console has no per-step veto over that once it's been sent.
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
        var idx = (int)Math.Round(y / (TimelineTrack.RowHeight + TimelineTrack.RowGap));
        idx = Math.Clamp(idx, 0, Tracks.Count - 1);
        return Tracks.ElementAtOrDefault(idx) ?? Tracks.FirstOrDefault();
    }

    private double TotalDurationMs()
    {
        var stepsEnd = Steps.Count == 0 ? 0 : Steps.Max(s => s.StartMs) + 1500;
        var audioEnd = AudioLanes.SelectMany(l => l.Clips)
            .Select(c => (double)(c.StartMs + c.DurationMs)).DefaultIfEmpty(0).Max();
        return Math.Max(stepsEnd, audioEnd);
    }

    private void RebuildRulerTicks()
    {
        RulerTicks.Clear();
        var total = TotalDurationMs();
        if (PxPerMs > 0)
        {
            int[] niceIntervals = { 100, 200, 500, 1000, 2000, 5000, 10000 };
            var interval = niceIntervals.FirstOrDefault(i => i * PxPerMs >= 50, niceIntervals[^1]);
            for (double t = 0; t <= total; t += interval)
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
        lane.Clips.Add(new AudioClip { FilePath = dlg.FileName, DurationMs = durationMs, StartMs = start });
        Dirty = true;
        RebuildRulerTicks();
    }

    [RelayCommand]
    private async Task ReplaceAudioClip(AudioClip? clip)
    {
        if (clip == null) return;
        var dlg = new OpenFileDialog { Filter = AudioFileFilter };
        if (dlg.ShowDialog() != true) return;
        PushHistory();
        clip.FilePath = dlg.FileName;
        clip.DurationMs = await AudioPlaybackService.ProbeDurationMsAsync(dlg.FileName);
        Dirty = true;
        RebuildRulerTicks();
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
            AudioLanes.Add(new AudioLane { Label = "AUDIO", RowIndex = 0 });
            AudioLanes.Add(new AudioLane { Label = "AMBIENT", RowIndex = 1 });
            return;
        }
        var row = 0;
        foreach (var dto in dtos)
        {
            var lane = new AudioLane { Label = dto.Label, RowIndex = row++ };
            foreach (var c in dto.Clips)
                lane.Clips.Add(new AudioClip { FilePath = c.FilePath, DurationMs = c.DurationMs, StartMs = c.StartMs, Loop = c.Loop });
            AudioLanes.Add(lane);
        }
    }

    // --- Playhead: local scrub + live hardware sync -----------------------------

    public void SetPlayheadFromPixel(double x)
    {
        if (IsLiveTracking || PxPerMs <= 0) return;
        PlayheadMs = Math.Max(0, x / PxPerMs);
    }

    private void OnSeqState(JsonElement root)
    {
        var slot = root.TryGetProperty("slot", out var s) ? s.GetInt32() : -1;
        if (slot != CurrentSlot) return;

        var playing = root.TryGetProperty("playing", out var p) && p.GetBoolean();
        var paused = root.TryGetProperty("paused", out var pa) && pa.GetBoolean();
        var elapsedMs = root.TryGetProperty("elapsedMs", out var e) ? e.GetInt32() : 0;

        if (!playing)
        {
            StopPlayheadTimer();
            IsLiveTracking = false;
            PlayheadMs = 0;
            return;
        }

        IsLiveTracking = true;
        _liveAnchorUtc = DateTime.UtcNow;
        _liveAnchorElapsedMs = elapsedMs;
        PlayheadMs = elapsedMs;

        if (paused)
        {
            StopPlayheadTimer();
            return;
        }

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

    private SequenceSnapshot Snapshot() => new(Name, Loop, AudioTrack, AudioLanesToDto(),
        Steps.Select(s => new SequenceStepDto { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs }).ToList());

    private void Apply(SequenceSnapshot snap)
    {
        Name = snap.Name;
        Loop = snap.Loop;
        AudioTrack = snap.Track;
        ApplyAudioLanesFromDto(snap.AudioLanes);
        Steps.Clear();
        foreach (var s in snap.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
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
        var idx = Steps.IndexOf(step);
        Steps.Insert(idx + 1, step.Clone());
        Dirty = true;
    }

    [RelayCommand]
    private void NewSequence()
    {
        CurrentSlot = null;
        Name = "";
        Loop = false;
        AudioTrack = 0;
        ApplyAudioLanesFromDto(null);
        Steps.Clear();
        SelectedStep = null;
        ClearHistory();
        Dirty = false;
    }

    // --- Firmware: 8 NVS slots --------------------------------------------------

    [RelayCommand]
    private void LoadSlot(SequenceSlotMeta? meta)
    {
        if (meta == null) return;
        _protocol.SeqLoad(meta.Slot);
    }

    private void OnSeqData(JsonElement root)
    {
        CurrentSlot = root.TryGetProperty("slot", out var s) ? s.GetInt32() : null;
        Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        Loop = root.TryGetProperty("loop", out var l) && l.GetBoolean();
        AudioTrack = root.TryGetProperty("track", out var t) ? t.GetInt32() : 0;
        Steps.Clear();
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
            foreach (var st in steps.EnumerateArray())
                Steps.Add(new SequenceStep
                {
                    AnimId = st.GetProperty("animId").GetInt32(),
                    Target = (ushort)st.GetProperty("target").GetInt32(),
                    StartMs = st.GetProperty("start").GetInt32(),
                });
        ApplyAudioLanesFromDto(CurrentSlot.HasValue ? _audioStore.Get(CurrentSlot.Value) : null);
        SelectedStep = null;
        ClearHistory();
        Dirty = false;
    }

    [RelayCommand]
    private void SaveToSlot()
    {
        var slot = CurrentSlot ?? FindFreeSlot();
        if (slot < 0) { MessageBox.Show("All 8 slots are occupied.", "Sequencer", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _protocol.SeqSave(slot, Name, Loop, AudioTrack, Steps);
        _audioStore.Set(slot, AudioLanesToDto());
        CurrentSlot = slot;
        Dirty = false;
    }

    private int FindFreeSlot()
    {
        var used = Catalog.Select(c => c.Slot).ToHashSet();
        for (var i = 0; i < _protocol.SeqSlotMax; i++) if (!used.Contains(i)) return i;
        return -1;
    }

    [RelayCommand]
    private void DeleteSlot(SequenceSlotMeta? meta)
    {
        if (meta == null) return;
        _protocol.SeqDelete(meta.Slot);
        _audioStore.Delete(meta.Slot);
        if (CurrentSlot == meta.Slot) NewSequence();
    }

    [RelayCommand] private void Play() { if (CurrentSlot.HasValue) _protocol.SeqRun(CurrentSlot.Value); }
    [RelayCommand] private void Stop() => _protocol.SeqStop();
    [RelayCommand] private void Pause() => _protocol.SeqPause();
    [RelayCommand] private void Resume() => _protocol.SeqResume();

    // --- Local library ------------------------------------------------------

    [RelayCommand]
    private void SaveToLibrary()
    {
        var id = Guid.NewGuid().ToString("N");
        var item = new SequenceLibraryItem
        {
            Id = id, Name = Name, Loop = Loop, AudioTrack = AudioTrack,
            AudioLanes = AudioLanesToDto(),
            Steps = Steps.Select(s => new SequenceStepDto { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs }).ToList(),
            SavedAt = DateTime.UtcNow,
        };
        _library.Save(id, item);
        RefreshLibrary();
    }

    [RelayCommand]
    private void LoadFromLibrary(SequenceLibraryItem? item)
    {
        if (item == null) return;
        CurrentSlot = null;
        Name = item.Name;
        Loop = item.Loop;
        AudioTrack = item.AudioTrack;
        ApplyAudioLanesFromDto(item.AudioLanes);
        Steps.Clear();
        foreach (var s in item.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
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

    [RelayCommand]
    private void PushToMaster(SequenceLibraryItem? item)
    {
        if (item == null) return;
        var slot = Catalog.FirstOrDefault(c => c.Name == item.Name)?.Slot ?? FindFreeSlot();
        if (slot < 0) { MessageBox.Show("All 8 slots are occupied.", "Sequencer", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var steps = item.Steps.Select(s => new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
        _protocol.SeqSave(slot, item.Name, item.Loop, item.AudioTrack, steps);
        _audioStore.Set(slot, item.AudioLanes);
    }

    [RelayCommand]
    private void PullFromMaster(SequenceSlotMeta? meta)
    {
        if (meta == null) return;
        // Loads the slot into the editor (OnSeqData), then immediately saves it to the library.
        void Once(JsonElement root)
        {
            _protocol.SeqDataReceived -= Once;
            SaveToLibrary();
        }
        _protocol.SeqDataReceived += Once;
        _protocol.SeqLoad(meta.Slot);
    }

    // --- Export / import ----------------------------------------------------------

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog { FileName = $"{(string.IsNullOrEmpty(Name) ? "sequence" : Name)}.b1seq.json", Filter = "B1 Sequence (*.b1seq.json)|*.b1seq.json" };
        if (dlg.ShowDialog() != true) return;
        var obj = new JsonObject
        {
            ["type"] = "b1-sequence", ["version"] = 3, ["name"] = Name, ["loop"] = Loop, ["audioTrack"] = AudioTrack,
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
            CurrentSlot = null;
            Name = obj["name"]?.GetValue<string>() ?? "";
            Loop = obj["loop"]?.GetValue<bool>() ?? false;
            AudioTrack = obj["audioTrack"]?.GetValue<int>() ?? 0;
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
            SelectedStep = null;
            ClearHistory();
            Dirty = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Import failed: " + ex.Message, "Sequencer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Rehearsal (client-side, persists nothing) --------------------------------

    [RelayCommand]
    private void ToggleRehearsal()
    {
        if (IsRehearsing) StopRehearsal(); else StartRehearsal();
    }

    private void StartRehearsal()
    {
        if (Steps.Count == 0 && AudioLanes.All(l => l.Clips.Count == 0)) return;
        IsRehearsing = true;
        ScheduleRehearsalPass();
    }

    // Absolute-time model (FIRMWARE-CONTRACT.md §6): one one-shot timer per step and per audio
    // clip, armed at its own StartMs from this pass's t=0, instead of a single chained timer —
    // steps/clips sharing a StartMs now actually fire together, matching what the firmware's
    // own player does, rather than always running back-to-back. Muted droids' steps are simply
    // skipped (see ToggleMute) — this only affects Rehearse, a real hardware Play always runs
    // every step, the console has no way to veto one once seqRun has been sent.
    private void ScheduleRehearsalPass()
    {
        DisposeRehearsalTimers();
        foreach (var step in Steps)
        {
            if (IsTrackMuted(step.Target)) continue;
            var timer = new System.Threading.Timer(_ => RunOnUiThread(() =>
            {
                if (!IsRehearsing) return;
                _protocol.PlayAnim(step.Target, step.AnimId, (uint)Random.Shared.Next());
            }), null, Math.Max(0, step.StartMs), System.Threading.Timeout.Infinite);
            _rehearsalTimers.Add(timer);
        }
        foreach (var clip in AudioLanes.SelectMany(l => l.Clips))
        {
            var timer = new System.Threading.Timer(_ => RunOnUiThread(() =>
            {
                if (!IsRehearsing) return;
                _audioPlayer.Play(clip.FilePath, clip.Loop);
            }), null, Math.Max(0, clip.StartMs), System.Threading.Timeout.Infinite);
            _rehearsalTimers.Add(timer);
        }
        var totalMs = (int)TotalDurationMs();
        var endTimer = new System.Threading.Timer(_ => RunOnUiThread(() =>
        {
            if (!IsRehearsing) return;
            if (Loop)
            {
                // Stop every player (including a looping ambient clip) before rearming the next
                // pass, or it would keep stacking a fresh MediaPlayer on top of the still-running
                // one every time the sequence loops.
                _audioPlayer.StopAll();
                ScheduleRehearsalPass();
            }
            else StopRehearsal();
        }), null, totalMs, System.Threading.Timeout.Infinite);
        _rehearsalTimers.Add(endTimer);
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action);
    }

    private void DisposeRehearsalTimers()
    {
        foreach (var t in _rehearsalTimers) t.Dispose();
        _rehearsalTimers.Clear();
    }

    private void StopRehearsal()
    {
        IsRehearsing = false;
        DisposeRehearsalTimers();
        _audioPlayer.StopAll();
    }
}
