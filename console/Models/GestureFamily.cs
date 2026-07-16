namespace b1_chat_console.Models;

/// <summary>One labeled row of the Sequencer's gesture library (e.g. "SCAN &amp; TRACK"), grouping
/// the gestures that share a timeline-clip color family (Converters/AnimFamilyToBrushConverter).</summary>
public class GestureFamily
{
    public string Label { get; init; } = "";
    public IReadOnlyList<GestureLibraryEntry> Gestures { get; init; } = Array.Empty<GestureLibraryEntry>();
}
