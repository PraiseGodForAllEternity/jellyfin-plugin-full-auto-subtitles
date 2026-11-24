using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullAutoSubtitles.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullAutoSubtitles.Api;

public class FixRequest
{
    public int Track { get; set; }

    /// <summary>Gets or sets the track's label, so a list that changed since can't hit another track.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the track to compare against; null downloads an exact match.</summary>
    public int? Reference { get; set; }

    /// <summary>Gets or sets a typed-in shift in seconds. Set means no comparison at all.</summary>
    public double? Shift { get; set; }

    public double? Speed { get; set; }
}

/// <summary>Admin only: everything here spends downloads or writes files.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("FullAutoSubtitles")]
public class FullAutoSubtitlesController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly ISubtitleManager _subtitles;
    private readonly ITaskManager _tasks;
    private readonly TrackService _tracks;
    private readonly TimingService _timing;
    private readonly StateStore _state;
    private readonly ILogger<FullAutoSubtitlesController> _logger;

    public FullAutoSubtitlesController(ILibraryManager library, ISubtitleManager subtitles, ITaskManager tasks, TrackService tracks, TimingService timing, StateStore state, ILogger<FullAutoSubtitlesController> logger)
    {
        _library = library;
        _subtitles = subtitles;
        _tasks = tasks;
        _tracks = tracks;
        _timing = timing;
        _state = state;
        _logger = logger;
    }

    [HttpGet("Status")]
    public ActionResult Status() => _state.Read(s => Ok(new
    {
        s.Used,
        Cap = Plugin.Config.DailyCap,
        Paused = s.PausedUntil > DateTime.UtcNow,
        Providers = _subtitles.GetSupportedProviders(new Movie()).Select(p => p.Name).ToList(),
        History = s.History.Take(60).Select(h => new { h.Id, h.Time, h.Title, h.Language, h.Result, h.Detail, CanUndo = h.CreatedFile is not null }).ToList()
    }));

    [HttpPost("Run")]
    public ActionResult Run()
    {
        _tasks.QueueIfNotRunning<FetchTask>();
        return NoContent();
    }

    [HttpGet("Tracks/{itemId}")]
    public ActionResult Tracks([FromRoute] Guid itemId)
    {
        if (Find(itemId) is not { } item)
        {
            return NotFound();
        }

        return Ok(_tracks.Streams(item).Where(s => s.Type == MediaStreamType.Subtitle)
            .Select(s => new { s.Index, Label = Label(s), s.IsExternal, IsText = s.IsTextSubtitleStream }));
    }

    [HttpPost("Fix/{itemId}/Preview")]
    public Task<ActionResult> Preview([FromRoute] Guid itemId, [FromBody] FixRequest body, CancellationToken token) => Guard(itemId, async item =>
    {
        var a = (await Plan(item, body, token).ConfigureAwait(false)).Alignment;
        return Ok(new { Verdict = a.Verdict.ToString(), a.Shift, a.Speed, a.Match, a.Message });
    });

    [HttpPost("Fix/{itemId}/Apply")]
    public Task<ActionResult> Apply([FromRoute] Guid itemId, [FromBody] FixRequest body, CancellationToken token) => Guard(itemId, async item =>
    {
        // Always from typed or previewed numbers, so a preview's download isn't paid for twice.
        if (body.Shift is null)
        {
            throw new TimingException("Preview the correction first.");
        }

        var track = _tracks.Streams(item).FirstOrDefault(s => s.Index == body.Track && s.Type == MediaStreamType.Subtitle);
        if (track is null || Label(track) != body.Label)
        {
            throw new TimingException("The subtitles changed since the preview. Search again.");
        }

        var entry = await _timing.Apply(item, await Plan(item, body, token).ConfigureAwait(false), token).ConfigureAwait(false);
        return Ok(new { entry.Id, entry.Detail });
    });

    [HttpPost("Undo/{id}")]
    public async Task<ActionResult> Undo([FromRoute] Guid id, CancellationToken token)
    {
        try
        {
            await _timing.Undo(id, token).ConfigureAwait(false);
            return NoContent();
        }
        catch (Exception ex) when (ex is TimingException or BusyException)
        {
            return Conflict(ex.Message);
        }
    }

    private Task<TimingPlan> Plan(Video item, FixRequest body, CancellationToken token) => body.Shift is { } shift
        ? _timing.Manual(item, body.Track, shift, body.Speed ?? 1, token)
        : _timing.Plan(item, body.Track, body.Reference, token);

    private static string Label(MediaStream s) => s.DisplayTitle ?? s.Language ?? "Subtitle";

    private Video? Find(Guid id) => !id.Equals(Guid.Empty) && _library.GetItemById(id) is Video { IsFileProtocol: true, IsShortcut: false } v and (Movie or Episode) ? v : null;

    private async Task<ActionResult> Guard(Guid id, Func<Video, Task<ActionResult>> action)
    {
        if (Find(id) is not { } item)
        {
            return NotFound();
        }

        try
        {
            return await action(item).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimingException or BusyException)
        {
            return Conflict(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Full Auto Subtitles: timing fix failed for {Item}", item.Name);
            return Conflict("That didn't work. See the server log.");
        }
    }
}
