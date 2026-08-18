using Jellyfin.Plugin.DuplicateMedia.Services;
using Xunit;

namespace Jellyfin.Plugin.DuplicateMedia.Tests;

public sealed class DeletionSafetyValidatorTests : IDisposable
{
    private readonly string _root;
    private readonly string _mediaPath;

    public DeletionSafetyValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zellyfin-duplicate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _mediaPath = Path.Combine(_root, "ABC-123.mp4");
        File.WriteAllBytes(_mediaPath, [1, 2, 3, 4]);
    }

    [Fact]
    public void IsInsideLibraryRoot_RejectsSiblingWithCommonPrefix()
    {
        var sibling = _root + "-other";
        var siblingFile = Path.Combine(sibling, "ABC-123.mp4");

        Assert.False(DeletionSafetyValidator.IsInsideLibraryRoot(siblingFile, [_root]));
    }

    [Fact]
    public void ValidateSelection_RejectsSelectingEveryFile()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DeletionSafetyValidator.ValidateSelection("ABC-123", "ABC-123", ["one", "two"], ["one", "two"]));

        Assert.Contains("must be kept", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSelection_RejectsWrongConfirmation()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DeletionSafetyValidator.ValidateSelection("ABC-123", "ABC-124", ["one"], ["one", "two"]));

        Assert.Contains("confirmation", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSelection_RejectsItemFromAnotherGroup()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DeletionSafetyValidator.ValidateSelection("ABC-123", "ABC-123", ["other"], ["one", "two"]));

        Assert.Contains("do not belong", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateBatchConfirmation_RequiresExactGroupCount()
    {
        DeletionSafetyValidator.ValidateBatchConfirmation(12, "영구삭제 12");

        var exception = Assert.Throws<ArgumentException>(() =>
            DeletionSafetyValidator.ValidateBatchConfirmation(12, "영구삭제 11"));
        Assert.Contains("영구삭제 12", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSnapshot_AcceptsUnchangedFileInsideLibrary()
    {
        var file = new FileInfo(_mediaPath);

        DeletionSafetyValidator.ValidateSnapshot(
            _mediaPath,
            _mediaPath,
            file.Length,
            file.LastWriteTimeUtc,
            file,
            [_root],
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ValidateSnapshot_RejectsChangedIndexedPath()
    {
        var file = new FileInfo(_mediaPath);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DeletionSafetyValidator.ValidateSnapshot(
                _mediaPath,
                Path.Combine(_root, "different.mp4"),
                file.Length,
                file.LastWriteTimeUtc,
                file,
                [_root],
                TimeSpan.FromSeconds(2)));

        Assert.Contains("path has changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSnapshot_RejectsChangedSize()
    {
        var file = new FileInfo(_mediaPath);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DeletionSafetyValidator.ValidateSnapshot(
                _mediaPath,
                _mediaPath,
                file.Length + 1,
                file.LastWriteTimeUtc,
                file,
                [_root],
                TimeSpan.FromSeconds(2)));

        Assert.Contains("size has changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSnapshot_RejectsChangedModificationTime()
    {
        var file = new FileInfo(_mediaPath);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DeletionSafetyValidator.ValidateSnapshot(
                _mediaPath,
                _mediaPath,
                file.Length,
                file.LastWriteTimeUtc.AddMinutes(-1),
                file,
                [_root],
                TimeSpan.FromSeconds(2)));

        Assert.Contains("modification time has changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
