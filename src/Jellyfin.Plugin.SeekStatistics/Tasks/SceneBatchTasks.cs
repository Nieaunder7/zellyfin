using System.Diagnostics;
using System.Text.Json;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeekStatistics.Tasks;

public sealed class SceneAnalysisBatchTask : IScheduledTask
{
    private readonly IServerConfigurationManager _configuration;
    private readonly ILogger<SceneAnalysisBatchTask> _logger;

    public SceneAnalysisBatchTask(IServerConfigurationManager configuration, ILogger<SceneAnalysisBatchTask> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string Name => "영상 장면 분석";
    public string Key => "ZellyfinSceneAnalysis";
    public string Description => "원본 영상의 장면 이미지를 분석하고 검색 색인을 갱신합니다. 완료한 영상은 건너뜁니다.";
    public string Category => "장면 분석";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks,
            MaxRuntimeTicks = TimeSpan.FromHours(12).Ticks
        }
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        SceneBatchRunner.RunAsync(_configuration.ApplicationPaths.DataPath, "scene-analysis-worker.py",
            ["--samples", "24", "--max-items", "1800", "--time-limit-seconds", "42600"],
            1800, "completedVideos", "pendingVideos", progress, cancellationToken, _logger);
}

public sealed class SceneTranslationBatchTask : IScheduledTask
{
    private readonly IServerConfigurationManager _configuration;
    private readonly ILogger<SceneTranslationBatchTask> _logger;

    public SceneTranslationBatchTask(IServerConfigurationManager configuration, ILogger<SceneTranslationBatchTask> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string Name => "장면 설명 한글 번역";
    public string Key => "ZellyfinSceneTranslationKo";
    public string Description => "저장된 영어 장면 설명을 로컬 모델로 한글 번역합니다. 영상을 다시 읽지 않습니다.";
    public string Category => "장면 분석";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = (TimeSpan.FromHours(17) + TimeSpan.FromMinutes(10)).Ticks,
            MaxRuntimeTicks = (TimeSpan.FromHours(2) + TimeSpan.FromMinutes(50)).Ticks
        }
    ];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        SceneBatchRunner.RunAsync(_configuration.ApplicationPaths.DataPath, "scene-translation-worker.py",
            ["--max-items", "1800", "--time-limit-seconds", "9600"],
            1800, "translatedVideos", "pendingVideos", progress, cancellationToken, _logger);
}

internal static class SceneBatchRunner
{
    public static async Task RunAsync(string dataPath, string script, string[] arguments, int maximumItems,
        string completedField, string pendingField, IProgress<double> progress, CancellationToken cancellationToken,
        ILogger logger)
    {
        progress.Report(0);
        var projectRoot = Path.GetFullPath(Path.Combine(dataPath, "..", "..", ".."));
        var workerRoot = Path.Combine(projectRoot, "runtime", "worker", "scene-analysis");
        var python = Path.Combine(workerRoot, ".venv", "Scripts", "python.exe");
        var scriptPath = Path.Combine(projectRoot, "scripts", script);
        var output = Path.Combine(dataPath, "scene-analysis");
        var activeMarker = Path.Combine(output, script == "scene-analysis-worker.py"
            ? "analysis-active"
            : "translation-active");
        foreach (var path in new[] { python, scriptPath })
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Scene batch dependency is missing", path);
        }

        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = projectRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        if (script == "scene-analysis-worker.py")
        {
            var database = Path.Combine(dataPath, "jellyfin.db");
            var ffmpeg = Path.Combine(projectRoot, "runtime", "system", "ffmpeg.exe");
            foreach (var path in new[] { database, ffmpeg })
            {
                if (!File.Exists(path)) throw new FileNotFoundException("Scene batch dependency is missing", path);
            }
            start.ArgumentList.Add("--library-db");
            start.ArgumentList.Add(database);
            start.ArgumentList.Add("--ffmpeg");
            start.ArgumentList.Add(ffmpeg);
        }
        var priorities = Path.Combine(workerRoot, "priority-items.txt");
        if (File.Exists(priorities))
        {
            start.ArgumentList.Add("--priority-ids");
            start.ArgumentList.Add(priorities);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        Directory.CreateDirectory(output);
        File.WriteAllText(activeMarker, DateTimeOffset.UtcNow.ToString("O"));
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Unable to start the scene batch worker.");
        }
        catch
        {
            File.Delete(activeMarker);
            throw;
        }
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });

        var target = maximumItems;
        async Task ReadOutputAsync()
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                logger.LogInformation("Scene batch: {Line}", line);
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.TryGetProperty(pendingField, out var pending) && pending.TryGetInt32(out var count))
                    {
                        target = Math.Max(1, Math.Min(maximumItems, count));
                    }
                    if (root.TryGetProperty(completedField, out var completed) && completed.TryGetInt32(out var done))
                    {
                        progress.Report(Math.Min(99, 100d * done / target));
                    }
                }
                catch (JsonException) { }
            }
        }

        async Task ReadErrorsAsync()
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line)) logger.LogWarning("Scene worker: {Line}", line);
            }
        }

        await Task.WhenAll(ReadOutputAsync(), ReadErrorsAsync(), process.WaitForExitAsync()).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Scene worker exited with code {process.ExitCode}.");
        File.Delete(activeMarker);
        progress.Report(100);
    }
}
