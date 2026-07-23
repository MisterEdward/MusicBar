namespace TaskbarMusic.Services;

/// <summary>
/// Issues monotonically increasing versions and identifies only the latest one
/// as current. Used to discard async work completed after a newer request.
/// </summary>
internal sealed class LatestVersionGate
{
    private long _latest;

    public long Advance() => Interlocked.Increment(ref _latest);

    public bool IsCurrent(long version) =>
        version == Volatile.Read(ref _latest);
}

/// <summary>
/// Exact media identity used for palette reuse. A structured key avoids
/// delimiter collisions between metadata fields.
/// </summary>
internal readonly record struct MediaTrackKey(
    string SourceApp,
    string Title,
    string Artist,
    string Album);
