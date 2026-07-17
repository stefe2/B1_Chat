namespace b1_chat_console.Models;

/// <summary>One labeled row of the Sequencer's gesture library (e.g. "SCAN &amp; TRACK"), grouping
/// the gestures that share a timeline-clip color family (Converters/AnimFamilyToBrushConverter).</summary>
public class GestureFamily
{
    public string Label { get; init; } = "";
    // Any animId of this family — lets the view color the row label with the family color
    // through the same AnimFamilyToBrushConverter used everywhere else (mockup's .fam-label).
    public int ColorAnimId { get; init; }
    public IReadOnlyList<GestureLibraryEntry> Gestures { get; init; } = Array.Empty<GestureLibraryEntry>();
}
