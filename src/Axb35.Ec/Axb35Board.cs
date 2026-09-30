using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace Axb35.Ec;

public sealed class FanReading
{
    public int Fan { get; set; }          // 0-based
    public bool Controllable { get; set; }
    public bool DutyKnown { get; set; }   // false when read through the BIOS WMI interface, which has no duty getter
    public bool Manual { get; set; }      // bit 7 of the duty register set = held by us
    public int? Duty { get; set; }        // 0..100 % while manual; null on firmware control or when not known
    public byte DutyRaw { get; set; }
    public int Rpm { get; set; }
}

/// <summary>
/// The ITE IT5570E embedded controller of the Bosgame M5 / BeyondMax (Sixunited AXB35-02).
///
/// Register map, measured on a Bosgame M5 (BIOS 3.11, 2026-09) and matching
/// github.com/nathanmarlor/strix-halo-fan-control (validated on the Bosgame M5, taken from the DSDT):
///   0x33 / 0x34  fan 1 / fan 2 duty: write 0x80 | duty (0..100) to hold it; 0x00 = firmware (boot value)
///   0x35-0x36, 0x37-0x38  fan 1 / fan 2 RPM, high byte first; the EC updates the two bytes separately
///   0x28-0x29    fan 3 RPM (no known control register on this firmware)
///   0x70         temperature, °C (tracks the die within ~1 °C)
///   0x31         power mode, read-only here (0x01 read on the test unit; 0 balanced / 1 performance / 2 quiet
///                per github.com/cmetz/ec-su_axb35-linux; not verified on this firmware, so never written)
/// NOT the cmetz map's 0x21/0x23/0x25 fan-mode registers: they read 0x00 on this firmware.
///
/// Two ways in. Where the BIOS has its WMI fan interface (see BiosWmi), temperature, fan 1-2 RPM and duty go through
/// it: memory-mapped EC RAM, which cannot collide with Windows' own EC driver. The EC ports (see AcpiEc) are then used
/// only for the checks at open, fan 3, and handing a fan back to the firmware, which that interface cannot do.
/// Without the interface everything goes through the ports.
///
/// Writes are refused unless the board reports AXB35 and the duty and temperature registers hold
/// values this map allows.
/// </summary>
public sealed class Axb35Board : IDisposable
{
    public const int FanCount = 3;
    public const int ControllableFans = 2;

    private static readonly byte[] DutyRegister = { 0x33, 0x34 };
    private static readonly byte[] RpmHigh = { 0x35, 0x37, 0x28 };
    private static readonly byte[] RpmLow = { 0x36, 0x38, 0x29 };
    private const byte TemperatureRegister = 0x70;
    private const byte PowerModeRegister = 0x31;
    private const byte ManualBit = 0x80;
    private const byte FirmwareControl = 0x00;

    private readonly AcpiEc _ec;
    private BiosWmi? _wmi;
    private readonly bool[] _wmiWriteChecked = new bool[ControllableFans];
    private readonly object _gate = new();
    private bool _stopped;  // StopHolding: from then on only releases and the fail-safe hold reach the EC
    private bool _disposed;

    private Axb35Board(AcpiEc ec, BiosWmi? wmi, string wmiReason)
    {
        _ec = ec;
        _wmi = wmi;
        WmiUnavailableReason = wmiReason;
    }

    public static int LastDumpRetries { get; private set; }

    public static List<int> LastDumpFailures { get; private set; } = new();

    public int EcRetries => _ec.Retries;

    public long EcTransactions => _ec.Transactions;

    public long EcDeferrals => _ec.Deferrals;

    public byte EcIgnoredStatusBits => _ec.IgnoredStatusBits;

    public long WmiCalls => _wmi?.Calls ?? 0;

    public bool UsesBiosWmi => _wmi is not null;

    /// <summary>Why the BIOS WMI interface is not in use; empty when it is.</summary>
    public string WmiUnavailableReason { get; private set; }

    public string Transport => _wmi is not null
        ? "BIOS WMI (memory-mapped EC RAM); EC ports only for fan 3 and release"
        : "EC ports 0x62/0x66 (" + WmiUnavailableReason + ")";

    public static string BoardDescription()
    {
        using RegistryKey? bios = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                             .OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return $"{bios?.GetValue("BaseBoardManufacturer")} {bios?.GetValue("BaseBoardProduct")}".Trim();
    }

