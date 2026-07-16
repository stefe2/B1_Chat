namespace b1_chat_console.Models;

/// <summary>
/// One ruler tick on the Sequencer timeline. Wholesale-rebuilt on zoom/duration change —
/// unlike clips, ticks have no per-item state worth preserving across a rebuild.
/// </summary>
public class TimelineTick
{
    public double Left { get; init; }
    public string Label { get; init; } = "";
    public bool Major { get; init; }
}
