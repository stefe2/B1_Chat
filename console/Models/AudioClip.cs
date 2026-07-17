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

    // Transient, not persisted: true while this clip is held/dragged (dimmed "in hand", same
    // idea as SequenceStep.Dragging) — see SequenceTimelineView.xaml.cs.
    [ObservableProperty] private bool _dragging;

    // Transient view state: vertical pixel offset while dragged, so the clip itself glides
    // with the cursor across lanes — the actual lane move only settles at mouse-up. Drives a
    // TranslateTransform in the view; never serialized.
    [ObservableProperty] private double _dragOffsetY;

    public string FileName => System.IO.Path.GetFileName(FilePath);

    public AudioClip Clone() => new() { FilePath = FilePath, DurationMs = DurationMs, StartMs = StartMs, Loop = Loop };
}
