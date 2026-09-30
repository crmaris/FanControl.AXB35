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
/// share for the EC. Windows' own ACPI EC driver does not take it, which is why callers verify writes
/// and why a transaction only starts on an idle EC (see WaitForIdle).
/// </summary>
internal sealed class AcpiEc : IDisposable
{
    private const byte DataPort = 0x62;
    private const byte CommandPort = 0x66;
    private const byte OutputBufferFull = 0x01;
    private const byte InputBufferFull = 0x02;
    private const byte BurstMode = 0x10;
    private const byte SciEventPending = 0x20;
    private const byte ReadCommand = 0x80;
    private const byte WriteCommand = 0x81;
    private const int HandshakeTimeoutMs = 100;
    private const int MutexTimeoutMs = 2000;

    // Signs that someone else is mid-transaction, or about to start one: a byte on its way in or out, burst mode
    // (Windows' driver holds the EC in burst for a whole sequence), or an event the driver is about to query.
    private const byte Busy = InputBufferFull | OutputBufferFull | BurstMode | SciEventPending;
    private const int IdleWaitMs = 20;
    private const int StickyMs = 100; // a BURST / SCI_EVT bit set this long is how this EC rests, not another user
    private static readonly long QuietTicks = Stopwatch.Frequency / 5000; // 200 µs

    private byte _busy = Busy;

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

    /// <summary>Transactions started (reads and writes, retries included).</summary>
    public long Transactions { get; private set; }

    /// <summary>Times a transaction waited for, or gave way to, another user of the EC.</summary>
    public long Deferrals { get; private set; }

    /// <summary>Status bits this EC turned out to rest with, and which are therefore not taken as a sign of another user.</summary>
    public byte IgnoredStatusBits { get; private set; }

    public byte Read(byte register) => Transact(() =>
    {
        Out(CommandPort, ReadCommand);
        WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
        GiveWayToEvent();
        Out(DataPort, register);
        WaitUntil(() => (Status() & OutputBufferFull) != 0, "output buffer to fill");
        return In(DataPort);
    });

    public void Write(byte register, byte value) => Transact(() =>
    {
        Out(CommandPort, WriteCommand);
        WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
        GiveWayToEvent();
        Out(DataPort, register);
        WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
        GiveWayToEvent();
        Out(DataPort, value);
        WaitUntil(() => (Status() & InputBufferFull) == 0, "input buffer to empty");
        return 0;
    });

    private byte Transact(Func<byte> transaction)
    {
        for (int attempt = 1; ; attempt++)
        {
            // Raise priority BEFORE taking the shared mutex: at normal priority on a saturated CPU the
            // holder can be preempted mid-transaction while other EC users time out waiting for it.
            ThreadPriority previous = RaisePriority();
            bool held = false;
            try
            {
                Acquire();
                held = true;
                WaitForIdle();
                Transactions++;
                return transaction();
            }
            catch (EcException ex) when (attempt < Attempts && ex is not EcLockTimeoutException)
            {
                Retries++;
            }
            finally
            {
                if (held)
                    Release();
                Thread.CurrentThread.Priority = previous;
            }
            Thread.Sleep(2);
        }
    }

    /// <summary>The status register (0x66). Reading it has no side effect on the EC.</summary>
    public byte Status() => In(CommandPort);

    public void Dispose()
    {
        _io.Dispose();
        _ecMutex?.Dispose();
    }

    // Windows' ACPI EC driver does not take Global\Access_EC. It uses these ports for the EC's event queries
    // (SCI_EVT, then QR_EC 0x84), and a transaction of ours that starts inside one of its own corrupts both: the
    // driver's side times out after a second and the System log gets ACPI event 13, "The embedded controller (EC) did
    // not respond within the specified timeout period". So start only on an EC that shows no other user, and still
    // shows none 200 µs later (the driver is interrupt-driven, with short idle-looking gaps between its steps).
    //
    // A byte already in the output buffer is someone else's answer on its way: v1.0 read it out of the way, which
    // guaranteed that transaction's timeout. Now it is left for its owner, and drained only if nobody collects it
    // within IdleWaitMs (then it really is stale, and would be read as our answer).
    private void WaitForIdle()
    {
        var clock = Stopwatch.StartNew();
        long quietSince = -1;
        byte stickyBits = 0;
        long stickySince = 0;
        bool waited = false;
        int drained = 0;
        while (true)
        {
            byte status = Status();
            long now = clock.ElapsedTicks;
            if ((status & _busy) == 0)
            {
                stickyBits = 0;
                if (quietSince < 0)
                    quietSince = now;
                else if (now - quietSince >= QuietTicks)
                    return;
                continue;
            }
            quietSince = -1;
            if (!waited)
            {
                waited = true;
                Deferrals++;
            }
            // A BURST or SCI_EVT bit that stays set without a break for StickyMs is how this EC rests, not another
            // user at work (the Bosgame M5's EC rests at 0x00/0x08). Stop waiting for it, as v1.0 never did.
            byte candidate = (byte)(status & _busy & (BurstMode | SciEventPending));
            if (candidate != stickyBits)
            {
                stickyBits = candidate;
                stickySince = now;
            }
            else if (candidate != 0 && now - stickySince >= StickyMs * Stopwatch.Frequency / 1000)
            {
                _busy &= (byte)~candidate;
                IgnoredStatusBits |= candidate;
                stickyBits = 0;
                clock.Restart();
                continue;
            }
            if (clock.ElapsedMilliseconds < IdleWaitMs || (candidate != 0 && clock.ElapsedMilliseconds < StickyMs))
                continue;
            if ((status & _busy) == OutputBufferFull && drained++ < 4)
            {
                In(DataPort); // uncollected for IdleWaitMs: stale
                stickyBits = 0;
                clock.Restart();
                continue;
            }
            throw new EcException($"EC busy (status 0x{status:X2}) for {clock.ElapsedMilliseconds} ms");
        }
    }

    // The EC can raise an event while we are between bytes. Windows' driver then writes QR_EC (0x84) as soon as the
    // input buffer is free, which cuts our transaction in two. Give way before sending the next byte instead: the
    // driver's query supersedes our unfinished command (a new command always does), gets its answer, and we retry.
    // Measured on a Bosgame M5: that event comes every ~10.2 s (it runs _Q49) and its query takes ~1 ms.
    private void GiveWayToEvent()
    {
        byte status = Status();
        if ((status & ((_busy & SciEventPending) | OutputBufferFull)) == 0)
            return;
        Deferrals++;
        throw new EcException($"gave way to an EC event (status 0x{status:X2})");
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
                throw new EcLockTimeoutException("Another program is holding the EC (Global\\Access_EC) too long");
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died holding it; we own it now.
        }
    }

    private void Release() => _ecMutex?.ReleaseMutex();

    // Not retried: five more 2 s waits would stall the caller for 10 s.
    private sealed class EcLockTimeoutException : EcException
    {
        public EcLockTimeoutException(string message) : base(message) { }
    }

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
