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
    private System.Threading.Timer? _rehearsalTimer;
    private int _rehearsalIndex;

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
        Steps.Select(s => new SequenceStepDto { AnimId = s.AnimId, Target = s.Target, DelayMs = s.DelayMs }).ToList());

    private void Apply(SequenceSnapshot snap)
    {
        Name = snap.Name;
        Loop = snap.Loop;
        AudioTrack = snap.Track;
        Steps.Clear();
        foreach (var s in snap.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, DelayMs = s.DelayMs });
        Dirty = true;
        UpdateUndoButtons();
    }

    private void PushHistory()
    {
        _history.Push(Snapshot());
        while (_history.Count > HistoryMax) { /* Stack a pas de RemoveAt : on tolere > 50 sur ce portage minimal */ break; }
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

    // --- Edition ----------------------------------------------------------------

    [RelayCommand]
    private void AddStep()
    {
        PushHistory();
        Steps.Add(new SequenceStep { AnimId = 0, Target = 0xFFFF, DelayMs = 1000 });
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

    // --- Firmware : 8 slots NVS --------------------------------------------------

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
                    Target = (ushort)st.GetProperty("targetId").GetInt32(),
                    DelayMs = st.GetProperty("delayMs").GetInt32(),
                });
        ClearHistory();
        Dirty = false;
    }

    [RelayCommand]
    private void SaveToSlot()
    {
        var slot = CurrentSlot ?? FindFreeSlot();
        if (slot < 0) { MessageBox.Show("Les 8 slots sont occupés.", "Séquenceur", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
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

    // --- Bibliotheque locale ------------------------------------------------------

    [RelayCommand]
    private void SaveToLibrary()
    {
        var id = Guid.NewGuid().ToString("N");
        var item = new SequenceLibraryItem
        {
            Id = id, Name = Name, Loop = Loop, AudioTrack = AudioTrack,
            Steps = Steps.Select(s => new SequenceStepDto { AnimId = s.AnimId, Target = s.Target, DelayMs = s.DelayMs }).ToList(),
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
        foreach (var s in item.Steps) Steps.Add(new SequenceStep { AnimId = s.AnimId, Target = s.Target, DelayMs = s.DelayMs });
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
        if (slot < 0) { MessageBox.Show("Les 8 slots sont occupés.", "Séquenceur", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var steps = item.Steps.Select(s => new SequenceStep { AnimId = s.AnimId, Target = s.Target, DelayMs = s.DelayMs });
        _protocol.SeqSave(slot, item.Name, item.Loop, item.AudioTrack, steps);
    }

    [RelayCommand]
    private void PullFromMaster(SequenceSlotMeta? meta)
    {
        if (meta == null) return;
        // Charge le slot dans l'editeur (OnSeqData), puis sauve immediatement en bibliotheque.
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
        var dlg = new SaveFileDialog { FileName = $"{(string.IsNullOrEmpty(Name) ? "sequence" : Name)}.b1seq.json", Filter = "Séquence B1 (*.b1seq.json)|*.b1seq.json" };
        if (dlg.ShowDialog() != true) return;
        var obj = new JsonObject
        {
            ["type"] = "b1-sequence", ["version"] = 1, ["name"] = Name, ["loop"] = Loop, ["audioTrack"] = AudioTrack,
            ["steps"] = new JsonArray(Steps.Select(s => (JsonNode)new JsonObject { ["animId"] = s.AnimId, ["target"] = s.Target, ["delayMs"] = s.DelayMs }).ToArray()),
        };
        File.WriteAllText(dlg.FileName, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Séquence B1 (*.b1seq.json)|*.b1seq.json|JSON (*.json)|*.json" };
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
                        Steps.Add(new SequenceStep { AnimId = so["animId"]?.GetValue<int>() ?? 0, Target = so["target"]?.GetValue<ushort>() ?? 0xFFFF, DelayMs = so["delayMs"]?.GetValue<int>() ?? 1000 });
            ClearHistory();
            Dirty = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Import impossible : " + ex.Message, "Séquenceur", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Repetition (client-side, ne persiste rien) --------------------------------

    [RelayCommand]
    private void ToggleRehearsal()
    {
        if (IsRehearsing) StopRehearsal(); else StartRehearsal();
    }

    private void StartRehearsal()
    {
        if (Steps.Count == 0) return;
        IsRehearsing = true;
        _rehearsalIndex = 0;
        if (AudioTrack > 0) _protocol.PlayTrack(AudioTrack);
        FireRehearsalStep();
    }

    private void FireRehearsalStep()
    {
        if (_rehearsalIndex >= Steps.Count)
        {
            if (Loop) { _rehearsalIndex = 0; }
            else { StopRehearsal(); return; }
        }
        var step = Steps[_rehearsalIndex];
        _protocol.PlayAnim(step.Target, step.AnimId, (uint)Random.Shared.Next());
        var delay = Math.Max(200, step.DelayMs);
        _rehearsalTimer?.Dispose();
        _rehearsalTimer = new System.Threading.Timer(_ =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            void Next() { _rehearsalIndex++; FireRehearsalStep(); }
            if (dispatcher == null || dispatcher.CheckAccess()) Next(); else dispatcher.Invoke(Next);
        }, null, delay, System.Threading.Timeout.Infinite);
    }

    private void StopRehearsal()
    {
        IsRehearsing = false;
        _rehearsalTimer?.Dispose();
        _rehearsalTimer = null;
    }
}
