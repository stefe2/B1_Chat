using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

// SEQ-H02 gap: RoundToGrid is a pure, easily-unit-testable function that had zero
// direct coverage (it was only exercised incidentally through drag/insert tests).
public sealed class SequencerGridSnapTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(49.0, 0)]
    [InlineData(149.0, 100)]
    [InlineData(151.0, 200)]
    [InlineData(250.0, 200)] // banker's rounding: Math.Round(2.5) rounds to even (2), not up
    [InlineData(350.0, 400)] // Math.Round(3.5) rounds to even (4)
    [InlineData(1_234.0, 1_200)]
    public void RoundToGrid_SnapsToTheNearestHundredMillisecondsWhenEnabled(double input, int expected)
    {
        var vm = CreateViewModel();
        vm.SnapToGrid = true;

        Assert.Equal(expected, vm.RoundToGrid(input));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(149.9, 149)]
    [InlineData(1_234.7, 1_234)]
    public void RoundToGrid_TruncatesToWholeMillisecondsWhenSnapIsOff(double input, int expected)
    {
        var vm = CreateViewModel();
        vm.SnapToGrid = false;

        Assert.Equal(expected, vm.RoundToGrid(input));
    }

    private static SequencerViewModel CreateViewModel() => new(
        new FakeSequencerProtocol(),
        new FakeSequencerSettings(),
        new FakeAudioPlayer(),
        new FakePlaybackTimerScheduler(),
        new FakePlaybackClock(),
        new FakePlaybackTimerScheduler(),
        new FakeSequencerPersistenceDialogs(),
        new ThrowingAtomicTextFileWriter(new InvalidOperationException("not used")),
        new FakeSequenceLibraryService(),
        preflightService: new PermissiveSequencerPreflightService());
}
