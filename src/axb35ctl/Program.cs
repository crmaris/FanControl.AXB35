using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Axb35.Ec;

namespace Axb35Ctl;

/// <summary>
/// Command-line access to the Bosgame M5 / AXB35 EC, sharing its code with the FanControl plugin.
/// For testing over SSH, and as a headless fallback. Needs PawnIO and an elevated shell.
/// </summary>
internal static class Program
{
    private const string Usage = """
        axb35ctl status                      temperature, power mode, fans 1-3 (duty, RPM)
        axb35ctl watch [seconds]             status once a second (default 30 s)
        axb35ctl set <1|2|all> <0-100>       set fan 1 and/or 2 to a duty % once (the EC may take it back)
        axb35ctl hold <1|2|all> <0-100> <s>  hold a duty for <s> seconds, re-writing it every 2 s, then release
        axb35ctl release <1|2|all>           hand fan 1 and/or 2 back to the firmware (0x00)
        axb35ctl dump                        read-only dump of EC RAM 0x00-0xFF, twice, changes marked *
        axb35ctl log <seconds> <file.csv> [--guard <C>]
                                             one CSV line a second: EC and GPU temperature, fan RPM and duty.
                                             --guard: if the EC reaches <C>, hold fans 1 and 2 at 100 %
        """;

    private static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        try
        {
            switch (command)
            {
                case "-h" or "--help" or "help":
                    Console.WriteLine(Usage);
                    return 0;
                case "dump":
                    Dump();
                    return 0;
            }

            using Axb35Board board = Axb35Board.Open();
            switch (command)
            {
                case "status":
                    PrintStatus(board);
                    return 0;
                case "watch":
                    int seconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 30;
                    for (int i = 0; i < seconds; i++)
                    {
                        PrintLine(board);
                        Thread.Sleep(1000);
                    }
                    return 0;
                case "set" when args.Length == 3:
                    int duty = int.Parse(args[2].TrimEnd('%'), CultureInfo.InvariantCulture);
                    if (duty < 0 || duty > 100)
                        throw new ArgumentException("duty must be 0..100");
                    foreach (int fan in ParseFans(args[1]))
                        board.SetDuty(fan, duty);
                    PrintStatus(board);
                    return 0;
                case "log" when args.Length >= 3:
                    int? guard = null;
                    int g = Array.IndexOf(args, "--guard");
                    if (g > 0 && g + 1 < args.Length)
                        guard = int.Parse(args[g + 1], CultureInfo.InvariantCulture);
                    Log(board, int.Parse(args[1], CultureInfo.InvariantCulture), args[2], guard);
                    return 0;
                case "hold" when args.Length == 4:
                    int holdDuty = int.Parse(args[2].TrimEnd('%'), CultureInfo.InvariantCulture);
                    var holdFans = ParseFans(args[1]).ToList();
                    int holdSeconds = int.Parse(args[3], CultureInfo.InvariantCulture);
                    try
                    {
                        for (int i = 0; i < holdSeconds; i++)
                        {
                            if (i % 2 == 0)
                                foreach (int fan in holdFans)
                                    board.SetDuty(fan, holdDuty);
                            PrintLine(board);
                            Thread.Sleep(1000);
                        }
                    }
                    finally
                    {
                        foreach (int fan in holdFans)
                            board.Release(fan);
                    }
                    return 0;
                case "release" when args.Length == 2:
                    foreach (int fan in ParseFans(args[1]))
                        board.Release(fan);
                    PrintStatus(board);
                    return 0;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is EcException or ArgumentException or FormatException)
        {
            Console.Error.WriteLine("axb35ctl: " + ex.Message);
            return 1;
        }
    }

