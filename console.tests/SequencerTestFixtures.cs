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

    public event Action? DroidsChanged;
    public event Action? AnimDurationsReceived;
    public event Action<bool>? LinkClosed;

    public void PlayAnim(ushort target, int animId, uint seed) =>
        Sent.Add(new SentGesture(target, animId, seed));

    public void RaiseDroidsChanged() => DroidsChanged?.Invoke();
    public void RaiseAnimDurationsReceived() => AnimDurationsReceived?.Invoke();
    public void RaiseLinkClosed(bool unexpected = true) => LinkClosed?.Invoke(unexpected);
}

internal sealed record SentGesture(ushort Target, int AnimId, uint Seed);

internal sealed class FakeAudioPlayer : ISequencerAudioPlayer
{
    public List<AudioAction> Actions { get; } = new();
    public void Play(string? path, bool loop = false) => Actions.Add(new("Play", path, loop));
    public void PauseAll() => Actions.Add(new("PauseAll", null, false));
    public void ResumeAll() => Actions.Add(new("ResumeAll", null, false));
    public void StopAll() => Actions.Add(new("StopAll", null, false));
}

internal sealed record AudioAction(string Kind, string? Path, bool Loop);

internal sealed class FakePlaybackTimerScheduler : IPlaybackTimerScheduler
{
    public List<Entry> Entries { get; } = new();

    public IDisposable Schedule(int dueTimeMs, Action callback)
    {
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