    public static bool IsSupportedBoard(out string board)
    {
        board = BoardDescription();
        return board.IndexOf("AXB35", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <param name="useBiosWmi">false forces the EC ports for everything (diagnostics).</param>
    public static Axb35Board Open(bool useBiosWmi = true)
    {
        RequireBoard();
        var ec = new AcpiEc();
        BiosWmi? wmi = null;
        try
        {
            string reason = "BIOS WMI disabled";
            if (useBiosWmi)
                wmi = BiosWmi.TryOpen(out reason);
            var result = new Axb35Board(ec, wmi, reason);
            result.CheckLayout();
            return result;
        }
        catch
        {
            wmi?.Dispose();
            ec.Dispose();
            throw;
        }
    }

    /// <summary>Diagnostics only: reads EC RAM 0x00..0xFF over the EC ports. Never writes.</summary>
    public static byte[] DumpRegisters()
    {
        RequireBoard();
        using var ec = new AcpiEc();
        byte[] data = new byte[256];
        LastDumpFailures = new List<int>();
        for (int register = 0; register < 256; register++)
        {
            try { data[register] = ec.Read((byte)register); }
            catch (EcException) { LastDumpFailures.Add(register); }
        }
        LastDumpRetries = ec.Retries;
        return data;
    }

    public int ReadTemperature()
    {
        lock (_gate)
        {
            CheckOpen();
            return _wmi is not null ? _wmi.Temperature() : _ec.Read(TemperatureRegister);
        }
    }

    /// <summary>Raw EC 0x31 over the EC ports (diagnostics).</summary>
    public byte ReadPowerModeRaw()
    {
        lock (_gate)
            return _ec.Read(PowerModeRegister);
    }

    /// <summary>
    /// Fans 1-2 through the BIOS WMI interface when it is in use (RPM only: it has no duty getter), otherwise RPM and
    /// duty over the EC ports. Fan 3 is always read over the EC ports.
    /// </summary>
    public FanReading ReadFan(int fan)
    {
        if (fan < 0 || fan >= FanCount)
            throw new ArgumentOutOfRangeException(nameof(fan));
        lock (_gate)
        {
            CheckOpen();
            var reading = new FanReading { Fan = fan, Controllable = fan < ControllableFans };
            if (_wmi is not null && reading.Controllable)
            {
                (int fan1, int fan2) = _wmi.Rpm();
                reading.Rpm = fan == 0 ? fan1 : fan2;
                return reading;
            }
            reading.Rpm = ReadRpm(fan);
            if (reading.Controllable)
                FillDuty(reading, _ec.Read(DutyRegister[fan]));
            return reading;
        }
    }

    /// <summary>Fan 1 or 2's duty register over the EC ports, whatever the transport (diagnostics and checks).</summary>
    public FanReading ReadDutyRegister(int fan)
    {
        CheckControllable(fan);
        lock (_gate)
        {
            var reading = new FanReading { Fan = fan, Controllable = true };
            FillDuty(reading, _ec.Read(DutyRegister[fan]));
            return reading;
        }
    }

    /// <summary>Holds fan 1 or 2 at a duty of 0..100 %. Refused after StopHolding.</summary>
    public void SetDuty(int fan, int percent) => Hold(fan, percent, failSafe: false);

    /// <summary>The fail-safe after a failed release: holds the fan even after StopHolding.</summary>
    public void HoldAfterFailedRelease(int fan, int percent) => Hold(fan, percent, failSafe: true);

    /// <summary>
    /// From now on only Release and HoldAfterFailedRelease reach the EC, so a late SetDuty (from another thread) cannot
    /// re-hold a fan after it has been handed back on the way out.
    /// </summary>
    public void StopHolding()
    {
        lock (_gate)
            _stopped = true;
    }

    private void Hold(int fan, int percent, bool failSafe)
    {
        CheckControllable(fan);
        percent = percent < 0 ? 0 : percent > 100 ? 100 : percent;
        byte value = (byte)(ManualBit | percent);
        lock (_gate)
        {
            CheckOpen();
            if (_stopped && !failSafe)
                throw new EcException("the plugin is closing; fans are no longer held");
            if (_wmi is null)
            {
                WriteVerified(DutyRegister[fan], value);
                return;
            }
            _wmi.SetDuty(fan, percent);
            if (_wmiWriteChecked[fan])
                return;
            // The first WMI hold of each fan is read back over the ports once: a BIOS whose method answers but does not
            // write 0x33/0x34 must not leave us believing a fan is held.
            byte actual = _ec.Read(DutyRegister[fan]);
            if (actual == value)
            {
                _wmiWriteChecked[fan] = true;
                return;
            }
            DisableWmi($"BIOS WMI set fan {fan + 1} to {percent}% but 0x{DutyRegister[fan]:X2} reads 0x{actual:X2}");
            WriteVerified(DutyRegister[fan], value);
        }
    }

    /// <summary>
    /// Hands fan 1 or 2 back to the firmware: 0x00, the value the register holds at boot. Always over the EC ports: the
    /// BIOS WMI interface can only hold a fan (0x80 | duty), never release it.
    /// </summary>
    public void Release(int fan)
    {
        CheckControllable(fan);
        lock (_gate)
        {
            CheckOpen();
            WriteVerified(DutyRegister[fan], FirmwareControl);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopped = true;
            _wmi?.Dispose();
            _ec.Dispose();
        }
    }

    private void CheckOpen()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Axb35Board));
    }

    private void DisableWmi(string reason)
    {
        _wmi?.Dispose();
        _wmi = null;
        WmiUnavailableReason = reason;
    }

    private static void FillDuty(FanReading reading, byte duty)
    {
        reading.DutyKnown = true;
        reading.DutyRaw = duty;
        reading.Manual = (duty & ManualBit) != 0;
        reading.Duty = reading.Manual ? duty & 0x7F : null;
    }

    // High byte first. The EC updates the two bytes separately, so a read can straddle an update:
    // read high, low, high again, and redo the low byte if the high byte moved.
    private int ReadRpm(int fan)
    {
        int rpm = -1;
        for (int attempt = 0; attempt < 3 && rpm < 0; attempt++)
        {
            byte high = _ec.Read(RpmHigh[fan]);
            byte low = _ec.Read(RpmLow[fan]);
            if (_ec.Read(RpmHigh[fan]) == high)
                rpm = (high << 8) | low;
        }
        if (rpm < 0)
            rpm = (_ec.Read(RpmHigh[fan]) << 8) | _ec.Read(RpmLow[fan]);
        // Fan 3 reports exactly 8000 while it spins down to a stop (seen by ec-su_axb35-linux and here).
        return fan == 2 && rpm == 8000 ? 0 : rpm;
    }

    private void CheckLayout()
    {
        for (int fan = 0; fan < ControllableFans; fan++)
        {
            byte duty = _ec.Read(DutyRegister[fan]);
            bool valid = duty == FirmwareControl || ((duty & ManualBit) != 0 && (duty & 0x7F) <= 100);
            if (!valid)
                throw new EcException($"Fan {fan + 1} duty register 0x{DutyRegister[fan]:X2} reads 0x{duty:X2}; not this EC firmware's layout, refusing to write");
        }
        int temperature = _ec.Read(TemperatureRegister);
        if (temperature < 5 || temperature > 115)
            throw new EcException($"Temperature register 0x{TemperatureRegister:X2} reads {temperature}; not this EC firmware's layout, refusing to write");

        // The WMI interface must be looking at the same EC RAM as the ports: same temperature, same fan 1 speed.
        if (_wmi is not null)
        {
            string? disagreement;
            try
            {
                int wmiTemperature = _wmi.Temperature();
                int portRpm = ReadRpm(0);
                int wmiRpm = _wmi.Rpm().Fan1;
                disagreement = Math.Abs(wmiTemperature - temperature) > 3 || Math.Abs(wmiRpm - portRpm) > 300
                    ? $"BIOS WMI disagrees with the EC ports ({wmiTemperature} vs {temperature} C, {wmiRpm} vs {portRpm} rpm)"
                    : null;
            }
            catch (EcException ex)
            {
                disagreement = "BIOS WMI check failed: " + ex.Message;
            }
            if (disagreement is not null)
                DisableWmi(disagreement);
        }
    }

    private void WriteVerified(byte register, byte value)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _ec.Write(register, value);
            if (_ec.Read(register) == value)
                return;
            Thread.Sleep(5);
        }
        throw new EcException($"EC register 0x{register:X2} did not keep 0x{value:X2}");
    }

    private static void RequireBoard()
    {
        if (!IsSupportedBoard(out string board))
            throw new EcException($"Not an AXB35 board ('{board}'); refusing to touch the EC");
    }

    private static void CheckControllable(int fan)
    {
        if (fan < 0 || fan >= ControllableFans)
            throw new ArgumentOutOfRangeException(nameof(fan), "only fans 1 and 2 have a duty register");
    }
}
