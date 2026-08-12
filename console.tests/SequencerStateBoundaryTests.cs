using System.Reflection;
using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.Tests;

public sealed class SequencerStateBoundaryTests
{
    [Fact]
    public void DocumentSnapshot_ExposesOnlyPersistentSequenceFields()
    {
        var publicProperties = typeof(SequenceSnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name);

        Assert.Equal(
            new[] { "AudioLanes", "Loop", "Name", "Steps" },
            publicProperties);
    }

    [Fact]
    public void DocumentSnapshot_UsesStructuralEqualityAcrossFreshDtoGraphs()
    {
        var first = CreateSnapshot();
        var second = CreateSnapshot();

        Assert.NotSame(first.Steps, second.Steps);
        Assert.NotSame(first.AudioLanes, second.AudioLanes);
        Assert.NotSame(first.AudioLanes[0].Clips, second.AudioLanes[0].Clips);
        Assert.True(first.DocumentEquals(second));
        Assert.False(first.DocumentEquals(null));
    }

    [Fact]
    public void DocumentSnapshot_ComparisonCoversEveryPersistentField()
    {
        var mutations = new Action<SequenceSnapshot>[]
        {
            snapshot => snapshot.Steps[0].AnimId++,
            snapshot => snapshot.Steps[0].Target++,
            snapshot => snapshot.Steps[0].StartMs++,
            snapshot => snapshot.AudioLanes[0].Label += " changed",
            snapshot => snapshot.AudioLanes[0].Clips[0].FilePath += ".new",
            snapshot => snapshot.AudioLanes[0].Clips[0].DurationMs++,
            snapshot => snapshot.AudioLanes[0].Clips[0].StartMs++,
            snapshot => snapshot.AudioLanes[0].Clips[0].Loop = false,
            snapshot => snapshot.Steps.Add(new SequenceStepDto()),
            snapshot => snapshot.AudioLanes.Add(new AudioLaneDto()),
            snapshot => snapshot.AudioLanes[0].Clips.Add(new AudioClipDto()),
        };

        Assert.False(CreateSnapshot().DocumentEquals(CreateSnapshot() with { Name = "Changed" }));
        Assert.False(CreateSnapshot().DocumentEquals(CreateSnapshot() with { Loop = false }));
        foreach (var mutate in mutations)
        {
            var baseline = CreateSnapshot();
            var changed = CreateSnapshot();
            mutate(changed);
            Assert.False(baseline.DocumentEquals(changed));
        }
    }

    [Fact]
    public void EditHistory_OwnsTransactionsCancellationAndRedoInvalidation()
    {
        var history = new SequencerEditHistory();
        var original = CreateSnapshot("Original");
        var edited = CreateSnapshot("Edited");

        Assert.True(history.Begin(original, dirty: false));
        Assert.False(history.Begin(original, dirty: false));
        Assert.True(history.Commit(edited));
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        Assert.Equal(original, history.Undo(edited));
        Assert.True(history.CanRedo);

        Assert.True(history.Begin(original, dirty: true));
        var cancelled = history.Cancel(edited);
        Assert.NotNull(cancelled);
        Assert.True(cancelled.Value.DocumentChanged);
        Assert.True(cancelled.Value.WasDirty);
        Assert.Equal(original, cancelled.Value.Snapshot);
        Assert.True(history.CanRedo); // cancellation never changes history

        Assert.True(history.Begin(original, dirty: true));
        Assert.True(history.Commit(edited));
        Assert.False(history.CanRedo); // a real branch invalidates the old future
    }

    [Fact]
    public void EditHistory_IgnoresNoOpAndBoundsBothDirections()
    {
        var history = new SequencerEditHistory(capacity: 2);
        var zero = CreateSnapshot("0");
        var one = CreateSnapshot("1");
        var two = CreateSnapshot("2");
        var three = CreateSnapshot("3");

        Assert.True(history.Begin(zero, dirty: false));
        Assert.False(history.Commit(CreateSnapshot("0")));
        Assert.Equal(0, history.UndoCount);

        Commit(history, zero, one);
        Commit(history, one, two);
        Commit(history, two, three);
        Assert.Equal(2, history.UndoCount);

        Assert.Equal(two, history.Undo(three));
        Assert.Equal(one, history.Undo(two));
        Assert.Null(history.Undo(one));
        Assert.Equal(2, history.RedoCount);

        Assert.Equal(two, history.Redo(one));
        Assert.Equal(three, history.Redo(two));
        Assert.Null(history.Redo(three));
        Assert.Equal(2, history.UndoCount);
    }

    [Fact]
    public void PlaybackPlan_PublishesReadOnlyRuntimeState()
    {
        var writableProperties = typeof(SequencerPlaybackPlan)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod != null);

        Assert.Empty(writableProperties);
    }

    private static void Commit(
        SequencerEditHistory history,
        SequenceSnapshot before,
        SequenceSnapshot after)
    {
        Assert.True(history.Begin(before, dirty: true));
        Assert.True(history.Commit(after));
    }

    private static SequenceSnapshot CreateSnapshot(string name = "Scene") => new(
        name,
        Loop: true,
        AudioLanes: new List<AudioLaneDto>
        {
            new()
            {
                Label = "VOICE",
                Clips = new List<AudioClipDto>
                {
                    new()
                    {
                        FilePath = "voice.wav",
                        DurationMs = 1_000,
                        StartMs = 250,
                        Loop = true,
                    },
                },
            },
        },
        Steps: new List<SequenceStepDto>
        {
            new() { AnimId = 2, Target = 0x1234, StartMs = 500 },
        });
}
