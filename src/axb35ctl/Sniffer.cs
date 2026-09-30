using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Axb35.Ec;

namespace Axb35Ctl;

/// <summary>
/// Read-only: samples the EC status register (port 0x66) as fast as PawnIO allows and reports every episode in which it
/// left its resting value, to show who else is talking to the EC and how often. It takes no lock and sends nothing to
/// the EC, so it cannot disturb a transaction; it can only miss very short ones.
/// </summary>
internal static class Sniffer
{
    private const double MergeGapMs = 2.0; // departures closer than this belong to one episode
    private const byte Activity = 0x73;    // OBF, IBF, BURST, SCI_EVT, SMI_EVT; CMD (0x08) only says what was written last

    private sealed class Episode
    {
        public long Start, End;
        public byte Flags;
        public readonly List<byte> Sequence = new();
    }

    public static void Run(double seconds, string? csvPath)
    {
        RequireBoard();
        using var ec = new AcpiEc();
        var counts = new long[256];
        var transitions = new List<(long Ticks, byte Status)>(1 << 16);
        long samples = 0;
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        DateTime startUtc = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        long end = (long)(seconds * Stopwatch.Frequency);
        byte last = ec.Status();
        transitions.Add((0, last));
        while (clock.ElapsedTicks < end)
        {
            byte status = ec.Status();
            samples++;
            counts[status]++;
            if (status != last)
            {
                transitions.Add((clock.ElapsedTicks, status));
                last = status;
            }
        }
        long elapsed = clock.ElapsedTicks;

        byte idle = (byte)Array.IndexOf(counts, counts.Max());
        List<Episode> episodes = Episodes(transitions, elapsed);
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        Console.WriteLine($"{samples:N0} samples in {Ms(elapsed) / 1000:0.0} s ({samples / (Ms(elapsed) / 1000):N0}/s, one every {Ms(elapsed) * 1000 / samples:0.0} µs), from {startUtc:HH:mm:ss.fff} UTC");
        Console.WriteLine("status share of time:");
        foreach (int value in Enumerable.Range(0, 256).Where(v => counts[v] > 0).OrderByDescending(v => counts[v]).Take(12))
            Console.WriteLine($"  0x{value:X2}  {Describe((byte)value),-28} {100.0 * counts[value] / samples,8:0.0000} %");
        Console.WriteLine($"resting value 0x{idle:X2}; {episodes.Count} episodes with OBF/IBF/BURST/SCI_EVT/SMI_EVT set (gaps under {MergeGapMs} ms merged)");

        foreach (var group in episodes.GroupBy(e => Kind(e.Flags)).OrderByDescending(g => g.Count()))
        {
            var starts = group.Select(e => e.Start).ToList();
            var gaps = starts.Zip(starts.Skip(1), (a, b) => Ms(b - a)).ToList();
            var lengths = group.Select(e => Ms(e.End - e.Start) * 1000).OrderBy(x => x).ToList();
            Console.WriteLine($"  {group.Key,-34} {group.Count(),6}   length median {lengths[lengths.Count / 2],7:0} µs, max {lengths.Last(),8:0} µs" +
                              (gaps.Count > 0 ? $"   spacing median {Median(gaps),8:0.0} ms" : ""));
            if (gaps.Count > 3)
            {
                var buckets = gaps.GroupBy(g => Math.Round(g / 5) * 5).OrderByDescending(b => b.Count()).Take(6);
                Console.WriteLine("      commonest spacings: " + string.Join(", ", buckets.Select(b => $"{b.Key:0} ms x{b.Count()}")));
            }
        }

        Console.WriteLine("first episodes (t = s since start):");
        foreach (Episode e in episodes.Take(30))
            Console.WriteLine($"  t {Ms(e.Start) / 1000,9:0.000000}  {Ms(e.End - e.Start) * 1000,7:0} µs  {Kind(e.Flags),-34} {Seq(e)}");

        if (csvPath is not null)
        {
            using var writer = new StreamWriter(csvPath);
            writer.WriteLine("utc,t_s,length_us,kind,flags,sequence");
            foreach (Episode e in episodes)
                writer.WriteLine(string.Join(",", startUtc.AddTicks((long)(Ms(e.Start) * TimeSpan.TicksPerMillisecond)).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
                    (Ms(e.Start) / 1000).ToString("0.000000", CultureInfo.InvariantCulture), (Ms(e.End - e.Start) * 1000).ToString("0", CultureInfo.InvariantCulture),
                    Kind(e.Flags), $"0x{e.Flags:X2}", Seq(e)));
            Console.WriteLine($"{episodes.Count} episodes written to {csvPath}");
        }
    }

    private static List<Episode> Episodes(List<(long Ticks, byte Status)> transitions, long elapsed)
    {
        long mergeTicks = (long)(MergeGapMs * Stopwatch.Frequency / 1000);
        var episodes = new List<Episode>();
        Episode? current = null;
        for (int i = 0; i < transitions.Count; i++)
        {
            (long ticks, byte status) = transitions[i];
            if ((status & Activity) != 0)
            {
                if (current is null || ticks - current.End > mergeTicks)
                {
                    current = new Episode { Start = ticks, End = ticks };
                    episodes.Add(current);
                }
                current.Flags |= (byte)(status & Activity);
                if (current.Sequence.Count == 0 || current.Sequence[current.Sequence.Count - 1] != status)
                    current.Sequence.Add(status);
                current.End = i + 1 < transitions.Count ? transitions[i + 1].Ticks : elapsed;
            }
        }
        return episodes;
    }

    private static string Kind(byte flags)
    {
        if ((flags & 0x20) != 0) return "EC event (SCI_EVT) + query";
        if ((flags & 0x10) != 0) return "burst-mode transaction";
        if ((flags & 0x40) != 0) return "SMI event";
        if ((flags & 0x03) != 0) return "plain transaction(s)";
        return "other";
    }

    private static string Describe(byte s)
    {
        var bits = new List<string>();
        if ((s & 0x01) != 0) bits.Add("OBF");
        if ((s & 0x02) != 0) bits.Add("IBF");
        if ((s & 0x08) != 0) bits.Add("CMD");
        if ((s & 0x10) != 0) bits.Add("BURST");
        if ((s & 0x20) != 0) bits.Add("SCI_EVT");
        if ((s & 0x40) != 0) bits.Add("SMI_EVT");
        if ((s & 0x84) != 0) bits.Add($"other 0x{s & 0x84:X2}");
        return bits.Count == 0 ? "(idle)" : string.Join("|", bits);
    }

    private static string Seq(Episode e) =>
        string.Join(" ", e.Sequence.Take(16).Select(s => s.ToString("X2"))) + (e.Sequence.Count > 16 ? $" ... ({e.Sequence.Count})" : "");

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    private static void RequireBoard()
    {
        if (!Axb35Board.IsSupportedBoard(out string board))
            throw new EcException($"Not an AXB35 board ('{board}'); refusing to touch the EC");
    }
}
