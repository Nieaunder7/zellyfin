namespace Jellyfin.Plugin.DuplicateMedia.Models;

public sealed record ChapterImageReference(
    int Index,
    long StartPositionTicks,
    string ImageTag);

public sealed record ScannedMediaItem(
    string ItemId,
    string ProductCode,
    string Name,
    string Path,
    long? SizeBytes,
    long? RuntimeTicks,
    int Width,
    int Height,
    string Container,
    DateTime? DateCreatedUtc,
    IReadOnlyList<ChapterImageReference> ChapterImages,
    DateTime DateModifiedUtc);

public sealed record DuplicateMediaItem(
    string ItemId,
    string ProductCode,
    string Name,
    string Path,
    long? SizeBytes,
    long? RuntimeTicks,
    int Width,
    int Height,
    string Container,
    DateTime? DateCreatedUtc,
    IReadOnlyList<ChapterImageReference> ChapterImages,
    DateTime DateModifiedUtc);

public sealed record DuplicateGroup(
    string ProductCode,
    int FileCount,
    string Classification,
    string Status,
    long TotalSizeBytes,
    long PotentialSavingsBytes,
    IReadOnlyList<DuplicateMediaItem> Items);

public sealed record DuplicateGroupPage(
    int StartIndex,
    int TotalRecordCount,
    IReadOnlyList<DuplicateGroup> Items);

public sealed record ScanRunInfo(
    long Id,
    string Status,
    DateTime StartedUtc,
    DateTime? CompletedUtc,
    long TotalItems,
    long ScannedItems,
    long MatchedItems,
    string Error);

public sealed record DuplicateMediaSummary(
    ScanRunInfo? LatestRun,
    long ResultRunId,
    int DuplicateGroupCount,
    int CandidateFileCount,
    long PotentialSavingsBytes,
    string DatabasePath);

public sealed record UpdateGroupStatusRequest(string Status);

public sealed record PermanentDeleteRequest(
    IReadOnlyList<string> ItemIds,
    string Confirmation);

public sealed record PermanentDeleteItemResult(
    string ItemId,
    string Path,
    long SizeBytes,
    bool FileDeleted,
    bool IndexRemoved,
    string Error);

public sealed record PermanentDeleteResult(
    string ProductCode,
    int RequestedCount,
    int DeletedCount,
    long DeletedBytes,
    IReadOnlyList<PermanentDeleteItemResult> Items);

public sealed record KeepGroupSelection(
    string ProductCode,
    IReadOnlyList<string> KeepItemIds);

public sealed record BatchPermanentDeleteRequest(
    IReadOnlyList<KeepGroupSelection> Groups,
    string Confirmation);

public sealed record BatchPermanentDeleteResult(
    int GroupCount,
    int RequestedCount,
    int DeletedCount,
    long DeletedBytes,
    IReadOnlyList<PermanentDeleteItemResult> Items);
