using TaskbarMusic.Interop;
using Xunit;

namespace TaskbarMusic.Tests;

public sealed class ScreenGeometryTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData(48, 0, 1920, 1080, (int)TaskbarEdge.Left)]
    [InlineData(0, 48, 1920, 1080, (int)TaskbarEdge.Top)]
    [InlineData(0, 0, 1872, 1080, (int)TaskbarEdge.Right)]
    [InlineData(0, 0, 1920, 1032, (int)TaskbarEdge.Bottom)]
    public void ResolvesEveryEdgeFromWorkArea(
        int left, int top, int right, int bottom, int expectedValue)
    {
        var expected = (TaskbarEdge)expectedValue;
        bool found = TaskbarGeometry.TryResolve(
            Monitor, new PixelRect(left, top, right, bottom), null, 48, out var result);

        Assert.True(found);
        Assert.Equal(expected, result.Edge);
        Assert.Equal(48, expected is TaskbarEdge.Left or TaskbarEdge.Right
            ? result.Bounds.Width
            : result.Bounds.Height);
    }

    [Theory]
    [InlineData(-48, 0, 0, 1080, (int)TaskbarEdge.Left)]
    [InlineData(0, -48, 1920, 0, (int)TaskbarEdge.Top)]
    [InlineData(1920, 0, 1968, 1080, (int)TaskbarEdge.Right)]
    [InlineData(0, 1080, 1920, 1128, (int)TaskbarEdge.Bottom)]
    public void ResolvesAutoHiddenEdgeFromOffscreenShellWindow(
        int left, int top, int right, int bottom, int expectedValue)
    {
        var expected = (TaskbarEdge)expectedValue;
        bool found = TaskbarGeometry.TryResolve(
            Monitor, Monitor, new PixelRect(left, top, right, bottom), 48, out var result);

        Assert.True(found);
        Assert.Equal(expected, result.Edge);
        Assert.Equal(48, expected is TaskbarEdge.Left or TaskbarEdge.Right
            ? result.Bounds.Width
            : result.Bounds.Height);
    }

    [Fact]
    public void UsesDpiFallbackForAutoHideActivationSliver()
    {
        var shell = new PixelRect(0, 1078, 1920, 1080);

        bool found = TaskbarGeometry.TryResolve(Monitor, Monitor, shell, 60, out var result);

        Assert.True(found);
        Assert.Equal(TaskbarEdge.Bottom, result.Edge);
        Assert.Equal(60, result.Bounds.Height);
    }

    [Fact]
    public void KnownAutoHideEdgeWinsAtSharedMonitorBoundary()
    {
        var parkedInAdjacentMonitor = new PixelRect(0, 1080, 1920, 1128);

        bool found = TaskbarGeometry.TryResolve(
            Monitor,
            Monitor,
            parkedInAdjacentMonitor,
            48,
            out var result,
            TaskbarEdge.Bottom);

        Assert.True(found);
        Assert.Equal(TaskbarEdge.Bottom, result.Edge);
        Assert.Equal(new PixelRect(0, 1032, 1920, 1080), result.Bounds);
    }

    [Fact]
    public void ReturnsFalseWithoutAnyTaskbarSignal()
    {
        Assert.False(TaskbarGeometry.TryResolve(Monitor, Monitor, null, 48, out _));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, true)]
    [InlineData(1, 2, 1918, 1078, true)]
    [InlineData(0, 0, 1920, 1040, false)]
    [InlineData(2000, 0, 3920, 1080, false)]
    public void FullscreenCoverageUsesTolerance(
        int left, int top, int right, int bottom, bool expected)
    {
        Assert.Equal(expected,
            FullscreenGeometry.CoversMonitor(new PixelRect(left, top, right, bottom), Monitor));
    }

    [Fact]
    public void RoundedHitTestRejectsTransparentCorners()
    {
        var pill = new PixelRect(100, 200, 300, 240);

        Assert.False(RoundedRectHitTest.Contains(pill, 100, 200, 20));
        Assert.True(RoundedRectHitTest.Contains(pill, 120, 200, 20));
        Assert.True(RoundedRectHitTest.Contains(pill, 101, 220, 20));
        Assert.True(RoundedRectHitTest.Contains(pill, 200, 220, 20));
        Assert.False(RoundedRectHitTest.Contains(pill, 300, 220, 20));
    }

    [Theory]
    [InlineData(2600, 2400, 1600, 0.8, 1760)]
    [InlineData(-1700, -1600, -1280, 1.25, -1405)]
    public void DpiConversionIsRelativeToWindowOrigin(
        double screenPixel, int windowPixelOrigin, double windowDipOrigin,
        double pixelsToDip, double expected)
    {
        Assert.Equal(expected, DpiCoordinates.ScreenPixelToDip(
            screenPixel, windowPixelOrigin, windowDipOrigin, pixelsToDip), 6);
    }

    [Fact]
    public void CatPlacementStaysOnTargetMonitorInPhysicalPixels()
    {
        var monitor = new PixelRect(-2560, 0, 0, 1440);
        var pill = new PixelRect(-180, 1370, -80, 1420);

        bool found = CatPlacement.TryResolve(
            pill, monitor, catWidth: 225, catHeight: 144, out var result);

        Assert.True(found);
        Assert.True(result.EntersFromLeft);
        Assert.Equal(-387, result.Bounds.Left);
        Assert.Equal(1282, result.Bounds.Top);
        Assert.True(result.Bounds.Left >= monitor.Left);
        Assert.True(result.Bounds.Right <= monitor.Right);
        Assert.True(result.Bounds.Top >= monitor.Top);
        Assert.True(result.Bounds.Bottom <= monitor.Bottom);
    }

    [Fact]
    public void CatPlacementFallsBackBelowNearTopEdge()
    {
        var pill = new PixelRect(10, 0, 70, 40);

        bool found = CatPlacement.TryResolve(
            pill, Monitor, catWidth: 150, catHeight: 96, out var result);

        Assert.True(found);
        Assert.False(result.EntersFromLeft);
        Assert.Equal(44, result.Bounds.Top);
    }
}
