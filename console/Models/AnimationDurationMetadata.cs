namespace b1_chat_console.Models;

public enum AnimationDurationKind
{
    Immediate,
    Finite,
    Infinite,
}

/// <summary>
/// Firmware-authored timing facts for gestures with fixed nominal durations.
/// Older firmware is represented with Provisional=true so every consumer can expose the
/// same honest fallback instead of silently inventing a different duration.
/// </summary>
public sealed record AnimationDurationMetadata(
    int AnimId,
    AnimationDurationKind Kind,
    int NominalMs,
    int FrameCount,
    int SettleMs = 0,
    bool Provisional = false);

public sealed record ResolvedAnimationDuration(
    AnimationDurationKind Kind,
    int NominalMs,
    int MinimumMs,
    int MaximumMs,
    int EffectiveMs,
    bool Provisional,
    string Summary,
    string Detail);
