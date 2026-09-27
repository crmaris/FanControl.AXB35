using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Axb35.Ec;

/// <summary>
/// The standard ACPI embedded-controller protocol (data port 0x62, command/status port 0x66) over
/// PawnIO's signed LpcACPIEC module, which allows exactly those two ports and nothing else.
/// Every transaction holds Global\Access_EC, the mutex LibreHardwareMonitor, FanControl and HWiNFO
/// share for the EC. Windows' own ACPI EC driver does not take it, which is why callers verify writes.
/// </summary>
internal sealed class AcpiEc : IDisposable
{
    private const byte DataPort = 0x62;
    private const byte CommandPort = 0x66;
    private const byte OutputBufferFull = 0x01;
    private const byte InputBufferFull = 0x02;
    private const byte ReadCommand = 0x80;
    private const byte WriteCommand = 0x81;
    private const int HandshakeTimeoutMs = 100;
    private const int MutexTimeoutMs = 500;

    private readonly PawnIoModule _io;
    private readonly Mutex? _ecMutex;

    public AcpiEc()
    {
        _io = PawnIoModule.Load(PawnIoModule.EmbeddedModule("LpcACPIEC.bin"));
        _ecMutex = OpenSharedMutex(@"Global\Access_EC");
    }

    // Windows' own ACPI EC driver runs transactions (and event queries) on the same two ports and
    // does not take Global\Access_EC, so a transaction of ours is occasionally cut into and times
    // out. Measured on a Bosgame M5: most reads answer in ~1 ms, some never do. Retrying the
    // whole transaction is what LibreHardwareMonitor does too (5 attempts).
    private const int Attempts = 5;

    public int Retries { get; private set; }

    public byte Read(byte register)
    {
        for (int attempt = 1; ; attempt++)
        {
            Acquire();
            ThreadPriority previous = RaisePriority();
            try
            {
                DrainStaleOutput();
                WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
                Out(CommandPort, ReadCommand);
                WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
                Out(DataPort, register);
                WaitUntil(() => (Status() & OutputBufferFull) != 0, "output buffer to fill");
                return In(DataPort);
            }
            catch (EcException) when (attempt < Attempts)
            {
                Retries++;
            }
            finally { Thread.CurrentThread.Priority = previous; Release(); }
            Thread.Sleep(2);
        }
    }

    public void Write(byte register, byte value)
    {
        for (int attempt = 1; ; attempt++)
        {
            Acquire();
            ThreadPriority previous = RaisePriority();
            try
            {
                WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
                Out(CommandPort, WriteCommand);
                WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
                Out(DataPort, register);
                WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
                Out(DataPort, value);
                WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
                return;
            }
            catch (EcException) when (attempt < Attempts)
            {
                Retries++;
            }
            finally { Thread.CurrentThread.Priority = previous; Release(); }
            Thread.Sleep(2);
        }
    }

    public void Dispose()
    {
        _io.Dispose();
        _ecMutex?.Dispose();
    }

    private byte Status() => In(CommandPort);

    // A byte left in the output buffer by someone else would be read as our answer.
    private void DrainStaleOutput()
    {
        for (int i = 0; i < 16 && (Status() & OutputBufferFull) != 0; i++)
            In(DataPort);
    }

    // Poll without sleeping OR yielding, at raised thread priority. The EC answers in ~1 ms, and when it
    // does it also raises an interrupt that Windows' own EC driver services; if our thread is not on a
    // CPU at that moment, that driver can take the answer byte first and ours never arrives. Measured on
    // a Bosgame M5: Thread.Sleep(1) (~15.6 ms) and Thread.Yield() both lost every read once all 32 CPU
    // threads were busy; a tight spin at Highest priority does not. A transaction lasts ~1 ms, so the
    // spin costs nothing measurable.
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > HandshakeTimeoutMs)
                throw new EcException($"EC timed out waiting for the {what}");
        }
    }

    private static ThreadPriority RaisePriority()
    {
        ThreadPriority previous = Thread.CurrentThread.Priority;
        try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }
        return previous;
    }

    private byte In(byte port) => (byte)_io.Execute("ioctl_pio_read", new long[] { port }, 1)[0];

    private void Out(byte port, byte value) => _io.Execute("ioctl_pio_write", new long[] { port, value }, 0);

    private void Acquire()
    {
        if (_ecMutex is null)
            return;
        try
        {
            if (!_ecMutex.WaitOne(MutexTimeoutMs))
                throw new EcException("Another program is holding the EC (Global\\Access_EC) too long");
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died holding it; we own it now.
        }
    }

    private void Release() => _ecMutex?.ReleaseMutex();

    // Inside FanControl, LibreHardwareMonitor has usually created this mutex already (world-accessible),
    // so opening it is the normal path. Otherwise create it, world-accessible where the API allows.
    private static Mutex? OpenSharedMutex(string name)
    {
        try { return Mutex.OpenExisting(name); }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { return null; }
        try
        {
#if NETFRAMEWORK
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.FullControl, AccessControlType.Allow));
            return new Mutex(false, name, out _, security);
#else
            return new Mutex(false, name);
#endif
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
