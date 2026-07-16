using System.Collections.ObjectModel;

namespace b1_chat_console.Models;

/// <summary>
/// One horizontal audio lane on the Sequencer timeline (e.g. "AUDIO" for sound effects,
/// "AMBIENT" for a looping background). Plain class — only its Clips collection needs to be
/// observable, the lane's own identity doesn't change in place (same reasoning TimelineTrack
/// used before Muted forced it to become an ObservableObject).
/// </summary>
public class AudioLane
{
    public string Label { get; set; } = "";
    public int RowIndex { get; set; }
    public ObservableCollection<AudioClip> Clips { get; } = new();
}
