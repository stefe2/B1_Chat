using CommunityToolkit.Mvvm.ComponentModel;

namespace b1_chat_console.Models;

/// <summary>Target = 0xFFFF signifie "tous les droides".</summary>
public partial class SequenceStep : ObservableObject
{
    [ObservableProperty] private int _animId;
    [ObservableProperty] private ushort _target = 0xFFFF;
    [ObservableProperty] private int _delayMs = 1000;

    public SequenceStep Clone() => new() { AnimId = AnimId, Target = Target, DelayMs = DelayMs };
}
