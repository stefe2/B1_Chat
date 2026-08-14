using System.Windows.Media;
using System.Windows.Threading;

namespace b1_chat_console.Services;

/// <summary>
/// The real <see cref="IMediaHandle"/>: a thin wrapper over WPF's <c>MediaPlayer</c> (Media
/// Foundation, no extra NuGet dependency). It owns exactly one player and guarantees that every
/// handler is detached and the player closed on the first Dispose, whatever path got us there —
/// natural end, decode failure, Stop, or an abandoned probe (SEQ-F05/F07).
///
/// Threading: <c>MediaPlayer</c> is a <c>DispatcherObject</c>, so its events only fire on a thread
/// with a running dispatcher. Every caller in the Sequencer reaches this class from the UI thread
/// (the playback wake callback marshals through <c>RunOnUiThread</c> first) — do not "optimize"
/// that away, or MediaEnded/MediaOpened silently never fire.
/// </summary>
public sealed class MediaPlayerHandle : IMediaHandle
{
    private readonly MediaPlayer _player = new();
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private bool _opened;
    private int? _pendingPositionMs;

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public MediaPlayerHandle()
    {
        _dispatcher = _player.Dispatcher;
        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    public int? NaturalDurationMs =>
        !_disposed && _player.NaturalDuration.HasTimeSpan
            ? (int)_player.NaturalDuration.TimeSpan.TotalMilliseconds
            : null;

    public void Open(string path)
    {
        if (_disposed) return;
        _opened = false;
        _pendingPositionMs = null;
        _player.Open(new Uri(path));
    }

    public void Play()
    {
        if (!_disposed) _player.Play();
    }

    public void Pause()
    {
        if (!_disposed) _player.Pause();
    }

    public void Stop()
    {
        if (!_disposed) _player.Stop();
    }

    public void Seek(int positionMs)
    {
        if (_disposed) return;
        var clamped = Math.Max(0, positionMs);
        if (!_opened)
        {
            // MediaPlayer opens asynchronously. Retain the request so MediaOpened applies the
            // offset before notifying subscribers and before queued playback becomes audible.
            _pendingPositionMs = clamped;
            return;
        }
        _player.Position = TimeSpan.FromMilliseconds(clamped);
    }

    public void Rewind()
    {
        if (!_disposed) _player.Position = TimeSpan.Zero;
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        _opened = true;
        if (_pendingPositionMs is { } positionMs)
        {
            _player.Position = TimeSpan.FromMilliseconds(positionMs);
            _pendingPositionMs = null;
        }
        Opened?.Invoke();
    }

    private void OnMediaEnded(object? sender, EventArgs e) => Ended?.Invoke();

    private void OnMediaFailed(object? sender, ExceptionEventArgs e) =>
        Failed?.Invoke(e.ErrorException?.Message ?? "The media could not be opened.");

    public void Dispose()
    {
        // AudioProbe deliberately does not retain the caller's SynchronizationContext while it
        // waits for a bounded result. MediaPlayer is dispatcher-affine, though: closing it from
        // that continuation throws and used to leave the native media resource alive until GC.
        // Marshal the complete teardown back to the owner rather than swallowing that failure.
        if (!_dispatcher.CheckAccess())
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                // Dispatcher shutdown owns the remaining WPF resources. Managed subscribers must
                // still be released so the abandoned handle cannot retain its Sequencer owner.
                Opened = null;
                Ended = null;
                Failed = null;
                _disposed = true;
                return;
            }

            _dispatcher.Invoke(DisposeCore, DispatcherPriority.Send);
            return;
        }

        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        _player.MediaOpened -= OnMediaOpened;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        Opened = null;
        Ended = null;
        Failed = null;
        // Stop and Close are separate so an unopened/failed player's Stop cannot prevent Close
        // from releasing the underlying Media Foundation resource.
        try { _player.Stop(); }
        catch (InvalidOperationException) { }
        try { _player.Close(); }
        catch (InvalidOperationException) { }
    }
}

public sealed class MediaPlayerHandleFactory : IMediaHandleFactory
{
    public IMediaHandle Create() => new MediaPlayerHandle();
}
