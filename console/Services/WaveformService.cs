using System.IO;
using NAudio.Wave;

namespace b1_chat_console.Services;

/// <summary>
/// Decodes an audio file into a fixed-resolution peak envelope for the Sequencer timeline's
/// waveform preview (NAudio — the only decoder in this app with raw sample access;
/// <see cref="MediaPlayerHandle"/>'s MediaPlayer has none).
///
/// SEQ-F06 replaced a permanent path-keyed cache that never expired: editing a file in place kept
/// showing the old envelope forever, a failed decode was memoized so a file created a second later
/// could never render, and the dictionary grew for the lifetime of the process. The key now
/// includes the file's size and last-write time, failures are not cached, and the cache is bounded.
/// </summary>
public sealed class WaveformService : IWaveformDecoder
{
    public const int Resolution = 120;
    public const int DefaultCacheCapacity = 64;

    /// <summary>Shared instance used by the application; tests construct their own.</summary>
    public static readonly WaveformService Shared = new();

    private readonly int _capacity;
    private readonly Func<string, CancellationToken, float[]?> _decode;
    private readonly Dictionary<string, float[]> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new(); // most recently used at the front
    private readonly object _gate = new();

    /// <summary>
    /// <paramref name="decoder"/> exists so the headless suite can exercise the cache key,
    /// eviction and retry policy without a codec; production always uses the NAudio decode.
    /// </summary>
    public WaveformService(
        int cacheCapacity = DefaultCacheCapacity,
        Func<string, CancellationToken, float[]?>? decoder = null)
    {
        _capacity = Math.Max(1, cacheCapacity);
        _decode = decoder ?? ComputePeaks;
    }

    public int CachedCount
    {
        get { lock (_gate) return _cache.Count; }
    }

    public Task<float[]?> GetPeaksAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult<float[]?>(null);
        return Task.Run(() => Decode(path, cancellationToken), cancellationToken);
    }

    private float[]? Decode(string path, CancellationToken cancellationToken)
    {
        var key = BuildKey(path);
        if (key == null) return null; // missing file: nothing to decode and nothing to remember

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                return cached;
            }
        }

        var peaks = _decode(path, cancellationToken);
        // A failed or cancelled decode is deliberately not cached, so the same clip can retry
        // once the file is readable again.
        if (peaks == null) return null;

        lock (_gate)
        {
            _cache[key] = peaks;
            Touch(key);
            while (_cache.Count > _capacity && _recency.Last is { } oldest)
            {
                _cache.Remove(oldest.Value);
                _recency.RemoveLast();
            }
        }
        return peaks;
    }

    private void Touch(string key)
    {
        var node = _recency.Find(key);
        if (node != null) _recency.Remove(node);
        _recency.AddFirst(key);
    }

    /// <summary>
    /// Cache identity = path plus the file's own size and last-write time. Replacing a file's
    /// contents under the same name therefore produces a different key instead of a stale hit.
    /// </summary>
    private static string? BuildKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }
        catch
        {
            return null;
        }
    }

    // One peak (max abs amplitude, 0..1) per Resolution-th slice of the file's total length —
    // a representative envelope for a small timeline clip, not a high-fidelity render.
    private static float[]? ComputePeaks(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            var channels = Math.Max(1, reader.WaveFormat.Channels);
            var totalFrames = reader.Length / 4 / channels; // 4 bytes per 32-bit float sample
            if (totalFrames <= 0) return null;

            var peaks = new float[Resolution];
            var framesPerBucket = Math.Max(1, totalFrames / Resolution);
            var buffer = new float[channels * 4096];
            long frameIndex = 0;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < read; i += channels)
                {
                    float frameMax = 0;
                    for (var c = 0; c < channels && i + c < read; c++)
                        frameMax = Math.Max(frameMax, Math.Abs(buffer[i + c]));
                    var bucket = (int)Math.Min(Resolution - 1, frameIndex / framesPerBucket);
                    if (frameMax > peaks[bucket]) peaks[bucket] = frameMax;
                    frameIndex++;
                }
            }
            return peaks;
        }
        catch
        {
            // Missing/corrupt/unsupported file, or a cancelled decode: the clip renders with no
            // waveform and the next attempt is free to try again.
            return null;
        }
    }
}
