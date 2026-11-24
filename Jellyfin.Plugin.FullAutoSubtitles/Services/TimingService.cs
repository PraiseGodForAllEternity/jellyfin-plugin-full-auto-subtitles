using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

/// <summary>A problem worth showing to the admin as is.</summary>
public class TimingException : Exception
{
    public TimingException(string message)
        : base(message)
    {
    }
}

public record TimingPlan(Alignment Alignment, List<Cue> Cues, MediaStream Track);

/// <summary>Makes corrected copies of subtitles. Originals are never touched.</summary>
public partial class TimingService
{
    private readonly TrackService _tracks;
    private readonly SubtitleNeeds _needs;
    private readonly StateStore _state;
    private readonly ISubtitleEncoder _encoder;
    private readonly ISubtitleManager _subtitles;
    private readonly ILibraryManager _library;
    private readonly ILibraryMonitor _monitor;

    public TimingService(TrackService tracks, SubtitleNeeds needs, StateStore state, ISubtitleEncoder encoder, ISubtitleManager subtitles, ILibraryManager library, ILibraryMonitor monitor)
    {
        _tracks = tracks;
        _needs = needs;
        _state = state;
        _encoder = encoder;
        _subtitles = subtitles;
        _library = library;
        _monitor = monitor;
    }

    /// <summary>Works out the correction. A null reference downloads an exact match to compare against.</summary>
    public async Task<TimingPlan> Plan(Video item, int target, int? reference, CancellationToken token)
    {
        var track = TextTrack(item, target);
        if (reference == target)
        {
            throw new TimingException("Pick a different track to compare against.");
        }

        var cues = Srt.Parse(await ReadTrack(item, track, token).ConfigureAwait(false));
        var other = Srt.Parse(reference is { } r
            ? await ReadTrack(item, TextTrack(item, r), token).ConfigureAwait(false)
            : await DownloadReference(item, track, token).ConfigureAwait(false));
        return new TimingPlan(Aligner.Align(cues, other), cues, track);
    }

    /// <summary>A correction the admin typed in.</summary>
    public async Task<TimingPlan> Manual(Video item, int target, double shift, double speed, CancellationToken token)
    {
        if (!double.IsFinite(shift) || Math.Abs(shift) > 600 || !double.IsFinite(speed) || speed is < 0.9 or > 1.1)
        {
            throw new TimingException("The shift must be within 600 seconds and the speed between 0.9 and 1.1.");
        }

        var track = TextTrack(item, target);
        var cues = Srt.Parse(await ReadTrack(item, track, token).ConfigureAwait(false));
        return new TimingPlan(new Alignment(AlignVerdict.Fixed, shift, speed, 1, "Manual correction."), cues, track);
    }

    public async Task<HistoryEntry> Apply(Video item, TimingPlan plan, CancellationToken token)
    {
        if (plan.Alignment.Verdict != AlignVerdict.Fixed)
        {
            throw new TimingException(plan.Alignment.Message);
        }

        var fixedCues = Aligner.Apply(plan.Cues, plan.Alignment).Where(c => c.End > 0).ToList();
        if (fixedCues.Count < Math.Max(1, plan.Cues.Count / 2))
        {
            throw new TimingException("That correction would push most of the lines off the start.");
        }

        var bytes = new UTF8Encoding(true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(Srt.Write(fixedCues))).ToArray();
        var language = Unsafe().Replace(plan.Track.Language?.ToLowerInvariant() ?? string.Empty, string.Empty);
        var flags = (plan.Track.IsForced ? ".forced" : string.Empty) + (plan.Track.IsHearingImpaired ? ".sdh" : string.Empty);
        var folders = new List<string> { item.GetInternalMetadataPath() };
        if (_library.GetLibraryOptions(item).SaveSubtitlesWithMedia)
        {
            folders.Insert(0, item.ContainingFolderPath);
        }

        var stem = Path.GetFileNameWithoutExtension(item.Path);
        string? written = null;

        await _tracks.Change(
            item,
            async () =>
            {
                foreach (var folder in folders)
                {
                    try
                    {
                        written = await WriteNew(folder, stem, (language.Length > 0 ? "." + language : string.Empty) + flags, bytes).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && folder != folders[^1])
                    {
                        // Media folder is read-only; use Jellyfin's own folder.
                    }
                }

                if (written is null)
                {
                    throw new TimingException("There are already too many corrected copies.");
                }
            },
            token).ConfigureAwait(false);

        var entry = new HistoryEntry
        {
            ItemId = item.Id,
            Title = FetchService.Title(item),
            Language = language,
            Result = "Timing fixed",
            Detail = FormattableString.Invariant($"Shift {plan.Alignment.Shift:+0.00;-0.00}s, speed {plan.Alignment.Speed:0.0000}"),
            CreatedFile = written,
            CreatedHash = Convert.ToHexString(SHA256.HashData(bytes))
        };
        _state.Log(entry);
        return entry;
    }

