namespace b1_chat_console.Models;

// (SequenceSlotMeta, the ESP32 slot-catalog row, was removed with the rest of the
// slot machinery — the console no longer talks to the master's sequence slots at all.)

public class SequenceLibraryItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Loop { get; set; }
    public List<SequenceTrackDto> Tracks { get; set; } = new();
    public List<AudioLaneDto> AudioLanes { get; set; } = new();
    public List<SequenceStepDto> Steps { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

/// <summary>One droid track of the sequence's layout (id + display name, in row order) —
/// saved with the sequence so a load/import with the fleet unplugged still lays every
/// step out on its own row (role "OFFLINE") instead of collapsing onto one line.</summary>
public class SequenceTrackDto
{
    public ushort Id { get; set; }
    public string Name { get; set; } = "";
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
