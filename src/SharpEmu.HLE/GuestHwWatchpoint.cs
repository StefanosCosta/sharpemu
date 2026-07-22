// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Diagnostic: a Linux perf_event_open HARDWARE data watchpoint. Programs the x86
/// debug registers (DR0-3, up to 4 addresses) via PERF_TYPE_BREAKPOINT to catch the
/// RIP + value + thread of every guest instruction that WRITES a target address -
/// from ANY guest thread, with zero page-protection and near-zero overhead. Unlike
/// <see cref="GuestAddrWriteCatcher"/> (page-protect + single-step, which crashes on
/// busy multi-threaded pages), a hardware breakpoint matches one exact 8-byte slot
/// and is inherently multi-thread-safe.
///
/// Enabled via <c>SHARPEMU_HW_WATCH=&lt;addrHex&gt;[;&lt;addrHex&gt;...][,&lt;len&gt;][,&lt;max&gt;]</c>
/// (<c>SHARPEMU_HW_WATCH_SIG=&lt;n&gt;</c> overrides the RT overflow signal, default 43).
/// Records print from <see cref="ArmAndFlush"/> (managed context). Data breakpoints
/// are POST-store traps, so the recorded RIP is the instruction AFTER the writing
/// store and the value already reflects the write.
///
/// REQUIRES <c>kernel.perf_event_paranoid &lt;= 1</c> (else perf_event_open -&gt; EACCES).
/// Linux-only (hardcodes the Linux gregs offset RIP=128 and the x86-64 syscall/ioctl
/// numbers); a no-op elsewhere.
/// </summary>
public static unsafe class GuestHwWatchpoint
{
    // Linux x86-64 syscall / fcntl / ioctl / perf constants.
    private const long NrPerfEventOpen = 298;
    private const int FSetfl = 4;
    private const int FGetfl = 3;
    private const int FSetown = 8;
    private const int FSetsig = 10;
    private const int FSetownEx = 15;
    private const int FOwnerTid = 2;
    private const int OAsync = 0x2000;
    private const ulong PerfIocEnable = 0x2400;
    private const ulong PerfIocDisable = 0x2401;
    private const int SigInfoFdOffset = 24; // Linux siginfo: si_fd for a POLL/fasync signal.
    private const int GregRip = 128;        // gregs byte offset (Linux x86-64).

