namespace b1_chat_console.Models;

/// <summary>
/// Captured persistent Sequencer document state. Editor-only state such as selection,
/// viewport, drag visuals, waveform peaks, mute and execution telemetry must never be
/// added here. Captured DTO graphs are treated as immutable by history consumers.
/// </summary>
public record SequenceSnapshot(
    string Name,
    bool Loop,
    List<AudioLaneDto> AudioLanes,
    List<SequenceStepDto> Steps)
{
    /// <summary>
    /// Structural document equality. The generated record equality cannot be used because
    /// the DTO lists intentionally contain fresh instances for every capture.
    /// </summary>
    public bool DocumentEquals(SequenceSnapshot? other)
    {
        if (other == null ||
            !string.Equals(Name, other.Name, StringComparison.Ordinal) ||
            Loop != other.Loop ||
            Steps.Count != other.Steps.Count ||
            AudioLanes.Count != other.AudioLanes.Count)
            return false;

        for (var i = 0; i < Steps.Count; i++)
        {
            var left = Steps[i];
            var right = other.Steps[i];
            if (left.AnimId != right.AnimId ||
                left.Target != right.Target ||
                left.StartMs != right.StartMs ||
                left.EndAfterMs != right.EndAfterMs)
                return false;
        }

        for (var laneIndex = 0; laneIndex < AudioLanes.Count; laneIndex++)
        {
            var leftLane = AudioLanes[laneIndex];
            var rightLane = other.AudioLanes[laneIndex];
            if (!string.Equals(leftLane.Label, rightLane.Label, StringComparison.Ordinal) ||
                leftLane.Clips.Count != rightLane.Clips.Count)
                return false;

            for (var clipIndex = 0; clipIndex < leftLane.Clips.Count; clipIndex++)
            {
                var left = leftLane.Clips[clipIndex];
                var right = rightLane.Clips[clipIndex];
                if (!string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal) ||
                    left.DurationMs != right.DurationMs ||
                    left.StartMs != right.StartMs ||
                    left.Loop != right.Loop)
                    return false;
            }
        }

        return true;
    }
}
