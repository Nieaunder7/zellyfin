using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeekStatistics.Tasks;

/// <summary>Restarts batches interrupted by a server shutdown or reboot.</summary>
public sealed class SceneBatchResumeService : BackgroundService
{
    private readonly IServerConfigurationManager _configuration;
    private readonly ITaskManager _tasks;
    private readonly ILogger<SceneBatchResumeService> _logger;

    public SceneBatchResumeService(IServerConfigurationManager configuration, ITaskManager tasks,
        ILogger<SceneBatchResumeService> logger)
    {
        _configuration = configuration;
        _tasks = tasks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.Combine(_configuration.ApplicationPaths.DataPath, "scene-analysis");
        var legacyMarker = Path.Combine(root, "resume-native-tasks-once");
        var analysisMarker = Path.Combine(root, "analysis-active");
        var translationMarker = Path.Combine(root, "translation-active");
        if (!File.Exists(legacyMarker) && !File.Exists(analysisMarker) && !File.Exists(translationMarker)) return;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(legacyMarker) || File.Exists(analysisMarker))
                    _tasks.QueueIfNotRunning<SceneAnalysisBatchTask>();
                if (File.Exists(legacyMarker) || File.Exists(translationMarker))
                    _tasks.QueueIfNotRunning<SceneTranslationBatchTask>();
                if (File.Exists(legacyMarker)) File.Delete(legacyMarker);
                _logger.LogInformation("Queued interrupted scene batch tasks after server startup.");
                return;
            }
            catch (Exception error) when (error is InvalidOperationException or IOException)
            {
                _logger.LogWarning(error, "Scene batch tasks are not ready; retrying handoff.");
            }
        }
    }
}
