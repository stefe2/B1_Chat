using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public sealed class SequencerPlaybackIntegrationTests
{
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
    public void Restart_RejectsQueuedCallbackFromThePreviousGeneration()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 700;
        var scheduler = new FakePlaybackTimerScheduler();
        var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 100, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var staleEvent = scheduler.Entries[0];

        vm.PlayCommand.Execute(null);
        var currentEvent = scheduler.Entries[2]; // event + end timer were captured by pass one

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
    public void RapidPlayRestarts_OnlyNewestGenerationCanDispatch()
    {
        var protocol = new FakeSequencerProtocol();
        protocol.Durations[2] = 100;
        var scheduler = new FakePlaybackTimerScheduler();
        using var vm = CreateViewModel(protocol, scheduler);
        vm.Steps.Add(new SequenceStep { StartMs = 50, Target = 0x1234, AnimId = 2 });

        vm.PlayCommand.Execute(null);
        var first = scheduler.Entries[0];
        vm.PlayCommand.Execute(null);
        var second = scheduler.Entries[2];
        vm.PlayCommand.Execute(null);
        var third = scheduler.Entries[4];

        first.InvokeEvenIfDisposed();
        second.InvokeEvenIfDisposed();
        third.Invoke();

        Assert.Equal(6, scheduler.Entries.Count); // event + natural-end timer per pass
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
        var staleEnd = scheduler.Entries[1];
        vm.PlayCommand.Execute(null);
        var currentEnd = scheduler.Entries[3];

        staleEnd.InvokeEvenIfDisposed();
        Assert.Equal(4, scheduler.Entries.Count);
        Assert.True(vm.IsPlaying);

        currentEnd.Invoke();
        Assert.Equal(6, scheduler.Entries.Count); // the current pass alone rearms the loop
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
        var scheduler = new FakePlaybackTimerScheduler();
        var vm = CreateViewModel(protocol, scheduler);
        var step = new SequenceStep { StartMs = 1_000, Target = 0xFFFF, AnimId = 2 };
        vm.Steps.Add(step);
        vm.SelectedStep = step;
        var sourceLane = vm.AudioLanes[0];
        var targetLane = vm.AudioLanes[1];
        var audio = new AudioClip { StartMs = 500, DurationMs = 100, FilePath = "fixture.wav" };
        sourceLane.Clips.Add(audio);

        Assert.True(vm.CanEditSequence);
        Assert.True(vm.InsertGestureCommand.CanExecute(1));
        vm.PlayCommand.Execute(null);

        Assert.False(vm.CanEditSequence);
        Assert.False(vm.InsertGestureCommand.CanExecute(1));
        Assert.False(vm.NudgeStartForwardCommand.CanExecute(null));
        Assert.False(vm.NudgeStartBackwardCommand.CanExecute(null));
        Assert.False(vm.AddAudioLaneCommand.CanExecute(null));
        Assert.False(vm.DeleteAudioLaneCommand.CanExecute(sourceLane));
        Assert.False(vm.AddAudioClipCommand.CanExecute(sourceLane));
        Assert.False(vm.ReplaceAudioClipCommand.CanExecute(audio));
        Assert.False(vm.DeleteAudioClipCommand.CanExecute(audio));
        Assert.False(vm.ClearTimelineCommand.CanExecute(null));
        Assert.False(vm.DeleteStepCommand.CanExecute(step));
        Assert.False(vm.DuplicateStepCommand.CanExecute(step));
        Assert.False(vm.LoadFromLibraryCommand.CanExecute(new SequenceLibraryItem()));
        Assert.False(vm.ImportCommand.CanExecute(null));
        vm.InsertGestureAt(3, vm.Tracks[0], 200);
        vm.MoveAudioClipToLane(audio, targetLane);
        Assert.Single(vm.Steps);
        Assert.Contains(audio, sourceLane.Clips);
        Assert.DoesNotContain(audio, targetLane.Clips);

        vm.PauseCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.False(vm.CanEditSequence);
        Assert.False(vm.DuplicateStepCommand.CanExecute(step));

        vm.StopCommand.Execute(null);
        Assert.True(vm.CanEditSequence);
        Assert.True(vm.InsertGestureCommand.CanExecute(1));
        Assert.True(vm.NudgeStartForwardCommand.CanExecute(null));
        Assert.True(vm.NudgeStartBackwardCommand.CanExecute(null));
        Assert.True(vm.AddAudioLaneCommand.CanExecute(null));
        Assert.True(vm.DeleteAudioLaneCommand.CanExecute(sourceLane));
        Assert.True(vm.AddAudioClipCommand.CanExecute(sourceLane));
        Assert.True(vm.ReplaceAudioClipCommand.CanExecute(audio));
        Assert.True(vm.DeleteAudioClipCommand.CanExecute(audio));
        Assert.True(vm.ClearTimelineCommand.CanExecute(null));
        Assert.True(vm.DeleteStepCommand.CanExecute(step));
        Assert.True(vm.DuplicateStepCommand.CanExecute(step));
        Assert.True(vm.LoadFromLibraryCommand.CanExecute(new SequenceLibraryItem()));
        Assert.True(vm.ImportCommand.CanExecute(null));
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

        vm.PlayCommand.Execute(null); // starts a new pass; it cannot resume the disconnected one
        Assert.Equal(4, scheduler.Entries.Count);
        Assert.Equal(500, scheduler.Entries[2].DueTimeMs);
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
        Assert.Equal(250, scheduler.Entries[2].DueTimeMs);
        Assert.Equal(TimeSpan.Zero, clock.Elapsed);
        vm.StopCommand.Execute(null);
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

        Assert.Equal(1, scheduler.Entries[2].DueTimeMs);
        scheduler.Entries[2].Invoke();
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

        Assert.Equal(0, scheduler.Entries[2].DueTimeMs);
        Assert.Equal(99, scheduler.Entries[3].DueTimeMs);
        scheduler.Entries[2].Invoke();
        Assert.Single(protocol.Sent);
        vm.StopCommand.Execute(null);
    }

    [Fact]
    public void PauseAmongSimultaneousEvents_ResumesOnlyThoseNotYetDispatched()
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
        Assert.Equal(500, scheduler.Entries[1].DueTimeMs);
        scheduler.Entries[0].Invoke(); // gesture dispatched; audio timer has not run yet
        clock.SetElapsed(TimeSpan.FromMilliseconds(500));
        vm.PauseCommand.Execute(null);
        vm.PlayCommand.Execute(null);

        Assert.Equal(0, scheduler.Entries[3].DueTimeMs);
        Assert.Equal(100, scheduler.Entries[4].DueTimeMs);
        scheduler.Entries[3].Invoke();
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
        Assert.Equal(650, scheduler.Entries[2].DueTimeMs);

        clock.SetElapsed(TimeSpan.FromMilliseconds(150));
        vm.PauseCommand.Execute(null);
        Assert.Equal(250, vm.PlayheadMs);
        vm.PlayCommand.Execute(null);

        Assert.Equal(500, scheduler.Entries[4].DueTimeMs);
        Assert.Equal(600, scheduler.Entries[5].DueTimeMs);
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
        var endTimer = scheduler.Entries[1];
        eventTimer.Invoke();
        endTimer.Invoke();

        Assert.False(vm.IsPlaying);
        Assert.False(vm.IsPaused);
        Assert.Equal(0, vm.PlayheadMs);
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
        using var vm = CreateViewModel(protocol, scheduler, executionScheduler: executionScheduler);
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
    public void LoopBoundary_KeepsInfiniteStateUntilAnExplicitStop()
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
        Assert.Single(protocol.Sent);
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
        vm.PlayCommand.Execute(null);

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
    public void PauseAndLoopBoundary_KeepAnAcceptedLeaseAlive()
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
        Assert.False(renewalTimer.Disposed);

        renewalTimer.Invoke();
        Assert.Equal(new SentLeaseRenewal(0x1234, 92, 5000),
            Assert.Single(protocol.LeaseRenewals));
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

    private static SequencerViewModel CreateViewModel(
        FakeSequencerProtocol protocol,
        FakePlaybackTimerScheduler scheduler,
        IPlaybackClock? clock = null,
        FakeAudioPlayer? audio = null,
        FakePlaybackTimerScheduler? executionScheduler = null) =>
        new(protocol, new SettingsService(), audio ?? new FakeAudioPlayer(), scheduler, clock,
            executionScheduler ?? new FakePlaybackTimerScheduler());

}
