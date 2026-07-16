namespace b1_chat_console.Models;

public record SequenceSlotMeta(int Slot, string Name, int StepCount, bool Loop, int Track);

public class SequenceLibraryItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Loop { get; set; }
    public int AudioTrack { get; set; }
    public List<AudioLaneDto> AudioLanes { get; set; } = new();
    public List<SequenceStepDto> Steps { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

/// <summary>Flat form (POCO) for JSON serialization of SequenceStep.</summary>
public class SequenceStepDto
{
    public int AnimId { get; set; }
    public ushort Target { get; set; } = 0xFFFF;
    public int StartMs { get; set; }
}

/// <summary>Flat form (POCO) for JSON serialization of AudioClip.</summary>
public class AudioClipDto
{
    public string FilePath { get; set; } = "";
    public int DurationMs { get; set; }
    public int StartMs { get; set; }
    public bool Loop { get; set; }
}

/// <summary>Flat form (POCO) for JSON serialization of AudioLane.</summary>
public class AudioLaneDto
{
    public string Label { get; set; } = "";
    public List<AudioClipDto> Clips { get; set; } = new();
}
