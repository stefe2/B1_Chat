using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
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
    private const int HistoryMax = 50;

    public ObservableCollection<SequenceSlotMeta> Catalog => _protocol.SeqCatalog;
    public ObservableCollection<SequenceLibraryItem> Library { get; } = new();
    public ObservableCollection<SequenceStep> Steps { get; } = new();
    public ObservableCollection<Droid> Targets => _protocol.Droids;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _loop;
    [ObservableProperty] private int _audioTrack;
    [ObservableProperty] private int? _currentSlot;
    [ObservableProperty] private bool _dirty;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    [ObservableProperty] private bool _isRehearsing;
    [ObservableProperty] private SequenceStep? _selectedStep;

    private readonly Stack<SequenceSnapshot> _history = new();
    private readonly Stack<SequenceSnapshot> _future = new();
    private readonly List<System.Threading.Timer> _rehearsalTimers = new();

    public SequencerViewModel(ProtocolClient protocol)
    {
        _protocol = protocol;
        _protocol.SeqDataReceived += OnSeqData;
        RefreshLibrary();
        _protocol.RequestSeqList();
    }

    private void RefreshLibrary()
    {
        Library.Clear();
        foreach (var item in _library.List()) Library.Add(item);
    }

    // --- Snapshot / undo-redo --------------------------------------------------

    private SequenceSnapshot Snapshot() => new(Name, Loop, AudioTrack,
        Steps.Select(s => new SequenceStepDto { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs }).ToList());

    private void Apply(SequenceSnapshot snap)
    {
        Name = snap.Name;
        Loop = snap.Loop;
        AudioTrack = snap.Track;
        Steps.Clear();
        foreach (var s in snap.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
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
    private void AddStep()
    {
        PushHistory();
        // Appends 1s after the last step's own start, so building a list top-to-bottom
        // still "feels" like the old relative-delay chain even though StartMs is absolute.
        var start = Steps.Count == 0 ? 0 : Steps.Max(s => s.StartMs) + 1000;
        Steps.Add(new SequenceStep { AnimId = 0, Target = 0xFFFF, StartMs = start });
        Dirty = true;
    }

    [RelayCommand]
    private void DeleteStep(SequenceStep? step)
    {
        if (step == null) return;
        PushHistory();
        Steps.Remove(step);
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
    private void MoveStepUp(SequenceStep? step)
    {
        if (step == null) return;
        var idx = Steps.IndexOf(step);
        if (idx <= 0) return;
        PushHistory();
        Steps.Move(idx, idx - 1);
        Dirty = true;
    }

    [RelayCommand]
    private void MoveStepDown(SequenceStep? step)
    {
        if (step == null) return;
        var idx = Steps.IndexOf(step);
        if (idx < 0 || idx >= Steps.Count - 1) return;
        PushHistory();
        Steps.Move(idx, idx + 1);
        Dirty = true;
    }

    [RelayCommand]
    private void NewSequence()
    {
        CurrentSlot = null;
        Name = "";
        Loop = false;
        AudioTrack = 0;
        Steps.Clear();
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
        ClearHistory();
        Dirty = false;
    }

    [RelayCommand]
    private void SaveToSlot()
    {
        var slot = CurrentSlot ?? FindFreeSlot();
        if (slot < 0) { MessageBox.Show("All 8 slots are occupied.", "Sequencer", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _protocol.SeqSave(slot, Name, Loop, AudioTrack, Steps);
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
        Steps.Clear();
        foreach (var s in item.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, StartMs = s.StartMs });
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
            ["type"] = "b1-sequence", ["version"] = 2, ["name"] = Name, ["loop"] = Loop, ["audioTrack"] = AudioTrack,
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
        if (Steps.Count == 0) return;
        IsRehearsing = true;
        if (AudioTrack > 0) _protocol.PlayTrack(AudioTrack);
        ScheduleRehearsalPass();
    }

    // Absolute-time model (FIRMWARE-CONTRACT.md §6): one one-shot timer per step,
    // armed at its own StartMs from this pass's t=0, instead of a single chained
    // timer — steps sharing a StartMs now actually fire together, matching what
    // the firmware's own player does, rather than always running back-to-back.
    private void ScheduleRehearsalPass()
    {
        DisposeRehearsalTimers();
        foreach (var step in Steps)
        {
            var timer = new System.Threading.Timer(_ => RunOnUiThread(() =>
            {
                if (!IsRehearsing) return;
                _protocol.PlayAnim(step.Target, step.AnimId, (uint)Random.Shared.Next());
            }), null, Math.Max(0, step.StartMs), System.Threading.Timeout.Infinite);
            _rehearsalTimers.Add(timer);
        }
        var totalMs = Steps.Max(s => s.StartMs) + 1500;
        var endTimer = new System.Threading.Timer(_ => RunOnUiThread(() =>
        {
            if (!IsRehearsing) return;
            if (Loop)
            {
                if (AudioTrack > 0) _protocol.PlayTrack(AudioTrack);
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
    }
}
