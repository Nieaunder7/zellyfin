using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DuplicateMedia.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string ProductCodePattern { get; set; } = @"(?<![A-Z0-9])(?<prefix>[A-Z]{3,5})[\s._-]?(?<number>\d{2,7})(?![A-Z0-9])";

    public bool IncludeParentFolder { get; set; } = true;
}
