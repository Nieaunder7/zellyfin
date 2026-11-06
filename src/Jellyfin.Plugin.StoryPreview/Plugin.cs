using Jellyfin.Plugin.StoryPreview.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StoryPreview;

public sealed class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "Story Preview";

    public override string Description => "Generates three on-demand scene preview frames without a library scan.";

    public override Guid Id => Guid.Parse("7e7d954b-8d6f-4e3c-b7de-e0b01987910f");
}
