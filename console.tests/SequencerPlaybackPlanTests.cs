using b1_chat_console.Models;

namespace b1_chat_console.Tests;

public sealed class SequencerPlaybackPlanTests
{
    [Fact]
    public void Capture_EmptyDocumentProducesAnEmptyZeroLengthPlan()
    {
        var plan = new SequencerDocumentBuilder().Capture(loop: true);

        Assert.Empty(plan.Events);
        Assert.Equal(0, plan.TotalDurationMs);
        Assert.True(plan.Loop);
    }

    [Fact]
    public void Capture_AudioOnlyDocumentPreservesPlaybackValuesAndTail()
    {
        var plan = new SequencerDocumentBuilder()
            .WithAudio(250, 1_750, @"C:\fixtures\voice.wav", loop: true)
            .Capture();

        var audio = Assert.IsType<AudioPlaybackEvent>(Assert.Single(plan.Events));
        Assert.Equal(250, audio.StartMs);
        Assert.Equal(1_750, audio.DurationMs);
        Assert.Equal(@"C:\fixtures\voice.wav", audio.FilePath);
        Assert.True(audio.Loop);
        Assert.Equal(2_000, plan.TotalDurationMs);
    }

    [Fact]
    public void Capture_GestureOnlyDocumentPreservesTargetAnimationSeedAndTail()
    {
        var plan = new SequencerDocumentBuilder()
            .WithGesture(125, 0x1234, 6, durationMs: 875)
            .Capture(seed: 0xCAFE);

        var gesture = Assert.IsType<GesturePlaybackEvent>(Assert.Single(plan.Events));
        Assert.Equal(125, gesture.StartMs);
        Assert.Equal((ushort)0x1234, gesture.Target);
        Assert.Equal(6, gesture.AnimId);
        Assert.Equal((uint)0xCAFE, gesture.Seed);
        Assert.Equal(875, gesture.DurationMs);
        Assert.Equal(1_000, plan.TotalDurationMs);
    }

    [Fact]
    public void Capture_DoesNotRetainMutableEditorState()
    {
        var step = new SequenceStep { StartMs = 400, Target = 0x1234, AnimId = 2 };
        var clip = new AudioClip
        {
            StartMs = 200,
            DurationMs = 900,
            FilePath = @"C:\audio\original.wav",
            Loop = true,
        };
        var lane = new AudioLane();
        lane.Clips.Add(clip);

        var plan = SequencerPlaybackPlan.Capture(
            new[] { step }, new[] { lane }, new Dictionary<int, int> { [2] = 700 },
            loop: true, nextSeed: () => 42);

        step.StartMs = 9_000;
        step.Target = 0xFFFF;
        step.AnimId = 17;
        clip.StartMs = 8_000;
        clip.DurationMs = 4_000;
        clip.FilePath = @"C:\audio\replacement.wav";
        clip.Loop = false;
        lane.Clips.Clear();

        var audio = Assert.IsType<AudioPlaybackEvent>(plan.Events[0]);
        Assert.Equal(200, audio.StartMs);
        Assert.Equal(900, audio.DurationMs);
        Assert.Equal(@"C:\audio\original.wav", audio.FilePath);
        Assert.True(audio.Loop);

        var gesture = Assert.IsType<GesturePlaybackEvent>(plan.Events[1]);
        Assert.Equal(400, gesture.StartMs);
        Assert.Equal((ushort)0x1234, gesture.Target);
        Assert.Equal(2, gesture.AnimId);
        Assert.Equal((uint)42, gesture.Seed);
        Assert.Equal(700, gesture.DurationMs);
        Assert.True(plan.Loop);
        Assert.Equal(1_100, plan.TotalDurationMs);
    }

    [Fact]
    public void Capture_SortsByStartThenStableSourceOrder()
    {
        var steps = new[]
        {
            new SequenceStep { StartMs = 500, AnimId = 1 },
            new SequenceStep { StartMs = 100, AnimId = 2 },
            new SequenceStep { StartMs = 100, AnimId = 3 },
        };

        var plan = SequencerPlaybackPlan.Capture(
            steps, Array.Empty<AudioLane>(), new Dictionary<int, int>(), loop: false,
            nextSeed: () => 1);

        Assert.Equal(new[] { 2, 3, 1 },
            plan.Events.Cast<GesturePlaybackEvent>().Select(e => e.AnimId));
    }

    [Fact]
    public void Capture_SameTimestampKeepsStableOrderAcrossGestureAndAudioSources()
    {
        var document = new SequencerDocumentBuilder()
            .WithGesture(500, 0x1001, 2)
            .WithGesture(500, 0x1002, 3)
            .WithAudio(500, 100, @"C:\fixtures\first.wav", laneName: "VOICE")
            .WithAudio(500, 100, @"C:\fixtures\second.wav", laneName: "AMBIENT");

        var plan = document.Capture();

        Assert.Collection(plan.Events,
            e => Assert.Equal(2, Assert.IsType<GesturePlaybackEvent>(e).AnimId),
            e => Assert.Equal(3, Assert.IsType<GesturePlaybackEvent>(e).AnimId),
            e => Assert.Equal(@"C:\fixtures\first.wav", Assert.IsType<AudioPlaybackEvent>(e).FilePath),
            e => Assert.Equal(@"C:\fixtures\second.wav", Assert.IsType<AudioPlaybackEvent>(e).FilePath));
        Assert.Equal(new[] { 0, 1, 2, 3 }, plan.Events.Select(e => e.SourceOrder));
        var batch = Assert.Single(plan.Batches);
        Assert.Equal(500, batch.StartMs);
        Assert.Equal(plan.Events, batch.Events);
    }

