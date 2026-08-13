using System.IO;

namespace b1_chat_console.Services;

/// <summary>
/// Bounded audio duration probe (SEQ-F05). Replaces a static helper that had no timeout, no
/// cancellation, and collapsed every failure — missing file, missing codec, unreadable stream —
/// into a duration of 0 ms. A file that never raised MediaOpened nor MediaFailed left its task
/// pending and its media object open for the rest of the session.
///
/// Every exit path disposes the handle, and every outcome is typed so the timeline can explain
/// itself instead of silently drawing a zero-width clip.
/// </summary>
public sealed class AudioProbe : IAudioProbe
{
    public const int DefaultTimeoutMs = 10_000;

    private readonly IMediaHandleFactory _factory;
    private readonly int _timeoutMs;
    private readonly Func<string, bool> _fileExists;

    public AudioProbe(
        IMediaHandleFactory? factory = null,
        int timeoutMs = DefaultTimeoutMs,
        Func<string, bool>? fileExists = null)
    {
        _factory = factory ?? new MediaPlayerHandleFactory();
        _timeoutMs = Math.Max(1, timeoutMs);
        _fileExists = fileExists ?? File.Exists;
    }

    public async Task<AudioProbeResult> ProbeAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return AudioProbeResult.Failure(AudioProbeStatus.FileMissing, "No audio file selected.");
        if (!_fileExists(path))
            return AudioProbeResult.Failure(
                AudioProbeStatus.FileMissing, $"File not found: {Path.GetFileName(path)}");
        if (cancellationToken.IsCancellationRequested)
            return AudioProbeResult.Failure(AudioProbeStatus.Cancelled, "Cancelled before opening the file.");

        var completion = new TaskCompletionSource<AudioProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IMediaHandle? handle = null;
        try
        {
            var media = _factory.Create();
            handle = media;
            media.Opened += () =>
            {
                var durationMs = media.NaturalDurationMs;
                // A genuinely empty but valid file reports 0 and stays a success; only a source
                // that reports no timespan at all counts as a decode failure.
                completion.TrySetResult(durationMs is null
                    ? AudioProbeResult.Failure(
                        AudioProbeStatus.DecodeFailed,
                        $"{Path.GetFileName(path)} opened but reported no duration. A required audio codec may be missing.")
                    : AudioProbeResult.Success(durationMs.Value));
            };
            media.Failed += message =>
                completion.TrySetResult(AudioProbeResult.Failure(AudioProbeStatus.DecodeFailed, message));

            media.Open(path);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var finished = await Task.WhenAny(
                completion.Task, Task.Delay(_timeoutMs, timeoutSource.Token)).ConfigureAwait(false);
            if (finished == completion.Task)
            {
                timeoutSource.Cancel(); // release the pending delay instead of letting it run out
                return await completion.Task.ConfigureAwait(false);
            }

            return cancellationToken.IsCancellationRequested
                ? AudioProbeResult.Failure(AudioProbeStatus.Cancelled, "Reading the duration was cancelled.")
                : AudioProbeResult.Failure(
                    AudioProbeStatus.Timeout,
                    $"{Path.GetFileName(path)} did not report a duration within {_timeoutMs} ms.");
        }
        catch (Exception ex)
        {
            // Bad URI, file deleted between the existence check and Open, codec blowing up on load.
            return AudioProbeResult.Failure(AudioProbeStatus.DecodeFailed, ex.Message);
        }
        finally
        {
            handle?.Dispose();
        }
    }
}
