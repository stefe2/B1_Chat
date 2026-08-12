using System.Globalization;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

/// <summary>Single source of truth for Sequencer gesture geometry, labels and tails.</summary>
public sealed class AnimationDurationProvider
{
    public const int FallbackFiniteMs = 1500;
    public const int DefaultInfiniteEndMs = 2000;
    public const int MoveJitterPerFrameMs = 60;

    private readonly IReadOnlyDictionary<int, AnimationDurationMetadata> _metadata;
    private readonly IReadOnlyDictionary<int, int> _legacyDurations;
    private readonly IReadOnlyDictionary<ushort, int> _speedPct;
    private readonly IReadOnlyCollection<Droid> _droids;

    public AnimationDurationProvider(
        IReadOnlyDictionary<int, AnimationDurationMetadata> metadata,
        IReadOnlyDictionary<int, int> legacyDurations,
        IReadOnlyDictionary<ushort, int> speedPct,
        IReadOnlyCollection<Droid> droids)
    {
        _metadata = metadata;
        _legacyDurations = legacyDurations;
        _speedPct = speedPct;
        _droids = droids;
    }

    public ResolvedAnimationDuration Resolve(SequenceStep step)
    {
        var metadata = GetMetadata(step.AnimId);
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

        var speeds = ResolveSpeeds(step.Target, out var targetDescription, out var provisional);
        var ranges = speeds.Select(speed => EstimateFiniteRange(metadata, speed)).ToArray();
        var minimum = ranges.Min(range => range.MinimumMs);
        var maximum = ranges.Max(range => range.MaximumMs);
        provisional |= metadata.Provisional;
        var mixed = speeds.Distinct().Count() > 1;
        var summary = minimum == maximum
            ? Seconds(maximum)
            : $"{Seconds(minimum)}–{Seconds(maximum)}";
        if (provisional) summary += " · provisional";
        var detail = $"Nominal {Seconds(metadata.NominalMs)}; estimated {summary.Split(" ·")[0]} for {targetDescription}.";
        if (mixed) detail += " Broadcast targets use different speed settings; the conservative upper bound drives the clip tail.";
        if (provisional) detail += " Missing firmware/config data uses the shared default estimate.";

        return new ResolvedAnimationDuration(
            metadata.Kind, metadata.NominalMs, minimum, maximum, maximum,
            provisional, summary, detail);
    }

    private AnimationDurationMetadata GetMetadata(int animId)
    {
        if (_metadata.TryGetValue(animId, out var value)) return value;
        var inferredKind = animId == 0
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

    private int[] ResolveSpeeds(ushort target, out string targetDescription, out bool provisional)
    {
        provisional = false;
        if (target != ushort.MaxValue)
        {
            targetDescription = $"droid {target:X4}";
            if (_speedPct.TryGetValue(target, out var speed)) return new[] { speed };
            provisional = true;
            return new[] { 50 };
        }

        targetDescription = "broadcast targets";
        var online = _droids.Where(droid => droid.Online || droid.IsMaster).Select(droid => droid.Id).Distinct().ToArray();
        if (online.Length == 0)
        {
            provisional = true;
            return new[] { 50 };
        }

        var result = new int[online.Length];
        for (var i = 0; i < online.Length; i++)
        {
            if (_speedPct.TryGetValue(online[i], out var speed)) result[i] = speed;
            else
            {
                result[i] = 50;
                provisional = true;
            }
        }
        return result;
    }

    internal static (int MinimumMs, int MaximumMs) EstimateFiniteRange(
        AnimationDurationMetadata metadata,
        int speedPct)
    {
        var clampedSpeed = Math.Max(10, speedPct);
        var scale = Math.Clamp(50.0 / clampedSpeed, 0.4, 4.0);
        var scaledNominal = metadata.NominalMs * scale;
        var jitter = Math.Max(0, metadata.FrameCount) * MoveJitterPerFrameMs;
        return (
            (int)Math.Max(0, Math.Floor(scaledNominal - jitter)),
            (int)Math.Min(int.MaxValue, Math.Ceiling(scaledNominal + jitter)));
    }

    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";
}