    private static void PrintStatus(Axb35Board board)
    {
        Console.WriteLine($"board        {Axb35Board.BoardDescription()}");
        Console.WriteLine($"temperature  {board.ReadTemperature()} C   (EC 0x70)");
        Console.WriteLine($"power mode   0x{board.ReadPowerModeRaw():X2}   (EC 0x31, read-only)");
        for (int fan = 0; fan < Axb35Board.FanCount; fan++)
        {
            FanReading f = board.ReadFan(fan);
            string control = !f.Controllable ? "no control register" : f.Manual ? $"held at {f.Duty}%" : "firmware";
            Console.WriteLine($"fan {fan + 1}        {f.Rpm,5} rpm   {control}" + (f.Controllable ? $"   (raw 0x{f.DutyRaw:X2})" : ""));
        }
        Console.WriteLine($"EC retries   {board.EcRetries}");
    }

    private static void PrintLine(Axb35Board board)
    {
        var fans = Enumerable.Range(0, Axb35Board.FanCount).Select(board.ReadFan).ToList();
        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {board.ReadTemperature(),3} C  " +
                          string.Join("  ", fans.Select(f => $"fan{f.Fan + 1} {f.Rpm,5} rpm {(f.Controllable ? f.Manual ? $"{f.Duty,3}%" : "fw  " : "    ")}")));
    }

    // Each line is flushed through to disk, so the last reading before a freeze or power loss survives.
    private static void Log(Axb35Board board, int seconds, string path, int? guardCelsius)
    {
        bool guardFired = false;
        using var file = new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read, 4096, System.IO.FileOptions.WriteThrough);
        using var writer = new System.IO.StreamWriter(file);
        if (file.Length == 0)
            writer.WriteLine("utc,ec_c,gpu_c,fan1_rpm,fan2_rpm,fan3_rpm,fan1_duty,fan2_duty,note");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < seconds; i++)
        {
            string note = "";
            try
            {
                int ec = board.ReadTemperature();
                double? gpu = GpuTemperature.Read();
                var fans = Enumerable.Range(0, Axb35Board.FanCount).Select(board.ReadFan).ToList();
                if (guardCelsius is int limit && ec >= limit && !guardFired)
                {
                    guardFired = true;
                    note = $"GUARD {ec}C: fans 1-2 to 100%";
                }
                if (guardFired && i % 2 == 0)
                {
                    board.SetDuty(0, 100); // re-asserted: the EC takes a held fan back on its own
                    board.SetDuty(1, 100);
                }
                string Duty(FanReading f) => f.Manual ? f.Duty.ToString() : "fw";
                writer.WriteLine(string.Join(",", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ec,
                    gpu?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", fans[0].Rpm, fans[1].Rpm, fans[2].Rpm,
                    Duty(fans[0]), Duty(fans[1]), note));
            }
            catch (EcException ex)
            {
                writer.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ},,,,,,,,read failed: {ex.Message.Replace(',', ';')}");
            }
            writer.Flush();
            file.Flush(true);
            int wait = (i + 1) * 1000 - (int)clock.ElapsedMilliseconds;
            if (wait > 0)
                Thread.Sleep(wait);
        }
    }

    private static void Dump()
    {
        byte[] first = Axb35Board.DumpRegisters();
        Thread.Sleep(2000);
        byte[] second = Axb35Board.DumpRegisters();
        Console.WriteLine("      " + string.Join("  ", Enumerable.Range(0, 16).Select(i => $"{i:X2}")));
        for (int row = 0; row < 256; row += 16)
        {
            var cells = Enumerable.Range(row, 16).Select(i => $"{second[i]:X2}" + (first[i] != second[i] ? "*" : " "));
            Console.WriteLine($"  {row:X2}  " + string.Join(" ", cells));
        }
        Console.WriteLine($"second pass: {Axb35Board.LastDumpRetries} retries, failed registers: " +
                          (Axb35Board.LastDumpFailures.Count == 0 ? "none" : string.Join(" ", Axb35Board.LastDumpFailures.Select(r => $"0x{r:X2}"))));
    }

    private static IEnumerable<int> ParseFans(string text)
    {
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
            return new[] { 0, 1 };
        int fan = int.Parse(text, CultureInfo.InvariantCulture);
        if (fan < 1 || fan > Axb35Board.ControllableFans)
            throw new ArgumentException("only fans 1 and 2 can be controlled (fan 3 has no known control register)");
        return new[] { fan - 1 };
    }
}
