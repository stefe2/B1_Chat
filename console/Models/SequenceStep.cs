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

    public SequenceStep Clone() => new() { AnimId = AnimId, Target = Target, StartMs = StartMs };
}
