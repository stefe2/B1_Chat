using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

/// <summary>Target = 0xFFFF means "all droids".</summary>
public partial class SequenceStep : ObservableObject
{
    [ObservableProperty] private int _animId;
    [ObservableProperty] private ushort _target = 0xFFFF;
    // Absolute offset from the sequence's own t=0 (not a delay from the
    // previous step — see FIRMWARE-CONTRACT.md §6).
    [ObservableProperty] private int _startMs;

    // Transient view state: true while the clip is being held/dragged on the timeline
    // (dimmed to show it's "in hand"). Never serialized — same idea as AudioClip.Dragging.
    [ObservableProperty] private bool _dragging;

    // Transient view state: vertical pixel offset while dragged, so the clip glides with the
    // cursor instead of hopping row-to-row — Target only settles at mouse-up (like the
    // horizontal snap). Drives a TranslateTransform in the view; never serialized.
    [ObservableProperty] private double _dragOffsetY;

    public SequenceStep Clone() => new() { AnimId = AnimId, Target = Target, StartMs = StartMs };
}
