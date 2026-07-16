namespace b1_chat_console.Models;

public record SequenceSnapshot(string Name, bool Loop, List<AudioLaneDto> AudioLanes, List<SequenceStepDto> Steps);
