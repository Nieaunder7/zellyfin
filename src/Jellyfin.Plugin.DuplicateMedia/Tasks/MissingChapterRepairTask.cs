using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DuplicateMedia.Tasks;

public sealed class MissingChapterRepairTask : IScheduledTask
{
    private const int BatchSize = 5;
    private static readonly Dictionary<Guid, DateTime> LastAttemptUtc = [];
    private static readonly Lock AttemptLock = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapterManager;
    private readonly IFileSystem _fileSystem;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<MissingChapterRepairTask> _logger;

    public MissingChapterRepairTask(
        ILibraryManager libraryManager,
        IChapterManager chapterManager,
        IFileSystem fileSystem,
        IServerConfigurationManager configurationManager,
        ILogger<MissingChapterRepairTask> logger)
    {
        _libraryManager = libraryManager;
        _chapterManager = chapterManager;
        _fileSystem = fileSystem;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    public string Name => "Repair missing chapters and chapter images";

    public string Key => "DuplicateMediaMissingChapterRepair";

    public string Description => "Creates configured interval chapters for videos without chapters and retries missing chapter images in small batches.";

    public string Category => "Duplicate Media";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(30).Ticks,
            MaxRuntimeTicks = TimeSpan.FromMinutes(20).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        if (_libraryManager.IsScanRunning)
        {
            _logger.LogInformation("Skipping missing chapter repair because a media library scan is running.");
            progress.Report(100);
            return;
        }

        var chapterDurationSeconds = _configurationManager.Configuration.DummyChapterDuration;
        if (chapterDurationSeconds <= 0)
        {
            _logger.LogInformation("Skipping missing chapter repair because dummy chapter duration is disabled.");
            progress.Report(100);
            return;
        }

        var chapterDurationTicks = TimeSpan.FromSeconds(chapterDurationSeconds).Ticks;
        var candidates = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            SourceTypes = [SourceType.Library],
            IsVirtualItem = false,
            IsFolder = false,
            HasChapterImages = false,
            Recursive = true,
            GroupByPresentationUniqueKey = false,
            DtoOptions = new DtoOptions(false)
            {
                EnableImages = false
            }
        })
        .OfType<Video>()
        .Where(video => IsEligible(video, chapterDurationTicks))
        .OrderBy(GetLastAttemptUtc)
        .ThenBy(video => video.Id)
        .Take(BatchSize)
        .ToArray();

        if (candidates.Length == 0)
        {
            _logger.LogInformation("Missing chapter repair found no eligible videos without chapter images.");
            progress.Report(100);
            return;
        }

        var directoryService = new DirectoryService(_fileSystem);
        var succeeded = 0;

        for (var index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = candidates[index];
            RecordAttempt(video.Id);

            try
            {
                var chapters = _chapterManager.GetChapters(video.Id);
                if (chapters.Count == 0)
                {
                    chapters = CreateDummyChapters(video, chapterDurationTicks);

                    // Persist markers before image extraction so a server restart cannot lose
                    // all chapter information for this video again.
                    _chapterManager.SaveChapters(video, chapters);
                }

                var success = await _chapterManager.RefreshChapterImages(
                    video,
                    directoryService,
                    chapters,
                    extractImages: true,
                    saveChapters: true,
                    cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    succeeded++;
                }
                else
                {
                    _logger.LogWarning("Chapter repair will retry {VideoPath} in a later batch.", video.Path);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chapter repair failed for {VideoPath}; it will be retried later.", video.Path);
            }

            progress.Report(100d * (index + 1) / candidates.Length);
        }

        _logger.LogInformation(
            "Missing chapter repair batch completed. Attempted {AttemptedCount} videos and completed {SucceededCount}.",
            candidates.Length,
            succeeded);
    }

    private static bool IsEligible(Video video, long chapterDurationTicks)
    {
        var runtime = video.RunTimeTicks.GetValueOrDefault();
        return runtime > chapterDurationTicks
            && runtime <= TimeSpan.FromHours(12).Ticks
            && !string.IsNullOrWhiteSpace(video.Path);
    }

    private static IReadOnlyList<ChapterInfo> CreateDummyChapters(Video video, long chapterDurationTicks)
    {
        var chapterCount = (int)(video.RunTimeTicks!.Value / chapterDurationTicks);
        var chapters = new ChapterInfo[chapterCount];

        for (var index = 0; index < chapterCount; index++)
        {
            chapters[index] = new ChapterInfo
            {
                StartPositionTicks = index * chapterDurationTicks
            };
        }

        return chapters;
    }

    private static DateTime GetLastAttemptUtc(Video video)
    {
        lock (AttemptLock)
        {
            return LastAttemptUtc.GetValueOrDefault(video.Id, DateTime.MinValue);
        }
    }

    private static void RecordAttempt(Guid itemId)
    {
        lock (AttemptLock)
        {
            LastAttemptUtc[itemId] = DateTime.UtcNow;
        }
    }
}
