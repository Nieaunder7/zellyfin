using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeekStatistics.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public double SeekThresholdSeconds { get; set; } = 7;

    public double DebounceSeconds { get; set; } = 2;
}
