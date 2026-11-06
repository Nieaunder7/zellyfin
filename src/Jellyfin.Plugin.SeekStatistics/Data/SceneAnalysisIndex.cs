using System.Text.Json;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SeekStatistics.Data;

public sealed class SceneAnalysisIndex
{
    public const string CurrentModelVersion = "scene-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _root;

    public SceneAnalysisIndex(IServerConfigurationManager configurationManager)
    {
        _root = Path.Combine(configurationManager.ApplicationPaths.DataPath, "scene-analysis");
    }

    public string Root => _root;

    public SceneAnalysisRecord? Get(Guid itemId)
    {
        var path = Path.Combine(_root, "items", $"{itemId:N}.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SceneAnalysisRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool IsComplete(BaseItem item)
    {
        if (item is not Video || string.IsNullOrWhiteSpace(item.Path)) return false;
        var record = Get(item.Id);
        return record is not null && GetEffectiveStatus(item, record) == "complete";
    }

    public static string GetEffectiveStatus(BaseItem item, SceneAnalysisRecord record)
    {
        if (record.Status is not ("complete" or "running" or "queued" or "failed")) return "unprocessed";
        if (record.ModelVersion != CurrentModelVersion || record.ItemId != item.Id || string.IsNullOrWhiteSpace(item.Path)) return "stale";
        try
        {
            var source = new FileInfo(item.Path);
            if (!source.Exists || source.Length != record.SourceSize || source.LastWriteTimeUtc.Ticks != record.SourceModifiedUtcTicks)
                return "stale";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "stale";
        }

        return record.Status == "complete" && (record.ExpectedSamples <= 0 || record.CompletedSamples != record.ExpectedSamples)
            ? "failed"
            : record.Status;
    }
}

public sealed class SceneAnalysisRecord
{
    public Guid ItemId { get; set; }
    public string Status { get; set; } = "unprocessed";
    public string ModelVersion { get; set; } = "";
    public long SourceSize { get; set; }
    public long SourceModifiedUtcTicks { get; set; }
    public int ExpectedSamples { get; set; }
    public int CompletedSamples { get; set; }
    public List<SceneAnalysisSample> Scenes { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class SceneAnalysisSample
{
    public long TimeTicks { get; set; }
    public string Description { get; set; } = "";
    public string? DescriptionKo { get; set; }
    public List<string> Tags { get; set; } = [];
    public int FrameIndex { get; set; }
}
