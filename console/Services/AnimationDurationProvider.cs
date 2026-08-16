using System.Globalization;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>Single source of truth for Sequencer gesture geometry, labels and tails.</summary>
public sealed class AnimationDurationProvider
{
    public const int FallbackFiniteMs = 1500;
    public const int DefaultInfiniteEndMs = 2000;
    private readonly IReadOnlyDictionary<int, AnimationDurationMetadata> _metadata;
    private readonly IReadOnlyDictionary<int, int> _legacyDurations;

    public AnimationDurationProvider(
        IReadOnlyDictionary<int, AnimationDurationMetadata> metadata,
        IReadOnlyDictionary<int, int> legacyDurations)
    {
        _metadata = metadata;
        _legacyDurations = legacyDurations;
    }

    public ResolvedAnimationDuration Resolve(SequenceStep step)
    {
        var metadata = GetMetadata(step);
        if (metadata.Kind == AnimationDurationKind.Infinite)
        {
            var endMs = Math.Max(100, step.EndAfterMs);
            var cycle = metadata.NominalMs > 0 ? $"; nominal cycle {Seconds(metadata.NominalMs)}" : "";
            return new ResolvedAnimationDuration(
                metadata.Kind, metadata.NominalMs, endMs, endMs, endMs,
                metadata.Provisional,
                $"FIXED {Seconds(endMs)}{(metadata.Provisional ? " · provisional" : "")}",
                $"Infinite gesture; Sequencer sends IDLE after {Seconds(endMs)}{cycle}." +
                (metadata.Provisional ? " Firmware metadata has not been received yet." : ""));
        }

        if (metadata.Kind == AnimationDurationKind.Immediate)
        {
            var settle = metadata.SettleMs > 0 ? $" Physical centering settles in about {Seconds(metadata.SettleMs)}." : "";
            return new ResolvedAnimationDuration(
                metadata.Kind, metadata.NominalMs, 0, 0, 0, metadata.Provisional,
                $"IMMEDIATE{(metadata.Provisional ? " · provisional" : "")}",
                "The command has no Sequencer tail." + settle +
                (metadata.Provisional ? " Firmware metadata has not been received yet." : ""));
        }

        var nominal = Math.Max(0, metadata.NominalMs);
        var summary = Seconds(nominal) + (metadata.Provisional ? " · provisional" : "");
        var detail = $"Fixed nominal duration {Seconds(nominal)}." +
            (metadata.Provisional ? " Firmware metadata has not been received yet." : "");

        return new ResolvedAnimationDuration(
            metadata.Kind, nominal, nominal, nominal, nominal,
            metadata.Provisional, summary, detail);
    }

    private AnimationDurationMetadata GetMetadata(SequenceStep step)
    {
        var animId = step.AnimId;
        if (_metadata.TryGetValue(animId, out var value)) return value;
        var inferredKind = step.GestureKey == "dialogue.talk"
            ? AnimationDurationKind.Infinite
            : animId == 0
            ? AnimationDurationKind.Immediate
            : animId is 16 or 17
                ? AnimationDurationKind.Infinite
                : AnimationDurationKind.Finite;
        var legacyNominal = _legacyDurations.TryGetValue(animId, out var duration) && duration >= 0
            ? duration
            : inferredKind == AnimationDurationKind.Finite ? FallbackFiniteMs : 0;
        return new AnimationDurationMetadata(
            animId, inferredKind,
            inferredKind == AnimationDurationKind.Finite ? legacyNominal : 0,
            0,
            animId == 0 ? 600 : 0,
            Provisional: true);
    }

    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";
}
