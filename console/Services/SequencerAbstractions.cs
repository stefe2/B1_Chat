using System.Collections.ObjectModel;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

public readonly record struct AnimExecutionReport(
    uint RequestId,
    ushort DroidId,
    int AnimId,
    string Phase,
    string? Reason,
    uint AtMs,
    int MeshSeq);

public enum AnimDispatchState
{
    Written,
    NotConnected,
    HandshakePending,
    CatalogMismatch,
    WriteFailed,
}

public readonly record struct AnimDispatchResult(uint RequestId, AnimDispatchState State)
{
    public bool Written => State == AnimDispatchState.Written;
}

public readonly record struct AnimMasterReceipt(
    uint RequestId,
    ushort Target,
    int AnimId,
    int MeshSeq,
    bool MeshQueued,
    bool LocalHandled,
    int LeaseMs = 0);

/// <summary>Narrow protocol surface consumed by the Sequencer and its headless tests.</summary>
public interface ISequencerProtocol
{
    ObservableCollection<Droid> Droids { get; }
    IReadOnlyDictionary<int, int> AnimDurationMs { get; }
    IReadOnlyDictionary<int, AnimationDurationMetadata> AnimDurationMetadata { get; }
    bool PortOpen { get; }
    bool SessionReady { get; }
    bool SupportsAnimLease { get; }
    bool SupportsGestureStop => false;
    bool SupportsSafeStop { get; }
    event Action? DroidsChanged;
    event Action? AnimDurationsReceived;
    event Action<bool>? LinkClosed;
    event Action<AnimMasterReceipt>? AnimMasterAccepted;
    event Action<AnimExecutionReport>? AnimExecutionReceived;
    AnimDispatchResult PlayAnim(ushort target, int animId, uint seed, ushort leaseMs = 0);
    AnimDispatchState RenewAnimLease(ushort target, int meshSeq, ushort leaseMs);
    AnimDispatchState StopGesture(ushort target, int animId) =>
        PlayAnim(target, 0, 1).State;
    AnimDispatchState SafeStop(ushort target);
    void SetServo(ushort target, bool enabled);
}

/// <summary>
/// Audio side effects consumed by the Sequencer. The audio-specific seams (probe, waveform
/// decoding, media handles) live in <c>AudioAbstractions.cs</c>.
/// </summary>
public interface ISequencerAudioPlayer
{
    /// <summary>
    /// <paramref name="clipId"/> is the playback plan's source order for this clip. It is optional
    /// so the existing dispatch call site stays unchanged; it exists only so a failure can name
    /// the clip that caused it (SEQ-F07). <paramref name="startOffsetMs"/> seeks overlapping audio
    /// when a stopped pass begins from a retained playhead.
    /// </summary>
    void Play(string? path, bool loop = false, int clipId = 0, int startOffsetMs = 0);
    void PauseAll();
    void ResumeAll();
    void StopAll();

    /// <summary>Raised when a clip cannot play. Playback of the other clips continues.</summary>
    event Action<AudioPlaybackFailure>? PlaybackFailed;
}

/// <summary>
/// One-shot callback scheduler. The current implementation still uses thread-pool timers; the
/// interface is the seam for deterministic tests and the later single-queue scheduler work.
/// </summary>
public interface IPlaybackTimerScheduler
{
    IDisposable Schedule(int dueTimeMs, Action callback);
}

/// <summary>
/// Creates one rearmable wake timer for an entire playback pass. Rearming changes the
/// next wake deadline without allocating another OS timer, regardless of event count.
/// </summary>
public interface IPlaybackWakeScheduler
{
    IPlaybackWakeTimer Create(Action callback);
}

public interface IPlaybackWakeTimer : IDisposable
{
    void Rearm(int dueTimeMs);
}

/// <summary>Monotonic elapsed-time source for playhead and Pause calculations.</summary>
public interface IPlaybackClock
{
    TimeSpan Elapsed { get; }
    void Restart();
}

public sealed class StopwatchPlaybackClock : IPlaybackClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public void Restart() => _stopwatch.Restart();
}

public sealed class ThreadPoolPlaybackTimerScheduler : IPlaybackTimerScheduler, IPlaybackWakeScheduler
{
    public IDisposable Schedule(int dueTimeMs, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new System.Threading.Timer(
            _ => callback(), null, Math.Max(0, dueTimeMs), System.Threading.Timeout.Infinite);
    }

    public IPlaybackWakeTimer Create(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new ThreadPoolPlaybackWakeTimer(callback);
    }

    private sealed class ThreadPoolPlaybackWakeTimer : IPlaybackWakeTimer
    {
        private readonly System.Threading.Timer _timer;
        private bool _disposed;

        public ThreadPoolPlaybackWakeTimer(Action callback) =>
            _timer = new System.Threading.Timer(
                _ => callback(), null, System.Threading.Timeout.Infinite,
                System.Threading.Timeout.Infinite);

        public void Rearm(int dueTimeMs)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ThreadPoolPlaybackWakeTimer));
            _timer.Change(Math.Max(0, dueTimeMs), System.Threading.Timeout.Infinite);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
    }
}
