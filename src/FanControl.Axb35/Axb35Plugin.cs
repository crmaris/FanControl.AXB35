using System;
using Axb35.Ec;
using FanControl.Plugins;

namespace FanControl.Axb35;

/// <summary>
/// FanControl plugin for the ITE IT5570E embedded controller of the Bosgame M5 / BeyondMax
/// (Sixunited AXB35-02), which LibreHardwareMonitor does not drive. Talks to it through PawnIO's
/// signed LpcACPIEC module. Exposes the EC temperature, the RPM of fans 1-3, and fans 1 and 2 as
/// controls (0-100 %). A fan FanControl releases, or every fan when FanControl closes, goes back
/// to the firmware (duty register 0x00, its boot value).
/// </summary>
public sealed class Axb35Plugin : IPlugin2
{
    private readonly IPluginLogger _logger;
    // FanControl names each plugin sensor "<plugin Name>/<sensor Id>" (PluginSensor.Identifier in
    // FanControl.Library), so these become "AXB35 EC/fan/1/control" etc. in userConfig.json. Changing
    // PluginName or an Id orphans every saved curve that uses it.
    internal const string PluginName = "AXB35 EC";

    private readonly Axb35Sensor _temperature = new("ec/temperature", "AXB35 EC temperature");
    private readonly Axb35Sensor[] _rpm = new Axb35Sensor[Axb35Board.FanCount];
    private readonly Axb35FanControl[] _controls = new Axb35FanControl[Axb35Board.ControllableFans];
    private Axb35Board? _board;
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
            Log($"EC opened on {Axb35Board.BoardDescription()}; {_board.ReadTemperature()} C");
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
            for (int fan = 0; fan < Axb35Board.FanCount; fan++)
            {
                FanReading reading = board.ReadFan(fan);
                _rpm[fan].Value = reading.Rpm;
                if (fan < Axb35Board.ControllableFans)
                    _controls[fan].Observe(reading);
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
        for (int fan = 0; fan < Axb35Board.ControllableFans; fan++)
        {
            try { board.Release(fan); }
            catch (Exception ex) { Log($"fan {fan + 1}: could not hand back to the firmware: {ex.Message}"); }
        }
        board.Dispose();
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

    public void Set(float val)
    {
        // Round up: a fan never runs slower than FanControl asked for.
        int duty = (int)Math.Ceiling(val - 1e-3);
        duty = duty < 0 ? 0 : duty > 100 ? 100 : duty;
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
        try
        {
            _plugin.Board?.Release(_fan);
        }
        catch (Exception ex)
        {
            _plugin.Log($"fan {_fan + 1}: could not hand back to the firmware: {ex.Message}");
        }
        _appliedDuty = -1;
        _announced = false;
        _plugin.Log($"fan {_fan + 1}: released to the firmware");
    }

    public void Update()
    {
        // Values are read together in Axb35Plugin.Update, once per cycle.
    }

    internal void Observe(FanReading reading)
    {
        // If the EC no longer holds what we set (EC reset, resume), forget it so the next Set re-applies.
        if (!reading.Manual || reading.Duty != _appliedDuty)
            _appliedDuty = -1;
        Value = reading.Manual ? reading.Duty : null;
    }
}
