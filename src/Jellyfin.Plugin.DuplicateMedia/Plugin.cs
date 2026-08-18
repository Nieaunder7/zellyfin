using System.Globalization;
using Jellyfin.Plugin.DuplicateMedia.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DuplicateMedia;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Duplicate Media Scanner";

    public override string Description => "Duplicate candidate dashboard with explicit, safety-checked permanent media deletion.";

    public override Guid Id => Guid.Parse("1e9d32f0-6b73-4c1c-a127-8f0c2cc78a13");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "duplicate_media",
                DisplayName = "중복 미디어",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Pages.duplicate_media.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuIcon = "content_copy"
            },
            new PluginPageInfo
            {
                Name = "duplicate_media.js",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Pages.duplicate_media.js", GetType().Namespace)
            }
        ];
    }
}
