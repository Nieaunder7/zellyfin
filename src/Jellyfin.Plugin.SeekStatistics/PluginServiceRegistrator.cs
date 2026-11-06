using Jellyfin.Plugin.SeekStatistics.Data;
using Jellyfin.Plugin.SeekStatistics.Services;
using Jellyfin.Plugin.SeekStatistics.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SeekStatistics;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SeekRepository>();
        serviceCollection.AddSingleton<SceneAnalysisIndex>();
        serviceCollection.AddSingleton<SynopsisIndex>();
        serviceCollection.AddSingleton<PerformerFilmographyIndex>();
        serviceCollection.AddHostedService<PlaybackMonitor>();
        serviceCollection.AddHostedService<SceneBatchResumeService>();
    }
}
