using Jellyfin.Plugin.DuplicateMedia.Models;
using Jellyfin.Plugin.DuplicateMedia.Services;
using Xunit;

namespace Jellyfin.Plugin.DuplicateMedia.Tests;

public sealed class DuplicateGroupClassifierTests
{
    [Fact]
    public void Classify_ReturnsLikelyDuplicateForMatchingMetadata()
    {
        var items = new[]
        {
            Item("SMOK-041-a.mp4", 1_000_000, TimeSpan.FromMinutes(60).Ticks, 1920, 1080),
            Item("SMOK-041-b.mp4", 1_000_000, TimeSpan.FromMinutes(60).Add(TimeSpan.FromSeconds(1)).Ticks, 1920, 1080)
        };

        Assert.Equal("LikelyDuplicate", DuplicateGroupClassifier.Classify(items));
    }

    [Fact]
    public void Classify_ReturnsAlternateEncodeForDifferentSizes()
    {
        var items = new[]
        {
            Item("SMOK-041-1080p.mp4", 1_000_000, TimeSpan.FromMinutes(60).Ticks, 1920, 1080),
            Item("SMOK-041-4k.mp4", 4_000_000, TimeSpan.FromMinutes(60).Ticks, 3840, 2160)
        };

        Assert.Equal("AlternateEncode", DuplicateGroupClassifier.Classify(items));
    }

    [Fact]
    public void Classify_ReturnsMultipartBeforeOtherComparisons()
    {
        var items = new[]
        {
            Item("SMOK-041-CD1.mp4", 1_000_000, TimeSpan.FromMinutes(30).Ticks, 1920, 1080),
            Item("SMOK-041-CD2.mp4", 1_100_000, TimeSpan.FromMinutes(32).Ticks, 1920, 1080)
        };

        Assert.Equal("Multipart", DuplicateGroupClassifier.Classify(items));
    }

    private static DuplicateMediaItem Item(string name, long size, long runtime, int width, int height)
    {
        return new DuplicateMediaItem(
            Guid.NewGuid().ToString("N"),
            "SMOK-041",
            name,
            $@"Z:\Media\{name}",
            size,
            runtime,
            width,
            height,
            "mp4",
            DateTime.UtcNow.AddDays(-1),
            Array.Empty<ChapterImageReference>(),
            DateTime.UtcNow);
    }
}
