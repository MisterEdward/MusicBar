namespace TaskbarMusic.Controls;

/// <summary>Platform-neutral timing for the idle motion mark.</summary>
internal static class MorphTimeline
{
    public static MorphTimelineState GetState(
        double elapsedSeconds,
        int frameCount,
        double holdSeconds,
        double morphSeconds)
    {
        if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (!double.IsFinite(elapsedSeconds)) elapsedSeconds = 0;
        elapsedSeconds = Math.Max(0, elapsedSeconds);
        holdSeconds = Math.Max(0, holdSeconds);
        morphSeconds = Math.Max(0.001, morphSeconds);

        double segment = holdSeconds + morphSeconds;
        double phase = elapsedSeconds / segment;
        int absoluteIndex = (int)Math.Floor(phase);
        double local = (phase - absoluteIndex) * segment;
        double raw = local <= holdSeconds ? 0 : (local - holdSeconds) / morphSeconds;
        raw = Math.Clamp(raw, 0, 1);

        int current = absoluteIndex % frameCount;
        int next = (current + 1) % frameCount;
        return new MorphTimelineState(current, next, raw, SmootherStep(raw));
    }

    private static double SmootherStep(double value) =>
        value * value * value * (value * (value * 6 - 15) + 10);
}

internal readonly record struct MorphTimelineState(
    int CurrentIndex,
    int NextIndex,
    double RawProgress,
    double Progress);
