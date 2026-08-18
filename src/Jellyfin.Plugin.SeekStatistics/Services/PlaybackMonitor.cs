using System.Collections.Concurrent;
using Jellyfin.Plugin.SeekStatistics.Data;
using Jellyfin.Plugin.SeekStatistics.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeekStatistics.Services;

public sealed class PlaybackMonitor : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly SeekRepository _repository;
    private readonly ILogger<PlaybackMonitor> _logger;
    private readonly ConcurrentDictionary<string, PlaybackState> _sessions = new();

    public PlaybackMonitor(ISessionManager sessionManager, SeekRepository repository, ILogger<PlaybackMonitor> logger)
    {
        _sessionManager = sessionManager;
        _repository = repository;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _repository.Initialize();
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("Seek Statistics playback monitor started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessions.Clear();
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        if (!TryGetIdentity(eventArgs, out var key, out var identity) || eventArgs.PlaybackPositionTicks is null)
        {
            return;
        }

        _sessions[key] = new PlaybackState(
            eventArgs.PlaybackPositionTicks.Value,
            DateTime.UtcNow,
            eventArgs.IsPaused,
            DateTime.MinValue,
            identity);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs eventArgs)
    {
        if (eventArgs.IsAutomated || eventArgs.PlaybackPositionTicks is null ||
            !TryGetIdentity(eventArgs, out var key, out var identity))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var currentPosition = eventArgs.PlaybackPositionTicks.Value;
        var state = _sessions.GetOrAdd(
            key,
            _ => new PlaybackState(currentPosition, now, eventArgs.IsPaused, DateTime.MinValue, identity));

        lock (state)
        {
            var configuration = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
            var result = SeekDetector.Detect(
                state.PositionTicks,
                currentPosition,
                state.ReportedUtc,
                now,
                state.IsPaused,
                configuration.SeekThresholdSeconds);

            var debounceElapsed = (now - state.LastSeekUtc).TotalSeconds;
            if (result.IsSeek && debounceElapsed >= configuration.DebounceSeconds)
            {
                var seekEvent = new SeekEvent(
                    now,
                    identity.UserId,
                    identity.UserName,
                    identity.ItemId,
                    identity.ItemName,
                    identity.ItemType,
                    identity.PlaySessionId,
                    identity.DeviceId,
                    identity.DeviceName,
                    identity.ClientName,
                    result.FromTicks,
                    result.ToTicks,
                    result.DeltaTicks,
                    result.Direction);

                try
                {
                    _repository.Insert(seekEvent);
                    state.LastSeekUtc = now;
                    _logger.LogInformation(
                        "Seek detected for {ItemName}: {Direction}, {FromSeconds:F1}s -> {ToSeconds:F1}s ({DeltaSeconds:F1}s)",
                        identity.ItemName,
                        result.Direction,
                        TimeSpan.FromTicks(result.FromTicks).TotalSeconds,
                        TimeSpan.FromTicks(result.ToTicks).TotalSeconds,
                        TimeSpan.FromTicks(result.DeltaTicks).TotalSeconds);
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Failed to store seek event");
                }
            }

            state.PositionTicks = currentPosition;
            state.ReportedUtc = now;
            state.IsPaused = eventArgs.IsPaused;
            state.Identity = identity;
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs eventArgs)
    {
        if (TryGetIdentity(eventArgs, out var key, out _))
        {
            _sessions.TryRemove(key, out _);
        }
    }

    private static bool TryGetIdentity(PlaybackProgressEventArgs eventArgs, out string key, out PlaybackIdentity identity)
    {
        key = string.Empty;
        identity = default!;
        if (eventArgs.Users.Count == 0 || eventArgs.Item is null)
        {
            return false;
        }

        var user = eventArgs.Users[0];
        var playSessionId = eventArgs.PlaySessionId ?? string.Empty;
        key = !string.IsNullOrWhiteSpace(playSessionId)
            ? playSessionId
            : $"{eventArgs.DeviceId}:{user.Id:N}:{eventArgs.Item.Id:N}";
        identity = new PlaybackIdentity(
            user.Id.ToString("N"),
            user.Username ?? string.Empty,
            eventArgs.Item.Id.ToString("N"),
            eventArgs.Item.Name ?? string.Empty,
            eventArgs.Item.GetType().Name,
            playSessionId,
            eventArgs.DeviceId ?? string.Empty,
            eventArgs.DeviceName ?? string.Empty,
            eventArgs.ClientName ?? string.Empty);
        return true;
    }

    private sealed class PlaybackState
    {
        public PlaybackState(long positionTicks, DateTime reportedUtc, bool isPaused, DateTime lastSeekUtc, PlaybackIdentity identity)
        {
            PositionTicks = positionTicks;
            ReportedUtc = reportedUtc;
            IsPaused = isPaused;
            LastSeekUtc = lastSeekUtc;
            Identity = identity;
        }

        public long PositionTicks { get; set; }
        public DateTime ReportedUtc { get; set; }
        public bool IsPaused { get; set; }
        public DateTime LastSeekUtc { get; set; }
        public PlaybackIdentity Identity { get; set; }
    }

    private sealed record PlaybackIdentity(
        string UserId,
        string UserName,
        string ItemId,
        string ItemName,
        string ItemType,
        string PlaySessionId,
        string DeviceId,
        string DeviceName,
        string ClientName);
}
