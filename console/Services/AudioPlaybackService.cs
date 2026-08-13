using System.IO;

namespace b1_chat_console.Services;

/// <summary>
/// Console-side audio playback for the Sequencer (the master's DFPlayer was retired in fw 1.6.0 —
/// this is the only audio source now). Several clips can play at once, one media handle each.
///
/// SEQ-F07 rewrote the lifecycle. Previously every player stayed in the tracked set until the next
/// global Stop, so a finished clip kept its media object open, <c>ResumeAll</c> called Play on
/// players that had already ended (restarting them from the beginning), and a decode failure mid
/// pass was completely silent. Now a clip that ends or fails detaches, closes and leaves the set,
/// Pause/Resume only touch genuinely active players, and failures are reported with the clip that
/// caused them.
///
/// Threading: callers reach this class from the UI thread (the playback wake callback marshals
/// first). The lock guards the tracked set against Stop arriving from another path, not against a
/// dispatcher-free caller — see <see cref="MediaPlayerHandle"/> for why that matters.
/// </summary>
public sealed class AudioPlaybackService : ISequencerAudioPlayer, IDisposable
{
    private readonly IMediaHandleFactory _factory;
    private readonly Func<string, bool> _fileExists;
    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();
    private bool _pausedAll;

    public AudioPlaybackService(IMediaHandleFactory? factory = null, Func<string, bool>? fileExists = null)
    {
        _factory = factory ?? new MediaPlayerHandleFactory();
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>Raised when a clip fails to play. The Sequencer surfaces it; playback continues.</summary>
    public event Action<AudioPlaybackFailure>? PlaybackFailed;

    /// <summary>Active (not yet ended or failed) players. Exposed for tests and diagnostics.</summary>
    public int ActiveCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>
    /// Starts an independent playback. <paramref name="clipId"/> is the plan's source order, used
    /// only to name the clip in a failure report — 0 when the caller has no identity to give.
    /// </summary>
    public void Play(string? path, bool loop = false, int clipId = 0)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!_fileExists(path))
        {
            PlaybackFailed?.Invoke(new AudioPlaybackFailure(
                clipId, path, $"File not found: {Path.GetFileName(path)}"));
            return;
        }

        var entry = new Entry(_factory.Create(), clipId, path, loop);
        entry.Handle.Ended += () => OnEnded(entry);
        entry.Handle.Failed += message => OnFailed(entry, message);

        lock (_gate) _entries.Add(entry);

        try
        {
            entry.Handle.Open(path);
            entry.Handle.Play();
        }
        catch (Exception ex)
        {
            OnFailed(entry, ex.Message);
        }
    }

    private void OnEnded(Entry entry)
    {
        if (entry.Loop)
        {
            // Looping clip: restart in place. It stays tracked until Stop, by design.
            entry.Handle.Rewind();
            entry.Handle.Play();
            return;
        }
        Retire(entry);
    }

    private void OnFailed(Entry entry, string message)
    {
        if (!Retire(entry)) return; // already retired: report a failure exactly once
        PlaybackFailed?.Invoke(new AudioPlaybackFailure(entry.ClipId, entry.Path, message));
    }

    private bool Retire(Entry entry)
    {
        lock (_gate)
        {
            if (!_entries.Remove(entry)) return false;
        }
        entry.Handle.Dispose(); // detaches handlers and closes the media object
        return true;
    }

    // MediaPlayer keeps its position while paused, so ResumeAll needs no seek bookkeeping. Clips
    // whose timer has not fired yet are simply never started — the caller cancels them instead.
    public void PauseAll()
    {
        Entry[] active;
        lock (_gate)
        {
            _pausedAll = true;
            active = _entries.ToArray();
        }
        foreach (var entry in active) entry.Handle.Pause();
    }

    public void ResumeAll()
    {
        Entry[] active;
        lock (_gate)
        {
            if (!_pausedAll) return;
            _pausedAll = false;
            // Ended and failed clips already left the set, so nothing here can be restarted
            // from zero by a Resume — that was the SEQ-F07 defect.
            active = _entries.ToArray();
        }
        foreach (var entry in active) entry.Handle.Play();
    }

    public void StopAll()
    {
        Entry[] active;
        lock (_gate)
        {
            active = _entries.ToArray();
            _entries.Clear();
            _pausedAll = false;
        }
        foreach (var entry in active)
        {
            entry.Handle.Stop();
            entry.Handle.Dispose();
        }
    }

    public void Dispose() => StopAll();

    private sealed record Entry(IMediaHandle Handle, int ClipId, string Path, bool Loop);
}
