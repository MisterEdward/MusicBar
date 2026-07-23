using System.Collections.Concurrent;
using TaskbarMusic.Services;
using Xunit;

namespace TaskbarMusic.Tests;

public sealed class LatestVersionGateTests
{
    [Fact]
    public void NewRequestInvalidatesEveryOlderVersion()
    {
        var gate = new LatestVersionGate();

        long first = gate.Advance();
        long second = gate.Advance();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void ConcurrentRequestsReceiveUniqueVersionsAndOneWinner()
    {
        var gate = new LatestVersionGate();
        var versions = new ConcurrentBag<long>();

        Parallel.For(0, 1_000, _ => versions.Add(gate.Advance()));

        Assert.Equal(1_000, versions.Distinct().Count());
        Assert.Single(versions, gate.IsCurrent);
        Assert.True(gate.IsCurrent(versions.Max()));
    }

    [Fact]
    public void TrackIdentityIncludesEveryMetadataFieldWithoutDelimiterCollisions()
    {
        var baseline = new MediaTrackKey("app", "title", "artist", "album");

        Assert.NotEqual(baseline, baseline with { SourceApp = "other" });
        Assert.NotEqual(baseline, baseline with { Title = "other" });
        Assert.NotEqual(baseline, baseline with { Artist = "other" });
        Assert.NotEqual(baseline, baseline with { Album = "other" });
        Assert.NotEqual(
            new MediaTrackKey("app\u001Ftitle", "song", "artist", "album"),
            new MediaTrackKey("app", "title\u001Fsong", "artist", "album"));
    }
}
