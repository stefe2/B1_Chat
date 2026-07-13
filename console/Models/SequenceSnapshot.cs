namespace b1_chat_console.Models;

public record SequenceSnapshot(string Name, bool Loop, int Track, List<SequenceStepDto> Steps);
