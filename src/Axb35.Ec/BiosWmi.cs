using System;
using System.Runtime.InteropServices;

namespace Axb35.Ec;

/// <summary>
/// The board's own ACPI-WMI interface: BIOS device \_SB.WMIB (PNP0C14, _UID "IP3POWERSWITCH"), WMI class
/// root\wmi:PowerSwitchInterface, GUID {99D89064-8D50-42BB-BEA9-155B2E5D0FCD}, AML method WMAA.
///
/// Its AML reads and writes the EC's RAM through the EC's memory window (OperationRegion ERAX, SystemMemory 0xFEC40400,
/// same offsets as the EC registers), NOT through the 0x62/0x66 handshake. That matters: Windows' own ACPI EC driver
/// runs the EC's event queries on those ports and takes no lock a user-mode program can share, so a port transaction
/// of ours can cut into one of its transactions, which then times out (System log, ACPI event 13). A memory read or
/// write has no handshake to cut into.
///
/// Called through advapi32's WMI data-block API, which goes straight to the kernel's WMI and the ACPI-WMI mapper: no
/// WMI service, no COM, well under a millisecond per call. Methods used (WmiMethodId, from the BIOS's own MOF):
///   3  SetFanControl(FanNumber 1|2, FanDuty 0..101): FAN1/FAN2 (0x33/0x34) = 0x80 | duty. Returns 0, or 0xFF if refused
///   4  GetFanControl(1): fan RPM, byte0 = 0x36, byte1 = 0x35 (fan 1), byte2 = 0x38, byte3 = 0x37 (fan 2)
///   11 GetHwTemp(1): byte0 = 0x70, the EC temperature the firmware's curve uses
/// There is no method that hands a fan back to the firmware (0x00) and none for fan 3; those stay on the EC ports.
/// SetPowerMode (1) exists too and is never called.
/// </summary>
internal sealed class BiosWmi : IDisposable
{
    private static readonly Guid InterfaceGuid = new("99d89064-8d50-42bb-bea9-155b2e5d0fcd");
    private const string InstanceName = @"ACPI\PNP0C14\IP3POWERSWITCH_0";
    private const uint WmiGuidExecute = 0x0010;
    private const uint SetFanControl = 3;
    private const uint GetFanControl = 4;
    private const uint GetHwTemp = 11;

    private IntPtr _handle;

    private BiosWmi(IntPtr handle) => _handle = handle;

    public long Calls { get; private set; }

    /// <summary>Null, with the reason, when this BIOS has no such interface or it does not answer like the M5's.</summary>
    public static BiosWmi? TryOpen(out string reason)
    {
        Guid guid = InterfaceGuid;
        uint error = WmiOpenBlock(ref guid, WmiGuidExecute, out IntPtr handle);
        if (error != 0)
        {
            reason = $"no BIOS WMI fan interface (WmiOpenBlock error {error})";
            return null;
        }
        var wmi = new BiosWmi(handle);
        try
        {
            int temperature = wmi.Temperature();
            if (temperature < 5 || temperature > 115)
                throw new EcException($"BIOS WMI temperature reads {temperature}");
            reason = "";
            return wmi;
        }
        catch (EcException ex)
        {
            wmi.Dispose();
            reason = "BIOS WMI fan interface unusable: " + ex.Message;
            return null;
        }
    }

    public int Temperature() => (int)(Call(GetHwTemp, 1) & 0xFF);

    /// <summary>Fan 1 and fan 2 RPM.</summary>
    public (int Fan1, int Fan2) Rpm()
    {
        // The AML reads the RPM bytes one at a time while the EC updates them, so one read can straddle an update
        // (0x08FF -> 0x0900 read as 0x09FF). A single torn read is the odd one out of three.
        (int, int) a = RpmOnce(), b = RpmOnce(), c = RpmOnce();
        return (Median(a.Item1, b.Item1, c.Item1), Median(a.Item2, b.Item2, c.Item2));
    }

    /// <summary>Holds fan 0 or 1 (0-based) at 0..100 %: the AML writes 0x80 | duty to 0x33 / 0x34.</summary>
    public void SetDuty(int fan, int percent)
    {
        uint result = Call(SetFanControl, (byte)(fan + 1), (byte)percent);
        if ((result & 0xFF) != 0)
            throw new EcException($"BIOS WMI refused fan {fan + 1} at {percent}% (result 0x{result & 0xFF:X2})");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            WmiCloseBlock(_handle);
        _handle = IntPtr.Zero;
    }

    private (int, int) RpmOnce()
    {
        uint value = Call(GetFanControl, 1);
        int fan1 = (int)((((value >> 8) & 0xFF) << 8) | (value & 0xFF));
        int fan2 = (int)((((value >> 24) & 0xFF) << 8) | ((value >> 16) & 0xFF));
        return (fan1, fan2);
    }

    private static int Median(int a, int b, int c) => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    private uint Call(uint method, params byte[] input)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(BiosWmi));
        byte[] output = new byte[16];
        uint size = (uint)output.Length;
        uint error = WmiExecuteMethodW(_handle, InstanceName, method, (uint)input.Length, input, ref size, output);
        Calls++;
        if (error != 0)
            throw new EcException($"BIOS WMI method {method} failed", (int)error);
        if (size < 4)
            throw new EcException($"BIOS WMI method {method} returned {size} bytes");
        return BitConverter.ToUInt32(output, 0);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint WmiOpenBlock(ref Guid guid, uint desiredAccess, out IntPtr dataBlockHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint WmiExecuteMethodW(IntPtr dataBlockHandle, string instanceName, uint methodId, uint inputSize, byte[] input, ref uint outputSize, byte[] output);

    [DllImport("advapi32.dll")]
    private static extern uint WmiCloseBlock(IntPtr dataBlockHandle);
}
