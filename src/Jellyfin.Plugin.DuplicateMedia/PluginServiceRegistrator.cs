using Jellyfin.Plugin.DuplicateMedia.Data;
using Jellyfin.Plugin.DuplicateMedia.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DuplicateMedia;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DuplicateMediaRepository>();
        serviceCollection.AddSingleton<ProductCodeExtractor>();
        serviceCollection.AddSingleton<DuplicateMediaScanner>();
        serviceCollection.AddSingleton<PermanentMediaDeletionService>();
    }
}
