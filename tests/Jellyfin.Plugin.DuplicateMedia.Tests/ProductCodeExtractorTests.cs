using Jellyfin.Plugin.DuplicateMedia.Services;
using Xunit;

namespace Jellyfin.Plugin.DuplicateMedia.Tests;

public sealed class ProductCodeExtractorTests
{
    private const string Pattern = @"(?<![A-Z0-9])(?<prefix>[A-Z]{3,5})[\s._-]?(?<number>\d{2,7})(?![A-Z0-9])";
    private readonly ProductCodeExtractor _extractor = new();

    [Theory]
    [InlineData("[SMOK-041] title.mp4", "SMOK-041")]
    [InlineData("smok041_1080p.mkv", "SMOK-041")]
    [InlineData("BACJ_098-reencode.mp4", "BACJ-098")]
    public void Extract_NormalizesCommonProductCodes(string source, string expected)
    {
        var result = _extractor.Extract(_extractor.CreateRegex(Pattern), [source]);

        Assert.Equal([expected], result);
    }

    [Fact]
    public void Extract_ReturnsDistinctCodesAcrossFileAndParentFolder()
    {
        var result = _extractor.Extract(_extractor.CreateRegex(Pattern), ["ABC-123 DEF_456.mp4", "ABC-123"]);

        Assert.Equal(["ABC-123", "DEF-456"], result);
    }

    [Theory]
    [InlineData("1920x1080_h264.mp4")]
    [InlineData("AB-123.mp4")]
    [InlineData("ordinary-title.mp4")]
    public void Extract_IgnoresNonMatchingTokens(string source)
    {
        var result = _extractor.Extract(_extractor.CreateRegex(Pattern), [source]);

        Assert.Empty(result);
    }

    [Fact]
    public void Extract_IgnoresDomainNameButKeepsProductCode()
    {
        var result = _extractor.Extract(_extractor.CreateRegex(Pattern), ["hhd800.com@SMOK-041.mp4"]);

        Assert.Equal(["SMOK-041"], result);
    }
}
