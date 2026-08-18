using Jellyfin.Plugin.DuplicateMedia.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.DuplicateMedia.Tasks;

public sealed class DuplicateMediaScanTask : IScheduledTask
{
    private readonly DuplicateMediaScanner _scanner;

    public DuplicateMediaScanTask(DuplicateMediaScanner scanner)
    {
        _scanner = scanner;
    }

    public string Name => "Scan duplicate media by unique code";

    public string Key => "DuplicateMediaProductCodeScan";

    public string Description => "Groups indexed videos by normalized unique code without modifying media files.";

    public string Category => "Duplicate Media";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return Array.Empty<TaskTriggerInfo>();
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _scanner.ScanAsync(progress, cancellationToken);
    }
}
