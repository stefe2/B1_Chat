namespace b1_chat_console.Models;

public enum SequencerPreflightSeverity
{
    Info,
    Warning,
    Error,
}

public enum SequencerPreflightCode
{
    Ready,
    AudioMissing,
    AudioUnreadable,
    AudioValidationPending,
    AudioDurationUnknown,
    InfiniteGestureUnterminated,
    GestureOverlap,
    DuplicateGestureTimestamp,
    BroadcastTargetConflict,
}

/// <summary>
/// One immutable, user-facing preflight finding. Source references are transient editor links:
/// they are never persisted and only let the UI move the playhead/select the affected gesture.
/// </summary>
public sealed record SequencerPreflightIssue(
    SequencerPreflightCode Code,
    SequencerPreflightSeverity Severity,
    string Title,
    string Detail,
    string Location,
    int StartMs = 0,
    SequenceStep? Step = null,
    AudioClip? AudioClip = null)
{
    public string SeverityText => Severity switch
    {
        SequencerPreflightSeverity.Error => "ERROR",
        SequencerPreflightSeverity.Warning => "WARNING",
        _ => "INFO",
    };

    public string Glyph => Severity switch
    {
        SequencerPreflightSeverity.Error => "✕",
        SequencerPreflightSeverity.Warning => "⚠",
        _ => "●",
    };

    public bool CanNavigate => Step != null || AudioClip != null;
}

public sealed record SequencerPreflightInput(
    IReadOnlyList<SequenceStep> Steps,
    IReadOnlyList<AudioLane> AudioLanes,
    IReadOnlySet<ushort> MutedTargets,
    int EffectiveSequenceEndMs);
