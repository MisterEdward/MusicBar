using TaskbarMusic.Controls;
using Xunit;

namespace TaskbarMusic.Tests;

public sealed class MorphTimelineTests
{
    [Fact]
    public void HoldsShapeBeforeFastMorph()
    {
        var held = MorphTimeline.GetState(1.0, 8, 1.05, 0.38);
        var changing = MorphTimeline.GetState(1.24, 8, 1.05, 0.38);

        Assert.Equal(0, held.CurrentIndex);
        Assert.Equal(0, held.Progress);
        Assert.Equal(0, changing.CurrentIndex);
        Assert.InRange(changing.Progress, 0.45, 0.55);
    }

    [Fact]
    public void AdvancesAndWrapsWithoutSkippingFrames()
    {
        const double segment = 1.05 + 0.38;

        var second = MorphTimeline.GetState(segment, 8, 1.05, 0.38);
        var wrapped = MorphTimeline.GetState(segment * 8, 8, 1.05, 0.38);

        Assert.Equal((1, 2), (second.CurrentIndex, second.NextIndex));
        Assert.Equal((0, 1), (wrapped.CurrentIndex, wrapped.NextIndex));
        Assert.Equal(0, second.Progress);
        Assert.Equal(0, wrapped.Progress);
    }

    [Fact]
    public void SanitizesInvalidElapsedTime()
    {
        var state = MorphTimeline.GetState(double.NaN, 4, 1, 0.4);

        Assert.Equal(0, state.CurrentIndex);
        Assert.Equal(0, state.Progress);
    }
}
