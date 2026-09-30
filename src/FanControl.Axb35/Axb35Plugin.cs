using System;
using Axb35.Ec;
using FanControl.Plugins;

namespace FanControl.Axb35;

/// <summary>
/// FanControl plugin for the ITE IT5570E embedded controller of the Bosgame M5 / BeyondMax
/// (Sixunited AXB35-02), which LibreHardwareMonitor does not drive. Reads and holds the fans through the BIOS's own
/// WMI interface where it exists (memory-mapped EC RAM, no EC-port handshake), otherwise through PawnIO's signed
/// LpcACPIEC module. Exposes the EC temperature, the RPM of fans 1-3, and fans 1 and 2 as controls (0-100 %). A fan
/// FanControl releases, or every fan when FanControl closes, goes back to the firmware (duty register 0x00, its boot
/// value).
/// </summary>
public sealed class Axb35Plugin : IPlugin2
{
    private readonly IPluginLogger _logger;
    // FanControl names each plugin sensor "<plugin Name>/<sensor Id>" (PluginSensor.Identifier in
    // FanControl.Library), so these become "AXB35 EC/fan/1/control" etc. in userConfig.json. Changing
    // PluginName or an Id orphans every saved curve that uses it.
    internal const string PluginName = "AXB35 EC";

    // Fan 3 has no BIOS WMI reading, so with that interface in use it is the only regular EC-port traffic left: read it
    // every 30 s (3 short transactions), not every cycle. It is a monitoring value only; it has no control register.
    // (On the test unit it reads 0 when cool and ~700 rpm under load, so it cannot be skipped when it reads 0.)
    private static readonly TimeSpan Fan3IntervalWithWmi = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstStats = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromHours(24);

    private readonly Axb35Sensor _temperature = new("ec/temperature", "AXB35 EC temperature");
    private readonly Axb35Sensor[] _rpm = new Axb35Sensor[Axb35Board.FanCount];
    private readonly Axb35FanControl[] _controls = new Axb35FanControl[Axb35Board.ControllableFans];
    private Axb35Board? _board;
    private DateTime _nextFan3 = DateTime.MinValue;
    private DateTime _nextStats = DateTime.MaxValue;
    private long _updates;
    private bool _usedBiosWmi;
    private DateTime _lastErrorLogged = DateTime.MinValue;

    public Axb35Plugin(IPluginLogger logger)
    {
        _logger = logger;
        for (int fan = 0; fan < Axb35Board.FanCount; fan++)
            _rpm[fan] = new Axb35Sensor($"fan/{fan + 1}/rpm", $"AXB35 Fan {fan + 1}");
        for (int fan = 0; fan < Axb35Board.ControllableFans; fan++)
            _controls[fan] = new Axb35FanControl(this, fan, $"{PluginName}/{_rpm[fan].Id}"); // paired id is used verbatim, no prefix added
    }

    public string Name => PluginName;

    internal Axb35Board? Board => _board;

    public void Initialize()
    {
        try
        {
            _board = Axb35Board.Open();
            Log($"EC opened on {Axb35Board.BoardDescription()}; {_board.ReadTemperature()} C; through {_board.Transport}");
            _usedBiosWmi = _board.UsesBiosWmi;
            _nextStats = DateTime.UtcNow + FirstStats;
        }
        catch (Exception ex)
        {
            _board = null;
            Log("not loaded: " + ex.Message);
        }
    }

    public void Load(IPluginSensorsContainer container)
    {
        if (_board is null)
            return;
        container.TempSensors.Add(_temperature);
        foreach (Axb35Sensor rpm in _rpm)
            container.FanSensors.Add(rpm);
        foreach (Axb35FanControl control in _controls)
            container.ControlSensors.Add(control);
    }

    public void Update()
    {
        Axb35Board? board = _board;
        if (board is null)
            return;
        try
        {
            _temperature.Value = board.ReadTemperature();
            for (int fan = 0; fan < Axb35Board.ControllableFans; fan++)
            {
                FanReading reading = board.ReadFan(fan);
                _rpm[fan].Value = reading.Rpm;
                _controls[fan].Observe(reading);
            }
            if (DateTime.UtcNow >= _nextFan3)
            {
                // Scheduled before the read, so a fan-3 read that keeps failing costs one attempt per interval, not one
                // per cycle (each holds the board while it retries, which would delay the fan 1-2 re-assert).
                _nextFan3 = board.UsesBiosWmi ? DateTime.UtcNow + Fan3IntervalWithWmi : DateTime.MinValue;
                try { _rpm[2].Value = board.ReadFan(2).Rpm; }
                catch (Exception ex) { LogThrottled("fan 3 read failed: " + ex.Message); }
            }
            if (_usedBiosWmi && !board.UsesBiosWmi)
            {
                _usedBiosWmi = false;
                Log("switched to " + board.Transport);
            }
            _updates++;
            if (DateTime.UtcNow >= _nextStats)
            {
                _nextStats = DateTime.UtcNow + StatsInterval;
                Log($"since start: {_updates} updates, {board.WmiCalls} BIOS WMI calls, {board.EcTransactions} EC-port transactions " +
                    $"({board.EcDeferrals} waited for another EC user, {board.EcRetries} retried)" +
                    (board.EcIgnoredStatusBits != 0 ? $"; this EC rests with status bits 0x{board.EcIgnoredStatusBits:X2}" : ""));
            }
        }
        catch (Exception ex)
        {
            LogThrottled("update failed: " + ex.Message);
        }
    }

