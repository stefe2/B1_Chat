using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;

namespace b1_chat_console.Services;

/// <summary>
/// Decodes an audio file into a fixed-resolution peak envelope for the Sequencer timeline's
/// waveform preview (NAudio — the only decoder in this app with raw sample access;
/// AudioPlaybackService's MediaPlayer has none). Results are cached by file path so the same
/// clip re-shown, or the same file used by several clips, is only decoded once.
/// </summary>
public static class WaveformService
{
    private const int Resolution = 120;

    private static readonly ConcurrentDictionary<string, Task<float[]?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<float[]?> GetPeaksAsync(string filePath) =>
        Cache.GetOrAdd(filePath, path => Task.Run(() => ComputePeaks(path)));

    // One peak (max abs amplitude, 0..1) per Resolution-th slice of the file's total length —
    // a representative envelope for a small timeline clip, not a high-fidelity render.
    private static float[]? ComputePeaks(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
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
            // Missing/corrupt/unsupported file: the clip simply renders with no waveform.
            return null;
        }
    }
}
