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
        int totalDurationMs,
        bool loop)
    {
        Events = events;
        TotalDurationMs = totalDurationMs;
        Loop = loop;
    }

    public IReadOnlyList<SequencerPlaybackEvent> Events { get; }
    public int TotalDurationMs { get; }
    public bool Loop { get; }

    public static SequencerPlaybackPlan Capture(
        IEnumerable<SequenceStep> steps,
        IEnumerable<AudioLane> audioLanes,
        IReadOnlyDictionary<int, int> animationDurationsMs,
        bool loop,
        Func<uint>? nextSeed = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(audioLanes);
        ArgumentNullException.ThrowIfNull(animationDurationsMs);

        nextSeed ??= static () => (uint)Random.Shared.Next();

        var captured = new List<SequencerPlaybackEvent>();
        var sourceOrder = 0;

        foreach (var step in steps)
        {
            var duration = animationDurationsMs.TryGetValue(step.AnimId, out var reported)
                && reported >= 0
                ? reported
                : DefaultGestureDurationMs;
            captured.Add(new GesturePlaybackEvent(
                Math.Max(0, step.StartMs), sourceOrder++, step.Target, step.AnimId,
                nextSeed(), duration));
        }

        foreach (var lane in audioLanes)
        {
            foreach (var clip in lane.Clips)
            {
                captured.Add(new AudioPlaybackEvent(
                    Math.Max(0, clip.StartMs), sourceOrder++, clip.FilePath,
                    Math.Max(0, clip.DurationMs), clip.Loop));
            }
        }

        var ordered = captured
            .OrderBy(e => e.StartMs)
            .ThenBy(e => e.SourceOrder)
            .ToArray();
        var total = ordered.Length == 0 ? 0 : ordered.Max(EventEndMs);

        return new SequencerPlaybackPlan(
            new ReadOnlyCollection<SequencerPlaybackEvent>(ordered), total, loop);
    }

    private static int EventEndMs(SequencerPlaybackEvent playbackEvent)
    {
        var duration = playbackEvent switch
        {
            GesturePlaybackEvent gesture => gesture.DurationMs,
            AudioPlaybackEvent audio => audio.DurationMs,
            _ => 0,
        };
        return (int)Math.Min(int.MaxValue, (long)playbackEvent.StartMs + duration);
    }
}

public abstract record SequencerPlaybackEvent(int StartMs, int SourceOrder);

public sealed record GesturePlaybackEvent(
    int StartMs,
    int SourceOrder,
    ushort Target,
    int AnimId,
    uint Seed,
    int DurationMs) : SequencerPlaybackEvent(StartMs, SourceOrder);

public sealed record AudioPlaybackEvent(
    int StartMs,
    int SourceOrder,
    string FilePath,
    int DurationMs,
    bool Loop) : SequencerPlaybackEvent(StartMs, SourceOrder);
