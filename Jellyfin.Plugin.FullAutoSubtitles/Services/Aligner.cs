using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.FullAutoSubtitles.Services;

public enum AlignVerdict
{
    AlreadyInSync,
    Fixed,
    NotConfident
}

public record Alignment(AlignVerdict Verdict, double Shift, double Speed, double Match, string Message);

/// <summary>
/// Finds the shift and speed that line one subtitle's timings up with another's.
/// Pure timing comparison, so the two can be in different languages.
/// </summary>
public static class Aligner
{
    private const double Bin = 0.1;
    private const double Window = 0.3;
    private const double MaxShift = 120;

    private static readonly double[] Speeds =
    {
        1, 25 / 23.976, 23.976 / 25, 24 / 23.976, 23.976 / 24, 25.0 / 24, 24.0 / 25
    };

    public static Alignment Align(IReadOnlyList<Cue> target, IReadOnlyList<Cue> reference)
    {
        if (target.Count < 30 || reference.Count < 30)
        {
            return new Alignment(AlignVerdict.NotConfident, 0, 1, 0, "Too few lines to compare (30 needed).");
        }

        var t = target.Select(c => c.Start).ToArray();
        var r = reference.Select(c => c.Start).OrderBy(x => x).ToArray();

        var tried = new List<(double Speed, double Shift, double Match)>();
        foreach (var speed in Speeds)
        {
            foreach (var shift in Peaks(t, r, speed))
            {
                tried.Add(Refine(t, r, speed, shift));
            }
        }

        var best = tried.OrderByDescending(x => x.Match).ThenBy(x => Math.Abs(x.Speed - 1)).First();
        // Nothing else that's really different may come close.
        var rival = tried.Where(x => Math.Abs((x.Speed - best.Speed) * t[^1]) + Math.Abs(x.Shift - best.Shift) > 2)
            .Select(x => x.Match).DefaultIfEmpty(0).Max();

        if (best.Match < 0.4 || rival > best.Match * 0.75)
        {
            return new Alignment(AlignVerdict.NotConfident, best.Shift, best.Speed, best.Match, "The two subtitles don't line up clearly enough.");
        }

        // A different cut lines up in one part and not another.
        for (var part = 0; part < 3; part++)
        {
            var slice = t.Skip(part * t.Length / 3).Take(t.Length / 3).ToArray();
            if (MatchRate(slice, r, best.Speed, best.Shift, out _) < best.Match * 0.6)
            {
                return new Alignment(AlignVerdict.NotConfident, best.Shift, best.Speed, best.Match, "Only part of it lines up. It was probably made for a different cut.");
            }
        }

        var moved = Math.Max(Math.Abs((t[0] * (best.Speed - 1)) + best.Shift), Math.Abs((t[^1] * (best.Speed - 1)) + best.Shift));
        return moved < 0.15
            ? new Alignment(AlignVerdict.AlreadyInSync, 0, 1, best.Match, "Already in sync with the reference.")
            : new Alignment(AlignVerdict.Fixed, best.Shift, best.Speed, best.Match, "Lines up with the reference after this correction.");
    }

    public static List<Cue> Apply(IEnumerable<Cue> cues, Alignment a)
        => cues.Select(c => new Cue((c.Start * a.Speed) + a.Shift, (c.End * a.Speed) + a.Shift, c.Text)).ToList();

    // Every pair of lines votes for the shift that would line it up.
    private static IEnumerable<double> Peaks(double[] t, double[] r, double speed)
    {
        var half = (int)(MaxShift / Bin);
        var votes = new int[(2 * half) + 1];
        var lo = 0;
        foreach (var x in t)
        {
            var p = x * speed;
            while (lo < r.Length && r[lo] < p - MaxShift)
            {
                lo++;
            }

            for (var i = lo; i < r.Length && r[i] <= p + MaxShift; i++)
            {
                var b = (int)Math.Round((r[i] - p) / Bin) + half;
                if (b >= 0 && b < votes.Length)
                {
                    votes[b]++;
                }
            }
        }

        var smooth = new int[votes.Length];
        for (var i = 1; i < votes.Length - 1; i++)
        {
            smooth[i] = votes[i - 1] + votes[i] + votes[i + 1];
        }

        for (var k = 0; k < 3; k++)
        {
            var at = Array.IndexOf(smooth, smooth.Max());
            yield return (at - half) * Bin;
            for (var i = Math.Max(0, at - 10); i <= Math.Min(smooth.Length - 1, at + 10); i++)
            {
                smooth[i] = -1;
            }
        }
    }

    private static (double Speed, double Shift, double Match) Refine(double[] t, double[] r, double speed, double shift)
    {
        for (var i = 0; i < 3; i++)
        {
            MatchRate(t, r, speed, shift, out var residual);
            shift += residual;
        }

        return (speed, shift, MatchRate(t, r, speed, shift, out _));
    }

    private static double MatchRate(double[] t, double[] r, double speed, double shift, out double residual)
    {
        var off = new List<double>();
        foreach (var x in t)
        {
            var p = (x * speed) + shift;
            var i = Array.BinarySearch(r, p);
            i = i < 0 ? ~i : i;
            var d = double.MaxValue;
            if (i < r.Length)
            {
                d = r[i] - p;
            }

            if (i > 0 && Math.Abs(r[i - 1] - p) < Math.Abs(d))
            {
                d = r[i - 1] - p;
            }

            if (Math.Abs(d) <= Window)
            {
                off.Add(d);
            }
        }

        off.Sort();
        residual = off.Count == 0 ? 0 : off[off.Count / 2];
        return t.Length == 0 ? 0 : (double)off.Count / t.Length;
    }
}