    public async Task Undo(Guid id, CancellationToken token)
    {
        var entry = _state.Read(s => s.History.FirstOrDefault(h => h.Id.Equals(id)));
        if (entry?.CreatedFile is not { } path || _library.GetItemById(entry.ItemId) is not Video item)
        {
            throw new TimingException("That correction can't be undone any more.");
        }

        await _tracks.Change(
            item,
            async () =>
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)));
                if (hash != entry.CreatedHash)
                {
                    throw new TimingException("The file was changed since, so it was left alone.");
                }

                _monitor.ReportFileSystemChangeBeginning(path);
                try
                {
                    File.Delete(path);
                }
                finally
                {
                    _monitor.ReportFileSystemChangeComplete(path, false);
                }
            },
            token).ConfigureAwait(false);

        _state.Change(s =>
        {
            entry.CreatedFile = null;
            entry.Result = "Undone";
            return true;
        });
    }

    private async Task<string?> WriteNew(string folder, string stem, string suffix, byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        for (var n = 1; n < 50; n++)
        {
            var path = Path.Combine(folder, $"{stem}.synced{(n == 1 ? string.Empty : n)}{suffix}.srt");
            if (File.Exists(path))
            {
                continue;
            }

            var created = false;
            _monitor.ReportFileSystemChangeBeginning(path);
            try
            {
                // CreateNew: never overwrites anything.
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                created = true;
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
                }

                return path;
            }
            catch when (created)
            {
                File.Delete(path);
                throw;
            }
            finally
            {
                _monitor.ReportFileSystemChangeComplete(path, false);
            }
        }

        return null;
    }

    private MediaStream TextTrack(Video item, int index)
    {
        var track = _tracks.Streams(item).FirstOrDefault(s => s.Index == index && s.Type == MediaStreamType.Subtitle)
            ?? throw new TimingException("That subtitle doesn't exist any more. Search again.");
        return track.IsTextSubtitleStream ? track : throw new TimingException("Picture-based subtitles can't be corrected here.");
    }

    private async Task<string> ReadTrack(Video item, MediaStream track, CancellationToken token)
    {
        var stream = await _encoder.GetSubtitles(item, item.Id.ToString("N"), track.Index, "srt", 0, 0, true, token).ConfigureAwait(false);
        return await ReadText(stream, token).ConfigureAwait(false);
    }

    private async Task<string> DownloadReference(Video item, MediaStream track, CancellationToken token)
    {
        if (string.IsNullOrEmpty(track.Language))
        {
            throw new TimingException("This subtitle has no language set, so no reference can be searched for.");
        }

        var config = Plugin.Config;
        var results = await _subtitles.SearchSubtitles(item, track.Language, true, true, token).ConfigureAwait(false);
        var best = _needs.Best(results, config, true) ?? throw new TimingException("No exact match was found to compare against.");
        if (!_state.TryTakeDownload(Math.Clamp(config.DailyCap, 0, 1000)))
        {
            throw new TimingException("Today's download allowance is used up.");
        }

        var response = await _subtitles.GetRemoteSubtitles(best.Id, token).ConfigureAwait(false);
        if (!string.Equals(response.Format, "srt", StringComparison.OrdinalIgnoreCase))
        {
            response.Stream.Dispose();
            throw new TimingException("The exact match isn't in a format that can be compared.");
        }

        return await ReadText(response.Stream, token).ConfigureAwait(false);
    }

    private static async Task<string> ReadText(Stream stream, CancellationToken token)
        => SubtitleText.Decode(await SubtitleText.ReadBytes(stream, token).ConfigureAwait(false));

    [GeneratedRegex("[^a-z-]")]
    private static partial Regex Unsafe();
}
