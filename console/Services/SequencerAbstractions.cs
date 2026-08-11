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
    bool SupportsAnimLease { get; }
    event Action? DroidsChanged;
    event Action? AnimDurationsReceived;
    event Action<bool>? LinkClosed;
    event Action<AnimMasterReceipt>? AnimMasterAccepted;
    event Action<AnimExecutionReport>? AnimExecutionReceived;
    AnimDispatchResult PlayAnim(ushort target, int animId, uint seed, ushort leaseMs = 0);
    AnimDispatchState RenewAnimLease(ushort target, int meshSeq, ushort leaseMs);
}

/// <summary>Audio side effects consumed by the Sequencer.</summary>
public interface ISequencerAudioPlayer
{
    void Play(string? path, bool loop = false);
    void PauseAll();
    void ResumeAll();
    void StopAll();
}

/// <summary>
/// One-shot callback scheduler. The current implementation still uses thread-pool timers; the
/// interface is the seam for deterministic tests and the later single-queue scheduler work.
/// </summary>
public interface IPlaybackTimerScheduler
{
    IDisposable Schedule(int dueTimeMs, Action callback);
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

public sealed class ThreadPoolPlaybackTimerScheduler : IPlaybackTimerScheduler
{
    public IDisposable Schedule(int dueTimeMs, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new System.Threading.Timer(
            _ => callback(), null, Math.Max(0, dueTimeMs), System.Threading.Timeout.Infinite);
    }
}