    public void Close()
    {
        Axb35Board? board = _board;
        _board = null;
        if (board is null)
            return;
        board.StopHolding();
        for (int fan = 0; fan < Axb35Board.ControllableFans; fan++)
            ReleaseFan(board, fan, logSuccess: false);
        board.Dispose();
    }

    // The one bad way out is a fan left held at the duty the curve last asked for, with nothing re-asserting it: at
    // idle that duty is low. If the hand-back fails, hold the fan at MaxDuty instead, so it fails loud rather than hot
    // (and if the EC takes it back later, it goes to the firmware curve).
    internal void ReleaseFan(Axb35Board board, int fan, bool logSuccess)
    {
        try
        {
            board.Release(fan);
            if (logSuccess)
                Log($"fan {fan + 1}: released to the firmware");
        }
        catch (Exception ex)
        {
            Log($"fan {fan + 1}: could not hand back to the firmware: {ex.Message}");
            try
            {
                board.HoldAfterFailedRelease(fan, Axb35FanControl.MaxDuty);
                Log($"fan {fan + 1}: held at {Axb35FanControl.MaxDuty}% instead");
            }
            catch (Exception fallback)
            {
                Log($"fan {fan + 1}: could not hold it at {Axb35FanControl.MaxDuty}% either: {fallback.Message}");
            }
        }
    }

    internal void Log(string message) => _logger.Log($"[{Name}] {message}");

    internal void LogThrottled(string message)
    {
        if (DateTime.UtcNow - _lastErrorLogged < TimeSpan.FromMinutes(1))
            return;
        _lastErrorLogged = DateTime.UtcNow;
        Log(message);
    }
}

internal sealed class Axb35Sensor : IPluginSensor
{
    public Axb35Sensor(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public float? Value { get; set; }

    public void Update()
    {
        // Values are read together in Axb35Plugin.Update, once per cycle.
    }
}

internal sealed class Axb35FanControl : IPluginControlSensor2
{
    private readonly Axb35Plugin _plugin;
    private readonly int _fan;
    private int _appliedDuty = -1; // -1 = not holding the fan; the firmware is in charge
    private bool _announced;       // log the first time FanControl takes the fan, once per release

    public Axb35FanControl(Axb35Plugin plugin, int fan, string pairedFanSensorId)
    {
        _plugin = plugin;
        _fan = fan;
        PairedFanSensorId = pairedFanSensorId;
        Id = $"fan/{fan + 1}/control";
        Name = $"AXB35 Fan {fan + 1}";
    }

    public string Id { get; }
    public string Name { get; }
    public string PairedFanSensorId { get; }
    public float? Value { get; private set; }

    // The EC firmware takes a held fan back on its own after a while, WITHOUT changing the duty register:
    // measured on a Bosgame M5 under full load, a fan held at "100 %" (register 0xE4) sank from ~4,400 to
    // ~1,300 rpm within a minute of the last write. Re-writing the duty every couple of seconds keeps it
    // held, which is what github.com/nathanmarlor/strix-halo-fan-control does on Linux too.
    private static readonly TimeSpan Reassert = TimeSpan.FromSeconds(2);
    private DateTime _lastWrite = DateTime.MinValue;

    // The board cannot hold BOTH fans at 100 % once the chip is hot: measured on a Bosgame M5 at 98 °C with all
    // 32 threads loaded, one of the two fans collapses (to ~1,400-2,000 rpm, repeatedly), whichever it is. At
    // 80 % both held ~4,000 rpm steadily - more total airflow than 100 % (8,000 vs 6,600 rpm combined) and far
    // more than the stock firmware (~6,200). So FanControl's 81-100 % all mean 80 % on the EC.
    internal const int MaxDuty = 80;

    public void Set(float val)
    {
        // Round up: a fan never runs slower than FanControl asked for (up to the MaxDuty ceiling).
        int duty = (int)Math.Ceiling(val - 1e-3);
        duty = duty < 0 ? 0 : duty > MaxDuty ? MaxDuty : duty;
        if (duty == _appliedDuty && DateTime.UtcNow - _lastWrite < Reassert)
            return; // FanControl calls Set every cycle; unchanged duties are re-written every 2 s only
        try
        {
            _plugin.Board?.SetDuty(_fan, duty);
            _lastWrite = DateTime.UtcNow;
            if (_appliedDuty < 0 && !_announced)
            {
                _plugin.Log($"fan {_fan + 1}: now driven by FanControl ({duty}%)");
                _announced = true;
            }
            _appliedDuty = duty;
            Value = duty;
        }
        catch (Exception ex)
        {
            _appliedDuty = -1;
            _plugin.LogThrottled($"fan {_fan + 1}: set {val:0}% failed: {ex.Message}");
        }
    }

    public void Reset()
    {
        Axb35Board? board = _plugin.Board;
        if (board is not null)
            _plugin.ReleaseFan(board, _fan, logSuccess: true);
        _appliedDuty = -1;
        _announced = false;
        Value = null;
    }

    public void Update()
    {
        // Values are read together in Axb35Plugin.Update, once per cycle.
    }

    internal void Observe(FanReading reading)
    {
        // Read through the BIOS WMI interface there is no duty to compare; the 2 s re-assert covers an EC reset.
        if (!reading.DutyKnown)
            return;
        // If the EC no longer holds what we set (EC reset, resume), forget it so the next Set re-applies.
        if (!reading.Manual || reading.Duty != _appliedDuty)
            _appliedDuty = -1;
        Value = reading.Manual ? reading.Duty : null;
    }
}
