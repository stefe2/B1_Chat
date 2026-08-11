using b1_chat_console.Services;

namespace b1_chat_console.Tests;

public sealed class PlaybackGenerationTests
{
    [Fact]
    public void Begin_InvalidatesThePreviousGeneration()
    {
        var generations = new PlaybackGeneration();

        var first = generations.Begin();
        var second = generations.Begin();

        Assert.False(generations.IsCurrent(first));
        Assert.True(generations.IsCurrent(second));
    }

    [Fact]
    public void Cancel_InvalidatesTheCurrentGeneration()
    {
        var generations = new PlaybackGeneration();
        var current = generations.Begin();

        generations.Cancel();

        Assert.False(generations.IsCurrent(current));
        Assert.False(generations.IsCurrent(0));
    }
}
