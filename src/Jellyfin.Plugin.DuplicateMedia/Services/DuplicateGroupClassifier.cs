using System.Text.RegularExpressions;
using Jellyfin.Plugin.DuplicateMedia.Models;

namespace Jellyfin.Plugin.DuplicateMedia.Services;

public static partial class DuplicateGroupClassifier
{
    private static readonly TimeSpan RuntimeTolerance = TimeSpan.FromSeconds(2);

    public static string Classify(IReadOnlyList<DuplicateMediaItem> items)
    {
        if (items.Any(item => MultipartPattern().IsMatch(item.Name) || MultipartPattern().IsMatch(item.Path)))
        {
            return "Multipart";
        }

        var sizes = items.Where(item => item.SizeBytes.GetValueOrDefault() > 0).Select(item => item.SizeBytes!.Value).Distinct().ToArray();
        var runtimes = items.Where(item => item.RuntimeTicks.GetValueOrDefault() > 0).Select(item => item.RuntimeTicks!.Value).ToArray();
        var resolutions = items.Where(item => item.Width > 0 && item.Height > 0).Select(item => (item.Width, item.Height)).Distinct().ToArray();

        var knownSizeCount = items.Count(item => item.SizeBytes.GetValueOrDefault() > 0);
        var sameSize = knownSizeCount == items.Count && sizes.Length == 1;
        var similarRuntime = runtimes.Length == 0 || runtimes.Max() - runtimes.Min() <= RuntimeTolerance.Ticks;
        var sameResolution = resolutions.Length <= 1;

        return sameSize && similarRuntime && sameResolution ? "LikelyDuplicate" : "AlternateEncode";
    }

    [GeneratedRegex(@"(?i)(?:^|[\s._-])(?:CD|DISC|DISK|PART|PT)[\s._-]*\d+(?:[\s._-]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex MultipartPattern();
}
