using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

/// <summary>
/// Finds the film that comes after a movie in its series: from a Jellyfin collection it's in,
/// or else from the TMDb film series in its metadata.
/// </summary>
public class FilmSeries
{
    private readonly ILibraryManager _library;
    private readonly ILogger<FilmSeries> _logger;

    public FilmSeries(ILibraryManager library, ILogger<FilmSeries> logger)
    {
        _library = library;
        _logger = logger;
    }

    public Movie? Next(User user, Movie movie)
    {
        try
        {
            foreach (var films in Series(user, movie))
            {
                var at = films.FindIndex(f => f.Id.Equals(movie.Id));
                if (at >= 0 && at + 1 < films.Count)
                {
                    return films[at + 1];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Full Auto Subtitles: couldn't read the series for {Name}", movie.Name);
        }

        return null;
    }

    /// <summary>Whether two movies belong to the same film series, as this user sees them.</summary>
    public bool Same(User user, Movie movie, BaseItem other)
    {
        try
        {
            return Series(user, movie).Any(films => films.Any(f => f.Id.Equals(other.Id)));
        }
        catch (Exception ex)
        {
            // When unsure, say yes: the caller then waits instead of risking a wrong track.
            _logger.LogDebug(ex, "Full Auto Subtitles: couldn't read the series for {Name}", movie.Name);
            return true;
        }
    }

    private IEnumerable<List<Movie>> Series(User user, Movie movie)
    {
        // Collections first, smallest first: a trilogy beats a 40-film franchise list.
        var boxSets = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true,
            DtoOptions = new DtoOptions(false)
        }).OfType<BoxSet>()
            .Where(b => (b.LinkedChildren ?? Array.Empty<LinkedChild>()).Any(c => Links(c, movie)))
            .OrderBy(b => b.LinkedChildren?.Length ?? 0);
        foreach (var boxSet in boxSets)
        {
            yield return Ordered(user, boxSet.GetLinkedChildren().OfType<Movie>());
        }

        // List queries can leave provider ids out; the full record has them.
        var full = _library.GetItemById(movie.Id) as Movie ?? movie;
        var tmdb = full.GetProviderId(MetadataProvider.TmdbCollection);
        if (string.IsNullOrWhiteSpace(tmdb))
        {
            yield break;
        }

        var matches = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.TmdbCollection.ToString()] = tmdb },
            DtoOptions = new DtoOptions(false)
        });

        // No real series is this big; treat it as no series rather than one giant one.
        if (matches.Count <= 50)
        {
            yield return Ordered(user, matches.OfType<Movie>().Where(m =>
                m.GetProviderId(MetadataProvider.TmdbCollection) is not { Length: > 0 } id
                || string.Equals(id, tmdb, StringComparison.Ordinal)));
        }
    }

    private static List<Movie> Ordered(User user, IEnumerable<Movie> films) => films
        .Where(m => m.IsVisibleStandalone(user))
        .GroupBy(m => m.Id)
        .Select(g => g.First())
        .OrderBy(m => m.PremiereDate ?? (m.ProductionYear is { } y ? new DateTime(y, 1, 1) : DateTime.MaxValue))
        .ThenBy(m => m.SortName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    // Collections store members by item id, library id or file path, depending on their age.
    private static bool Links(LinkedChild? child, BaseItem item)
    {
        if (child is null)
        {
            return false;
        }

        if (child.ItemId.HasValue && child.ItemId.Value.Equals(item.Id))
        {
            return true;
        }

#pragma warning disable CS0618
        return (Guid.TryParse(child.LibraryItemId, out var libraryId) && libraryId.Equals(item.Id))
            || (!string.IsNullOrEmpty(child.Path) && string.Equals(child.Path, item.Path, StringComparison.OrdinalIgnoreCase));
#pragma warning restore CS0618
    }
}
