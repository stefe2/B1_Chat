using System.Windows.Media;

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
    private bool _disposed;

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public MediaPlayerHandle()
    {
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

    public void Rewind()
    {
        if (!_disposed) _player.Position = TimeSpan.Zero;
    }

    private void OnMediaOpened(object? sender, EventArgs e) => Opened?.Invoke();

    private void OnMediaEnded(object? sender, EventArgs e) => Ended?.Invoke();

    private void OnMediaFailed(object? sender, ExceptionEventArgs e) =>
        Failed?.Invoke(e.ErrorException?.Message ?? "The media could not be opened.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.MediaOpened -= OnMediaOpened;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        Opened = null;
        Ended = null;
        Failed = null;
        try
        {
            _player.Stop();
            _player.Close();
        }
        catch
        {
            // A player that never opened can throw on Close; nothing useful is left to report
            // and the handle is being discarded either way.
        }
    }
}

public sealed class MediaPlayerHandleFactory : IMediaHandleFactory
{
    public IMediaHandle Create() => new MediaPlayerHandle();
}
