using System.Collections.ObjectModel;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.Tests;

internal sealed class FakeSequencerProtocol : ISequencerProtocol
{
    public ObservableCollection<Droid> Droids { get; } = new();
    public Dictionary<int, int> Durations { get; } = new();
    public IReadOnlyDictionary<int, int> AnimDurationMs => Durations;
    public List<SentGesture> Sent { get; } = new();
    public List<SentLeaseRenewal> LeaseRenewals { get; } = new();
    public List<ushort> SafeStops { get; } = new();
    public List<SentServoCommand> ServoCommands { get; } = new();
    private uint _nextRequestId;

    public event Action? DroidsChanged;
    public event Action? AnimDurationsReceived;
    public event Action<bool>? LinkClosed;
    public event Action<AnimMasterReceipt>? AnimMasterAccepted;
    public event Action<AnimExecutionReport>? AnimExecutionReceived;
    public AnimDispatchState NextDispatchState { get; set; } = AnimDispatchState.Written;
    public bool SupportsAnimLease { get; set; } = true;
    public bool SupportsSafeStop { get; set; } = true;

    public AnimDispatchResult PlayAnim(ushort target, int animId, uint seed, ushort leaseMs = 0)
    {
        var requestId = ++_nextRequestId;
        Sent.Add(new SentGesture(requestId, target, animId, seed, leaseMs));
        return new AnimDispatchResult(requestId, NextDispatchState);
    }

    public AnimDispatchState RenewAnimLease(ushort target, int meshSeq, ushort leaseMs)
    {
        LeaseRenewals.Add(new SentLeaseRenewal(target, meshSeq, leaseMs));
        return NextDispatchState;
    }

    public AnimDispatchState SafeStop(ushort target)
    {
        SafeStops.Add(target);
        return NextDispatchState;
    }

    public void SetServo(ushort target, bool enabled) =>
        ServoCommands.Add(new SentServoCommand(target, enabled));

    public void RaiseDroidsChanged() => DroidsChanged?.Invoke();
    public void RaiseAnimDurationsReceived() => AnimDurationsReceived?.Invoke();
    public void RaiseLinkClosed(bool unexpected = true) => LinkClosed?.Invoke(unexpected);
    public void RaiseAnimMasterAccepted(uint requestId, ushort target, int animId,
        int meshSeq = 77, bool meshQueued = true, bool localHandled = false,
        int leaseMs = 0) =>
        AnimMasterAccepted?.Invoke(new AnimMasterReceipt(
            requestId, target, animId, meshSeq, meshQueued, localHandled, leaseMs));
    public void RaiseAnimExecution(uint requestId, ushort droidId, int animId,
        string phase, string? reason = null) =>
        AnimExecutionReceived?.Invoke(new AnimExecutionReport(
            requestId, droidId, animId, phase, reason, 1234, 77));
}

internal sealed record SentGesture(uint RequestId, ushort Target, int AnimId, uint Seed, ushort LeaseMs);
internal sealed record SentLeaseRenewal(ushort Target, int MeshSeq, ushort LeaseMs);
internal sealed record SentServoCommand(ushort Target, bool Enabled);

internal sealed class FakeAudioPlayer : ISequencerAudioPlayer
{
    public List<AudioAction> Actions { get; } = new();
    public void Play(string? path, bool loop = false) => Actions.Add(new("Play", path, loop));
    public void PauseAll() => Actions.Add(new("PauseAll", null, false));
    public void ResumeAll() => Actions.Add(new("ResumeAll", null, false));
    public void StopAll() => Actions.Add(new("StopAll", null, false));
}

internal sealed record AudioAction(string Kind, string? Path, bool Loop);

internal sealed class FakeSequencerSettings : ISequencerSettings
{
    public string? LastSequencePath { get; set; }
    public string? LastSceneId { get; set; }
    public int SequencePathWrites { get; private set; }
    public int SceneIdWrites { get; private set; }

    public void SetLastSequencePath(string? path)
    {
        LastSequencePath = path;
        SequencePathWrites++;
    }

    public void SetLastSceneId(string? sceneId)
    {
        LastSceneId = sceneId;
        SceneIdWrites++;
    }
}

internal sealed class FakeSequencerPersistenceDialogs : ISequencerPersistenceDialogs
{
    public string? ExportPath { get; set; }
    public string? ImportPath { get; set; }
    public bool ConfirmResult { get; set; }
    public bool DeleteConfirmResult { get; set; }
    public string? SceneNameResult { get; set; }
    public List<string> ConfirmationRequests { get; } = new();
    public List<(string Title, string Message)> Errors { get; } = new();
    public int ExportSelections { get; private set; }
    public int ImportSelections { get; private set; }
    public int SceneNamePrompts { get; private set; }
    public List<string> DeleteConfirmationRequests { get; } = new();

    public string? ChooseExportPath(string suggestedFileName)
    {
        ExportSelections++;
        return ExportPath;
    }

