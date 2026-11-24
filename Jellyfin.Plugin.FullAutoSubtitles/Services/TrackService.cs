using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

public class BusyException : Exception
{
    public BusyException()
        : base("Someone is watching this (or another episode of the show), or just stopped. Try again a little later.")
    {
    }
}

/// <summary>
/// Adds or removes subtitle files safely. Jellyfin renumbers every track when that happens,
/// so it waits until nobody is watching and moves each user's saved track choices along.
/// </summary>
public class TrackService
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);
    private readonly Dictionary<Guid, DateTime> _stopped = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISessionManager _sessions;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly IMediaSourceManager _sources;
    private readonly ILibraryManager _library;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<TrackService> _logger;

    public TrackService(ISessionManager sessions, IUserManager users, IUserDataManager userData, IMediaSourceManager sources, ILibraryManager library, IFileSystem fileSystem, ILogger<TrackService> logger)
    {
        _sessions = sessions;
        _users = users;
        _userData = userData;
        _sources = sources;
        _library = library;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    // Jellyfin replaced the Users property with GetUsers() partway through 10.11, so a direct call
    // to either one fails on some 10.11 servers.
    public IEnumerable<User> Users()
    {
        var type = typeof(IUserManager);
        var method = type.GetMethod("GetUsers", Type.EmptyTypes) ?? type.GetProperty("Users")?.GetMethod
            ?? throw new NotSupportedException("This Jellyfin version has no way to list users.");
        return (IEnumerable<User>)method.Invoke(_users, null)!;
    }

    public IReadOnlyList<MediaStream> Streams(BaseItem item) => _sources.GetMediaStreams(item.Id);

    public void NoteStopped(BaseItem item)
    {
        lock (_stopped)
        {
            foreach (var old in _stopped.Where(s => DateTime.UtcNow - s.Value > Grace).Select(s => s.Key).ToList())
            {
                _stopped.Remove(old);
            }

            _stopped[Group(item)] = DateTime.UtcNow;
        }
    }

    public bool IsBusy(BaseItem item)
    {
        var group = Group(item);
        lock (_stopped)
        {
            if (_stopped.TryGetValue(group, out var at) && DateTime.UtcNow - at < Grace)
            {
                return true;
            }
        }

        foreach (var session in _sessions.Sessions.ToList())
        {
            if (session.NowPlayingItem is { } playing)
            {
                var watched = session.FullNowPlayingItem ?? _library.GetItemById(playing.Id);
                if (playing.Id.Equals(item.Id)
                    || (watched is not null && Group(watched).Equals(group))
                    || string.Equals(session.PlayState?.MediaSourceId, item.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Runs a change to an item's subtitle files, refreshes it, and carries saved choices over.</summary>
    public async Task Change(Video item, Func<Task> change, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsBusy(item))
            {
                throw new BusyException();
            }

            var before = Streams(item);
            try
            {
                await change().ConfigureAwait(false);
            }
            finally
            {
                // Even after a failure something may have been written, so always resync.
                try
                {
                    await Refresh(item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Full Auto Subtitles: refreshing {Item} failed; trying once more", item.Name);
                    await Task.Delay(2000, CancellationToken.None).ConfigureAwait(false);
                    await Refresh(item).ConfigureAwait(false);
                }

                Remap(item, before, Streams(item));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Refresh(Video item)
        => item.RefreshMetadata(new MetadataRefreshOptions(new DirectoryService(_fileSystem)), CancellationToken.None);

    internal static Dictionary<int, int> Map(IReadOnlyList<MediaStream> before, IReadOnlyList<MediaStream> after)
    {
        var map = new Dictionary<int, int>();
        // Tracks inside the file keep their order; separate files are known by path.
        var oldIn = before.Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
        var newIn = after.Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
        if (oldIn.Count == newIn.Count)
        {
            for (var i = 0; i < oldIn.Count; i++)
            {
                map[oldIn[i].Index] = newIn[i].Index;
            }
        }

        foreach (var s in before.Where(s => s.IsExternal))
        {
            var match = after.FirstOrDefault(a => a.IsExternal && a.Type == s.Type && string.Equals(a.Path, s.Path, StringComparison.Ordinal));
            if (match is not null)
            {
                map[s.Index] = match.Index;
            }
        }

        return map;
    }

    private static Guid Group(BaseItem item) => item is Episode { SeriesId: var id } && !id.Equals(Guid.Empty) ? id : item.Id;

    private void Remap(Video item, IReadOnlyList<MediaStream> before, IReadOnlyList<MediaStream> after)
    {
        var map = Map(before, after);
        foreach (var user in Users().ToList())
        {
            try
            {
                var data = _userData.GetUserData(user, item);
                if (data is null)
                {
                    continue;
                }

                // A choice whose track is gone is cleared; -1 (subtitles off) is kept.
                int? Move(int? i) => i is null or < 0 ? i : map.TryGetValue(i.Value, out var n) ? n : null;
                var audio = Move(data.AudioStreamIndex);
                var sub = Move(data.SubtitleStreamIndex);
                if (audio != data.AudioStreamIndex || sub != data.SubtitleStreamIndex)
                {
                    data.AudioStreamIndex = audio;
                    data.SubtitleStreamIndex = sub;
                    _userData.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Full Auto Subtitles: couldn't move saved track choices for {Item}", item.Name);
            }
        }
    }
}