    [Fact]
    public void Capture_WarnsAboutSameTargetAndBroadcastTargetAmbiguity()
    {
        var steps = new[]
        {
            new SequenceStep { StartMs = 500, Target = ushort.MaxValue, AnimId = 1 },
            new SequenceStep { StartMs = 500, Target = ushort.MaxValue, AnimId = 2 },
            new SequenceStep { StartMs = 500, Target = 0x1234, AnimId = 3 },
            new SequenceStep { StartMs = 500, Target = 0x1234, AnimId = 4 },
        };

        var plan = SequencerPlaybackPlan.Capture(
            steps, Array.Empty<AudioLane>(), new Dictionary<int, int>(), false, () => 1);

        Assert.Equal(3, plan.Warnings.Count);
        Assert.Equal(2, plan.Warnings.Count(warning =>
            warning.Code == SequencerScheduleWarningCode.MultipleGesturesForTarget));
        Assert.Single(plan.Warnings, warning =>
            warning.Code == SequencerScheduleWarningCode.BroadcastTargetOverlap);
        Assert.All(plan.Warnings, warning => Assert.Equal(500, warning.StartMs));
    }

    [Fact]
    public void Capture_UsesSharedFallbackAndClampsNegativeEditorValues()
    {
        var step = new SequenceStep { StartMs = -100, AnimId = 7 };
        var plan = SequencerPlaybackPlan.Capture(
            new[] { step }, Array.Empty<AudioLane>(), new Dictionary<int, int>(), false,
            () => 1);

        var gesture = Assert.IsType<GesturePlaybackEvent>(Assert.Single(plan.Events));
        Assert.Equal(0, gesture.StartMs);
        Assert.Equal(SequencerPlaybackPlan.DefaultGestureDurationMs, gesture.DurationMs);
        Assert.Equal(SequencerPlaybackPlan.DefaultGestureDurationMs, plan.TotalDurationMs);
    }

    [Fact]
    public void Capture_UsesFallbackForMissingOrNegativeGestureDurationButPreservesZero()
    {
        var steps = new[]
        {
            new SequenceStep { StartMs = 0, AnimId = 1 },
            new SequenceStep { StartMs = 0, AnimId = 2 },
            new SequenceStep { StartMs = 0, AnimId = 3 },
        };
        var durations = new Dictionary<int, int> { [2] = -1, [3] = 0 };

        var plan = SequencerPlaybackPlan.Capture(
            steps, Array.Empty<AudioLane>(), durations, false, () => 1);
        var gestures = plan.Events.Cast<GesturePlaybackEvent>().ToArray();

        Assert.Equal(SequencerPlaybackPlan.DefaultGestureDurationMs, gestures[0].DurationMs);
        Assert.Equal(SequencerPlaybackPlan.DefaultGestureDurationMs, gestures[1].DurationMs);
        Assert.Equal(0, gestures[2].DurationMs);
    }

    [Fact]
    public void Capture_ClampsNegativeAudioValuesAndSaturatesOverflowingTail()
    {
        var lane = new AudioLane();
        lane.Clips.Add(new AudioClip { StartMs = -5, DurationMs = -10, FilePath = "invalid.wav" });
        lane.Clips.Add(new AudioClip
        {
            StartMs = int.MaxValue - 10,
            DurationMs = int.MaxValue,
            FilePath = "long.wav",
        });

        var plan = SequencerPlaybackPlan.Capture(
            Array.Empty<SequenceStep>(), new[] { lane }, new Dictionary<int, int>(), false);

        var invalid = Assert.IsType<AudioPlaybackEvent>(plan.Events[0]);
        Assert.Equal(0, invalid.StartMs);
        Assert.Equal(0, invalid.DurationMs);
        Assert.Equal(int.MaxValue, plan.TotalDurationMs);
    }

    [Fact]
    public void Capture_TalkAndPowerDownUseTheReportedIndicativeDurations()
    {
        const int powerDown = 16;
        const int talk = 17;
        var plan = SequencerPlaybackPlan.Capture(
            new[]
            {
                new SequenceStep { StartMs = 100, AnimId = powerDown },
                new SequenceStep { StartMs = 200, AnimId = talk },
            },
            Array.Empty<AudioLane>(),
            new Dictionary<int, int> { [powerDown] = 3_000, [talk] = 4_000 },
            false,
            () => 1);

        var gestures = plan.Events.Cast<GesturePlaybackEvent>().ToArray();
        Assert.Equal((powerDown, 3_000), (gestures[0].AnimId, gestures[0].DurationMs));
        Assert.Equal((talk, 4_000), (gestures[1].AnimId, gestures[1].DurationMs));
        Assert.Equal(4_200, plan.TotalDurationMs);
    }
}