    public string? ChooseImportPath()
    {
        ImportSelections++;
        return ImportPath;
    }

    public string? PromptForSceneName(string initialName, string title)
    {
        SceneNamePrompts++;
        return SceneNameResult;
    }

    public bool ConfirmDiscardUnsavedChanges(string replacementDescription)
    {
        ConfirmationRequests.Add(replacementDescription);
        return ConfirmResult;
    }

    public bool ConfirmMoveSceneToTrash(string sceneName)
    {
        DeleteConfirmationRequests.Add(sceneName);
        return DeleteConfirmResult;
    }

    public void ShowError(string title, string message) => Errors.Add((title, message));
}

internal sealed class FakeSequenceLibraryService : ISequenceLibraryService
{
    public List<SequenceLibraryItem> Items { get; } = new();
    public List<SequenceLibraryIssue> Issues { get; } = new();
    public List<SequenceLibraryItem> Saved { get; } = new();
    public List<string> Trashed { get; } = new();
    public Exception? SaveError { get; set; }
    public Exception? TrashError { get; set; }

    public SequenceLibraryScan Scan() => new(Items.ToArray(), Issues.ToArray());
    public SequenceLibraryItem? Get(string id) => Items.FirstOrDefault(item => item.Id == id);
    public void Save(SequenceLibraryItem item)
    {
        if (SaveError != null) throw SaveError;
        Saved.Add(item);
        Items.RemoveAll(existing => existing.Id == item.Id);
        Items.Add(item);
    }
    public void MoveToTrash(string id)
    {
        if (TrashError != null) throw TrashError;
        if (Items.RemoveAll(item => item.Id == id) == 0)
            throw new FileNotFoundException("Scene is missing.");
        Trashed.Add(id);
    }
}

internal sealed class ThrowingAtomicTextFileWriter(Exception exception) : IAtomicTextFileWriter
{
    public int Attempts { get; private set; }

    public void WriteAllText(string destinationPath, string contents)
    {
        Attempts++;
        throw exception;
    }
}

internal sealed class FakePlaybackTimerScheduler : IPlaybackTimerScheduler
{
    public List<Entry> Entries { get; } = new();
    public bool FailNextSchedule { get; set; }

    public IDisposable Schedule(int dueTimeMs, Action callback)
    {
        if (FailNextSchedule)
        {
            FailNextSchedule = false;
            throw new InvalidOperationException("Simulated playback scheduler failure.");
        }
        var entry = new Entry(dueTimeMs, callback);
        Entries.Add(entry);
        return entry;
    }

    internal sealed class Entry(int dueTimeMs, Action callback) : IDisposable
    {
        public int DueTimeMs { get; } = dueTimeMs;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public void Invoke()
        {
            if (Disposed) throw new InvalidOperationException("Cannot invoke a disposed fake timer.");
            callback();
        }
        public void InvokeEvenIfDisposed() => callback();
    }
}

internal sealed class FakePlaybackClock : IPlaybackClock
{
    public TimeSpan Elapsed { get; private set; }
    public void Restart() => Elapsed = TimeSpan.Zero;
    public void SetElapsed(TimeSpan elapsed) => Elapsed = elapsed;
}

internal sealed class SequencerDocumentBuilder
{
    public List<SequenceStep> Steps { get; } = new();
    public List<AudioLane> AudioLanes { get; } = new();
    public Dictionary<int, int> Durations { get; } = new();

    public SequencerDocumentBuilder WithGesture(
        int startMs,
        ushort target,
        int animId,
        int durationMs = SequencerPlaybackPlan.DefaultGestureDurationMs)
    {
        Steps.Add(new SequenceStep { StartMs = startMs, Target = target, AnimId = animId });
        Durations[animId] = durationMs;
        return this;
    }

    public SequencerDocumentBuilder WithAudio(
        int startMs,
        int durationMs,
        string path = @"C:\fixtures\audio.wav",
        bool loop = false,
        string laneName = "AUDIO")
    {
        var lane = AudioLanes.FirstOrDefault(l => l.Label == laneName);
        if (lane == null)
        {
            lane = new AudioLane { Label = laneName, RowIndex = AudioLanes.Count };
            AudioLanes.Add(lane);
        }
        lane.Clips.Add(new AudioClip
        {
            StartMs = startMs,
            DurationMs = durationMs,
            FilePath = path,
            Loop = loop,
        });
        return this;
    }

    public SequencerPlaybackPlan Capture(bool loop = false, uint seed = 1) =>
        SequencerPlaybackPlan.Capture(Steps, AudioLanes, Durations, loop, () => seed);
}

internal sealed class TemporaryJsonFixture : IDisposable
{
    public TemporaryJsonFixture()
    {
        DirectoryPath = Path.Combine(
            Path.GetTempPath(), "b1-chat-sequencer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string Write(string fileName, string json)
    {
        var path = Path.Combine(DirectoryPath, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
