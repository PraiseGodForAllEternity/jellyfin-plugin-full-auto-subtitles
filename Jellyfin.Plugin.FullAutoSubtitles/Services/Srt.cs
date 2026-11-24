using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

public record Cue(double Start, double End, string Text);

public static partial class Srt
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxCues = 6000;

    public static List<Cue> Parse(string content)
    {
        var cues = new List<Cue>();
        var lines = content.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Timing().Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var text = new StringBuilder();
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !Timing().IsMatch(lines[i + 1]))
            {
                text.Append(lines[++i]).Append('\n');
            }

            var start = Seconds(m, 1);
            var end = Seconds(m, 5);
            if (text.Length > 0 && end >= start)
            {
                cues.Add(new Cue(start, end, text.ToString().TrimEnd('\n')));
            }

            if (cues.Count > MaxCues)
            {
                throw new TimingException("That subtitle has too many lines.");
            }
        }

        return cues.OrderBy(c => c.Start).ToList();
    }

    public static string Write(IEnumerable<Cue> cues)
    {
        var sb = new StringBuilder();
        var n = 1;
        foreach (var c in cues.Where(c => c.End > 0))
        {
            sb.Append(n++).Append("\r\n").Append(Stamp(c.Start)).Append(" --> ").Append(Stamp(c.End)).Append("\r\n")
                .Append(c.Text.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
        }

        return sb.ToString();
    }

    private static double Seconds(Match m, int g)
    {
        int N(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
        return (N(g) * 3600) + (N(g + 1) * 60) + N(g + 2) + (N(g + 3) / 1000.0);
    }

    private static string Stamp(double s)
    {
        var ms = (long)Math.Round(Math.Max(0, s) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }

    [GeneratedRegex(@"^\s*([0-9]{1,3}):([0-9]{2}):([0-9]{2})[,.]([0-9]{3})\s*-->\s*([0-9]{1,3}):([0-9]{2}):([0-9]{2})[,.]([0-9]{3})")]
    private static partial Regex Timing();
}
