namespace b1_chat_console.Services;

/// <summary>
/// Audio-side test seams (SEQ-F05/F06/F07, covered by SEQ-H05). The Sequencer's audio used to
/// call two static classes directly, which made every failure path untestable and let a probe
/// leave a media object open forever. Everything below is an interface so the headless suite can
/// drive success, failure, timeout and cancellation without Media Foundation or a real file.
/// </summary>

/// <summary>Why a duration probe ended. Only <see cref="AudioProbeStatus.Ok"/> carries a duration.</summary>
public enum AudioProbeStatus
{
    Ok,
    FileMissing,
    DecodeFailed,
    Timeout,
    Cancelled,
}

/// <summary>
/// Typed probe outcome. The previous implementation collapsed every failure to 0 ms, which the
/// timeline then rendered as a zero-width clip with no explanation — see SEQ-F04/F05.
/// </summary>
public readonly record struct AudioProbeResult(AudioProbeStatus Status, int DurationMs, string? Message)
{
    public bool Ok => Status == AudioProbeStatus.Ok;

    public static AudioProbeResult Success(int durationMs) =>
        new(AudioProbeStatus.Ok, Math.Max(0, durationMs), null);

    public static AudioProbeResult Failure(AudioProbeStatus status, string message) =>
        new(status, 0, message);

    /// <summary>Short operator-facing reason, used for the clip badge tooltip.</summary>
    public string Describe() => Status switch
    {
        AudioProbeStatus.Ok => "Duration read.",
        AudioProbeStatus.FileMissing => Message ?? "File not found.",
        AudioProbeStatus.DecodeFailed => Message ?? "The file could not be decoded.",
        AudioProbeStatus.Timeout => Message ?? "Reading the duration timed out.",
        AudioProbeStatus.Cancelled => Message ?? "Reading the duration was cancelled.",
        _ => "Unknown probe state.",
    };
}

/// <summary>Reads an audio file's duration without blocking the UI and without leaking handles.</summary>
public interface IAudioProbe
{
    Task<AudioProbeResult> ProbeAsync(string? path, CancellationToken cancellationToken = default);
}

/// <summary>Decodes a peak envelope for the timeline waveform preview.</summary>
public interface IWaveformDecoder
{
    Task<float[]?> GetPeaksAsync(string? path, CancellationToken cancellationToken = default);
}

/// <summary>A clip whose playback failed, identified so the operator learns which one.</summary>
public readonly record struct AudioPlaybackFailure(int ClipId, string FilePath, string Message)
{
    public string FileName => string.IsNullOrEmpty(FilePath)
        ? "(unknown file)"
        : System.IO.Path.GetFileName(FilePath);
}

/// <summary>
/// One media object's lifecycle, narrow enough to fake. The WPF implementation wraps
/// <c>MediaPlayer</c>; the headless suite drives the same state machine with no codec involved.
/// Implementations must tolerate <see cref="Stop"/>/<see cref="IDisposable.Dispose"/> being
/// called more than once.
/// </summary>
public interface IMediaHandle : IDisposable
{
    event Action? Opened;
    event Action? Ended;
    event Action<string>? Failed;

    /// <summary>Known only after <see cref="Opened"/>; null when the source reports no timespan.</summary>
    int? NaturalDurationMs { get; }

    void Open(string path);
    void Play();
    void Pause();
    void Stop();

    /// <summary>Seeks to a non-negative position within the opened source.</summary>
    void Seek(int positionMs);

    /// <summary>Seeks back to the start, used to restart a looping clip.</summary>
    void Rewind();
}

public interface IMediaHandleFactory
{
    IMediaHandle Create();
}