    // perf_event_attr byte offsets (x86-64).
    private const int AttrSize = 128;
    private const int OffType = 0;
    private const int OffSize = 4;
    private const int OffSamplePeriod = 16;
    private const int OffBitfield = 40;
    private const int OffWakeupEvents = 48;
    private const int OffBpType = 52;
    private const int OffBpAddr = 56;
    private const int OffBpLen = 64;
    private const uint PerfTypeBreakpoint = 5;
    private const uint HwBreakpointW = 2;
    // disabled(bit0) | exclude_kernel(bit5) | exclude_hv(bit6).
    private const ulong AttrBitfield = 0x1UL | 0x20UL | 0x40UL;

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, void* attr, long pid, long cpu, long groupFd, ulong flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")]
    private static extern int fcntl_ptr(int fd, int cmd, void* arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ulong arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc")]
    private static extern int gettid();

    [DllImport("libc", SetLastError = true)]
    private static extern long read(int fd, void* buf, ulong count);

    // ---- configuration (parsed once) ----
    private static readonly bool _linux = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
    private static readonly ulong[] _addrs;
    private static readonly ulong _len;
    private static readonly bool _enabled;
    private static readonly int _overflowSignal;

    // ---- shared state ----
    private struct Rec { public ulong Rip; public ulong Addr; public ulong Value; public int Tid; public long Seq; }
    private const int RecCap = 8192;
    private static readonly Rec[] _recs = new Rec[RecCap];
    private static long _recSeq;
    private static int _recWrite;
    private static int _recFlush;
    private static int _armedThreads;
    private static long _overflows;
    private static int _armLogged;
    private static long _diagPrinted;

    // ---- per-thread perf fds ----
    [ThreadStatic] private static int[]? _fds;
    [ThreadStatic] private static bool _attached;
    [ThreadStatic] private static int _flushTicks;

    public static bool Enabled => _enabled;
    public static int OverflowSignal => _overflowSignal;

    static GuestHwWatchpoint()
    {
        _overflowSignal = 43;
        if (int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_HW_WATCH_SIG"), out var s) && s > 0 && s < 64)
        {
            _overflowSignal = s;
        }

        if (_linux && TryParse(Environment.GetEnvironmentVariable("SHARPEMU_HW_WATCH"), out _addrs, out _len))
        {
            _enabled = true;
        }
        else
        {
            _addrs = [];
            _len = 8;
        }
    }

    /// <summary>
    /// Attach a hardware write-watch on the current thread for each configured
    /// address. Called once at each guest thread's entry (managed context; allocation
    /// fine here, never in the signal path). Idempotent per thread.
    /// </summary>
    public static void AttachCurrentThread()
    {
        if (!_enabled || _attached)
        {
            return;
        }

        _attached = true;
        var fds = new int[_addrs.Length];
        var tid = gettid();
        byte* attr = stackalloc byte[AttrSize];
        int* owner = stackalloc int[2];
        owner[0] = FOwnerTid;
        owner[1] = tid;
        for (var i = 0; i < _addrs.Length; i++)
        {
            fds[i] = -1;
            new Span<byte>(attr, AttrSize).Clear();
            *(uint*)(attr + OffType) = PerfTypeBreakpoint;
            *(uint*)(attr + OffSize) = AttrSize;
            *(ulong*)(attr + OffSamplePeriod) = 1;
            *(ulong*)(attr + OffBitfield) = AttrBitfield;
            *(uint*)(attr + OffWakeupEvents) = 1;
            *(uint*)(attr + OffBpType) = HwBreakpointW;
            *(ulong*)(attr + OffBpAddr) = _addrs[i];
            *(ulong*)(attr + OffBpLen) = _len;

            var fd = (int)syscall(NrPerfEventOpen, attr, 0, -1, -1, 0);
            if (fd < 0)
            {
                Console.Error.WriteLine(
                    $"[HW] perf_event_open failed for 0x{_addrs[i]:X16} errno={Marshal.GetLastPInvokeError()} " +
                    "(need kernel.perf_event_paranoid<=1)");
                continue;
            }

            // Route the overflow signal to THIS thread before enabling, so the very
            // first hit is delivered correctly (ucontext RIP = this thread's RIP).
            var r1 = fcntl(fd, FSetsig, _overflowSignal);
            var r2 = fcntl_ptr(fd, FSetownEx, owner);
            var r3 = fcntl(fd, FSetfl, OAsync);
            var r4 = ioctl(fd, PerfIocEnable, 0);
            fds[i] = fd;
            if (Interlocked.Increment(ref _attachDiag) <= 12)
            {
                Console.Error.WriteLine(
                    $"[HW] attach tid={tid} addr=0x{_addrs[i]:X16} fd={fd} setsig={r1} setown={r2} setfl={r3} enable={r4} errno={Marshal.GetLastPInvokeError()}");
            }
        }

        _fds = fds;
        Interlocked.Increment(ref _armedThreads);
    }

    private static int _attachDiag;
    private static int _countDiag;

    /// <summary>Close this thread's perf fds (call from the thread's teardown).</summary>
    public static void DetachCurrentThread()
    {
        if (!_enabled || !_attached)
        {
            return;
        }

        var fds = _fds;
        if (fds != null)
        {
            for (var i = 0; i < fds.Length; i++)
            {
                if (fds[i] >= 0)
                {
                    _ = close(fds[i]);
                    fds[i] = -1;
                }
            }
        }

        _attached = false;
        Interlocked.Decrement(ref _armedThreads);
    }

    /// <summary>
    /// Signal-handler entry for the perf overflow signal. Records the writer RIP, the
    /// target's (just-written) value, and the tid. Signal-safe: no allocation, no
    /// locks, no Console. sample_period=1 auto-rearms, so no re-arm work is needed.
    /// </summary>
    public static void HandleOverflow(nint gregsBase, nint siginfo)
    {
        if (!_enabled)
        {
            return;
        }

        var rip = *(ulong*)((byte*)gregsBase + GregRip);
        // rip==0 is the fabricated warm-up ucontext (WarmUpPosixSignalPath): return
        // before touching guest memory, which is not mapped yet at that point. A real
        // guest overflow always carries a genuine image RIP (>= 0x800000000).
        if (rip == 0)
        {
            return;
        }

        Interlocked.Increment(ref _overflows);
        var tid = gettid();

        // Which of the <=4 addresses fired: match si_fd against this thread's fds.
        var index = -1;
        var fds = _fds;
        if (siginfo != 0 && fds != null)
        {
            var siFd = *(int*)((byte*)siginfo + SigInfoFdOffset);
            for (var i = 0; i < fds.Length; i++)
            {
                if (fds[i] == siFd)
                {
                    index = i;
                    break;
                }
            }
        }

        if (index >= 0)
        {
            Record(rip, _addrs[index], ReadGuest(_addrs[index]), tid);
        }
        else
        {
            // Fallback: attribute to all configured addresses' current values.
            for (var i = 0; i < _addrs.Length; i++)
            {
                Record(rip, _addrs[i], ReadGuest(_addrs[i]), tid);
            }
        }
    }

    // Guest memory is identity-mapped; only dereference addresses inside the guest
    // heap/image window so a stray fire on a not-yet-committed page can't fault the
    // signal handler.
    private static ulong ReadGuest(ulong addr) =>
        addr >= 0x0000000400000000UL && addr < 0x0000000900000000UL
            ? *(ulong*)(addr & ~7UL)
            : 0UL;

    private static void Record(ulong rip, ulong addr, ulong value, int tid)
    {
        var slot = Interlocked.Increment(ref _recWrite) - 1;
        if (slot - Volatile.Read(ref _recFlush) >= RecCap)
        {
            return;
        }

        ref var rec = ref _recs[slot % RecCap];
        rec.Rip = rip;
        rec.Addr = addr;
        rec.Value = value;
        rec.Tid = tid;
        rec.Seq = Interlocked.Increment(ref _recSeq);
    }

    /// <summary>Managed-context pass (import boundary): print captured writes; also a
    /// belt-and-suspenders attach for the dispatching thread.</summary>
    public static void ArmAndFlush()
    {
        if (!_enabled)
        {
            return;
        }

        AttachCurrentThread();

        // TEST: perf breakpoints may have been armed before the target page was mapped
        // (attach is at first CallNativeEntry, before the JobSystem allocates its
        // semaphores). Re-arm this thread once, later, when the page is surely mapped and
        // churning - if records only appear after this, arm-before-map was the cause.
        if (_attached && Volatile.Read(ref _overflows) == 0)
        {
            _flushTicks++;
            if (_flushTicks == 200)
            {
                DetachCurrentThread();
                AttachCurrentThread();
            }
        }

        // Diagnostic: read the kernel-level hit count of THIS thread's perf fds. This
        // thread (a guest thread at an import boundary) watches words it itself wrote,
        // so a nonzero count proves the breakpoint fires (=> signal delivery is the gap);
        // zero across all threads proves the breakpoint never arms in-process.
        var fdsDiag = _fds;
        if (fdsDiag != null && Interlocked.Increment(ref _countDiag) <= 40)
        {
            long c0 = -1;
            if (fdsDiag[0] >= 0)
            {
                ulong val = 0;
                if (read(fdsDiag[0], &val, 8) == 8)
                {
                    c0 = (long)val;
                }
            }
            if (c0 > 0)
            {
                Console.Error.WriteLine($"[HW] kcount tid={gettid()} fd0={fdsDiag[0]} hits={c0} overflows={Volatile.Read(ref _overflows)}");
            }
        }

        if (Interlocked.Exchange(ref _armLogged, 1) == 0)
        {
            Console.Error.WriteLine(
                $"[HW] armed {_addrs.Length} addr(s) len={_len} sig={_overflowSignal} " +
                $"threads={Volatile.Read(ref _armedThreads)}");
        }

        var flush = Volatile.Read(ref _recFlush);
        var write = Volatile.Read(ref _recWrite);
        while (flush != write && flush < write)
        {
            ref var rec = ref _recs[flush % RecCap];
            Console.Error.WriteLine(
                $"[HW] seq={rec.Seq} tid={rec.Tid} write addr=0x{rec.Addr:X16} " +
                $"rip=0x{rec.Rip:X16} value=0x{rec.Value:X16} (rip is AFTER the store)");
            flush++;
        }
        Volatile.Write(ref _recFlush, flush);

        var ov = Volatile.Read(ref _overflows);
        if (ov - Volatile.Read(ref _diagPrinted) >= 100000)
        {
            Volatile.Write(ref _diagPrinted, ov);
            Console.Error.WriteLine($"[HW] overflows={ov} threads={Volatile.Read(ref _armedThreads)}");
        }
    }

    /// <summary>Pre-JIT the signal-path methods and warm the libc P/Invoke stubs so
    /// nothing JITs inside a signal frame.</summary>
    public static void WarmUp()
    {
        if (!_enabled)
        {
            return;
        }

        var t = typeof(GuestHwWatchpoint);
        foreach (var name in new[] { nameof(HandleOverflow), nameof(AttachCurrentThread), nameof(DetachCurrentThread), "Record" })
        {
            var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }

        // Warm the P/Invoke marshalling stubs with harmless calls (all fail benignly).
        _ = syscall(NrPerfEventOpen, null, 0, -1, -1, 0);
        _ = fcntl(-1, FGetfl, 0);
        int* tmp = stackalloc int[2];
        _ = fcntl_ptr(-1, FGetfl, tmp);
        _ = ioctl(-1, PerfIocDisable, 0);
        _ = gettid();
        _ = close(-1);
    }

    private static bool TryParse(string? value, out ulong[] addrs, out ulong len)
    {
        addrs = [];
        len = 8;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<ulong>();
        foreach (var tok in parts[0].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var span = tok.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) && list.Count < 4)
            {
                list.Add(a);
            }
        }

        if (list.Count == 0)
        {
            return false;
        }

        if (parts.Length > 1 && ulong.TryParse(parts[1], out var pLen) && (pLen is 1 or 2 or 4 or 8))
        {
            len = pLen;
        }

        addrs = list.ToArray();
        return true;
    }
}
