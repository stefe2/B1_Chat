using b1_chat_console.Models;
using b1_chat_console.Services;

namespace b1_chat_console.Tests;

public sealed class AnimationDurationProviderTests
{
    [Fact]
    public void FiniteDuration_IsFixedAtTheFirmwareNominalValue()
    {
        var metadata = new Dictionary<int, AnimationDurationMetadata>
        {
            [2] = new(2, AnimationDurationKind.Finite, 1_000, 2),
        };
        var provider = new AnimationDurationProvider(
            metadata,
            new Dictionary<int, int>());

        var targeted = provider.Resolve(new SequenceStep { AnimId = 2, Target = 0x1001 });
        Assert.Equal((1_000, 1_000, 1_000), (targeted.MinimumMs, targeted.MaximumMs, targeted.EffectiveMs));

        var broadcast = provider.Resolve(new SequenceStep { AnimId = 2, Target = ushort.MaxValue });
        Assert.Equal((1_000, 1_000, 1_000),
            (broadcast.MinimumMs, broadcast.MaximumMs, broadcast.EffectiveMs));
        Assert.False(broadcast.Provisional);
        Assert.Contains("Fixed nominal duration", broadcast.Detail);
    }

    [Fact]
    public void ImmediateAndInfiniteKindsHaveHonestDistinctTails()
    {
        var provider = new AnimationDurationProvider(
            new Dictionary<int, AnimationDurationMetadata>
            {
                [0] = new(0, AnimationDurationKind.Immediate, 0, 0, SettleMs: 600),
                [17] = new(17, AnimationDurationKind.Infinite, 300, 2),
            },
            new Dictionary<int, int>());

        var idle = provider.Resolve(new SequenceStep { AnimId = 0 });
        Assert.Equal(0, idle.EffectiveMs);
        Assert.Contains("centering", idle.Detail);

        var talk = provider.Resolve(new SequenceStep { AnimId = 17, EndAfterMs = 3_500 });
        Assert.Equal(3_500, talk.EffectiveMs);
        Assert.Contains("IDLE after 3.50 s", talk.Detail);
        Assert.Contains("cycle 0.30 s", talk.Detail);
    }

    [Fact]
    public void DisconnectedFallbackIsSharedAndVisiblyProvisional()
    {
        var provider = new AnimationDurationProvider(
            new Dictionary<int, AnimationDurationMetadata>(),
            new Dictionary<int, int>());

        var resolved = provider.Resolve(new SequenceStep { AnimId = 9, Target = ushort.MaxValue });

        Assert.True(resolved.Provisional);
        Assert.Equal(AnimationDurationProvider.FallbackFiniteMs, resolved.NominalMs);
        Assert.Equal(1_500, resolved.EffectiveMs);
        Assert.Contains("provisional", resolved.Summary);
    }
}
