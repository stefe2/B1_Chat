namespace b1_chat_console.Models;

/// <summary>
/// One entry in the Sequencer's gesture-library chip row (Views/SequenceTimelineView). Plain
/// class rather than a named ValueTuple — XAML {Binding} needs real reflectable properties,
/// which named tuple elements (compiler-alias-only, backed by Item1/Item2) don't reliably give it.
/// </summary>
public class GestureLibraryEntry
{
    public int Id { get; init; }
    public string Name { get; init; } = "";

    // DarkComboBoxStyle displays the closed selection through ToString(), not DisplayMemberPath
    // (see docs/KNOWN-PITFALLS.md) — the inspector's GESTURE combo needs this the same way
    // TimelineTrack needs it for the TARGET TRACK combo.
    public override string ToString() => Name;
}
