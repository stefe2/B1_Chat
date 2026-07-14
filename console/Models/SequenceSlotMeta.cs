namespace b1_chat_console.Models;

public record SequenceSlotMeta(int Slot, string Name, int StepCount, bool Loop, int Track);

public class SequenceLibraryItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Loop { get; set; }
    public int AudioTrack { get; set; }
    public int AudioDurationMs { get; set; }
    public List<SequenceStepDto> Steps { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

/// <summary>Flat form (POCO) for JSON serialization of SequenceStep.</summary>
public class SequenceStepDto
{
    public int AnimId { get; set; }
    public ushort Target { get; set; } = 0xFFFF;
    public int DelayMs { get; set; }
}
