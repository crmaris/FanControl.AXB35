using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Win32;

namespace Axb35.Ec;

public sealed class FanReading
{
    public int Fan { get; set; }          // 0-based
    public bool Controllable { get; set; }
    public bool Manual { get; set; }      // bit 7 of the duty register set = held by us
    public int? Duty { get; set; }        // 0..100 % while manual; null on firmware control
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
    private readonly object _gate = new();

    private Axb35Board(AcpiEc ec) => _ec = ec;

    public static int LastDumpRetries { get; private set; }

    public static List<int> LastDumpFailures { get; private set; } = new();

    public int EcRetries => _ec.Retries;

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

    public static Axb35Board Open()
    {
        RequireBoard();
        var ec = new AcpiEc();
        var result = new Axb35Board(ec);
        try
        {
            result.CheckLayout();
        }
        catch
        {
            ec.Dispose();
            throw;
        }
        return result;
    }

    /// <summary>Diagnostics only: reads EC RAM 0x00..0xFF. Never writes.</summary>
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
            return _ec.Read(TemperatureRegister);
    }

    public byte ReadPowerModeRaw()
    {
        lock (_gate)
            return _ec.Read(PowerModeRegister);
    }

    public FanReading ReadFan(int fan)
    {
        if (fan < 0 || fan >= FanCount)
            throw new ArgumentOutOfRangeException(nameof(fan));
        lock (_gate)
        {
            var reading = new FanReading { Fan = fan, Controllable = fan < ControllableFans, Rpm = ReadRpm(fan) };
            if (reading.Controllable)
            {
                byte duty = _ec.Read(DutyRegister[fan]);
                reading.DutyRaw = duty;
                reading.Manual = (duty & ManualBit) != 0;
                reading.Duty = reading.Manual ? duty & 0x7F : null;
            }
            return reading;
        }
    }

    /// <summary>Holds fan 1 or 2 at a duty of 0..100 %.</summary>
    public void SetDuty(int fan, int percent)
    {
        CheckControllable(fan);
        percent = percent < 0 ? 0 : percent > 100 ? 100 : percent;
        lock (_gate)
            WriteVerified(DutyRegister[fan], (byte)(ManualBit | percent));
    }

    /// <summary>Hands fan 1 or 2 back to the firmware: 0x00, the value the register holds at boot.</summary>
    public void Release(int fan)
    {
        CheckControllable(fan);
        lock (_gate)
            WriteVerified(DutyRegister[fan], FirmwareControl);
    }

    public void Dispose() => _ec.Dispose();

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
