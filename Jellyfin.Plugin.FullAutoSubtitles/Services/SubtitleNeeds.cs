using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.FullAutoSubtitles.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

/// <summary>Decides whether a viewer needs a subtitle, and which search result is best.</summary>
public partial class SubtitleNeeds
{
    private readonly ILocalizationManager _localization;

    public SubtitleNeeds(ILocalizationManager localization)
    {
        _localization = localization;
    }

    public bool Same(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var names = _localization.FindLanguageInfo(a)?.ThreeLetterISOLanguageNames;
        var other = _localization.FindLanguageInfo(b)?.ThreeLetterISOLanguageNames;
        return names is not null && other is not null && names.Intersect(other, StringComparer.OrdinalIgnoreCase).Any();
    }

    public string? Known(string? language)
    {
        var code = language?.Split(',')[0].Trim();
        return !string.IsNullOrEmpty(code) && SafeCode().IsMatch(code) && _localization.FindLanguageInfo(code) is not null ? code : null;
    }

    public bool Needs(IReadOnlyList<MediaStream> streams, string language, SubtitlePlaybackMode mode, PluginConfiguration config)
    {
        if (mode is SubtitlePlaybackMode.None or SubtitlePlaybackMode.OnlyForced)
        {
            return false;
        }

        if (streams.Any(s => s.Type == MediaStreamType.Subtitle && Same(s.Language, language) && !IsForced(s)
            && (s.IsTextSubtitleStream || config.ImageSubtitlesCount)))
        {
            return false;
        }

        if (mode == SubtitlePlaybackMode.Always)
        {
            return true;
        }

        // Otherwise only when the audio is known to be in another language.
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).OrderByDescending(s => s.IsDefault).ToList();
        return audio.Count > 0
            && !string.IsNullOrEmpty(audio[0].Language)
            && !string.Equals(audio[0].Language, "und", StringComparison.OrdinalIgnoreCase)
            && !audio.Any(s => Same(s.Language, language));
    }

    public RemoteSubtitleInfo? Best(IEnumerable<RemoteSubtitleInfo> results, PluginConfiguration config, bool exactOnly)
    {
        var usable = results.Where(r => r.Forced != true
            && (config.AllowMachineTranslated || (r.AiTranslated != true && r.MachineTranslated != true))
            && (!exactOnly || r.IsHashMatch == true));
        return usable
            .OrderByDescending(r => r.IsHashMatch == true)
            .ThenByDescending(r => config.HearingImpaired switch
            {
                HearingImpairedChoice.Prefer => r.HearingImpaired == true,
                HearingImpairedChoice.Avoid => r.HearingImpaired != true,
                _ => true
            })
            .FirstOrDefault();
    }

    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex SafeCode();

    private static bool IsForced(MediaStream s)
        => s.IsForced || (s.Title?.Contains("forced", StringComparison.OrdinalIgnoreCase) ?? false);
}
