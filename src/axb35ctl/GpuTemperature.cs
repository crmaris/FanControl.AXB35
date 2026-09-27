using System;
using System.Runtime.InteropServices;

namespace Axb35Ctl;

/// <summary>
/// The GPU temperature Windows' Task Manager shows (D3DKMT adapter performance data), in °C.
/// Returns null if no adapter reports one.
/// </summary>
internal static class GpuTemperature
{
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Enum2 { public uint NumAdapters; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] private struct AdapterInfo { public uint Handle; public Luid Luid; public uint NumOfSources; public int PrecisePresentRegionsPreferred; }
    [StructLayout(LayoutKind.Sequential)] private struct PerfData { public uint PhysicalAdapterIndex; public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth; public uint FanRPM, Power, Temperature; public byte PowerStateOverride; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryInfo { public uint Adapter; public int Type; public IntPtr Data; public uint DataSize; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref Enum2 e);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryInfo q);

    private const int AdapterPerfData = 62; // KMTQAITYPE_ADAPTERPERFDATA

    public static double? Read()
    {
        var e = new Enum2();
        D3DKMTEnumAdapters2(ref e);
        if (e.NumAdapters == 0)
            return null;
        int size = Marshal.SizeOf(typeof(AdapterInfo));
        e.Adapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PerfData)));
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0)
                return null;
            for (int i = 0; i < e.NumAdapters; i++)
            {
                var adapter = (AdapterInfo)Marshal.PtrToStructure(e.Adapters + i * size, typeof(AdapterInfo))!;
                Marshal.StructureToPtr(new PerfData(), buffer, false);
                var q = new QueryInfo { Adapter = adapter.Handle, Type = AdapterPerfData, Data = buffer, DataSize = (uint)Marshal.SizeOf(typeof(PerfData)) };
                if (D3DKMTQueryAdapterInfo(ref q) == 0)
                {
                    var perf = (PerfData)Marshal.PtrToStructure(buffer, typeof(PerfData))!;
                    if (perf.Temperature > 0)
                        return perf.Temperature / 10.0;
                }
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(e.Adapters);
        }
    }
}
