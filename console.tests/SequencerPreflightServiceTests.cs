using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public sealed class SequencerPreflightServiceTests
{
    [Fact]
    public void EmptyScene_IsReadyWithoutAConnection()
    {
        var issues = Analyze(Input(portOpen: false, sessionReady: false));

        var ready = Assert.Single(issues);
        Assert.Equal(SequencerPreflightCode.Ready, ready.Code);
        Assert.Equal(SequencerPreflightSeverity.Info, ready.Severity);
    }

    [Fact]
    public void AudioOnlyScene_DoesNotRequireADroidConnection()
    {
        var lane = Lane(Clip("tone.mp3", durationMs: 1_500));

        var issues = Analyze(Input(
            portOpen: false,
            sessionReady: false,
            audioLanes: new[] { lane }), existingFiles: "tone.mp3");

        Assert.Equal(SequencerPreflightCode.AudioOnly, Assert.Single(issues).Code);
    }

    [Fact]
    public void GestureWithClosedPort_IsBlocked()
    {
        var issues = Analyze(Input(
            portOpen: false,
            steps: new[] { Step(0x1234) },
            droids: new[] { Master(0x1234) }));

        Assert.Contains(issues, issue => issue.Code == SequencerPreflightCode.PortClosed &&
            issue.Severity == SequencerPreflightSeverity.Error);
    }

    [Fact]
    public void GestureBeforeHandshake_IsBlocked()
    {
        var issues = Analyze(Input(
            sessionReady: false,
            steps: new[] { Step(0x1234) },
            droids: new[] { Master(0x1234) }));

        Assert.Contains(issues, issue => issue.Code == SequencerPreflightCode.SessionNotReady);
        Assert.DoesNotContain(issues, issue => issue.Code == SequencerPreflightCode.PortClosed);
    }

    [Fact]
    public void ReadySessionWithoutMaster_IsBlocked()
    {
        var target = new Droid { Id = 0x1234, Online = true };

        var issues = Analyze(Input(
            steps: new[] { Step(0x1234) },
            droids: new[] { target }));

        Assert.Contains(issues, issue => issue.Code == SequencerPreflightCode.MasterUnavailable);
    }

    [Fact]
    public void OfflineTarget_NamesAndLinksTheAffectedGesture()
    {
        var step = Step(0x4002, startMs: 700);
        var issues = Analyze(Input(
            steps: new[] { step },
            droids: new[]
            {
                Master(0x4001),
                new Droid { Id = 0x4002, Name = "B1 Right", Online = false },
            }));

        var issue = Assert.Single(issues, finding => finding.Code == SequencerPreflightCode.TargetOffline);
        Assert.Same(step, issue.Step);
        Assert.Equal(700, issue.StartMs);
        Assert.Contains("B1 Right (4002)", issue.Location);
    }

    [Fact]
    public void BroadcastWithoutOnlineRecipients_IsBlocked()
    {
        var offlineMaster = new Droid { Id = 0x4001, IsMaster = true, Online = false };

        var issues = Analyze(Input(
            steps: new[] { Step(ushort.MaxValue) },
            droids: new[] { offlineMaster }));

        Assert.Contains(issues, issue => issue.Code == SequencerPreflightCode.BroadcastWithoutRecipients);
    }

    [Fact]
    public void MutedGesture_DoesNotRequireAConnectionOrRecipient()
    {
        var issues = Analyze(Input(
            portOpen: false,
            sessionReady: false,
            steps: new[] { Step(0x4002) },
            mutedTargets: new HashSet<ushort> { 0x4002 }));

        Assert.DoesNotContain(issues, issue => issue.Severity == SequencerPreflightSeverity.Error);
    }

    [Fact]
    public void MissingAudio_IsBlockedAndNamesTheLaneAndFile()
    {
        var clip = Clip("missing.mp3", 1_500);

        var issues = Analyze(Input(audioLanes: new[] { Lane(clip, "DIALOGUE") }));

        var issue = Assert.Single(issues);
        Assert.Equal(SequencerPreflightCode.AudioMissing, issue.Code);
        Assert.Same(clip, issue.AudioClip);
        Assert.Contains("DIALOGUE · missing.mp3", issue.Location);
    }

    [Fact]
    public void UnreadableAudio_IsBlockedWithProbeReason()
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
    public void PendingAudioValidation_WarnsButDoesNotBlock()
    {
        var clip = Clip("pending.mp3", 1_500);
        clip.ValidationPending = true;

        var issue = Assert.Single(Analyze(
            Input(audioLanes: new[] { Lane(clip) }), existingFiles: "pending.mp3"));

        Assert.Equal(SequencerPreflightCode.AudioValidationPending, issue.Code);
        Assert.Equal(SequencerPreflightSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void UnknownAudioDuration_WarnsButDoesNotBlock()
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
    public void InfiniteGestureWithoutRepresentedEndpoint_IsBlocked(int animId)
    {
        var step = Step(0x1234, animId: animId);
        step.EndAfterMs = 0;

        var issues = Analyze(Input(
            steps: new[] { step },
            droids: new[] { Master(0x1234) },
            effectiveEndMs: 100));

        Assert.Contains(issues, issue =>
            issue.Code == SequencerPreflightCode.InfiniteGestureUnterminated &&
            ReferenceEquals(issue.Step, step));
    }

    [Fact]
    public void InfiniteGestureWithEndpointInsideScene_IsReady()
    {
        var step = Step(0x1234, startMs: 500, animId: 17);
        step.EndAfterMs = 2_000;

        var issues = Analyze(Input(
            steps: new[] { step },
            droids: new[] { Master(0x1234) },
            effectiveEndMs: 2_500));

        Assert.DoesNotContain(issues, issue => issue.Severity == SequencerPreflightSeverity.Error);
    }

    [Fact]
    public void ErrorsSortBeforeWarningsAndInformation()
    {
        var issues = Analyze(Input(
            portOpen: false,
            sessionReady: false,
            steps: new[] { Step(0x9999) },
            audioLanes: new[] { Lane(Clip("empty.wav", 0)) }),
            existingFiles: "empty.wav");

        Assert.Equal(SequencerPreflightSeverity.Error, issues[0].Severity);
        Assert.Equal(SequencerPreflightSeverity.Warning, issues[^1].Severity);
    }

    [Fact]
    public void BlockingPreflight_InterceptsPlayAndOpensThePanel()
    {
        var protocol = new FakeSequencerProtocol { PortOpen = false, SessionReady = false };
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = ViewModel(protocol, scheduler);
        vm.Steps.Add(Step(0x1234));

        vm.PlayCommand.Execute(null);

        Assert.Equal(SequencerTransportState.Stopped, vm.TransportState);
        Assert.True(vm.HasPreflightErrors);
        Assert.True(vm.IsPreflightOpen);
        Assert.Empty(scheduler.Entries);
        Assert.Empty(protocol.Sent);
    }

    [Fact]
    public void BlockingPreflight_InterceptsRestartBeforeReplacingTheActivePass()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(Master(0x1234));
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = ViewModel(protocol, scheduler);
        vm.Steps.Add(Step(0x1234, startMs: 500));
        vm.PlayCommand.Execute(null);
        var activeTimerCount = scheduler.ActiveWakeTimers;
        protocol.PortOpen = false;
        protocol.SessionReady = false;

        vm.RestartCommand.Execute(null);

        Assert.Equal(SequencerTransportState.Playing, vm.TransportState);
        Assert.True(vm.IsPreflightOpen);
        Assert.Equal(activeTimerCount, scheduler.ActiveWakeTimers);
    }

    [Fact]
    public void BlockingPreflight_LeavesPausedPassRetainedInsteadOfResumingIt()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(Master(0x1234));
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = ViewModel(protocol, scheduler);
        vm.Steps.Add(Step(0x1234, startMs: 500));
        vm.PlayCommand.Execute(null);
        vm.PlayCommand.Execute(null);
        protocol.PortOpen = false;
        protocol.SessionReady = false;

        vm.PlayCommand.Execute(null);

        Assert.Equal(SequencerTransportState.Paused, vm.TransportState);
        Assert.True(vm.IsPreflightOpen);
        Assert.Equal(0, scheduler.ActiveWakeTimers);
    }

    [Fact]
    public void WarningPreflight_AllowsPlayback()
    {
        var protocol = new FakeSequencerProtocol { PortOpen = false, SessionReady = false };
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = ViewModel(protocol, scheduler, fileExists: _ => true);
        vm.AudioLanes[0].Clips.Add(Clip("empty.wav", 0));

        vm.PlayCommand.Execute(null);

        Assert.True(vm.HasPreflightWarnings);
        Assert.False(vm.HasPreflightErrors);
        Assert.Equal(SequencerTransportState.Playing, vm.TransportState);
        Assert.Single(scheduler.Entries);
    }

    [Fact]
    public void GoToFinding_SelectsGestureAndMovesStoppedPlayhead()
    {
        var protocol = new FakeSequencerProtocol { PortOpen = false, SessionReady = false };
        using var vm = ViewModel(protocol, new FakePlaybackTimerScheduler());
        var step = Step(0x4002, startMs: 850);
        vm.Steps.Add(step);
        vm.TogglePreflightCommand.Execute(null);
        var issue = vm.PreflightIssues.Single(finding => finding.Step == step);

        vm.GoToPreflightIssueCommand.Execute(issue);

        Assert.Same(step, vm.SelectedStep);
        Assert.Equal(850, vm.PlayheadMs);
    }

    [Fact]
    public void FixingConnectionAndRoster_AllowsTheNextPlayAttempt()
    {
        var protocol = new FakeSequencerProtocol { PortOpen = false, SessionReady = false };
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = ViewModel(protocol, scheduler);
        vm.Steps.Add(Step(0x1234));
        vm.PlayCommand.Execute(null);
        protocol.PortOpen = true;
        protocol.SessionReady = true;
        protocol.Droids.Add(Master(0x1234));
        protocol.RaiseDroidsChanged();

        vm.PlayCommand.Execute(null);

        Assert.False(vm.HasPreflightErrors);
        Assert.Equal(SequencerTransportState.Playing, vm.TransportState);
    }

    private static IReadOnlyList<SequencerPreflightIssue> Analyze(
        SequencerPreflightInput input,
        params string[] existingFiles)
    {
        var existing = existingFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SequencerPreflightService(path => existing.Contains(path)).Analyze(input);
    }

    private static SequencerPreflightInput Input(
        bool portOpen = true,
        bool sessionReady = true,
        IReadOnlyList<Droid>? droids = null,
        IReadOnlyList<SequenceStep>? steps = null,
        IReadOnlyList<AudioLane>? audioLanes = null,
        IReadOnlySet<ushort>? mutedTargets = null,
        int effectiveEndMs = 10_000) =>
        new(
            portOpen,
            sessionReady,
            droids ?? Array.Empty<Droid>(),
            steps ?? Array.Empty<SequenceStep>(),
            audioLanes ?? Array.Empty<AudioLane>(),
            mutedTargets ?? new HashSet<ushort>(),
            effectiveEndMs);

    private static SequenceStep Step(
        ushort target,
        int startMs = 0,
        int animId = 2) =>
        new() { Target = target, StartMs = startMs, AnimId = animId };

    private static Droid Master(ushort id) =>
        new() { Id = id, Name = "Master", IsMaster = true, Online = true };

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
        Func<string, bool>? fileExists = null) =>
        new(
            protocol,
            new FakeSequencerSettings(),
            new FakeAudioPlayer(),
            scheduler,
            new FakePlaybackClock(),
            new FakePlaybackTimerScheduler(),
            library: new FakeSequenceLibraryService(),
            preflightService: new SequencerPreflightService(fileExists ?? (_ => true)));
}
