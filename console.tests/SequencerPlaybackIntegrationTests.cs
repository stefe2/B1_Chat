using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;
using b1_chat_console.Views;
using System.Windows.Input;

namespace b1_chat_console.Tests;

public sealed class SequencerPlaybackIntegrationTests
{
    [Theory]
    [InlineData("Play", SequencerTransportState.Playing)]
    [InlineData("Restart", SequencerTransportState.Playing)]
    [InlineData("Pause", SequencerTransportState.Paused)]
    [InlineData("Resume", SequencerTransportState.Playing)]
    [InlineData("Stop", SequencerTransportState.Stopped)]
    [InlineData("NaturalEnd", SequencerTransportState.Stopped)]
    [InlineData("Loop", SequencerTransportState.Playing)]
    [InlineData("Disconnect", SequencerTransportState.Stopped)]
    [InlineData("FailedStart", SequencerTransportState.Stopped)]
    public void TransportTransitionTable_HasOneConsistentObservableState(
        string transition, SequencerTransportState expected)
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 0, Target = 0xFFFF, AnimId = 2 });

        switch (transition)
        {
            case "Play":
                vm.PlayCommand.Execute(null);
                break;
            case "Restart":
                vm.PlayCommand.Execute(null);
                vm.RestartCommand.Execute(null);
                break;
            case "Pause":
                vm.PlayCommand.Execute(null);
                vm.PlayCommand.Execute(null);
                break;
            case "Resume":
                vm.PlayCommand.Execute(null);
                vm.PlayCommand.Execute(null);
                vm.PlayCommand.Execute(null);
                break;
            case "Stop":
                vm.PlayCommand.Execute(null);
                vm.StopCommand.Execute(null);
                break;
            case "NaturalEnd":
                vm.PlayCommand.Execute(null);
                scheduler.Entries[0].Invoke();
                scheduler.Entries[1].Invoke();
                break;
            case "Loop":
                vm.Loop = true;
                vm.PlayCommand.Execute(null);
                scheduler.Entries[0].Invoke();
                scheduler.Entries[1].Invoke();
                break;
            case "Disconnect":
                vm.PlayCommand.Execute(null);
                protocol.RaiseLinkClosed();
                break;
            case "FailedStart":
                scheduler.FailNextSchedule = true;
                Assert.Throws<InvalidOperationException>(() => vm.PlayCommand.Execute(null));
                break;
            default:
                throw new InvalidOperationException($"Unknown transition fixture: {transition}.");
        }

        Assert.Equal(expected, vm.TransportState);
        Assert.Equal(expected == SequencerTransportState.Playing, vm.IsPlaying);
        Assert.Equal(expected == SequencerTransportState.Paused, vm.IsPaused);
        Assert.Equal(expected == SequencerTransportState.Playing, vm.IsLiveTracking);
        Assert.Equal(expected == SequencerTransportState.Stopped, vm.CanEditSequence);
        Assert.Equal(expected == SequencerTransportState.Playing, vm.PauseCommand.CanExecute(null));
    }

    [Fact]
    public void TransportTransition_NotifiesEveryDerivedUiProperty()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 0, Target = 0xFFFF, AnimId = 2 });
        var changed = new HashSet<string>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != null) changed.Add(args.PropertyName);
        };

        vm.PlayCommand.Execute(null);

        Assert.Contains(nameof(vm.TransportState), changed);
        Assert.Contains(nameof(vm.IsPlaying), changed);
        Assert.Contains(nameof(vm.IsPaused), changed);
        Assert.Contains(nameof(vm.IsLiveTracking), changed);
        Assert.Contains(nameof(vm.CanEditSequence), changed);
    }

    [Fact]
    public void PrimaryTransport_DoubleClickPausesInsteadOfRestartingAndThirdPressResumes()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var originalWake = Assert.Single(scheduler.Entries);
        Assert.Equal("⏸", vm.PrimaryTransportGlyph);

        vm.PlayCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.True(originalWake.Disposed);
        Assert.Single(scheduler.Entries);
        Assert.Equal("▶", vm.PrimaryTransportGlyph);

        vm.PlayCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.Equal(2, scheduler.Entries.Count);
        Assert.Empty(protocol.Sent);
    }

    [Fact]
    public void StopRetainsCursor_ReturnToStartIsSeparateAndPlayFromCursorSkipsPastEvents()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        protocol.Durations[3] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0xFFFF, AnimId = 2 });
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 3 });

        vm.PlayCommand.Execute(null);
        clock.SetElapsed(TimeSpan.FromMilliseconds(300));
        vm.PlayCommand.Execute(null);
        Assert.True(vm.IsPaused);
        vm.StopCommand.Execute(null);

        Assert.Equal(300, vm.PlayheadMs);
        Assert.True(vm.ReturnToStartCommand.CanExecute(null));
        vm.PlayCommand.Execute(null);
        Assert.Equal(200, scheduler.Entries[^1].DueTimeMs);
        scheduler.Entries[^1].Invoke();
        Assert.Equal(3, Assert.Single(protocol.Sent).AnimId);

        vm.StopCommand.Execute(null);
        vm.ReturnToStartCommand.Execute(null);
        Assert.Equal(0, vm.PlayheadMs);
        Assert.False(vm.ReturnToStartCommand.CanExecute(null));
    }

    [Fact]
    public void PlayFromCursorSeeksOverlappingAudioButDoesNotRecreatePastGestures()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 1_000;
        var scheduler = new FakePlaybackTimerScheduler();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, audio: audio);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0xFFFF, AnimId = 2 });
        vm.AudioLanes[0].Clips.Add(new AudioClip
        {
            StartMs = 100,
            DurationMs = 1_000,
            FilePath = "overlap.wav",
        });
        vm.PlayheadMs = 450;

        vm.PlayCommand.Execute(null);

        var played = Assert.Single(audio.Actions, action => action.Kind == "Play");
        Assert.Equal("overlap.wav", played.Path);
        Assert.Equal(350, played.StartOffsetMs);
        Assert.Empty(protocol.Sent);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void PlayFromCursorSeeksLoopingAudioToItsCurrentCycle()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, audio: audio);
        vm.AudioLanes[0].Clips.Add(new AudioClip
        {
            StartMs = 100,
            DurationMs = 500,
            FilePath = "loop.wav",
            Loop = true,
        });
        vm.Steps.Add(new SequenceStep { StartMs = 2_000, Target = 0xFFFF, AnimId = 2 });
        vm.PlayheadMs = 1_350;

        vm.PlayCommand.Execute(null);

        var played = Assert.Single(audio.Actions, action => action.Kind == "Play");
        Assert.Equal(250, played.StartOffsetMs);
        Assert.True(played.Loop);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void MovingThePausedPlayheadStopsTheRetainedPassAndNextPlaySeeksAudio()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, clock, audio);
        vm.AudioLanes[0].Clips.Add(new AudioClip
        {
            StartMs = 0,
            DurationMs = 1_000,
            FilePath = "seek-after-pause.wav",
        });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        vm.PlayCommand.Execute(null);
        Assert.True(vm.IsPaused);

        vm.SetPlayheadFromPixel(600 * vm.PxPerMs);

        Assert.False(vm.IsPaused);
        Assert.False(vm.IsPlaying);
        Assert.Equal(600, vm.PlayheadMs);
        Assert.Equal("StopAll", audio.Actions[^1].Kind);

        vm.PlayCommand.Execute(null);
        var plays = audio.Actions.Where(action => action.Kind == "Play").ToArray();
        Assert.Equal(2, plays.Length);
        Assert.Equal(600, plays[1].StartOffsetMs);
        Assert.DoesNotContain(audio.Actions, action => action.Kind == "ResumeAll");
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void ClickingTheUnchangedPausedPositionKeepsThePassResumable()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, clock, audio);
        vm.AudioLanes[0].Clips.Add(new AudioClip
        {
            StartMs = 0,
            DurationMs = 1_000,
            FilePath = "unchanged-pause.wav",
        });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        vm.PlayCommand.Execute(null);

        vm.SetPlayheadFromPixel(vm.PlayheadMs * vm.PxPerMs);

        Assert.True(vm.IsPaused);
        vm.PlayCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.Equal("ResumeAll", audio.Actions[^1].Kind);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void MovingThePausedPlayheadUsesNormalStopCleanupForAnInfiniteGesture()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep
        {
            StartMs = 0,
            Target = 0x1234,
            AnimId = 16,
            EndAfterMs = 2_000,
        });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        vm.PlayCommand.Execute(null);
        Assert.True(vm.IsPaused);

        vm.SetPlayheadFromPixel(600 * vm.PxPerMs);

        Assert.False(vm.IsPaused);
        Assert.Equal(2, protocol.Sent.Count);
        Assert.Equal(16, protocol.Sent[0].AnimId);
        Assert.Equal(0, protocol.Sent[1].AnimId);
        Assert.Equal((ushort)0x1234, protocol.Sent[1].Target);
    }

    [Fact]
    public void FollowDefaultsOnForANewPassButManualSuspensionSurvivesPauseResume()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });
        vm.FollowPlayhead = false;

        vm.PlayCommand.Execute(null);
        Assert.True(vm.FollowPlayhead);
        vm.FollowPlayhead = false;
        vm.PlayCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.False(vm.FollowPlayhead);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void TimelineNavigationMath_AnchorsZoomAndUsesABoundedComfortCorridor()
    {
        Assert.Equal(92, SequenceTimelineView.CalculateWheelZoom(80, 120), precision: 0);
        Assert.InRange(SequenceTimelineView.CalculateWheelZoom(80, 30), 82.8, 82.9);
        Assert.Equal(300, SequenceTimelineView.CalculateWheelZoom(299, 120));
        Assert.Equal(20, SequenceTimelineView.CalculateWheelZoom(21, -120));

        Assert.True(MainWindow.ShouldYieldWheelToTimeline(ModifierKeys.Control, insideTimelineViewport: true));
        Assert.True(MainWindow.ShouldYieldWheelToTimeline(ModifierKeys.Shift, insideTimelineViewport: true));
        Assert.False(MainWindow.ShouldYieldWheelToTimeline(ModifierKeys.None, insideTimelineViewport: true));
        Assert.False(MainWindow.ShouldYieldWheelToTimeline(ModifierKeys.Control, insideTimelineViewport: false));

        Assert.Equal(400, SequenceTimelineView.CalculatePointerCenteredOffset(
            currentOffset: 100, pointerViewportX: 200,
            oldPxPerSecond: 80, newPxPerSecond: 160, scrollableWidth: 1_000));
        Assert.Equal(212, SequenceTimelineView.CalculateFollowOffset(
            currentOffset: 0, playheadContentX: 500, viewportWidth: 400, scrollableWidth: 1_000));
        Assert.Equal(212, SequenceTimelineView.CalculateFollowOffset(
            currentOffset: 212, playheadContentX: 350, viewportWidth: 400, scrollableWidth: 1_000));

        Assert.True(SequenceTimelineView.MatchesAutomaticScrollTarget(212, 212));
        Assert.True(SequenceTimelineView.MatchesAutomaticScrollTarget(212, 212.5));
        Assert.False(SequenceTimelineView.MatchesAutomaticScrollTarget(212, 215));
        Assert.False(SequenceTimelineView.MatchesAutomaticScrollTarget(null, 212));
        Assert.True(SequenceTimelineView.ShouldRestoreFollowAfterScrollbarInteraction(
            followWasEnabled: true, isPlaying: true));
        Assert.False(SequenceTimelineView.ShouldRestoreFollowAfterScrollbarInteraction(
            followWasEnabled: false, isPlaying: true));
        Assert.False(SequenceTimelineView.ShouldRestoreFollowAfterScrollbarInteraction(
            followWasEnabled: true, isPlaying: false));
        Assert.Equal(0, SequenceTimelineView.CalculateFollowOffset(
            currentOffset: 500, playheadContentX: 0, viewportWidth: 400, scrollableWidth: 1_000));
    }

    [Fact]
    public void EmptyDocument_PlayIsANoOp()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, audio: audio);

        vm.PlayCommand.Execute(null);

        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.Empty(scheduler.Entries);
        Assert.Empty(protocol.Sent);
        Assert.Empty(audio.Actions);
    }

    [Fact]
    public void EmptyDocument_WithManualEndpointRunsOneSilentTimedPass()
    {
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(new FakeSequencerProtocol(), scheduler);
        vm.PlayheadMs = 750;
        vm.SetSequenceEndAtPlayheadCommand.Execute(null);
        vm.ReturnToStartCommand.Execute(null);

        vm.PlayCommand.Execute(null);

        Assert.True(vm.IsPlaying);
        Assert.Equal(750, Assert.Single(scheduler.Entries).DueTimeMs);
        scheduler.Entries[0].Invoke();
        Assert.False(vm.IsPlaying);
        Assert.Equal(750, vm.PlayheadMs);
    }

    [Fact]
    public void AudioOnlyPass_DispatchesCapturedPathAndLoopValue()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, audio: audio);
        var clip = new AudioClip
        {
            StartMs = 100,
            DurationMs = 500,
            FilePath = @"C:\fixtures\original.wav",
            Loop = true,
        };
        vm.AudioLanes[0].Clips.Add(clip);

        vm.PlayCommand.Execute(null);
        clip.FilePath = @"C:\fixtures\replacement.wav";
        clip.Loop = false;
        clip.StartMs = 9_000;

        Assert.Equal(100, scheduler.Entries[0].DueTimeMs);
        scheduler.Entries[0].Invoke();

        var played = Assert.Single(audio.Actions, action => action.Kind == "Play");
        Assert.Equal(@"C:\fixtures\original.wav", played.Path);
        Assert.True(played.Loop);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void LoopingAudioRunsUntilManualSceneEndpointThenStopsExactlyOnce()
    {
        var scheduler = new FakePlaybackTimerScheduler();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(new FakeSequencerProtocol(), scheduler, audio: audio);
        Assert.True(vm.InsertAudioClip(vm.AudioLanes[0], new AudioClip
        {
            StartMs = 100,
            DurationMs = 500,
            FilePath = "ambient.wav",
            Loop = true,
        }));
        vm.PlayheadMs = 2_000;
        vm.SetSequenceEndAtPlayheadCommand.Execute(null);
        vm.ReturnToStartCommand.Execute(null);

        vm.PlayCommand.Execute(null);
        Assert.Equal(100, scheduler.Entries[0].DueTimeMs);
        scheduler.Entries[0].Invoke();

        var played = Assert.Single(audio.Actions, action => action.Kind == "Play");
        Assert.True(played.Loop);
        Assert.Equal(1_900, scheduler.Entries[1].DueTimeMs);
        scheduler.Entries[1].Invoke();

        Assert.False(vm.IsPlaying);
        Assert.Equal(2_000, vm.PlayheadMs);
        Assert.Equal(2, audio.Actions.Count(action => action.Kind == "StopAll"));
    }

    [Fact]
    public void WholePassLoopStopsOldAudioAtEndpointBeforeRearmingFromZero()
    {
        var scheduler = new FakePlaybackTimerScheduler();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(new FakeSequencerProtocol(), scheduler, audio: audio);
        Assert.True(vm.InsertAudioClip(vm.AudioLanes[0], new AudioClip
        {
            StartMs = 100,
            DurationMs = 500,
            FilePath = "ambient.wav",
            Loop = true,
        }));
        vm.PlayheadMs = 1_500;
        vm.SetSequenceEndAtPlayheadCommand.Execute(null);
        vm.EditableLoop = true;
        vm.ReturnToStartCommand.Execute(null);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        scheduler.Entries[1].Invoke();

        Assert.True(vm.IsPlaying);
        Assert.Equal(100, scheduler.Entries[2].DueTimeMs);
        Assert.Single(audio.Actions, action => action.Kind == "Play");
        Assert.Equal(2, audio.Actions.Count(action => action.Kind == "StopAll"));
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void PauseResumePreservesLoopingAudioAndRemainingExplicitEndpointTime()
    {
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(
            new FakeSequencerProtocol(), scheduler, audio: audio, clock: clock);
        Assert.True(vm.InsertAudioClip(vm.AudioLanes[0], new AudioClip
        {
            StartMs = 100,
            DurationMs = 500,
            FilePath = "ambient.wav",
            Loop = true,
        }));
        vm.PlayheadMs = 2_000;
        vm.SetSequenceEndAtPlayheadCommand.Execute(null);
        vm.ReturnToStartCommand.Execute(null);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        clock.SetElapsed(TimeSpan.FromMilliseconds(600));
        vm.PauseCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.Equal(1_400, scheduler.Entries[^1].DueTimeMs);
        Assert.Contains(audio.Actions, action => action.Kind == "PauseAll");
        Assert.Contains(audio.Actions, action => action.Kind == "ResumeAll");
        scheduler.Entries[^1].Invoke();
        Assert.False(vm.IsPlaying);
        Assert.Equal(2_000, vm.PlayheadMs);
    }

    [Fact]
    public void LargeTimeline_UsesOneRearmableWakeTimerRegardlessOfEventCount()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        Assert.True(vm.BeginStepDrag());
        for (var i = 0; i < 10_000; i++)
            vm.Steps.Add(new SequenceStep
            {
                StartMs = 100 + i,
                Target = ushort.MaxValue,
                AnimId = i % 16,
            });
        Assert.True(vm.CompleteEditTransaction());

        vm.PlayCommand.Execute(null);

        Assert.Equal(1, scheduler.CreatedWakeTimers);
        Assert.Equal(1, scheduler.ActiveWakeTimers);
        Assert.Single(scheduler.Entries);
        Assert.Equal(100, scheduler.Entries[0].DueTimeMs);
        vm.StopCommand.Execute(null);
        Assert.Equal(0, scheduler.ActiveWakeTimers);
        Assert.True(scheduler.Entries[0].Disposed);
    }

    [Fact]
    public void MaximumSupportedTimeline_RebuildsOnceAndKeepsRulerBoundedAtMaximumZoom()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.PxPerSecond = 300;
        var extentRefreshes = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.TimelineWidthPx)) extentRefreshes++;
        };

        Assert.True(vm.BeginStepDrag());
        for (var i = 0; i < SequenceImportService.MaxSteps; i++)
            vm.Steps.Add(new SequenceStep
            {
                StartMs = i == SequenceImportService.MaxSteps - 1
                    ? SequenceImportService.MaxTimelineMs - 1_500
                    : i,
                Target = ushort.MaxValue,
                AnimId = i % 16,
            });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(vm.CompleteEditTransaction());
        stopwatch.Stop();

        Assert.Equal(SequenceImportService.MaxTimelineMs, vm.TotalDurationMsValue);
        Assert.Equal(1, extentRefreshes);
        Assert.InRange(vm.RulerTicks.Count, 1, SequencerViewModel.MaxRulerTickCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Maximum-size timeline refresh took {stopwatch.Elapsed.TotalSeconds:0.000} s.");
    }

    [Fact]
    public void MaximumSupportedTimeline_RulerIntervalPreservesDensityAndCountLimits()
    {
        const double maximumZoomPxPerMs = 0.3;
        var interval = SequencerViewModel.SelectRulerIntervalMs(
            SequenceImportService.MaxTimelineMs,
            maximumZoomPxPerMs);

        Assert.True(interval * maximumZoomPxPerMs >= 50);
        Assert.True(Math.Floor(SequenceImportService.MaxTimelineMs / (double)interval) + 1
            <= SequencerViewModel.MaxRulerTickCount);
        Assert.Equal(300_000, interval);
    }

    [Fact]
    public void LateWake_DrainsAllDueBatchesInStableOrderAndRearmsFromMonotonicNow()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[1] = 100;
        protocol.Durations[2] = 100;
        protocol.Durations[3] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0x1001, AnimId = 1 });
        vm.Steps.Add(new SequenceStep { StartMs = 200, Target = 0x1002, AnimId = 2 });
        vm.Steps.Add(new SequenceStep { StartMs = 300, Target = 0x1003, AnimId = 3 });

        vm.PlayCommand.Execute(null);
        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        scheduler.Entries[0].Invoke();

        Assert.Equal(new[] { 1, 2 }, protocol.Sent.Select(sent => sent.AnimId));
        Assert.Equal(50, scheduler.Entries[1].DueTimeMs);
        clock.SetElapsed(TimeSpan.FromMilliseconds(300));
        scheduler.Entries[1].Invoke();
        Assert.Equal(new[] { 1, 2, 3 }, protocol.Sent.Select(sent => sent.AnimId));
        Assert.Equal(1, scheduler.CreatedWakeTimers);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void RepeatedPasses_SendSameTimeGesturesInIdenticalEditorOrder()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = ushort.MaxValue, AnimId = 5 });
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0x1001, AnimId = 3 });
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0x1001, AnimId = 7 });

        for (var pass = 0; pass < 20; pass++)
        {
            vm.RestartCommand.Execute(null);
            scheduler.Entries[^1].Invoke();
        }

        var expected = Enumerable.Repeat(new[] { 5, 3, 7 }, 20).SelectMany(ids => ids);
        Assert.Equal(expected, protocol.Sent.Select(sent => sent.AnimId));
        Assert.True(vm.HasScheduleWarnings);
        Assert.Contains("last received command wins", vm.ScheduleWarningText);
        Assert.Contains("mesh arrival order is not guaranteed", vm.ScheduleWarningText);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void Restart_RejectsQueuedCallbackFromThePreviousGeneration()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 700;
        var scheduler = new FakePlaybackTimerScheduler();
        var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var staleEvent = scheduler.Entries[0];

        vm.RestartCommand.Execute(null);
        var currentEvent = scheduler.Entries[1]; // one wake timer was created for each pass

        Assert.True(staleEvent.Disposed);
        staleEvent.InvokeEvenIfDisposed();
        Assert.Empty(protocol.Sent);

        currentEvent.InvokeEvenIfDisposed();
        var sent = Assert.Single(protocol.Sent);
        Assert.Equal((ushort)0x1234, sent.Target);
        Assert.Equal(2, sent.AnimId);

        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void RapidExplicitRestarts_OnlyNewestGenerationCanDispatch()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 50, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var first = scheduler.Entries[0];
        vm.RestartCommand.Execute(null);
        var second = scheduler.Entries[1];
        vm.RestartCommand.Execute(null);
        var third = scheduler.Entries[2];

        first.InvokeEvenIfDisposed();
        second.InvokeEvenIfDisposed();
        third.Invoke();

        Assert.Equal(4, scheduler.Entries.Count); // three pass wakes + current pass end rearm
        Assert.Equal(2, Assert.Single(protocol.Sent).AnimId);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void StaleLoopEndCallback_CannotRearmAnOlderPass()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Loop = true;
        vm.Steps.Add(new SequenceStep { StartMs = 50, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var staleEnd = scheduler.Entries[1];
        vm.RestartCommand.Execute(null);
        scheduler.Entries[2].Invoke();
        var currentEnd = scheduler.Entries[3];

        staleEnd.InvokeEvenIfDisposed();
        Assert.Equal(4, scheduler.Entries.Count);
        Assert.True(vm.IsPlaying);

        currentEnd.Invoke();
        Assert.Equal(5, scheduler.Entries.Count); // the current pass alone rearms the loop
        Assert.True(vm.IsPlaying);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void ActivePass_UsesItsImmutableSnapshot()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 700;
        var scheduler = new FakePlaybackTimerScheduler();
        var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { StartMs = 100, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        step.Target = 0xFFFF; // bypass the locked UI to prove the callback itself is isolated
        step.AnimId = 17;
        step.StartMs = 9_000;

        scheduler.Entries[0].InvokeEvenIfDisposed();

        var sent = Assert.Single(protocol.Sent);
        Assert.Equal((ushort)0x1234, sent.Target);
        Assert.Equal(2, sent.AnimId);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void TrackMute_IsEvaluatedWhenEachEventDispatches()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0xFFFF, AnimId = 2 });
        vm.Steps.Add(new SequenceStep { StartMs = 200, Target = 0xFFFF, AnimId = 3 });

        vm.PlayCommand.Execute(null);
        var broadcast = Assert.Single(vm.Tracks, t => t.IsBroadcast);
        broadcast.Muted = true;
        scheduler.Entries[0].InvokeEvenIfDisposed();
        Assert.Empty(protocol.Sent);

        broadcast.Muted = false;
        scheduler.Entries[1].InvokeEvenIfDisposed();
        Assert.Equal(3, Assert.Single(protocol.Sent).AnimId);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void IndividualTrackMute_DoesNotMuteOtherTargets()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1001, Name = "R2" });
        protocol.Droids.Add(new Droid { Id = 0x1002, Name = "D-O" });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0x1001, AnimId = 2 });
        vm.Steps.Add(new SequenceStep { StartMs = 200, Target = 0x1002, AnimId = 3 });

        vm.Tracks.Single(t => t.Id == 0x1001).Muted = true;
        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        scheduler.Entries[1].Invoke();

        var sent = Assert.Single(protocol.Sent);
        Assert.Equal((ushort)0x1002, sent.Target);
        Assert.Equal(3, sent.AnimId);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void PersistentEditingCommands_AreLockedForPlayAndPause()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1234, Name = "Test droid" });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { StartMs = 1_000, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);
        vm.SelectedStep = step;
        var sourceLane = vm.AudioLanes[0];
        var targetLane = vm.AudioLanes[1];
        var audio = new AudioClip { StartMs = 500, DurationMs = 100, FilePath = "fixture.wav" };
        sourceLane.Clips.Add(audio);
        var libraryItem = new SequenceLibraryItem();
        vm.PlayheadMs = 2_000;
        vm.SetSequenceEndAtPlayheadCommand.Execute(null);

        void AssertPersistentCommands(bool expected)
        {
            Assert.Equal(expected, vm.InsertGestureCommand.CanExecute(1));
            Assert.Equal(expected, vm.NudgeStartForwardCommand.CanExecute(null));
            Assert.Equal(expected, vm.NudgeStartBackwardCommand.CanExecute(null));
            Assert.Equal(expected, vm.AddAudioLaneCommand.CanExecute(null));
            Assert.Equal(expected, vm.DeleteAudioLaneCommand.CanExecute(sourceLane));
            Assert.Equal(expected, vm.AddAudioClipCommand.CanExecute(sourceLane));
            Assert.Equal(expected, vm.ReplaceAudioClipCommand.CanExecute(audio));
            Assert.Equal(expected, vm.DeleteAudioClipCommand.CanExecute(audio));
            Assert.Equal(expected, vm.ClearTimelineCommand.CanExecute(null));
            Assert.Equal(expected, vm.DeleteStepCommand.CanExecute(step));
            Assert.Equal(expected, vm.DuplicateStepCommand.CanExecute(step));
            Assert.Equal(expected, vm.DeleteFromLibraryCommand.CanExecute(libraryItem));
            Assert.Equal(expected, vm.SaveSceneCommand.CanExecute(null));
            Assert.Equal(expected, vm.SaveSceneAsCommand.CanExecute(null));
            Assert.Equal(expected, vm.SetSequenceEndAtPlayheadCommand.CanExecute(null));
            Assert.Equal(expected, vm.UseAutomaticSequenceEndCommand.CanExecute(null));
        }

        void AssertInspectionAndRuntimeControlsRemainAvailable()
        {
            Assert.True(vm.ArmTrackCommand.CanExecute(vm.Tracks[0]));
            Assert.True(vm.ToggleMuteCommand.CanExecute(vm.Tracks[0]));
            Assert.True(vm.ExportCommand.CanExecute(null));
            Assert.True(vm.NewSceneCommand.CanExecute(null));
            Assert.True(vm.OpenSceneLibraryCommand.CanExecute(null));
            Assert.True(vm.LoadFromLibraryCommand.CanExecute(libraryItem));
            Assert.True(vm.ImportCommand.CanExecute(null));
        }

        void AssertDirectMutationGuards()
        {
            var originalTarget = step.Target;
            var originalStepCount = vm.Steps.Count;
            vm.InsertGestureAt(3, vm.Tracks[0], 200);
            vm.MoveAudioClipToLane(audio, targetLane);
            vm.SelectedStepTrack = vm.Tracks.Single(t => t.IsBroadcast);
            Assert.Equal(originalStepCount, vm.Steps.Count);
            Assert.Equal(originalTarget, step.Target);
            Assert.Contains(audio, sourceLane.Clips);
            Assert.DoesNotContain(audio, targetLane.Clips);
        }

        Assert.True(vm.CanEditSequence);
        AssertPersistentCommands(expected: true);
        AssertInspectionAndRuntimeControlsRemainAvailable();
        vm.PlayCommand.Execute(null);

        Assert.False(vm.CanEditSequence);
        AssertPersistentCommands(expected: false);
        AssertInspectionAndRuntimeControlsRemainAvailable();
        AssertDirectMutationGuards();

        vm.PauseCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.False(vm.CanEditSequence);
        AssertPersistentCommands(expected: false);
        AssertInspectionAndRuntimeControlsRemainAvailable();
        AssertDirectMutationGuards();

        vm.StopCommand.Execute(null);
        Assert.True(vm.CanEditSequence);
        AssertPersistentCommands(expected: true);
        AssertInspectionAndRuntimeControlsRemainAvailable();
    }

    [Fact]
    public void UndoAndRedoAvailability_FollowsTheSamePlayAndPauseEditLock()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0xFFFF, AnimId = 2 });
        Assert.True(vm.BeginStepDrag());
        vm.Steps[0].StartMs += 100;
        Assert.True(vm.CompleteEditTransaction()); // creates a valid Undo snapshot without opening UI

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.PlayCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
        vm.PauseCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        Assert.True(vm.UndoCommand.CanExecute(null));

        vm.UndoCommand.Execute(null); // creates a valid Redo snapshot
        Assert.True(vm.RedoCommand.CanExecute(null));
        vm.PlayCommand.Execute(null);
        Assert.False(vm.RedoCommand.CanExecute(null));
        vm.PauseCommand.Execute(null);
        Assert.False(vm.RedoCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        Assert.True(vm.RedoCommand.CanExecute(null));
    }

    [Fact]
    public void EditTransactionMatrix_CommitsOneUndoableChangeAndOneDerivedRefresh()
    {
        static string Fingerprint(SequencerViewModel vm)
        {
            var steps = string.Join(";", vm.Steps.Select(s => $"{s.AnimId},{s.Target},{s.StartMs},{s.EndAfterMs}"));
            var lanes = string.Join(";", vm.AudioLanes.Select(l =>
                $"{l.Label}[{string.Join("/", l.Clips.Select(c => $"{c.FilePath},{c.DurationMs},{c.StartMs},{c.Loop}"))}]"));
            return $"{vm.Name}|{vm.Loop}|{vm.SequenceEndMs}|{steps}|{lanes}";
        }

        var cases = new (string Name, Action<SequencerViewModel> Arrange, Action<SequencerViewModel> Edit)[]
        {
            ("sequence name", _ => { }, vm => Assert.True(vm.SetSequenceName("Scene A"))),
            ("sequence loop", _ => { }, vm => vm.EditableLoop = true),
            ("sequence end", _ => { }, vm =>
            {
                vm.PlayheadMs = 2_500;
                vm.SetSequenceEndAtPlayheadCommand.Execute(null);
            }),
            ("insert gesture", _ => { }, vm => vm.InsertGestureAt(2, vm.Tracks[0], 100)),
            ("gesture animation", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
                vm.SelectedStep = vm.Steps[0];
            }, vm => vm.SelectedStepAnimId = 3),
            ("gesture target", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
            }, vm => Assert.True(vm.SetStepTarget(vm.Steps[0], 0x1234))),
            ("nudge gesture", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
                vm.SelectedStep = vm.Steps[0];
            }, vm => vm.NudgeStartForwardCommand.Execute(null)),
            ("infinite gesture end", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 17, Target = 0xFFFF, StartMs = 100 });
                vm.SelectedStep = vm.Steps[0];
            }, vm => vm.NudgeEndLongerCommand.Execute(null)),
            ("duplicate gesture", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
            }, vm => vm.DuplicateStepCommand.Execute(vm.Steps[0])),
            ("delete gesture", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
            }, vm => vm.DeleteStepCommand.Execute(vm.Steps[0])),
            ("gesture drag", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
            }, vm =>
            {
                Assert.True(vm.BeginStepDrag());
                vm.Steps[0].StartMs = 350;
                Assert.True(vm.CompleteEditTransaction());
            }),
            ("add audio lane", _ => { }, vm => vm.AddAudioLaneCommand.Execute(null)),
            ("delete audio lane", _ => { }, vm => vm.DeleteAudioLaneCommand.Execute(vm.AudioLanes[1])),
            ("rename audio lane", _ => { }, vm =>
            {
                Assert.True(vm.BeginLaneRename());
                vm.AudioLanes[0].Label = "MUSIC";
                Assert.True(vm.CompleteEditTransaction());
            }),
            ("reorder audio lane", _ => { }, vm => Assert.True(vm.MoveAudioLane(vm.AudioLanes[1], 0))),
            ("insert audio clip", _ => { }, vm =>
                Assert.True(vm.InsertAudioClip(vm.AudioLanes[0],
                    new AudioClip { FilePath = "insert.wav", DurationMs = 100, StartMs = 10 }))),
            ("replace audio source", vm =>
            {
                vm.AudioLanes[0].Clips.Add(new AudioClip { FilePath = "old.wav", DurationMs = 100, StartMs = 10 });
            }, vm => Assert.True(vm.ReplaceAudioClipSource(
                vm.AudioLanes[0].Clips[0], "new.wav", 250))),
            ("audio loop", vm =>
            {
                vm.AudioLanes[0].Clips.Add(new AudioClip { FilePath = "loop.wav", DurationMs = 100, StartMs = 10 });
            }, vm => vm.ToggleAudioLoopCommand.Execute(vm.AudioLanes[0].Clips[0])),
            ("move audio clip lane", vm =>
            {
                vm.AudioLanes[0].Clips.Add(new AudioClip { FilePath = "move.wav", DurationMs = 100, StartMs = 10 });
            }, vm => vm.MoveAudioClipToLane(vm.AudioLanes[0].Clips[0], vm.AudioLanes[1])),
            ("delete audio clip", vm =>
            {
                vm.AudioLanes[0].Clips.Add(new AudioClip { FilePath = "delete.wav", DurationMs = 100, StartMs = 10 });
            }, vm => vm.DeleteAudioClipCommand.Execute(vm.AudioLanes[0].Clips[0])),
            ("audio drag", vm =>
            {
                vm.AudioLanes[0].Clips.Add(new AudioClip { FilePath = "drag.wav", DurationMs = 100, StartMs = 10 });
            }, vm =>
            {
                Assert.True(vm.BeginAudioClipDrag());
                vm.AudioLanes[0].Clips[0].StartMs = 250;
                Assert.True(vm.CompleteEditTransaction());
            }),
            ("clear timeline", vm =>
            {
                vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
            }, vm => vm.ClearTimelineCommand.Execute(null)),
        };

        foreach (var editCase in cases)
        {
            var protocol = new FakeSequencerProtocol();
            protocol.Durations[2] = 100;
            var scheduler = new FakePlaybackTimerScheduler();
            using var vm = CreateViewModel(protocol, scheduler);
            editCase.Arrange(vm);
            vm.EstablishSavedCheckpoint();
            var before = Fingerprint(vm);
            var derivedRefreshes = 0;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.TimelineWidthPx)) derivedRefreshes++;
            };

            editCase.Edit(vm);
            var after = Fingerprint(vm);

            Assert.True(vm.Dirty, editCase.Name);
            Assert.True(vm.UndoCommand.CanExecute(null), editCase.Name);
            Assert.False(vm.RedoCommand.CanExecute(null), editCase.Name);
            Assert.NotEqual(before, after);
            Assert.Equal(1, derivedRefreshes);

            vm.UndoCommand.Execute(null);
            Assert.Equal(before, Fingerprint(vm));
            Assert.False(vm.UndoCommand.CanExecute(null));
            Assert.True(vm.RedoCommand.CanExecute(null));
            vm.RedoCommand.Execute(null);
            Assert.Equal(after, Fingerprint(vm));
            Assert.True(vm.UndoCommand.CanExecute(null));
            Assert.False(vm.RedoCommand.CanExecute(null));
        }
    }

    [Fact]
    public void DurationMetadataAndTargetConfigRefreshOneSharedStepProjectionAndCachedExtent()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0x1234, StartMs = 100 });

        Assert.True(vm.Steps[0].DurationProvisional);
        Assert.Equal(1_500, vm.Steps[0].ResolvedDurationMs);
        Assert.Equal(1_600, vm.TotalDurationMsValue);

        protocol.DurationMetadata[2] = new AnimationDurationMetadata(
            2, AnimationDurationKind.Finite, 1_000, 2);
        protocol.Speeds[0x1234] = 50;
        protocol.RaiseAnimDurationsReceived();

        Assert.False(vm.Steps[0].DurationProvisional);
        Assert.Equal(1_120, vm.Steps[0].ResolvedDurationMs);
        Assert.Equal(1_220, vm.TotalDurationMsValue);
        Assert.Contains("0.88", vm.Steps[0].DurationSummary);

        protocol.Speeds[0x1234] = 100;
        protocol.RaiseAnimConfigurationChanged();
        Assert.Equal(620, vm.Steps[0].ResolvedDurationMs);
        Assert.Equal(720, vm.TotalDurationMsValue);
    }

    [Fact]
    public void RepeatedUnchangedDroidTelemetry_DoesNotRebuildTimelineOrInterruptAnimations()
    {
        var protocol = new FakeSequencerProtocol();
        var droid = new Droid { Id = 0x1234, Name = "B1", Online = true };
        protocol.Droids.Add(droid);
        using var vm = CreateViewModel(protocol, new FakePlaybackTimerScheduler());
        vm.Steps.Add(new SequenceStep { AnimId = 2, Target = ushort.MaxValue, StartMs = 100 });
        var extentRefreshes = 0;
        var trackRefreshes = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.TimelineWidthPx)) extentRefreshes++;
            if (args.PropertyName == nameof(vm.TracksHeightPx)) trackRefreshes++;
        };

        protocol.RaiseDroidsChanged();
        protocol.RaiseDroidsChanged();

        Assert.Equal(0, extentRefreshes);
        Assert.Equal(0, trackRefreshes);

        droid.Online = false;
        protocol.RaiseDroidsChanged();
        Assert.Equal(1, extentRefreshes); // broadcast duration target set changed
        Assert.Equal(0, trackRefreshes);

        droid.Name = "B1 renamed";
        protocol.RaiseDroidsChanged();
        Assert.Equal(1, extentRefreshes);
        Assert.Equal(1, trackRefreshes); // only the roster projection changed
    }

    [Fact]
    public void EditTransactions_IgnoreNoOpsAndClearRedoOnlyAfterARealChange()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 0 };
        vm.Steps.Add(step);
        vm.SelectedStep = step;
        var refreshes = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.TimelineWidthPx)) refreshes++;
        };

        Assert.True(vm.BeginStepDrag());
        Assert.False(vm.CompleteEditTransaction()); // selection/click with no persistent movement
        vm.NudgeStartBackwardCommand.Execute(null); // already at zero
        vm.MoveAudioClipToLane(new AudioClip(), vm.AudioLanes[0]); // not in the document
        var unchangedAudio = new AudioClip { FilePath = "same.wav", DurationMs = 100, StartMs = 10 };
        vm.AudioLanes[0].Clips.Add(unchangedAudio);
        Assert.False(vm.ReplaceAudioClipSource(unchangedAudio, "same.wav", 100));

        Assert.False(vm.Dirty);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal(0, refreshes);

        vm.NudgeStartForwardCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.RedoCommand.CanExecute(null));

        vm.SelectedStep = vm.Steps[0];
        vm.NudgeStartForwardCommand.Execute(null);
        Assert.False(vm.RedoCommand.CanExecute(null));
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void DragThresholdAndTransaction_DistinguishClickReturnAndRealMovement()
    {
        Assert.False(SequenceTimelineView.ExceedsDragThreshold(new(10, 10), new(12, 12)));
        Assert.True(SequenceTimelineView.ExceedsDragThreshold(new(10, 10), new(15, 10)));

        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });

        Assert.True(vm.BeginStepDrag());
        vm.Steps[0].StartMs = 300;
        vm.Steps[0].StartMs = 100;
        Assert.False(vm.CompleteEditTransaction());
        Assert.False(vm.Dirty);
        Assert.False(vm.UndoCommand.CanExecute(null));

        Assert.True(vm.BeginStepDrag());
        vm.Steps[0].StartMs = 300;
        Assert.True(vm.CompleteEditTransaction());
        Assert.True(vm.Dirty);
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void CancelEditTransaction_RestoresDocumentDirtyStateAndHistory()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 });
        vm.AudioLanes[0].Clips.Add(new AudioClip
        {
            FilePath = "original.wav", DurationMs = 100, StartMs = 20,
        });
        vm.EstablishSavedCheckpoint();

        Assert.True(vm.BeginStepDrag());
        vm.Steps[0].StartMs = 900;
        vm.AudioLanes[0].Clips[0].FilePath = "cancelled.wav";
        vm.AudioLanes[0].Label = "CANCELLED";
        Assert.True(vm.CancelEditTransaction());

        Assert.Equal(100, vm.Steps[0].StartMs);
        Assert.Equal("original.wav", vm.AudioLanes[0].Clips[0].FilePath);
        Assert.Equal("AMBIENT", vm.AudioLanes[0].Label);
        Assert.False(vm.Dirty);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.CancelEditTransaction()); // cancellation is idempotent

        vm.SelectedStep = vm.Steps[0];
        vm.NudgeStartForwardCommand.Execute(null);
        Assert.True(vm.Dirty);
        Assert.True(vm.UndoCommand.CanExecute(null));
        var committedStart = vm.Steps[0].StartMs;

        Assert.True(vm.BeginStepDrag());
        vm.Steps[0].StartMs = 1_500;
        Assert.True(vm.CancelEditTransaction());
        Assert.Equal(committedStart, vm.Steps[0].StartMs);
        Assert.True(vm.Dirty);
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void UndoAndRedoHistory_RetainExactlyTheNewestFiftyEditsInOrder()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 0 });
        vm.SelectedStep = vm.Steps[0];

        for (var i = 0; i < 55; i++) vm.NudgeStartForwardCommand.Execute(null);
        Assert.Equal(5_500, vm.Steps[0].StartMs);

        var undoCount = 0;
        while (vm.UndoCommand.CanExecute(null))
        {
            vm.UndoCommand.Execute(null);
            undoCount++;
        }
        Assert.Equal(50, undoCount);
        Assert.Equal(500, vm.Steps[0].StartMs); // the oldest five snapshots were evicted

        var redoCount = 0;
        while (vm.RedoCommand.CanExecute(null))
        {
            vm.RedoCommand.Execute(null);
            redoCount++;
        }
        Assert.Equal(50, redoCount);
        Assert.Equal(5_500, vm.Steps[0].StartMs);
    }

    [Fact]
    public void TransientEditorAndTelemetryState_DoesNotCreateDocumentHistory()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { AnimId = 2, Target = 0xFFFF, StartMs = 100 };
        var clip = new AudioClip { FilePath = "transient.wav", DurationMs = 100, StartMs = 10 };
        vm.Steps.Add(step);
        vm.AudioLanes[0].Clips.Add(clip);

        vm.SelectedStep = step;
        vm.ArmTrackCommand.Execute(vm.Tracks[0]);
        vm.ToggleMuteCommand.Execute(vm.Tracks[0]);
        vm.PxPerSecond = 140;
        vm.SnapToGrid = false;
        vm.FollowPlayhead = false;
        vm.PlayheadMs = 75;
        step.Dragging = true;
        step.DragOffsetY = 8;
        step.ExecutionSummary = "DONE";
        step.ExecutionDetail = "telemetry";
        step.ExecutionTone = "completed";
        clip.Peaks = new[] { 0.1f, 0.5f };
        clip.Dragging = true;
        clip.DragOffsetY = 4;

        Assert.False(vm.Dirty);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public void LinkLoss_StopsThePassAndInvalidatesItsQueuedCallbacks()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var queued = scheduler.Entries[0];
        protocol.RaiseLinkClosed();

        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.True(vm.CanEditSequence);
        Assert.True(queued.Disposed);
        queued.InvokeEvenIfDisposed();
        Assert.Empty(protocol.Sent);
    }

    [Fact]
    public void LinkLossWhilePaused_CancelsTheRetainedPassAndPreventsResume()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, clock, audio);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var queued = scheduler.Entries[0];
        clock.SetElapsed(TimeSpan.FromMilliseconds(100));
        vm.PauseCommand.Execute(null);
        protocol.RaiseLinkClosed();

        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.True(vm.CanEditSequence);
        Assert.Contains(audio.Actions, action => action.Kind == "PauseAll");
        Assert.Equal("StopAll", audio.Actions[^1].Kind);
        queued.InvokeEvenIfDisposed();
        Assert.Empty(protocol.Sent);

        vm.PlayCommand.Execute(null); // starts a new pass from the retained diagnostic cursor
        Assert.Equal(2, scheduler.Entries.Count);
        Assert.Equal(400, scheduler.Entries[1].DueTimeMs);
    }

    [Fact]
    public void Pause_UsesMonotonicElapsedTimeAndResumeSchedulesTheRemainder()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        vm.PauseCommand.Execute(null);

        Assert.Equal(250, vm.PlayheadMs);
        vm.PlayCommand.Execute(null); // Resume
        Assert.Equal(250, scheduler.Entries[1].DueTimeMs);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void PauseLeavesAnAlreadyDispatchedFiniteGestureRunningToCompletion()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 800;
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var sent = Assert.Single(protocol.Sent);
        protocol.RaiseAnimExecution(sent.RequestId, sent.Target, sent.AnimId, "started");

        vm.PauseCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.Single(protocol.Sent); // Pause sends no replacement/stop gesture.

        protocol.RaiseAnimExecution(sent.RequestId, sent.Target, sent.AnimId, "completed");
        Assert.Equal("DONE", step.ExecutionSummary);
        Assert.Equal("completed", step.ExecutionTone);
        Assert.True(vm.IsPaused); // target completion does not resume the PC transport.
    }

    [Fact]
    public void PauseAtEventBoundary_DoesNotDispatchTheSameEventTwiceAfterResume()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        clock.SetElapsed(TimeSpan.FromMilliseconds(500));
        scheduler.Entries[0].Invoke();
        vm.PauseCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.Equal(3, scheduler.Entries.Count); // original pair + only the remaining end timer
        Assert.Equal(100, scheduler.Entries[2].DueTimeMs);
        scheduler.Entries[2].Invoke();
        Assert.Single(protocol.Sent);
    }

    [Fact]
    public void PauseImmediatelyBeforeEventBoundary_ReschedulesTheUnfiredEvent()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        clock.SetElapsed(TimeSpan.FromMilliseconds(499));
        vm.PauseCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.Equal(1, scheduler.Entries[1].DueTimeMs);
        scheduler.Entries[1].Invoke();
        Assert.Single(protocol.Sent);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void PauseImmediatelyAfterEventBoundary_DoesNotRescheduleTheFiredEvent()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        clock.SetElapsed(TimeSpan.FromMilliseconds(501));
        vm.PauseCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.Equal(3, scheduler.Entries.Count);
        Assert.Equal(99, scheduler.Entries[2].DueTimeMs);
        scheduler.Entries[2].Invoke();
        Assert.Single(protocol.Sent);
    }

    [Fact]
    public void PauseAfterMissedEventBoundary_ReschedulesTheUnfiredEventImmediately()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        clock.SetElapsed(TimeSpan.FromMilliseconds(501));
        vm.PauseCommand.Execute(null); // the event timer had not reached the UI dispatcher
        vm.PlayCommand.Execute(null);

        Assert.Equal(0, scheduler.Entries[1].DueTimeMs);
        scheduler.Entries[1].Invoke();
        Assert.Equal(99, scheduler.Entries[2].DueTimeMs);
        Assert.Single(protocol.Sent);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void SimultaneousEvents_DispatchAsOneAtomicOrderedBatchAcrossPause()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, clock, audio);
        vm.Steps.Add(new SequenceStep { StartMs = 500, Target = 0xFFFF, AnimId = 2 });
        vm.AudioLanes[0].Clips.Add(new AudioClip
        {
            StartMs = 500,
            DurationMs = 100,
            FilePath = @"C:\fixtures\simultaneous.wav",
        });

        vm.PlayCommand.Execute(null);
        Assert.Equal(500, scheduler.Entries[0].DueTimeMs);
        Assert.Single(scheduler.Entries);
        scheduler.Entries[0].Invoke(); // gesture then audio are drained by the same wake
        Assert.Single(protocol.Sent);
        Assert.Single(audio.Actions, action => action.Kind == "Play");
        clock.SetElapsed(TimeSpan.FromMilliseconds(500));
        vm.PauseCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.Equal(100, scheduler.Entries[2].DueTimeMs);
        scheduler.Entries[2].Invoke();
        Assert.Single(protocol.Sent);
        Assert.Single(audio.Actions, action => action.Kind == "Play");
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void MultiplePauseResumeCycles_KeepCumulativeMonotonicPosition()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 750, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        clock.SetElapsed(TimeSpan.FromMilliseconds(100));
        vm.PauseCommand.Execute(null);
        vm.PlayCommand.Execute(null);
        Assert.Equal(650, scheduler.Entries[1].DueTimeMs);

        clock.SetElapsed(TimeSpan.FromMilliseconds(150));
        vm.PauseCommand.Execute(null);
        Assert.Equal(250, vm.PlayheadMs);
        vm.PlayCommand.Execute(null);

        Assert.Equal(500, scheduler.Entries[2].DueTimeMs);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void RepeatedStop_IsIdempotentAndKeepsOldCallbacksInvalid()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var audio = new FakeAudioPlayer();
        using var vm = CreateViewModel(protocol, scheduler, audio: audio);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var queued = scheduler.Entries[0];
        vm.StopCommand.Execute(null);
        vm.StopCommand.Execute(null);
        vm.StopCommand.Execute(null);

        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.True(vm.CanEditSequence);
        Assert.All(scheduler.Entries, entry => Assert.True(entry.Disposed));
        queued.InvokeEvenIfDisposed();
        Assert.Empty(protocol.Sent);
        Assert.Equal(4, audio.Actions.Count(action => action.Kind == "StopAll"));
    }

    [Fact]
    public void NaturalEnd_StopsTransportAndInvalidatesTheCompletedPass()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 50, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var eventTimer = scheduler.Entries[0];
        eventTimer.Invoke();
        var endTimer = scheduler.Entries[1];
        endTimer.Invoke();

        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.Equal(150, vm.PlayheadMs);
        Assert.All(scheduler.Entries, entry => Assert.True(entry.Disposed));
        eventTimer.InvokeEvenIfDisposed();
        Assert.Single(protocol.Sent);
    }

    [Fact]
    public void Dispose_StopsThePassAndInvalidatesItsQueuedCallbacks()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0xFFFF, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var queued = scheduler.Entries[0];
        vm.Dispose();

        Assert.False(vm.IsPlaying);
        Assert.True(queued.Disposed);
        queued.InvokeEvenIfDisposed();
        Assert.Empty(protocol.Sent);
        vm.Dispose(); // cleanup is idempotent
    }

    [Fact]
    public void TargetedExecutionReports_UpdateTheGestureTelemetryWithoutBlockingPlayback()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 500;
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var sent = Assert.Single(protocol.Sent);

        Assert.Equal("WRITE", step.ExecutionSummary);
        Assert.True(vm.IsPlaying);

        protocol.RaiseAnimMasterAccepted(sent.RequestId, 0x1234, 2,
            meshSeq: 77, meshQueued: true, localHandled: false);
        Assert.Equal("MASTER", step.ExecutionSummary);
        Assert.Contains("master: accepted", step.ExecutionDetail);

        protocol.RaiseAnimExecution(sent.RequestId, 0x1234, 2, "started");
        Assert.Equal("START", step.ExecutionSummary);
        Assert.Equal("started", step.ExecutionTone);

        protocol.RaiseAnimExecution(sent.RequestId, 0x1234, 2, "completed");
        Assert.Equal("DONE", step.ExecutionSummary);
        Assert.Equal("completed", step.ExecutionTone);
        Assert.Contains("4660: completed", step.ExecutionDetail);
    }

    [Fact]
    public void BroadcastExecutionReports_AggregateAllOnlineDroidsAndSurfaceRejection()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[3] = 500;
        foreach (var id in new ushort[] { 100, 200, 300 })
            protocol.Droids.Add(new Droid { Id = id, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { StartMs = 20, Target = ushort.MaxValue, AnimId = 3 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var requestId = Assert.Single(protocol.Sent).RequestId;
        Assert.Equal("WRITE", step.ExecutionSummary);

        protocol.RaiseAnimMasterAccepted(requestId, ushort.MaxValue, 3,
            meshSeq: 77, meshQueued: true, localHandled: true);
        Assert.Equal("MASTER", step.ExecutionSummary);

        protocol.RaiseAnimExecution(requestId, 100, 3, "started");
        protocol.RaiseAnimExecution(requestId, 200, 3, "started");
        Assert.Equal("ACK 2/3", step.ExecutionSummary);

        protocol.RaiseAnimExecution(requestId, 300, 3, "rejected", "servosOff");
        Assert.Equal("REJ 1/3", step.ExecutionSummary);
        Assert.Equal("rejected", step.ExecutionTone);
        Assert.Contains("300: rejected (servosOff)", step.ExecutionDetail);
        Assert.True(vm.IsPlaying);
    }

    [Theory]
    [InlineData(AnimDispatchState.NotConnected, "NO LINK")]
    [InlineData(AnimDispatchState.HandshakePending, "NOT READY")]
    [InlineData(AnimDispatchState.WriteFailed, "WRITE FAIL")]
    public void LocalDispatchFailure_IsImmediateAndDoesNotArmExecutionTimeouts(
        AnimDispatchState state, string expectedSummary)
    {
        var protocol = new FakeSequencerProtocol { NextDispatchState = state };
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        var step = new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();

        Assert.Equal(expectedSummary, step.ExecutionSummary);
        Assert.Equal("rejected", step.ExecutionTone);
        Assert.Contains("serial dispatch failed", step.ExecutionDetail);
        Assert.Empty(executionScheduler.Entries);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void MismatchedMasterReceipt_CannotClaimAcceptanceForAnotherCommand()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var requestId = Assert.Single(protocol.Sent).RequestId;

        protocol.RaiseAnimMasterAccepted(requestId, 0x9999, 2);
        protocol.RaiseAnimMasterAccepted(requestId, 0x1234, 9);
        Assert.Equal("WRITE", step.ExecutionSummary);

        protocol.RaiseAnimMasterAccepted(requestId, 0x1234, 2,
            meshSeq: 91, meshQueued: false, localHandled: false);
        Assert.Equal("MESH FAIL", step.ExecutionSummary);
        Assert.Equal("rejected", step.ExecutionTone);
        Assert.Contains("mesh queue failed", step.ExecutionDetail);
    }

    [Fact]
    public void MissingTargetReport_ExpiresAsUnconfirmedWithoutStoppingPlayback()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 500;
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock, executionScheduler: executionScheduler);
        var step = new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        Assert.Equal(2, executionScheduler.Entries.Count);
        Assert.Equal(1500, executionScheduler.Entries[0].DueTimeMs);

        executionScheduler.Entries[0].Invoke();

        Assert.Equal("UNCONF", step.ExecutionSummary);
        Assert.Equal("timeout", step.ExecutionTone);
        Assert.Contains("4660: no start report", step.ExecutionDetail);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void PartialBroadcastTimeout_RecoversWhenTheMissingReportArrivesLate()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[3] = 500;
        foreach (var id in new ushort[] { 100, 200, 300 })
            protocol.Droids.Add(new Droid { Id = id, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        var step = new SequenceStep { StartMs = 20, Target = ushort.MaxValue, AnimId = 3 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var requestId = Assert.Single(protocol.Sent).RequestId;
        protocol.RaiseAnimExecution(requestId, 100, 3, "started");
        protocol.RaiseAnimExecution(requestId, 200, 3, "started");

        executionScheduler.Entries[0].Invoke();
        Assert.Equal("MISS 1/3", step.ExecutionSummary);
        Assert.Contains("300: no start report", step.ExecutionDetail);

        protocol.RaiseAnimExecution(requestId, 300, 3, "started");
        Assert.Equal("ACK 3/3", step.ExecutionSummary);
        Assert.Equal("started", step.ExecutionTone);
        Assert.DoesNotContain("no start report", step.ExecutionDetail);
    }

    [Fact]
    public void StartedGesture_ExpiresAtCompletionDeadlineAndRecoversFromLateCompletion()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 500;
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        var step = new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var requestId = Assert.Single(protocol.Sent).RequestId;
        protocol.RaiseAnimExecution(requestId, 0x1234, 2, "started");
        Assert.Equal(2000, executionScheduler.Entries[1].DueTimeMs);

        executionScheduler.Entries[1].Invoke();
        Assert.Equal("TIMEOUT", step.ExecutionSummary);
        Assert.Contains("4660: completion timeout", step.ExecutionDetail);

        protocol.RaiseAnimExecution(requestId, 0x1234, 2, "completed");
        Assert.Equal("DONE", step.ExecutionSummary);
        Assert.Equal("completed", step.ExecutionTone);
        Assert.DoesNotContain("completion timeout", step.ExecutionDetail);
    }

    [Fact]
    public void LoopingGesture_OnlyRequiresStartAndDelayedStartCannotRegressCompletion()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[17] = 4000;
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        var step = new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 };
        vm.Steps.Add(step);

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var requestId = Assert.Single(protocol.Sent).RequestId;
        Assert.Single(executionScheduler.Entries);

        protocol.RaiseAnimExecution(requestId, 0x1234, 17, "started");
        executionScheduler.Entries[0].Invoke();
        Assert.Equal("START", step.ExecutionSummary);

        protocol.RaiseAnimExecution(requestId, 0x1234, 17, "interrupted");
        protocol.RaiseAnimExecution(requestId, 0x1234, 17, "started");
        Assert.Equal("STOP", step.ExecutionSummary);
        Assert.Equal("interrupted", step.ExecutionTone);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void Stop_SendsTargetedIdleToAnActiveInfiniteGestureExactlyOnce(int infiniteAnimId)
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[infiniteAnimId] = 4000;
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = infiniteAnimId });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        vm.StopCommand.Execute(null);
        vm.StopCommand.Execute(null);

        Assert.Collection(protocol.Sent,
            sent => { Assert.Equal((ushort)0x1234, sent.Target); Assert.Equal(infiniteAnimId, sent.AnimId); },
            sent => { Assert.Equal((ushort)0x1234, sent.Target); Assert.Equal(0, sent.AnimId); });
    }

    [Fact]
    public void BroadcastInfinite_WithPerDroidFiniteOverride_CleansOnlyRemainingTargets()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[17] = 4000;
        protocol.Durations[2] = 500;
        foreach (var id in new ushort[] { 100, 200, 300 })
            protocol.Droids.Add(new Droid { Id = id, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = ushort.MaxValue, AnimId = 17 });
        vm.Steps.Add(new SequenceStep { StartMs = 40, Target = 200, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        scheduler.Entries[1].Invoke();
        vm.StopCommand.Execute(null);

        Assert.Equal(4, protocol.Sent.Count);
        Assert.Equal((ushort)ushort.MaxValue, protocol.Sent[0].Target);
        Assert.Equal((ushort)200, protocol.Sent[1].Target);
        Assert.Equal(new ushort[] { 100, 300 }, protocol.Sent.Skip(2).Select(s => s.Target));
        Assert.All(protocol.Sent.Skip(2), sent => Assert.Equal(0, sent.AnimId));
    }

    [Fact]
    public void RepeatedInfiniteGestures_OnTheSameDroid_RequireOneCleanup()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[16] = 3000;
        protocol.Durations[17] = 4000;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 16 });
        vm.Steps.Add(new SequenceStep { StartMs = 40, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        scheduler.Entries[1].Invoke();
        vm.StopCommand.Execute(null);

        Assert.Equal(new[] { 16, 17, 0 }, protocol.Sent.Select(s => s.AnimId));
        Assert.All(protocol.Sent, sent => Assert.Equal((ushort)0x1234, sent.Target));
    }

    [Fact]
    public void FailedInfiniteDispatch_DoesNotCreateAFalseCleanupTarget()
    {
        var protocol = new FakeSequencerProtocol { NextDispatchState = AnimDispatchState.WriteFailed };
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        vm.StopCommand.Execute(null);

        Assert.Single(protocol.Sent);
        Assert.Equal(17, protocol.Sent[0].AnimId);
    }

    [Fact]
    public void FailedIdleCleanup_RemainsRetryableOnARepeatedStop()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        protocol.NextDispatchState = AnimDispatchState.WriteFailed;
        vm.StopCommand.Execute(null);
        protocol.NextDispatchState = AnimDispatchState.Written;
        vm.StopCommand.Execute(null);

        Assert.Equal(new[] { 17, 0, 0 }, protocol.Sent.Select(s => s.AnimId));
    }

    [Fact]
    public void NaturalEnd_CleansAnInfiniteGesture()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[17] = 4000;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        scheduler.Entries[1].Invoke();

        Assert.Equal(new[] { 17, 0 }, protocol.Sent.Select(s => s.AnimId));
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void ExplicitInfiniteEnd_UsesPersistedLengthAndDoesNotStopAReplacementGesture()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 500;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep
        {
            StartMs = 20,
            Target = 0x1234,
            AnimId = 17,
            EndAfterMs = 350,
        });
        vm.Steps.Add(new SequenceStep { StartMs = 370, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        Assert.Equal(20, scheduler.Entries[0].DueTimeMs);
        scheduler.Entries[0].Invoke();
        Assert.Equal(350, scheduler.Entries[1].DueTimeMs);
        scheduler.Entries[1].Invoke();

        // The finite replacement shares the explicit endpoint. Editor order dispatches it
        // first; ownership checking then suppresses the stale TALK termination.
        Assert.Equal(new[] { 17, 2 }, protocol.Sent.Select(item => item.AnimId));
    }

    [Fact]
    public void LoopBoundary_EndsInfiniteGestureBeforeStartingTheNextPass()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[17] = 4000;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Loop = true;
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        scheduler.Entries[1].Invoke();
        Assert.Equal(new[] { 17, 0 }, protocol.Sent.Select(sent => sent.AnimId));
        Assert.True(vm.IsPlaying);

        vm.StopCommand.Execute(null);
        Assert.Equal(new[] { 17, 0 }, protocol.Sent.Select(s => s.AnimId));
    }

    [Fact]
    public void MeshFailureOnFiniteOverride_RestoresThePreviousInfiniteStateForCleanup()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 500;
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });
        vm.Steps.Add(new SequenceStep { StartMs = 40, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var infiniteRequest = protocol.Sent[0].RequestId;
        protocol.RaiseAnimMasterAccepted(infiniteRequest, 0x1234, 17,
            meshSeq: 76, leaseMs: 5000);
        var infiniteRenewal = executionScheduler.Entries[1];
        scheduler.Entries[1].Invoke();
        var finiteRequest = protocol.Sent[1].RequestId;
        protocol.RaiseAnimMasterAccepted(finiteRequest, 0x1234, 2,
            meshQueued: false, localHandled: false);
        infiniteRenewal.Invoke();
        vm.StopCommand.Execute(null);

        Assert.Equal(new[] { 17, 2, 0 }, protocol.Sent.Select(s => s.AnimId));
        Assert.Equal((ushort)0x1234, protocol.Sent[^1].Target);
        Assert.Equal(new SentLeaseRenewal(0x1234, 76, 5000),
            Assert.Single(protocol.LeaseRenewals));
    }

    [Fact]
    public void Restart_CleansTheOldInfiniteGestureBeforeArmingTheNewPass()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[17] = 4000;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        vm.RestartCommand.Execute(null);

        Assert.Equal(new[] { 17, 0 }, protocol.Sent.Select(s => s.AnimId));
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void Dispose_CleansAnActiveInfiniteGesture()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 16 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        vm.Dispose();

        Assert.Equal(new[] { 16, 0 }, protocol.Sent.Select(s => s.AnimId));
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void LinkLossCleanupFailure_RemainsRetryableAfterTransportStops()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        protocol.NextDispatchState = AnimDispatchState.NotConnected;
        protocol.RaiseLinkClosed();
        Assert.False(vm.IsPlaying);
        Assert.Equal(new[] { 17, 0 }, protocol.Sent.Select(s => s.AnimId));

        protocol.NextDispatchState = AnimDispatchState.Written;
        vm.StopCommand.Execute(null);
        Assert.Equal(new[] { 17, 0, 0 }, protocol.Sent.Select(s => s.AnimId));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void InfiniteGesture_UsesFiveSecondLeaseAndRenewsEveryTwoSeconds(int animId)
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = animId });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var sent = Assert.Single(protocol.Sent);
        Assert.Equal((ushort)5000, sent.LeaseMs);

        protocol.RaiseAnimMasterAccepted(sent.RequestId, sent.Target, sent.AnimId,
            meshSeq: 91, meshQueued: true, leaseMs: 5000);
        var firstRenewal = executionScheduler.Entries[1];
        Assert.Equal(2000, firstRenewal.DueTimeMs);
        firstRenewal.Invoke();

        var renewal = Assert.Single(protocol.LeaseRenewals);
        Assert.Equal(new SentLeaseRenewal(0x1234, 91, 5000), renewal);
        Assert.Equal(2000, executionScheduler.Entries[2].DueTimeMs);
    }

    [Fact]
    public void FiniteGestureAndUnsupportedFirmware_DoNotUseAnimationLeases()
    {
        var protocol = new FakeSequencerProtocol { SupportsAnimLease = false };
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });
        vm.Steps.Add(new SequenceStep { StartMs = 40, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        scheduler.Entries[1].Invoke();

        Assert.All(protocol.Sent, sent => Assert.Equal((ushort)0, sent.LeaseMs));
        Assert.Empty(protocol.LeaseRenewals);
    }

    [Fact]
    public void PauseKeepsLeaseButExplicitLoopBoundaryTerminatesIt()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[17] = 4000;
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        vm.Loop = true;
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var sent = Assert.Single(protocol.Sent);
        protocol.RaiseAnimMasterAccepted(sent.RequestId, sent.Target, sent.AnimId,
            meshSeq: 92, leaseMs: 5000);
        var renewalTimer = executionScheduler.Entries[1];

        vm.PauseCommand.Execute(null);
        Assert.False(renewalTimer.Disposed);
        vm.PlayCommand.Execute(null);
        scheduler.Entries[2].Invoke();
        Assert.True(renewalTimer.Disposed);
        Assert.Equal(new[] { 17, 0 }, protocol.Sent.Select(item => item.AnimId));
        Assert.Empty(protocol.LeaseRenewals);
    }

    [Fact]
    public void StopCancelsLeaseBeforeCleanupAndQueuedCallbackCannotRenewIt()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var sent = Assert.Single(protocol.Sent);
        protocol.RaiseAnimMasterAccepted(sent.RequestId, sent.Target, sent.AnimId,
            meshSeq: 93, leaseMs: 5000);
        var renewalTimer = executionScheduler.Entries[1];

        vm.StopCommand.Execute(null);
        Assert.True(renewalTimer.Disposed);
        renewalTimer.InvokeEvenIfDisposed();

        Assert.Empty(protocol.LeaseRenewals);
        Assert.Equal(new[] { 17, 0 }, protocol.Sent.Select(item => item.AnimId));
    }

    [Fact]
    public void FailedMeshAcceptanceDoesNotArmLeaseRenewal()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 16 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var sent = Assert.Single(protocol.Sent);
        protocol.RaiseAnimMasterAccepted(sent.RequestId, sent.Target, sent.AnimId,
            meshSeq: 94, meshQueued: false, localHandled: false, leaseMs: 5000);

        Assert.Single(executionScheduler.Entries); // execution START timeout only
        Assert.Empty(protocol.LeaseRenewals);
    }

    [Fact]
    public void SafeStopCancelsTheShowAndUsesFleetSafeHoldWithoutTargetedIdle()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Droids.Add(new Droid { Id = 0x1234, Online = true });
        var scheduler = new FakePlaybackTimerScheduler();
        var executionScheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock, executionScheduler: executionScheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 17 });

        vm.PlayCommand.Execute(null);
        scheduler.Entries[0].Invoke();
        var sent = Assert.Single(protocol.Sent);
        protocol.RaiseAnimMasterAccepted(sent.RequestId, sent.Target, sent.AnimId,
            meshSeq: 95, leaseMs: 5000);
        var renewalTimer = executionScheduler.Entries[1];

        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        vm.SafeStopCommand.Execute(null);
        scheduler.Entries[1].InvokeEvenIfDisposed();
        renewalTimer.InvokeEvenIfDisposed();

        Assert.Equal((ushort)ushort.MaxValue, Assert.Single(protocol.SafeStops));
        Assert.Single(protocol.Sent);
        Assert.Empty(protocol.LeaseRenewals);
        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.Equal(250, vm.PlayheadMs);
    }

    [Fact]
    public void SafeStopFallsBackToBroadcastIdleOnOlderFirmware()
    {
        var protocol = new FakeSequencerProtocol { SupportsSafeStop = false };
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        vm.SafeStopCommand.Execute(null);

        Assert.Empty(protocol.SafeStops);
        var fallback = Assert.Single(protocol.Sent);
        Assert.Equal((ushort)ushort.MaxValue, fallback.Target);
        Assert.Equal(0, fallback.AnimId);
        Assert.Equal((ushort)0, fallback.LeaseMs);
    }

    [Fact]
    public void EmergencyStopCancelsQueuedEventsAndDisablesFleetServosImmediately()
    {
        var protocol = new FakeSequencerProtocol();
        var scheduler = new FakePlaybackTimerScheduler();
        var clock = new FakePlaybackClock();
        using var vm = CreateViewModel(protocol, scheduler, clock);
        vm.Steps.Add(new SequenceStep { StartMs = 20, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var queuedGesture = scheduler.Entries[0];
        clock.SetElapsed(TimeSpan.FromMilliseconds(250));
        vm.EmergencyStopCommand.Execute(null);
        queuedGesture.InvokeEvenIfDisposed();

        Assert.Empty(protocol.Sent);
        Assert.Equal(new SentServoCommand(ushort.MaxValue, false),
            Assert.Single(protocol.ServoCommands));
        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.Equal(250, vm.PlayheadMs);
    }

    private static SequencerViewModel CreateViewModel(
        FakeSequencerProtocol protocol,
        FakePlaybackTimerScheduler scheduler,
        IPlaybackClock? clock = null,
        FakeAudioPlayer? audio = null,
        FakePlaybackTimerScheduler? executionScheduler = null) =>
        new(protocol, new FakeSequencerSettings(), audio ?? new FakeAudioPlayer(), scheduler, clock,
            executionScheduler ?? new FakePlaybackTimerScheduler(),
            library: new FakeSequenceLibraryService(),
            preflightService: new PermissiveSequencerPreflightService());

}
