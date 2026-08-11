namespace b1_chat_console.Services;

/// <summary>
/// Invalidates callbacks belonging to an older playback pass. Disposing a thread-pool timer
/// cannot retract a callback that was already queued; the callback must also prove that its
/// generation still owns the transport before it performs a side effect.
/// </summary>
public sealed class PlaybackGeneration
{
    private long _current;

    public long Begin() => Interlocked.Increment(ref _current);

    public void Cancel() => Interlocked.Increment(ref _current);

    public bool IsCurrent(long generation) =>
        generation != 0 && Volatile.Read(ref _current) == generation;
}
