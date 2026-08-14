using System.Collections.ObjectModel;

namespace b1_chat_console.Models;

/// <summary>
/// Immutable snapshot of one Sequencer pass. Playback must consume these value records rather
/// than the live editor objects: deleting, moving, or replacing a clip after capture can then
/// never change a callback that was already scheduled.
/// </summary>
public sealed class SequencerPlaybackPlan
{
    public const int DefaultGestureDurationMs = 1500;

    private SequencerPlaybackPlan(
        IReadOnlyList<SequencerPlaybackEvent> events,
        IReadOnlyList<SequencerPlaybackBatch> batches,
        IReadOnlyList<SequencerScheduleWarning> warnings,
        int totalDurationMs,
        bool loop,
        int? explicitEndMs)
    {
        Events = events;
        Batches = batches;
        Warnings = warnings;
        TotalDurationMs = totalDurationMs;
        Loop = loop;
        ExplicitEndMs = explicitEndMs;
    }

    public IReadOnlyList<SequencerPlaybackEvent> Events { get; }
    public IReadOnlyList<SequencerPlaybackBatch> Batches { get; }
    public IReadOnlyList<SequencerScheduleWarning> Warnings { get; }
    public int TotalDurationMs { get; }
    public bool Loop { get; }
    public int? ExplicitEndMs { get; }

    public static SequencerPlaybackPlan Capture(
        IEnumerable<SequenceStep> steps,
        IEnumerable<AudioLane> audioLanes,
        IReadOnlyDictionary<int, int> animationDurationsMs,
        bool loop,
        Func<uint>? nextSeed = null,
        Func<SequenceStep, int>? resolveDurationMs = null,
        int? sequenceEndMs = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(audioLanes);
        ArgumentNullException.ThrowIfNull(animationDurationsMs);

        nextSeed ??= static () => (uint)Random.Shared.Next();

        var captured = new List<SequencerPlaybackEvent>();
        var sourceOrder = 0;

        var infiniteGestures = new List<(GesturePlaybackEvent Gesture, int EndMs)>();
        foreach (var step in steps)
        {
            var duration = step.AnimId is 16 or 17
                ? Math.Max(100, step.EndAfterMs)
                : resolveDurationMs != null
                    ? Math.Max(0, resolveDurationMs(step))
                    : animationDurationsMs.TryGetValue(step.AnimId, out var reported)
                      && reported >= 0
                        ? reported
                        : DefaultGestureDurationMs;
            var gesture = new GesturePlaybackEvent(
                Math.Max(0, step.StartMs), sourceOrder++, step.Target, step.AnimId,
                nextSeed(), duration);
            captured.Add(gesture);
            if (step.AnimId is 16 or 17)
                infiniteGestures.Add((gesture, EventEndMs(gesture)));
        }

        foreach (var lane in audioLanes)
        {
            foreach (var clip in lane.Clips)
            {
                captured.Add(new AudioPlaybackEvent(
                    Math.Max(0, clip.StartMs), sourceOrder++, clip.FilePath,
                    Math.Max(0, clip.EffectiveDurationMs), clip.Loop));
            }
        }

        foreach (var (gesture, endMs) in infiniteGestures)
            captured.Add(new GestureTerminationPlaybackEvent(
                endMs, sourceOrder++, gesture.SourceOrder, gesture.Target));

        var ordered = captured
            .OrderBy(e => e.StartMs)
            .ThenBy(e => e.SourceOrder)
            .ToArray();
        var contentEnd = ordered.Length == 0 ? 0 : ordered.Max(EventEndMs);
        var explicitEnd = sequenceEndMs.HasValue ? Math.Max(0, sequenceEndMs.Value) : (int?)null;
        var total = Math.Max(contentEnd, explicitEnd ?? 0);
        var batches = ordered
            .GroupBy(playbackEvent => playbackEvent.StartMs)
            .Select(group => new SequencerPlaybackBatch(
                group.Key,
                new ReadOnlyCollection<SequencerPlaybackEvent>(group.ToArray())))
            .ToArray();
        var warnings = FindScheduleWarnings(batches);

        return new SequencerPlaybackPlan(
            new ReadOnlyCollection<SequencerPlaybackEvent>(ordered),
            new ReadOnlyCollection<SequencerPlaybackBatch>(batches),
            new ReadOnlyCollection<SequencerScheduleWarning>(warnings),
            total,
            loop,
            explicitEnd);
    }

    private static SequencerScheduleWarning[] FindScheduleWarnings(
        IReadOnlyList<SequencerPlaybackBatch> batches)
    {
        var warnings = new List<SequencerScheduleWarning>();
        foreach (var batch in batches)
        {
            var gestures = batch.Events.OfType<GesturePlaybackEvent>().ToArray();
            foreach (var targetGroup in gestures.GroupBy(gesture => gesture.Target))
            {
                if (targetGroup.Count() < 2) continue;
                var target = targetGroup.Key == ushort.MaxValue
                    ? "broadcast"
                    : $"droid {targetGroup.Key}";
                warnings.Add(new SequencerScheduleWarning(
                    SequencerScheduleWarningCode.MultipleGesturesForTarget,
                    batch.StartMs,
                    $"{targetGroup.Count()} gestures target {target} simultaneously; they are sent in editor order and the last received command wins."));
            }

            if (gestures.Any(gesture => gesture.Target == ushort.MaxValue) &&
                gestures.Any(gesture => gesture.Target != ushort.MaxValue))
            {
                warnings.Add(new SequencerScheduleWarning(
                    SequencerScheduleWarningCode.BroadcastTargetOverlap,
                    batch.StartMs,
                    "Broadcast and targeted gestures share this timestamp. The console sends them in editor order, but mesh arrival order is not guaranteed."));
            }
        }
        return warnings.ToArray();
    }

    private static int EventEndMs(SequencerPlaybackEvent playbackEvent)
    {
        var duration = playbackEvent switch
        {
            GesturePlaybackEvent gesture => gesture.DurationMs,
            GestureTerminationPlaybackEvent => 0,
            AudioPlaybackEvent audio => audio.DurationMs,
            _ => 0,
        };
        return (int)Math.Min(int.MaxValue, (long)playbackEvent.StartMs + duration);
    }
}

public sealed record SequencerPlaybackBatch(
    int StartMs,
    IReadOnlyList<SequencerPlaybackEvent> Events);

public enum SequencerScheduleWarningCode
{
    MultipleGesturesForTarget,
    BroadcastTargetOverlap,
}

public sealed record SequencerScheduleWarning(
    SequencerScheduleWarningCode Code,
    int StartMs,
    string Message);

public abstract record SequencerPlaybackEvent(int StartMs, int SourceOrder);

public sealed record GesturePlaybackEvent(
    int StartMs,
    int SourceOrder,
    ushort Target,
    int AnimId,
    uint Seed,
    int DurationMs) : SequencerPlaybackEvent(StartMs, SourceOrder);

public sealed record GestureTerminationPlaybackEvent(
    int StartMs,
    int SourceOrder,
    int GestureSourceOrder,
    ushort Target) : SequencerPlaybackEvent(StartMs, SourceOrder);

public sealed record AudioPlaybackEvent(
    int StartMs,
    int SourceOrder,
    string FilePath,
    int DurationMs,
    bool Loop) : SequencerPlaybackEvent(StartMs, SourceOrder);
