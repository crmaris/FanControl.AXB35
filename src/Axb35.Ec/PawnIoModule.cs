using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Axb35.Ec;

/// <summary>
/// One loaded PawnIO module (github.com/namazso/PawnIO). PawnIO is the signed driver FanControl and
/// LibreHardwareMonitor already use; it only runs signed modules, and each module decides what it
/// allows. Same IOCTLs as LibreHardwareMonitor's PawnIo class, without its CsWin32 dependency.
/// </summary>
internal sealed class PawnIoModule : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821 << 2);
    private const uint IoctlExecute = DeviceType | (0x841 << 2);
    private const int FunctionNameLength = 32;

    private readonly SafeFileHandle _handle;

    private PawnIoModule(SafeFileHandle handle) => _handle = handle;

    public static PawnIoModule Load(byte[] module)
    {
        SafeFileHandle handle = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new EcException("PawnIO is not reachable. Is PawnIO installed, and is this process elevated?", Marshal.GetLastWin32Error());

        if (!DeviceIoControl(handle, IoctlLoadBinary, module, (uint)module.Length, null, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new EcException("PawnIO refused the module", error);
        }

        return new PawnIoModule(handle);
    }

    public static byte[] EmbeddedModule(string name)
    {
        using Stream? stream = typeof(PawnIoModule).Assembly.GetManifestResourceStream("Axb35.Ec." + name);
        if (stream is null)
            throw new EcException($"PawnIO module {name} is not embedded in {typeof(PawnIoModule).Assembly.GetName().Name}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public long[] Execute(string function, long[] input, int outputCount)
    {
        byte[] inBuffer = new byte[FunctionNameLength + input.Length * sizeof(long)];
        Encoding.ASCII.GetBytes(function, 0, Math.Min(function.Length, FunctionNameLength - 1), inBuffer, 0);
        Buffer.BlockCopy(input, 0, inBuffer, FunctionNameLength, input.Length * sizeof(long));
        byte[] outBuffer = new byte[outputCount * sizeof(long)];

        if (!DeviceIoControl(_handle, IoctlExecute, inBuffer, (uint)inBuffer.Length, outBuffer, (uint)outBuffer.Length, out uint returned, IntPtr.Zero))
            throw new EcException($"PawnIO {function} failed", Marshal.GetLastWin32Error());

        long[] result = new long[outputCount];
        Buffer.BlockCopy(outBuffer, 0, result, 0, (int)Math.Min(returned, (uint)outBuffer.Length));
        return result;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, uint inSize, byte[]? outBuffer, uint outSize, out uint returned, IntPtr overlapped);
}

public class EcException : Exception
{
    public EcException(string message) : base(message) { }
    public EcException(string message, int win32Error) : base($"{message} (Win32 error {win32Error})") { }
}
