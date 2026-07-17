using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

/// <summary>
/// One horizontal audio lane on the Sequencer timeline (e.g. "AUDIO" for sound effects,
/// "AMBIENT" for a looping background). ObservableObject because Label is renamable in
/// place from the rail (direct user request) — the TextBox edits it live.
/// </summary>
public partial class AudioLane : ObservableObject
{
    // Single source of truth for the per-lane vertical footprint (Canvas Height + bottom
    // Margin in SequenceTimelineView.xaml) — mirrors TimelineTrack.RowHeight/RowGap, used by
    // SequencerViewModel.AudioLaneAtY to hit-test a cross-lane audio-clip drag.
    public const double RowHeight = 52;
    public const double RowGap = 0;

    [ObservableProperty] private string _label = "";
    public int RowIndex { get; set; }
    public ObservableCollection<AudioClip> Clips { get; } = new();
}
