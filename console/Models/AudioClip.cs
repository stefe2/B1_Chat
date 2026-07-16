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

    public string FileName => System.IO.Path.GetFileName(FilePath);

    public AudioClip Clone() => new() { FilePath = FilePath, DurationMs = DurationMs, StartMs = StartMs, Loop = Loop };
}
