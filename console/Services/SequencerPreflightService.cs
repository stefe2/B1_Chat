using System.IO;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

public interface ISequencerPreflightService
{
    IReadOnlyList<SequencerPreflightIssue> Analyze(SequencerPreflightInput input);
}

/// <summary>
/// Side-effect-free, manually requested Scene-content analysis. It deliberately ignores live
/// connection and roster state, sends no protocol traffic, probes no media, and never gates Play.
/// </summary>
public sealed class SequencerPreflightService : ISequencerPreflightService
{
    private readonly Func<string, bool> _fileExists;
    private readonly IReadOnlyList<string> _gestureNames;

    public SequencerPreflightService(
        Func<string, bool>? fileExists = null,
        IReadOnlyList<string>? gestureNames = null)
    {
        _fileExists = fileExists ?? File.Exists;
        _gestureNames = gestureNames ?? Array.Empty<string>();
    }

    public IReadOnlyList<SequencerPreflightIssue> Analyze(SequencerPreflightInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = new List<SequencerPreflightIssue>();
        var activeSteps = input.Steps
            .Where(step => !input.MutedTargets.Contains(step.Target))
            .ToArray();

        AnalyzeInfiniteGestures(input, activeSteps, issues);
        AnalyzeGestureConflicts(activeSteps, issues);
        AnalyzeAudio(input, issues);

        if (issues.Count == 0)
        {
            issues.Add(new SequencerPreflightIssue(
                SequencerPreflightCode.Ready,
                SequencerPreflightSeverity.Info,
                "No potential issue found",
                "This manual check found no audio, timing, or gesture-end issue in the Scene.",
                $"{activeSteps.Length} active gesture{(activeSteps.Length == 1 ? "" : "s")}"));
        }

        return issues
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.StartMs)
            .ThenBy(issue => issue.Code)
            .ToArray();
    }

    private void AnalyzeInfiniteGestures(
        SequencerPreflightInput input,
        IEnumerable<SequenceStep> activeSteps,
        ICollection<SequencerPreflightIssue> issues)
    {
        foreach (var step in activeSteps.Where(step => step.AnimId is 16 or 17))
        {
            var terminationMs = (long)Math.Max(0, step.StartMs) + step.EndAfterMs;
            if (step.EndAfterMs >= 100 && terminationMs <= input.EffectiveSequenceEndMs) continue;

            issues.Add(GestureIssue(
                SequencerPreflightCode.InfiniteGestureUnterminated,
                step,
                "Infinite gesture has no safe endpoint",
                "TALK and POWER_DOWN require a represented endpoint of at least 100 ms inside the Scene boundary so the Sequencer can send targeted IDLE cleanup.",
                step.Target == ushort.MaxValue ? "All droids" : step.Target.ToString("X4")));
        }
    }

    private void AnalyzeAudio(
        SequencerPreflightInput input,
        ICollection<SequencerPreflightIssue> issues)
    {
        foreach (var lane in input.AudioLanes)
        {
            foreach (var clip in lane.Clips)
            {
                var location = $"{lane.Label} · {DisplayFileName(clip.FilePath)}";
                if (string.IsNullOrWhiteSpace(clip.FilePath) ||
                    clip.ProbeStatus == AudioProbeStatus.FileMissing ||
                    !_fileExists(clip.FilePath))
                {
                    issues.Add(AudioIssue(
                        SequencerPreflightCode.AudioMissing,
                        SequencerPreflightSeverity.Error,
                        clip,
                        "Audio file is missing",
                        "Replace the file or remove this clip before playback.",
                        location));
                    continue;
                }

                if (clip.ValidationPending)
                {
                    issues.Add(AudioIssue(
                        SequencerPreflightCode.AudioValidationPending,
                        SequencerPreflightSeverity.Warning,
                        clip,
                        "Audio validation is still running",
                        "Wait for validation to finish for a reliable duration and codec result.",
                        location));
                    continue;
                }

                if (clip.ProbeStatus != AudioProbeStatus.Ok)
                {
                    issues.Add(AudioIssue(
                        SequencerPreflightCode.AudioUnreadable,
                        SequencerPreflightSeverity.Error,
                        clip,
                        "Audio file is not playable",
                        clip.ProbeMessage ?? "The audio decoder could not open this file.",
                        location));
                    continue;
                }

                if (clip.DurationMs <= 0)
                    issues.Add(AudioIssue(
                        SequencerPreflightCode.AudioDurationUnknown,
                        SequencerPreflightSeverity.Warning,
                        clip,
                        "Audio duration is unknown",
                        "Playback is allowed, but this clip cannot define the automatic Scene endpoint.",
                        location));
            }
        }
    }

    private void AnalyzeGestureConflicts(
        IReadOnlyList<SequenceStep> activeSteps,
        ICollection<SequencerPreflightIssue> issues)
    {
        var ordered = activeSteps
            .Select((step, sourceOrder) => new GestureSpan(
                step,
                sourceOrder,
                Math.Max(0, step.StartMs),
                GestureEndMs(step)))
            .OrderBy(span => span.StartMs)
            .ThenBy(span => span.SourceOrder)
            .ToArray();

        var longestByTarget = new Dictionary<ushort, GestureSpan>();
        var lastByTarget = new Dictionary<ushort, GestureSpan>();
        GestureSpan? longestTargeted = null;
        GestureSpan? lastTargeted = null;
        GestureSpan? longestBroadcast = null;
        GestureSpan? lastBroadcast = null;

        foreach (var later in ordered)
        {
            if (later.Step.Target == ushort.MaxValue)
            {
                AddGestureConflict(FindConflict(lastBroadcast, longestBroadcast, later), later,
                    broadcastTarget: false, issues);
                AddGestureConflict(FindConflict(lastTargeted, longestTargeted, later), later,
                    broadcastTarget: true, issues);
                lastBroadcast = later;
                if (longestBroadcast == null || later.EndMs >= longestBroadcast.Value.EndMs)
                    longestBroadcast = later;
                continue;
            }

            lastByTarget.TryGetValue(later.Step.Target, out var lastSameTarget);
            longestByTarget.TryGetValue(later.Step.Target, out var longestSameTarget);
            AddGestureConflict(FindConflict(
                    lastByTarget.ContainsKey(later.Step.Target) ? lastSameTarget : null,
                    longestByTarget.ContainsKey(later.Step.Target) ? longestSameTarget : null,
                    later),
                later, broadcastTarget: false, issues);
            AddGestureConflict(FindConflict(lastBroadcast, longestBroadcast, later), later,
                broadcastTarget: true, issues);

            lastByTarget[later.Step.Target] = later;
            if (!longestByTarget.TryGetValue(later.Step.Target, out var currentLongest) ||
                later.EndMs >= currentLongest.EndMs)
                longestByTarget[later.Step.Target] = later;
            lastTargeted = later;
            if (longestTargeted == null || later.EndMs >= longestTargeted.Value.EndMs)
                longestTargeted = later;
        }
    }

    private static GestureSpan? FindConflict(
        GestureSpan? last,
        GestureSpan? longest,
        GestureSpan later)
    {
        if (last is { } latest && latest.StartMs == later.StartMs) return latest;
        return longest is { } spanning && spanning.EndMs > later.StartMs ? spanning : null;
    }

    private void AddGestureConflict(
        GestureSpan? earlier,
        GestureSpan later,
        bool broadcastTarget,
        ICollection<SequencerPreflightIssue> issues)
    {
        if (earlier == null) return;
        var sameTime = earlier.Value.StartMs == later.StartMs;
        var code = broadcastTarget
            ? SequencerPreflightCode.BroadcastTargetConflict
            : sameTime
                ? SequencerPreflightCode.DuplicateGestureTimestamp
                : SequencerPreflightCode.GestureOverlap;
        var title = code switch
        {
            SequencerPreflightCode.BroadcastTargetConflict => "Broadcast and targeted gestures conflict",
            SequencerPreflightCode.DuplicateGestureTimestamp => "Multiple gestures share one target and time",
            _ => "Gesture overlaps an earlier command",
        };
        var behavior = broadcastTarget
            ? "Broadcast and targeted delivery can reach the same droid in an order the mesh cannot guarantee."
            : sameTime
                ? "Both commands are sent in editor order; the last command received by the droid wins."
                : "This later command can interrupt the earlier gesture before its represented duration ends.";
        var detail = $"{behavior} Earlier clip: {GestureName(earlier.Value.Step.AnimId)} at {FormatTime(earlier.Value.StartMs)}.";

        issues.Add(new SequencerPreflightIssue(
            code,
            SequencerPreflightSeverity.Warning,
            title,
            detail,
            $"{TargetLabel(later.Step.Target)} · {GestureName(later.Step.AnimId)} · {FormatTime(later.StartMs)}",
            later.StartMs,
            Step: later.Step));
    }

    private static int GestureEndMs(SequenceStep step)
    {
        var duration = step.IsInfinite ? step.EndAfterMs : step.ResolvedDurationMs;
        return (int)Math.Min(int.MaxValue, (long)Math.Max(0, step.StartMs) + Math.Max(0, duration));
    }

    private static string TargetLabel(ushort target) =>
        target == ushort.MaxValue ? "All droids" : target.ToString("X4");

    private readonly record struct GestureSpan(
        SequenceStep Step,
        int SourceOrder,
        int StartMs,
        int EndMs);

    private SequencerPreflightIssue GestureIssue(
        SequencerPreflightCode code,
        SequenceStep step,
        string title,
        string detail,
        string target) =>
        new(code, SequencerPreflightSeverity.Error, title, detail,
            $"{target} · {GestureName(step.AnimId)} · {FormatTime(step.StartMs)}",
            Math.Max(0, step.StartMs), Step: step);

    private static SequencerPreflightIssue AudioIssue(
        SequencerPreflightCode code,
        SequencerPreflightSeverity severity,
        AudioClip clip,
        string title,
        string detail,
        string location) =>
        new(code, severity, title, detail,
            $"{location} · {FormatTime(clip.StartMs)}",
            Math.Max(0, clip.StartMs), AudioClip: clip);

    private string GestureName(int animId)
    {
        var name = animId >= 0 && animId < _gestureNames.Count
            ? _gestureNames[animId]
            : animId switch
            {
                16 => "POWER_DOWN",
                17 => "TALK",
                _ => "gesture",
            };
        return $"{name} · #{animId}";
    }

    private static string DisplayFileName(string path) =>
        string.IsNullOrWhiteSpace(path) ? "(empty path)" : Path.GetFileName(path);

    private static string FormatTime(int milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}
