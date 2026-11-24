using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

public class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Time { get; set; } = DateTime.UtcNow;

    public Guid ItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;

    public string Result { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets the file Fix timing created, and its hash, so undo removes only that.</summary>
    public string? CreatedFile { get; set; }

    public string? CreatedHash { get; set; }
}

public class State
{
    public string Day { get; set; } = string.Empty;

    public int Used { get; set; }

    public DateTime? PausedUntil { get; set; }

    public Dictionary<string, DateTime> Misses { get; set; } = new();

    public List<HistoryEntry> History { get; set; } = new();
}

/// <summary>Small persisted state: today's count, misses and recent activity.</summary>
public class StateStore
{
    private readonly object _lock = new();
    private readonly string _file;
    private readonly ILogger<StateStore> _logger;
    private State _state = new();

    public StateStore(IApplicationPaths paths, ILogger<StateStore> logger)
    {
        _logger = logger;
        _file = Path.Combine(paths.PluginConfigurationsPath, "FullAutoSubtitles.state.json");
        try
        {
            if (File.Exists(_file))
            {
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_file)) ?? new State();
                _state.Misses ??= new();
                _state.History = (_state.History ?? new()).Where(h => h is not null).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Full Auto Subtitles: couldn't read saved state; starting fresh");
        }
    }

    public T Read<T>(Func<State, T> read)
    {
        lock (_lock)
        {
            Roll();
            return read(_state);
        }
    }

    public T Change<T>(Func<State, T> change)
    {
        lock (_lock)
        {
            Roll();
            var result = change(_state);
            var now = DateTime.UtcNow;
            foreach (var old in _state.Misses.Where(m => m.Value < now).Select(m => m.Key).ToList())
            {
                _state.Misses.Remove(old);
            }

            // Fixes stay undoable for longer than plain activity.
            var keep = _state.History.Where(h => h.CreatedFile is not null).Take(500)
                .Concat(_state.History.Where(h => h.CreatedFile is null).Take(200)).ToHashSet();
            _state.History = _state.History.Where(keep.Contains).ToList();
            try
            {
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
                File.Move(tmp, _file, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Full Auto Subtitles: couldn't save state");
            }

            return result;
        }
    }

    public void Log(HistoryEntry entry) => Change(s =>
    {
        s.History.Insert(0, entry);
        return true;
    });

    /// <summary>Takes one download from today's allowance, or says no.</summary>
    public bool TryTakeDownload(int cap) => Change(s =>
    {
        if (s.Used >= cap || s.PausedUntil > DateTime.UtcNow)
        {
            return false;
        }

        s.Used++;
        return true;
    });

    private void Roll()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (_state.Day != today)
        {
            _state.Day = today;
            _state.Used = 0;
        }
    }
}
