using System.IO;
using b1_chat_console.Models;

namespace b1_chat_console.Services;

public interface ISequencerPreflightService
{
    IReadOnlyList<SequencerPreflightIssue> Analyze(SequencerPreflightInput input);
}

/// <summary>
/// Side-effect-free Scene readiness analysis. It reports only facts observable by the console;
/// it does not send protocol traffic, probe media, or change Windows/system settings.
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

        AnalyzeConnection(input, activeSteps, issues);
        AnalyzeGestureTargets(input, activeSteps, issues);
        AnalyzeInfiniteGestures(input, activeSteps, issues);
        AnalyzeAudio(input, issues);

        if (issues.Count == 0)
        {
            var audioCount = input.AudioLanes.Sum(lane => lane.Clips.Count);
            if (activeSteps.Length == 0 && audioCount > 0)
            {
                issues.Add(new SequencerPreflightIssue(
                    SequencerPreflightCode.AudioOnly,
                    SequencerPreflightSeverity.Info,
                    "Audio-only Scene is ready",
                    "No droid connection is required because this Scene contains no active gesture commands.",
                    $"{audioCount} audio clip{(audioCount == 1 ? "" : "s")}"));
            }
            else
            {
                issues.Add(new SequencerPreflightIssue(
                    SequencerPreflightCode.Ready,
                    SequencerPreflightSeverity.Info,
                    "Scene is ready",
                    "No blocking readiness or infinite-gesture safety issue was found.",
                    $"{activeSteps.Length} active gesture{(activeSteps.Length == 1 ? "" : "s")}"));
            }
        }

        return issues
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.StartMs)
            .ThenBy(issue => issue.Code)
            .ToArray();
    }

    private static void AnalyzeConnection(
        SequencerPreflightInput input,
        IReadOnlyCollection<SequenceStep> activeSteps,
        ICollection<SequencerPreflightIssue> issues)
    {
        if (activeSteps.Count == 0) return;

        if (!input.PortOpen)
        {
            issues.Add(new SequencerPreflightIssue(
                SequencerPreflightCode.PortClosed,
                SequencerPreflightSeverity.Error,
                "Droid connection is closed",
                "Connect the console to a master before playing gesture commands.",
                "Connection"));
            return;
        }

        if (!input.SessionReady)
        {
            issues.Add(new SequencerPreflightIssue(
                SequencerPreflightCode.SessionNotReady,
                SequencerPreflightSeverity.Error,
                "Master handshake is not ready",
                "Wait for the firmware handshake to complete before playing gesture commands.",
                "Connection"));
            return;
        }

        if (!input.Droids.Any(droid => droid.IsMaster && droid.Online))
        {
            issues.Add(new SequencerPreflightIssue(
                SequencerPreflightCode.MasterUnavailable,
                SequencerPreflightSeverity.Error,
                "No online master is available",
                "The serial session is ready, but the live roster does not contain an online master.",
                "Droid roster"));
        }
    }

    private void AnalyzeGestureTargets(
        SequencerPreflightInput input,
        IEnumerable<SequenceStep> activeSteps,
        ICollection<SequencerPreflightIssue> issues)
    {
        var online = input.Droids.Where(droid => droid.Online).ToArray();
        foreach (var step in activeSteps)
        {
            if (step.Target == ushort.MaxValue)
            {
                if (online.Length == 0)
                    issues.Add(GestureIssue(
                        SequencerPreflightCode.BroadcastWithoutRecipients,
                        step,
                        "Broadcast has no online recipient",
                        "No droid in the current roster can receive this broadcast gesture.",
                        "All droids"));
                continue;
            }

            var target = input.Droids.FirstOrDefault(droid => droid.Id == step.Target);
            if (target?.Online == true) continue;
            var label = target == null || string.IsNullOrWhiteSpace(target.Name)
                ? step.Target.ToString("X4")
                : $"{target.Name} ({step.Target:X4})";
            issues.Add(GestureIssue(
                SequencerPreflightCode.TargetOffline,
                step,
                "Gesture target is offline",
                "Commands are not queued for offline droids; this clip would be missed.",
                label));
        }
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
