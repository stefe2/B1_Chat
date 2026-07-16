using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

/// <summary>One placed audio file on an AudioLane. Clips within a lane may overlap.</summary>
public partial class AudioClip : ObservableObject
{
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private int _durationMs;
    [ObservableProperty] private int _startMs;
    // Restarts on completion during Rehearse (local) — see SequencerViewModel.ScheduleRehearsalPass.
    [ObservableProperty] private bool _loop;

    // Waveform preview (WaveformService), populated asynchronously after load/add/replace — null
    // until then, or if decoding failed (missing/corrupt file), in which case no waveform renders.
    [ObservableProperty] private float[]? _peaks;

    // Transient, not persisted: true while this clip is the target of a cross-lane drag (see
    // SequenceTimelineView.xaml.cs) — dims the real clip while a ghost follows the cursor.
    [ObservableProperty] private bool _dragging;

    public string FileName => System.IO.Path.GetFileName(FilePath);

    public AudioClip Clone() => new() { FilePath = FilePath, DurationMs = DurationMs, StartMs = StartMs, Loop = Loop };
}
