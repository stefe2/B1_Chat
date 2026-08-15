using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public sealed class SequencerPreflightServiceTests
{
    [Fact]
    public void EmptyScene_IsReadyWithoutAConnection()
    {
        var issues = Analyze(Input());

        var ready = Assert.Single(issues);
        Assert.Equal(SequencerPreflightCode.Ready, ready.Code);
        Assert.Equal(SequencerPreflightSeverity.Info, ready.Severity);
    }

    [Fact]
    public void AudioSceneWithoutIssues_IsReady()
    {
        var lane = Lane(Clip("tone.mp3", durationMs: 1_500));

        var issues = Analyze(Input(audioLanes: new[] { lane }), existingFiles: "tone.mp3");

        Assert.Equal(SequencerPreflightCode.Ready, Assert.Single(issues).Code);
    }

    [Fact]
    public void MissingAudio_IsReportedAndNamesTheLaneAndFile()
    {
        var clip = Clip("missing.mp3", 1_500);

        var issues = Analyze(Input(audioLanes: new[] { Lane(clip, "DIALOGUE") }));

        var issue = Assert.Single(issues);
        Assert.Equal(SequencerPreflightCode.AudioMissing, issue.Code);
        Assert.Same(clip, issue.AudioClip);
        Assert.Contains("DIALOGUE · missing.mp3", issue.Location);
    }

    [Fact]
    public void UnreadableAudio_IsReportedWithProbeReason()
    {
        var clip = Clip("broken.mp3", 0);
        clip.ProbeStatus = AudioProbeStatus.DecodeFailed;
        clip.ProbeMessage = "Unsupported stream.";

        var issue = Assert.Single(Analyze(
            Input(audioLanes: new[] { Lane(clip) }), existingFiles: "broken.mp3"));

        Assert.Equal(SequencerPreflightCode.AudioUnreadable, issue.Code);
        Assert.Contains("Unsupported stream", issue.Detail);
    }

    [Fact]
    public void PendingAudioValidation_IsReportedAsAWarning()
    {
        var clip = Clip("pending.mp3", 1_500);
        clip.ValidationPending = true;

        var issue = Assert.Single(Analyze(
            Input(audioLanes: new[] { Lane(clip) }), existingFiles: "pending.mp3"));

        Assert.Equal(SequencerPreflightCode.AudioValidationPending, issue.Code);
        Assert.Equal(SequencerPreflightSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void UnknownAudioDuration_IsReportedAsAWarning()
    {
        var issue = Assert.Single(Analyze(
            Input(audioLanes: new[] { Lane(Clip("empty.wav", 0)) }),
            existingFiles: "empty.wav"));

        Assert.Equal(SequencerPreflightCode.AudioDurationUnknown, issue.Code);
        Assert.Equal(SequencerPreflightSeverity.Warning, issue.Severity);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void InfiniteGestureWithoutRepresentedEndpoint_IsReported(int animId)
    {
        var step = Step(0x1234, animId: animId);
        step.EndAfterMs = 0;

        var issues = Analyze(Input(steps: new[] { step }, effectiveEndMs: 100));

        Assert.Contains(issues, issue =>
            issue.Code == SequencerPreflightCode.InfiniteGestureUnterminated &&
            ReferenceEquals(issue.Step, step));
    }

    [Fact]
    public void InfiniteGestureWithEndpointInsideScene_IsReady()
    {
        var step = Step(0x1234, startMs: 500, animId: 17);
        step.EndAfterMs = 2_000;

        var issues = Analyze(Input(steps: new[] { step }, effectiveEndMs: 2_500));

        Assert.DoesNotContain(issues, issue => issue.Severity == SequencerPreflightSeverity.Error);
    }

    [Fact]
    public void FiniteSameTargetOverlap_WarnsAndLinksTheLaterClip()
    {
        var earlier = Step(0x1234, startMs: 100, durationMs: 1_000);
        var later = Step(0x1234, startMs: 800, animId: 3, durationMs: 500);

        var issues = Analyze(Input(steps: new[] { earlier, later }));

        var issue = Assert.Single(issues, finding => finding.Code == SequencerPreflightCode.GestureOverlap);
        Assert.Equal(SequencerPreflightSeverity.Warning, issue.Severity);
        Assert.Same(later, issue.Step);
        Assert.Equal(800, issue.StartMs);
        Assert.Contains("earlier gesture", issue.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GesturesThatOnlyTouchAtTheirEndpoints_DoNotConflict()
    {
        var issues = Analyze(Input(
            steps: new[]
            {
                Step(0x1234, durationMs: 500),
                Step(0x1234, startMs: 500, animId: 3, durationMs: 500),
            }));

        Assert.DoesNotContain(issues, issue => issue.Code is
            SequencerPreflightCode.GestureOverlap or
            SequencerPreflightCode.DuplicateGestureTimestamp or
            SequencerPreflightCode.BroadcastTargetConflict);
    }

    [Fact]
    public void SameTimeImmediateCommands_AreStillFlaggedAsDuplicates()
    {
        var later = Step(0x1234, animId: 0, durationMs: 0);
        var issues = Analyze(Input(
            steps: new[]
            {
                Step(0x1234, animId: 1, durationMs: 0),
                later,
            }));

        var issue = Assert.Single(issues,
            finding => finding.Code == SequencerPreflightCode.DuplicateGestureTimestamp);
        Assert.Same(later, issue.Step);
    }

    [Fact]
    public void BroadcastAndTargetedOverlap_WarnsEvenWhenStartsDiffer()
    {
        var targeted = Step(0x1234, startMs: 400, animId: 3, durationMs: 500);
        var issues = Analyze(Input(
            steps: new[]
            {
                Step(ushort.MaxValue, durationMs: 1_000),
                targeted,
            }));

        var issue = Assert.Single(issues,
            finding => finding.Code == SequencerPreflightCode.BroadcastTargetConflict);
        Assert.Same(targeted, issue.Step);
        Assert.Equal(SequencerPreflightSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void TargetedThenBroadcastOverlap_IsDetectedInTheReverseOrder()
    {
        var broadcast = Step(ushort.MaxValue, startMs: 400, animId: 3, durationMs: 500);
        var issues = Analyze(Input(
            steps: new[]
            {
                Step(0x1234, durationMs: 1_000),
                broadcast,
            }));

        var issue = Assert.Single(issues,
            finding => finding.Code == SequencerPreflightCode.BroadcastTargetConflict);
        Assert.Same(broadcast, issue.Step);
    }

    [Fact]
    public void DifferentTargetsAndMutedTracks_DoNotCreateConflicts()
    {
        var issues = Analyze(Input(
            steps: new[]
            {
                Step(0x4001, durationMs: 1_000),
                Step(0x4002, startMs: 300, durationMs: 1_000),
                Step(ushort.MaxValue, startMs: 300, durationMs: 1_000),
            }, mutedTargets: new HashSet<ushort> { ushort.MaxValue }));

        Assert.DoesNotContain(issues, issue => issue.Code is
            SequencerPreflightCode.GestureOverlap or
            SequencerPreflightCode.DuplicateGestureTimestamp or
            SequencerPreflightCode.BroadcastTargetConflict);
    }

    [Fact]
    public void InfiniteTargetOverlap_IsStillReportedForRepair()
    {
        var infinite = Step(0x4002, animId: 17);
        infinite.EndAfterMs = 2_000;
        var later = Step(0x4002, startMs: 1_500, animId: 3, durationMs: 500);

        var issues = Analyze(Input(steps: new[] { infinite, later }, effectiveEndMs: 2_000));

        Assert.Contains(issues, issue => issue.Code == SequencerPreflightCode.GestureOverlap &&
            ReferenceEquals(issue.Step, later));
    }

    [Fact]
    public void ErrorsSortBeforeWarningsAndInformation()
    {
        var unterminated = Step(0x9999, animId: 17);
        unterminated.EndAfterMs = 0;
        var issues = Analyze(Input(
            steps: new[] { unterminated },
            audioLanes: new[] { Lane(Clip("empty.wav", 0)) }),
            existingFiles: "empty.wav");

        Assert.Equal(SequencerPreflightSeverity.Error, issues[0].Severity);
        Assert.Equal(SequencerPreflightSeverity.Warning, issues[^1].Severity);
    }

    [Fact]
    public void PreflightRunsOnlyWhenThePanelIsOpenedManually()
    {
        var protocol = new FakeSequencerProtocol { PortOpen = false, SessionReady = false };
        var scheduler = new FakePlaybackTimerScheduler();
        var preflight = new CountingPreflightService();
        using var vm = ViewModel(protocol, scheduler, preflightService: preflight);
        vm.Steps.Add(Step(0x1234, startMs: 500));

        vm.PlayCommand.Execute(null);
        vm.PlayCommand.Execute(null); // Pause.
        vm.PlayCommand.Execute(null); // Resume.
        vm.RestartCommand.Execute(null);

        Assert.Equal(SequencerTransportState.Playing, vm.TransportState);
        Assert.Equal(0, preflight.CallCount);
        Assert.False(vm.IsPreflightOpen);

        vm.StopCommand.Execute(null);
        vm.TogglePreflightCommand.Execute(null);
        Assert.Equal(1, preflight.CallCount);
        Assert.True(vm.IsPreflightOpen);

        // Closed means no background scan. Open means live Scene-content feedback.
        vm.Steps.Add(Step(0x4002, startMs: 750));
        Assert.Equal(2, preflight.CallCount);
        Assert.True(vm.IsPreflightOpen);

        vm.TogglePreflightCommand.Execute(null);
        Assert.Equal(2, preflight.CallCount);
        Assert.False(vm.IsPreflightOpen);

        vm.TogglePreflightCommand.Execute(null);
        Assert.Equal(3, preflight.CallCount);
    }

    [Fact]
    public void OpenPreflightRefreshesWhenATimelineConflictIsRepaired()
    {
        using var vm = ViewModel(new FakeSequencerProtocol(), new FakePlaybackTimerScheduler());
        vm.Steps.Add(Step(0x1234, startMs: 500, animId: 0, durationMs: 0));
        var later = Step(0x1234, startMs: 500, animId: 0, durationMs: 0);
        vm.Steps.Add(later);
        vm.SelectedStep = later;
        vm.TogglePreflightCommand.Execute(null);

        Assert.Contains(vm.PreflightIssues,
            issue => issue.Code == SequencerPreflightCode.DuplicateGestureTimestamp);

        vm.NudgeStartForwardCommand.Execute(null);

        Assert.True(vm.IsPreflightOpen);
        Assert.DoesNotContain(vm.PreflightIssues, issue => issue.Code is
            SequencerPreflightCode.GestureOverlap or
            SequencerPreflightCode.DuplicateGestureTimestamp or
            SequencerPreflightCode.BroadcastTargetConflict);
        Assert.Equal(SequencerPreflightCode.Ready, Assert.Single(vm.PreflightIssues).Code);
    }

    [Fact]
    public void ReportedPreflightError_DoesNotBlockPlayback()
    {
        var protocol = new FakeSequencerProtocol { PortOpen = false, SessionReady = false };
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = ViewModel(protocol, scheduler, fileExists: _ => false);
        vm.AudioLanes[0].Clips.Add(Clip("missing.wav", 1_000));
        vm.TogglePreflightCommand.Execute(null);

        Assert.Contains(vm.PreflightIssues,
            issue => issue.Severity == SequencerPreflightSeverity.Error);
        vm.TogglePreflightCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.Equal(SequencerTransportState.Playing, vm.TransportState);
        Assert.False(vm.IsPreflightOpen);
        Assert.Single(scheduler.Entries);
    }

    [Fact]
    public void GoToFinding_SelectsGestureAndMovesStoppedPlayhead()
    {
        var protocol = new FakeSequencerProtocol { PortOpen = false, SessionReady = false };
        using var vm = ViewModel(protocol, new FakePlaybackTimerScheduler());
        vm.Steps.Add(Step(0x4002, durationMs: 1_000));
        var step = Step(0x4002, startMs: 850, animId: 3);
        vm.Steps.Add(step);
        vm.TogglePreflightCommand.Execute(null);
        var issue = vm.PreflightIssues.Single(finding => finding.Step == step);

        vm.GoToPreflightIssueCommand.Execute(issue);

        Assert.Same(step, vm.SelectedStep);
        Assert.Equal(850, vm.PlayheadMs);
    }

    [Fact]
    public void GoToFinding_IsDisabledWhileTransportIsActive()
    {
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = ViewModel(
            new FakeSequencerProtocol { PortOpen = false, SessionReady = false },
            scheduler,
            fileExists: _ => true);
        var clip = Clip("empty.wav", 0);
        vm.AudioLanes[0].Clips.Add(clip);
        vm.TogglePreflightCommand.Execute(null);
        var issue = vm.PreflightIssues.Single(finding => finding.AudioClip == clip);

        Assert.True(vm.GoToPreflightIssueCommand.CanExecute(issue));
        vm.PlayCommand.Execute(null);

        Assert.Equal(SequencerTransportState.Playing, vm.TransportState);
        Assert.False(vm.GoToPreflightIssueCommand.CanExecute(issue));
    }

    private static IReadOnlyList<SequencerPreflightIssue> Analyze(
        SequencerPreflightInput input,
        params string[] existingFiles)
    {
        var existing = existingFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SequencerPreflightService(path => existing.Contains(path)).Analyze(input);
    }

    private static SequencerPreflightInput Input(
        IReadOnlyList<SequenceStep>? steps = null,
        IReadOnlyList<AudioLane>? audioLanes = null,
        IReadOnlySet<ushort>? mutedTargets = null,
        int effectiveEndMs = 10_000) =>
        new(
            steps ?? Array.Empty<SequenceStep>(),
            audioLanes ?? Array.Empty<AudioLane>(),
            mutedTargets ?? new HashSet<ushort>(),
            effectiveEndMs);

    private static SequenceStep Step(
        ushort target,
        int startMs = 0,
        int animId = 2,
        int durationMs = SequencerPlaybackPlan.DefaultGestureDurationMs) =>
        new()
        {
            Target = target,
            StartMs = startMs,
            AnimId = animId,
            ResolvedDurationMs = durationMs,
        };

    private static AudioClip Clip(string path, int durationMs) =>
        new() { FilePath = path, DurationMs = durationMs, ProbeStatus = AudioProbeStatus.Ok };

    private static AudioLane Lane(AudioClip clip, string label = "AUDIO")
    {
        var lane = new AudioLane { Label = label };
        lane.Clips.Add(clip);
        return lane;
    }

    private static SequencerViewModel ViewModel(
        FakeSequencerProtocol protocol,
        FakePlaybackTimerScheduler scheduler,
        Func<string, bool>? fileExists = null,
        ISequencerPreflightService? preflightService = null) =>
        new(
            protocol,
            new FakeSequencerSettings(),
            new FakeAudioPlayer(),
            scheduler,
            new FakePlaybackClock(),
            new FakePlaybackTimerScheduler(),
            library: new FakeSequenceLibraryService(),
            preflightService: preflightService ?? new SequencerPreflightService(fileExists ?? (_ => true)));

    private sealed class CountingPreflightService : ISequencerPreflightService
    {
        public int CallCount { get; private set; }

        public IReadOnlyList<SequencerPreflightIssue> Analyze(SequencerPreflightInput input)
        {
            CallCount++;
            return new[]
            {
                new SequencerPreflightIssue(
                    SequencerPreflightCode.AudioMissing,
                    SequencerPreflightSeverity.Error,
                    "Potential problem",
                    "Manual advisory result.",
                    "Test"),
            };
        }
    }
}
