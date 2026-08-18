using Jellyfin.Plugin.SeekStatistics.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SeekStatistics;

public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Seek Statistics";

    public override string Description => "Collect inferred seeking activity during playback.";

    public override Guid Id => Guid.Parse("7ef8c86b-034a-4ca7-9bdb-2d2aa57c9166");
}
