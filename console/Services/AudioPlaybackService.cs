using System.IO;
using System.Windows.Media;

namespace b1_chat_console.Services;

/// <summary>
/// Console-side audio playback for the Sequencer (DFPlayer set aside "for now" per
/// CLAUDE.md — the console plays local audio files directly instead of relaying a track
/// number to the master's DFPlayer). Thin wrapper around WPF's built-in MediaPlayer (Media
/// Foundation, no extra NuGet dependency) — supports several clips playing concurrently
/// (one MediaPlayer each), since a sequence can now have multiple audio lanes/clips.
/// </summary>
public class AudioPlaybackService
{
    private readonly List<MediaPlayer> _players = new();

    /// <summary>Starts a new, independent playback. When loop is true, restarts on completion
    /// until StopAll() is called — used for a looping ambient clip during Rehearse (local).</summary>
    public void Play(string? path, bool loop = false)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var player = new MediaPlayer();
        if (loop)
        {
            player.MediaEnded += (_, _) =>
            {
                player.Position = TimeSpan.Zero;
                player.Play();
            };
        }
        _players.Add(player);
        player.Open(new Uri(path));
        player.Play();
    }

    public void StopAll()
    {
        foreach (var player in _players)
        {
            player.Stop();
            player.Close();
        }
        _players.Clear();
    }

    /// <summary>Opens the file just long enough to read its duration, then closes it.</summary>
    public static Task<int> ProbeDurationMsAsync(string path)
    {
        var tcs = new TaskCompletionSource<int>();
        if (!File.Exists(path)) { tcs.SetResult(0); return tcs.Task; }

        var probe = new MediaPlayer();
        probe.MediaOpened += (_, _) =>
        {
            var ms = probe.NaturalDuration.HasTimeSpan ? (int)probe.NaturalDuration.TimeSpan.TotalMilliseconds : 0;
            probe.Close();
            tcs.TrySetResult(ms);
        };
        probe.MediaFailed += (_, _) =>
        {
            probe.Close();
            tcs.TrySetResult(0);
        };
        probe.Open(new Uri(path));
        return tcs.Task;
    }
}
