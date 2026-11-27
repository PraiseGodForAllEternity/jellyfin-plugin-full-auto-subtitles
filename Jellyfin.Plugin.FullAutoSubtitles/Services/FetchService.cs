using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.FullAutoSubtitles.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

/// <summary>Finds what each active viewer is likely to watch next and fetches missing subtitles for it.</summary>
public sealed class FetchService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly ITVSeriesManager _tv;
    private readonly ISubtitleManager _subtitles;
    private readonly TrackService _tracks;
    private readonly SubtitleNeeds _needs;
    private readonly StateStore _state;
    private readonly FilmSeries _series;
    private readonly ILogger<FetchService> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Dictionary<(Guid User, Guid Item), DateTime> _seriesDone = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private int _pending;

    public FetchService(ISessionManager sessions, ILibraryManager library, ITVSeriesManager tv, ISubtitleManager subtitles, TrackService tracks, SubtitleNeeds needs, StateStore state, FilmSeries series, ILogger<FetchService> logger)
    {
        _sessions = sessions;
        _library = library;
        _tv = tv;
        _subtitles = subtitles;
        _tracks = tracks;
        _needs = needs;
        _state = state;
        _series = series;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnStopped;
        _sessions.PlaybackProgress += OnProgress;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped -= OnStopped;
        _sessions.PlaybackProgress -= OnProgress;
        _stop.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stop.Dispose();
        _running.Dispose();
        _oneAtATime.Dispose();
    }

    public async Task Run(IProgress<double>? progress, CancellationToken token)
    {
        var config = Plugin.Config;
        if (!config.Enabled || !await _running.WaitAsync(0, token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var since = DateTime.UtcNow.AddDays(-Math.Clamp(config.ActiveWithinDays, 1, 365));
            var lists = new List<Queue<(Video Item, string Language)>>();
            foreach (var user in _tracks.Users().ToList())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (user.LastActivityDate >= since && !user.HasPermission(PermissionKind.IsDisabled))
                    {
                        var wanted = Wanted(user, config);
                        if (wanted.Count > 0)
                        {
                            lists.Add(wanted);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Full Auto Subtitles: couldn't work out what a user watches next");
                }
            }

            // One title per user per round, so one person can't use the whole allowance.
            var total = Math.Max(1, lists.Sum(l => l.Count));
            var done = 0;
            while (lists.Any(l => l.Count > 0))
            {
                foreach (var list in lists.Where(l => l.Count > 0))
                {
                    token.ThrowIfCancellationRequested();
                    var (item, language) = list.Dequeue();
                    if (!await Fetch(item, language, config, token).ConfigureAwait(false))
                    {
                        return;
                    }

                    progress?.Report(100.0 * ++done / total);
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    private static string MissKey(BaseItem item, string language) => item.Id.ToString("N") + ":" + language.ToLowerInvariant();

    private Queue<(Video Item, string Language)> Wanted(User user, PluginConfiguration config)
    {
        var result = new Queue<(Video, string)>();
        var language = _needs.Known(user.SubtitleLanguagePreference);
        if (language is null || user.SubtitleMode is SubtitlePlaybackMode.None or SubtitlePlaybackMode.OnlyForced)
        {
            return result;
        }

        var ahead = Math.Clamp(config.EpisodesAhead, 0, 5);
        var items = new List<BaseItem>();
        items.AddRange(_library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsResumable = true,
            Recursive = true,
            Limit = 6,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            DtoOptions = new DtoOptions(false)
        }));
        var starts = items.OfType<Episode>().ToList();
        if (ahead > 0)
        {
            var next = _tv.GetNextUp(new NextUpQuery { User = user, Limit = 6, EnableResumable = false }, new DtoOptions(false)).Items;
            items.AddRange(next);
            starts.AddRange(next.OfType<Episode>());
        }

        if (config.PrepareNextInSeries)
        {
            foreach (var movie in items.OfType<Movie>().ToList())
            {
                if (_series.Next(user, movie) is { } next)
                {
                    items.Add(next);
                }
            }
        }

        foreach (var episode in starts)
        {
            items.AddRange(Following(user, episode, ahead));
        }

        if (config.IncludeFavoriteMovies)
        {
            items.AddRange(_library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsFavorite = true,
                IsPlayed = false,
                Recursive = true,
                Limit = 6,
                DtoOptions = new DtoOptions(false)
            }));
        }

        var seen = new HashSet<Guid>();
        foreach (var found in items)
        {
            if (seen.Add(found.Id) && Candidate(user, found, language, config) is { } item)
            {
                result.Enqueue((item, language));
            }
        }

        return result;
    }

    private Video? Candidate(User user, BaseItem found, string language, PluginConfiguration config)
        => _library.GetItemById(found.Id) is Video { IsFileProtocol: true, IsVirtualItem: false, IsShortcut: false } item
            && item is Movie or Episode
            && item.IsVisibleStandalone(user)
            && _needs.Needs(_tracks.Streams(item), language, user.SubtitleMode, config)
                ? item
                : null;

    private static IEnumerable<Episode> Following(User user, Episode episode, int count)
    {
        if (episode.Series is not { } series || count == 0)
        {
            return Enumerable.Empty<Episode>();
        }

        // Specials sit outside the running order.
        var all = series.GetEpisodes(user, new DtoOptions(false), false).OfType<Episode>()
            .Where(e => !e.IsVirtualItem && (e.ParentIndexNumber ?? 0) > 0).ToList();
        var at = all.FindIndex(e => e.Id.Equals(episode.Id));
        return at < 0 ? Enumerable.Empty<Episode>() : all.Skip(at + 1).Take(count);
    }

    // False means stop for now (allowance used up).
    private async Task<bool> Fetch(Video item, string language, PluginConfiguration config, CancellationToken token)
    {
        await _oneAtATime.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await FetchOne(item, language, config, token).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<bool> FetchOne(Video item, string language, PluginConfiguration config, CancellationToken token)
    {
        var key = MissKey(item, language);
        var cap = Math.Clamp(config.DailyCap, 0, 1000);
        if (_state.Read(s => s.Used >= cap || s.PausedUntil > DateTime.UtcNow))
        {
            return false;
        }

        if (_state.Read(s => s.Misses.TryGetValue(key, out var until) && until > DateTime.UtcNow) || _tracks.IsBusy(item))
        {
            return true;
        }

        // Another user in this run may already have caused it to be fetched.
        if (_tracks.Streams(item).Any(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !s.IsForced && _needs.Same(s.Language, language)))
        {
            return true;
        }

        var entry = new HistoryEntry { ItemId = item.Id, Title = Title(item), Language = language };
        void Wait(int days) => _state.Change(s => s.Misses[key] = DateTime.UtcNow.AddDays(days));
        var retry = Math.Clamp(config.RetryAfterDays, 1, 90);
        try
        {
            var results = await _subtitles.SearchSubtitles(item, language, false, true, token).ConfigureAwait(false);
            var best = _needs.Best(results, config, config.ExactMatchOnly);
            if (best is null)
            {
                Wait(results.Length == 0 ? 1 : retry);
                entry.Result = "Not found";
                entry.Detail = results.Length == 0 ? "No results from your subtitle providers." : results.Length + " results, none suitable.";
                _state.Log(entry);
                return true;
            }

            if (!_state.TryTakeDownload(cap))
            {
                return false;
            }

            // Download first: Change rechecks that nobody is watching, and saving is quick.
            var response = await _subtitles.GetRemoteSubtitles(best.Id, token).ConfigureAwait(false);
            var bytes = await SubtitleText.ReadBytes(response.Stream, token).ConfigureAwait(false);
            if (string.Equals(response.Format, "srt", StringComparison.OrdinalIgnoreCase) && Srt.Parse(SubtitleText.Decode(bytes)).Count == 0)
            {
                throw new TimingException("What the provider sent isn't a subtitle.");
            }

            // Never trust a name from outside: save under the language that was asked for.
            response.Language = language;
            response.Stream = new MemoryStream(bytes);
            await _tracks.Change(item, () => _subtitles.UploadSubtitle(item, response), token).ConfigureAwait(false);

            if (_tracks.Streams(item).Any(s => s.IsExternal && s.Type == MediaStreamType.Subtitle && _needs.Same(s.Language, language)))
            {
                entry.Result = "Added";
                // If someone deletes it, don't bring the same one straight back.
                Wait(90);
            }
            else
            {
                // Don't spend the allowance on it again and again.
                Wait(retry);
                entry.Result = "Problem";
                entry.Detail = "Downloaded, but Jellyfin didn't pick it up. ";
            }

            entry.Detail += $"{best.ProviderName}{(best.IsHashMatch == true ? ", exact match" : ", not an exact match")}{(best.HearingImpaired == true ? ", hearing impaired" : string.Empty)}";
            _state.Log(entry);
            return true;
        }
        catch (BusyException)
        {
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex.GetType().Name == "RateLimitExceededException")
        {
            _state.Change(s => s.PausedUntil = DateTime.UtcNow.Date.AddDays(1));
            entry.Result = "Paused";
            entry.Detail = "The provider's daily limit is used up. Continues tomorrow (UTC).";
            _state.Log(entry);
            return false;
        }
        catch (TimingException ex)
        {
            Wait(retry);
            entry.Result = "Problem";
            entry.Detail = ex.Message;
            _state.Log(entry);
            return true;
        }
        catch (Exception ex)
        {
            Wait(1);
            _logger.LogWarning(ex, "Full Auto Subtitles: couldn't fetch for {Item}", item.Name);
            entry.Result = "Problem";
            entry.Detail = "The download failed. See the server log.";
            _state.Log(entry);
            return true;
        }
    }

    internal static string Title(BaseItem item) => item is Episode e
        ? $"{e.SeriesName} S{e.ParentIndexNumber:00}E{e.IndexNumber:00} {e.Name}"
        : item.Name + (item.ProductionYear is { } y ? $" ({y})" : string.Empty);

    // Most of the way through a film: get the next one in its series ready.
    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var config = Plugin.Config;
        if (!config.Enabled || !config.PrepareNextInSeries || e.Item is not Movie movie || e.Users is not { Count: > 0 } users
            || movie.RunTimeTicks is not > 0 || e.PlaybackPositionTicks is not { } position || position < movie.RunTimeTicks * 0.7)
        {
            return;
        }

        lock (_seriesDone)
        {
            foreach (var old in _seriesDone.Where(d => DateTime.UtcNow - d.Value > TimeSpan.FromHours(6)).Select(d => d.Key).ToList())
            {
                _seriesDone.Remove(old);
            }

            if (!_seriesDone.TryAdd((users[0].Id, movie.Id), DateTime.UtcNow))
            {
                return;
            }
        }

        var user = users[0];
        _ = Task.Run(async () =>
        {
            try
            {
                var language = _needs.Known(user.SubtitleLanguagePreference);
                if (language is null || user.SubtitleMode is SubtitlePlaybackMode.None or SubtitlePlaybackMode.OnlyForced
                    || _series.Next(user, movie) is not { } next || Candidate(user, next, language, config) is not { } item)
                {
                    return;
                }

                await Fetch(item, language, config, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Full Auto Subtitles: couldn't prepare the next film after {Name}", movie.Name);
            }
        });
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is not (Movie or Episode))
        {
            return;
        }

        _tracks.NoteStopped(e.Item);
        if (!Plugin.Config.Enabled || Interlocked.Exchange(ref _pending, 1) == 1)
        {
            return;
        }

        // Once things are quiet, get the next ones ready.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(3), _stop.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _pending, 0);
                await Run(null, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full Auto Subtitles: run failed");
            }
            finally
            {
                Interlocked.Exchange(ref _pending, 0);
            }
        });
    }
}

public class FetchTask : IScheduledTask
{
    private readonly FetchService _fetch;

    public FetchTask(FetchService fetch)
    {
        _fetch = fetch;
    }

    public string Name => "Get subtitles ready";

    public string Key => "FullAutoSubtitlesFetch";

    public string Description => "Fetches missing subtitles for what people are likely to watch next.";

    public string Category => "Full Auto Subtitles";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => _fetch.Run(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }
    };
}
