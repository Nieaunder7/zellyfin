namespace Jellyfin.Plugin.SeekStatistics.Services;

public static class SeekDetector
{
    public static SeekDetectionResult Detect(
        long previousPositionTicks,
        long currentPositionTicks,
        DateTime previousReportUtc,
        DateTime currentReportUtc,
        bool wasPaused,
        double thresholdSeconds)
    {
        var elapsedTicks = Math.Max(0, (currentReportUtc - previousReportUtc).Ticks);
        var expectedPositionTicks = previousPositionTicks + (wasPaused ? 0 : elapsedTicks);
        var deviationTicks = currentPositionTicks - expectedPositionTicks;
        var thresholdTicks = TimeSpan.FromSeconds(thresholdSeconds).Ticks;

        if (Math.Abs(deviationTicks) < thresholdTicks)
        {
            return new SeekDetectionResult(false, expectedPositionTicks, currentPositionTicks, deviationTicks, string.Empty);
        }

        return new SeekDetectionResult(
            true,
            expectedPositionTicks,
            currentPositionTicks,
            deviationTicks,
            deviationTicks > 0 ? "Forward" : "Backward");
    }
}

public sealed record SeekDetectionResult(
    bool IsSeek,
    long FromTicks,
    long ToTicks,
    long DeltaTicks,
    string Direction);
