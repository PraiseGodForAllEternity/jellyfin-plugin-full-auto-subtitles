using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FullAutoSubtitles.Configuration;

public enum HearingImpairedChoice
{
    NoPreference = 0,
    Prefer = 1,
    Avoid = 2
}

public class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    /// <summary>Automatic downloads per UTC day; the rest of the account's allowance stays free for people.</summary>
    public int DailyCap { get; set; } = 15;

    public int EpisodesAhead { get; set; } = 2;

    public int ActiveWithinDays { get; set; } = 30;

    public int RetryAfterDays { get; set; } = 7;

    public bool IncludeFavoriteMovies { get; set; } = true;

    public bool ExactMatchOnly { get; set; }

    public bool AllowMachineTranslated { get; set; }

    public bool ImageSubtitlesCount { get; set; } = true;

    public HearingImpairedChoice HearingImpaired { get; set; } = HearingImpairedChoice.NoPreference;
}
