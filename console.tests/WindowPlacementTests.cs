using b1_chat_console.Services;

namespace b1_chat_console.Tests;

public sealed class WindowPlacementTests
{
    [Fact]
    public void LargeMonitorPreservesPreferredWidthAndCentersWithVerticalMargins()
    {
        var work = new WindowPlacement.WindowPixelBounds(0, 0, 1920, 1040);

        var result = WindowPlacement.CalculateCenteredBounds(
            work, preferredWidth: 1500, preferredHeight: work.Height, margin: 16);

        Assert.Equal(new WindowPlacement.WindowPixelBounds(210, 16, 1500, 1008), result);
    }

    [Fact]
    public void SmallerOffsetMonitorClampsBothDimensionsInsideItsOwnWorkArea()
    {
        var work = new WindowPlacement.WindowPixelBounds(1920, 180, 1280, 720);

        var result = WindowPlacement.CalculateCenteredBounds(
            work, preferredWidth: 1500, preferredHeight: work.Height, margin: 16);

        Assert.Equal(new WindowPlacement.WindowPixelBounds(1936, 196, 1248, 688), result);
        Assert.InRange(result.Left, work.Left, work.Left + work.Width - result.Width);
        Assert.InRange(result.Top, work.Top, work.Top + work.Height - result.Height);
    }

    [Fact]
    public void MonitorLeftOfPrimaryKeepsNegativeCoordinatesWithinThatMonitor()
    {
        var work = new WindowPlacement.WindowPixelBounds(-1600, -120, 1600, 900);

        var result = WindowPlacement.CalculateCenteredBounds(
            work, preferredWidth: 1500, preferredHeight: work.Height, margin: 12);

        Assert.Equal(new WindowPlacement.WindowPixelBounds(-1550, -108, 1500, 876), result);
        Assert.True(result.Left >= work.Left);
        Assert.True(result.Top >= work.Top);
    }

    [Fact]
    public void TinyWorkAreaStillReturnsPositiveBounds()
    {
        var work = new WindowPlacement.WindowPixelBounds(40, 50, 10, 8);

        var result = WindowPlacement.CalculateCenteredBounds(
            work, preferredWidth: 1500, preferredHeight: 1000, margin: 20);

        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(44, result.Left);
        Assert.Equal(53, result.Top);
    }
}
