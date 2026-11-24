using System;
using System.Collections.Generic;
using Jellyfin.Plugin.FullAutoSubtitles.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FullAutoSubtitles;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer xml)
        : base(paths, xml)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    public override string Name => "Full Auto Subtitles";

    public override Guid Id => new("73ea3279-6311-43d9-ab10-94331164ddd6");

    public override string Description => "Fetches missing subtitles before you watch, and fixes the timing of ones you have.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo { Name = "FullAutoSubtitles", EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html" }
    };
}
