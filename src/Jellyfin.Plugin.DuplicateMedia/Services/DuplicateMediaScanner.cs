using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.DuplicateMedia.Data;
using Jellyfin.Plugin.DuplicateMedia.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DuplicateMedia.Services;

public sealed class DuplicateMediaScanner
{
    private const int QueryPageLimit = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly DuplicateMediaRepository _repository;
    private readonly ProductCodeExtractor _extractor;
    private readonly IChapterManager _chapterManager;
    private readonly ILogger<DuplicateMediaScanner> _logger;

    public DuplicateMediaScanner(
        ILibraryManager libraryManager,
        DuplicateMediaRepository repository,
        ProductCodeExtractor extractor,
        IChapterManager chapterManager,
        ILogger<DuplicateMediaScanner> logger)
    {
        _libraryManager = libraryManager;
        _repository = repository;
        _extractor = extractor;
        _chapterManager = chapterManager;
        _logger = logger;
    }

    public Task ScanAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var regex = _extractor.CreateRegex(configuration.ProductCodePattern);
        var query = new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IsFolder = false,
            Recursive = true,
            GroupByPresentationUniqueKey = false,
            EnableTotalRecordCount = true,
            Limit = QueryPageLimit
        };

        var totalItems = _libraryManager.GetCount(query);
        var runId = _repository.StartRun(totalItems);
        long scannedItems = 0;
        long matchedItems = 0;

        try
        {
            for (var startIndex = 0; startIndex < totalItems; startIndex += QueryPageLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                query.StartIndex = startIndex;
                var videos = _libraryManager.GetItemList(query).OfType<Video>().ToArray();
                var batch = new List<ScannedMediaItem>();

                foreach (var video in videos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedItems++;
                    var path = video.Path ?? string.Empty;
                    var sources = new List<string?>
                    {
                        video.Name,
                        Path.GetFileNameWithoutExtension(path)
                    };

                    if (configuration.IncludeParentFolder && !string.IsNullOrWhiteSpace(path))
                    {
                        sources.Add(Path.GetFileName(Path.GetDirectoryName(path)));
                    }

                    var productCodes = _extractor.Extract(regex, sources);
                    if (productCodes.Count > 0)
                    {
                        matchedItems++;
                    }

                    foreach (var productCode in productCodes)
                    {
                        var file = new FileInfo(path);
                        var sizeBytes = file.Exists ? file.Length : video.Size;
                        DateTime? dateCreatedUtc = file.Exists ? file.CreationTimeUtc : null;
                        var dateModifiedUtc = file.Exists ? file.LastWriteTimeUtc : video.DateModified.ToUniversalTime();
                        var chapterImages = _chapterManager.GetChapters(video.Id)
                            .Select((chapter, index) => new ChapterImageReference(index, chapter.StartPositionTicks, chapter.ImageTag ?? string.Empty))
                            .Where(chapter => chapter.ImageTag.Length > 0)
                            .ToArray();
                        batch.Add(new ScannedMediaItem(
                            video.Id.ToString("N"),
                            productCode,
                            video.Name ?? string.Empty,
                            path,
                            sizeBytes,
                            video.RunTimeTicks,
                            video.Width,
                            video.Height,
                            video.Container ?? string.Empty,
                            dateCreatedUtc,
                            chapterImages,
                            dateModifiedUtc));
                    }
                }

                _repository.AddBatch(runId, batch, scannedItems, matchedItems);
                progress.Report(totalItems == 0 ? 100 : 100d * scannedItems / totalItems);

                if (videos.Length == 0)
                {
                    break;
                }
            }

            _repository.CompleteRun(runId, scannedItems, matchedItems);
            progress.Report(100);
            _logger.LogInformation(
                "Duplicate Media scan completed. Scanned {ScannedItems} videos and matched {MatchedItems} unique codes.",
                scannedItems,
                matchedItems);
            return Task.CompletedTask;
        }
        catch (OperationCanceledException)
        {
            _repository.CancelRun(runId, scannedItems, matchedItems);
            throw;
        }
        catch (Exception ex)
        {
            _repository.FailRun(runId, scannedItems, matchedItems, ex.Message);
            _logger.LogError(ex, "Duplicate Media scan failed.");
            throw;
        }
    }
}
