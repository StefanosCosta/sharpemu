// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Diagnostic: reliably capture the RIP of every guest instruction that WRITES a
/// single target address, even on a busy heap page. Unlike
/// <see cref="GuestWriteRipWatch"/> (which unprotects on a fault and only re-arms at
/// import boundaries, so a target write in the same code burst as another write to
/// the page is missed), this keeps the page armed continuously: on each write-fault
/// it records the writer, then SINGLE-STEPS the one faulting store (clear protection,
/// set the Trap Flag) and RE-PROTECTS on the resulting trap - so the very next write
/// faults too. That makes it deterministic for a write-once-early field.
///
/// Enabled via <c>SHARPEMU_CATCH_WRITE=&lt;addrHex&gt;[,&lt;maxCatches&gt;]</c>. Records
/// go to stderr from <see cref="ArmAndFlush"/> (managed context). Linux-only
/// (hardcodes the Linux gregs offsets RIP=128/EFLAGS=136); guest memory is
/// identity-mapped. Reliable for a single-threaded writer (the same caveat as
/// <see cref="GuestRipBreakpoint"/>); concurrent writers to the page can still race
/// the protect/unprotect window.
/// </summary>
public static unsafe class GuestAddrWriteCatcher
{
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int ProtExec = 0x4;
    private const ulong PageMask = 0xFFFUL;

    private const int GregRip = 128;
    private const int GregEfl = 136;
    private const ulong TrapFlag = 0x100;

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    private static readonly bool _linux = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
    private static readonly ulong _target;
    private static readonly ulong _page;
    private static readonly long _maxCatches;
    private static readonly bool _enabled;

    private static int _pageWritable;
    private static int _armed;      // 1 = page protected read-only
    private static int _disarmed;   // 1 = done (target caught maxCatches times)
    private static long _catches;
    private static long _pageFaults;
    private static long _diagPf;
    private static int _activeResteps;   // stores currently mid-single-step on any thread

    // SHARPEMU_CATCH_WRITE_PAGE=1: record EVERY write to the target's page (addr +
    // rip), not just the exact target address - for finding a struct-creation write
    // whose exact slot varies per run.
    private static readonly bool _pageMode =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_CATCH_WRITE_PAGE"), "1", StringComparison.Ordinal);

    // Record ring (signal-safe: scalars only, no allocation/Console in the handler).
    private struct Rec { public ulong Rip; public ulong Addr; public ulong Before; public long Seq; }
    private const int RecCap = 8192;
    private static readonly Rec[] _recs = new Rec[RecCap];
    private static long _recSeq;
    private static int _recWrite;
    private static int _recFlush;

    // Per-thread re-step state: the faulting store is being single-stepped so the
    // page can be re-protected the instant it retires.
    [ThreadStatic] private static bool _restepping;
    // A target write is being single-stepped; after it retires we read the new value
    // and only record it if non-zero (skips constructor/zeroing writes).
    [ThreadStatic] private static ulong _pendingRip;
    [ThreadStatic] private static ulong _pendingAddr;

    // SHARPEMU_CATCH_WRITE_NONZERO=1: only record target writes whose new value is
    // non-zero (the real assignment, not a constructor zeroing the field).
    private static readonly bool _nonZeroOnly =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_CATCH_WRITE_NONZERO"), "1", StringComparison.Ordinal);

    public static bool Enabled => _enabled;

    static GuestAddrWriteCatcher()
    {
        if (_linux && TryParse(Environment.GetEnvironmentVariable("SHARPEMU_CATCH_WRITE"), out _target, out _maxCatches))
        {
            _page = _target & ~PageMask;
            _enabled = true;
        }
    }

    public static void WarmUp()
    {
        if (!_enabled)
        {
            return;
        }

        var t = typeof(GuestAddrWriteCatcher);
        foreach (var name in new[] { nameof(TryHandleWriteFault), nameof(TryHandleTrap) })
        {
            var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }

        var scratch = NativeMemory.AllocZeroed(0x1000);
        _ = Mprotect((nint)scratch, 0x1000, ProtRead | ProtWrite);
        NativeMemory.Free(scratch);
    }

    /// <summary>
    /// Managed-context pass (call beside the other write-watch flushes): protect the
    /// target page once it is mapped, and print any captured writes.
    /// </summary>
    public static void ArmAndFlush()
    {
        if (!_enabled)
        {
            return;
        }

        var flush = Volatile.Read(ref _recFlush);
        var write = Volatile.Read(ref _recWrite);
        while (flush != write && flush < write)
        {
            ref var rec = ref _recs[flush % RecCap];
            // Current value at the written address (after the store retired) - shows
            // what was written (approx; later writes to the same slot would change it).
            var now = (rec.Addr >= 0x0000000400000000UL && rec.Addr < 0x0000000900000000UL)
                ? *(ulong*)(rec.Addr & ~7UL) : 0UL;
            Console.Error.WriteLine(
                $"[CW] seq={rec.Seq} write addr=0x{rec.Addr:X16} rip=0x{rec.Rip:X16} " +
                $"before=0x{rec.Before:X16} now=0x{now:X16}");
            flush++;
        }
        Volatile.Write(ref _recFlush, flush);

        if (Volatile.Read(ref _disarmed) != 0)
        {
            return;
        }

        // Re-protect EVERY pass (like GuestWriteRipWatch): SharpEmu lazily (re)commits
        // guest heap pages during execution, which resets our read-only protection -
        // arming once and trusting _armed would silently stop catching writes. Skip
        // only while a store is mid-single-step on this thread (its TF trap re-protects).
        if (!_restepping && Volatile.Read(ref _activeResteps) == 0)
        {
            // Fails (ENOMEM) until the page is mapped; retry next pass.
            if (Mprotect((nint)_page, 0x1000, ProtRead) == 0)
            {
                Volatile.Write(ref _armed, 1);
                if (Interlocked.Exchange(ref _pageWritable, 1) == 0)
                {
                    Console.Error.WriteLine($"[CW] armed page 0x{_page:X16} watching 0x{_target:X16}");
                }
            }
        }

        // Heartbeat so a no-catch run still shows whether the page is armed + busy.
        var pf = Volatile.Read(ref _pageFaults);
        if (pf - Volatile.Read(ref _diagPf) >= 200000)
        {
            Volatile.Write(ref _diagPf, pf);
            Console.Error.WriteLine($"[CW] pageFaults={pf} catches={Volatile.Read(ref _catches)} armed={Volatile.Read(ref _armed)}");
        }
    }

    /// <summary>
    /// SIGSEGV write-fault hook. If the fault is on the target page, record the
    /// writer when it hits the target address, then single-step the store (unprotect
    /// + set TF) so it completes; the SIGTRAP re-protects. Returns true if consumed.
    /// </summary>
    public static bool TryHandleWriteFault(ulong faultAddress, nint gregsBase)
    {
        if (!_enabled || Volatile.Read(ref _armed) == 0)
        {
            return false;
        }

        if (faultAddress < _page || faultAddress >= _page + 0x1000)
        {
            return false;
        }

        Interlocked.Increment(ref _pageFaults);
        var g = (byte*)gregsBase;

        var isTarget = faultAddress >= _target && faultAddress < _target + 8;
        if (isTarget && _nonZeroOnly)
        {
            // Defer the decision to TryHandleTrap: record only if the store wrote a
            // non-zero value (the real assignment, not a constructor zeroing).
            _pendingRip = *(ulong*)(g + GregRip);
            _pendingAddr = faultAddress;
        }
        else if (isTarget || _pageMode)
        {
            var slot = Interlocked.Increment(ref _recWrite) - 1;
            if (slot - Volatile.Read(ref _recFlush) < RecCap)
            {
                ref var rec = ref _recs[slot % RecCap];
                rec.Rip = *(ulong*)(g + GregRip);
                rec.Addr = faultAddress;
                rec.Before = *(ulong*)(faultAddress & ~7UL);
                rec.Seq = Interlocked.Increment(ref _recSeq);
            }

            if (isTarget && Interlocked.Increment(ref _catches) >= _maxCatches)
            {
                Volatile.Write(ref _disarmed, 1);
            }
        }

        // Unprotect + single-step this one store so it retires, then re-protect on
        // the trap (unless we're now disarmed).
        _ = Mprotect((nint)_page, 0x1000, ProtRead | ProtWrite | ProtExec);
        Volatile.Write(ref _armed, 0);
        *(ulong*)(g + GregEfl) |= TrapFlag;
        if (!_restepping)
        {
            Interlocked.Increment(ref _activeResteps);
        }

        _restepping = true;
        return true;
    }

    /// <summary>
    /// SIGTRAP hook: the single-stepped store has retired; re-protect the target page
    /// so the next write faults too. Returns true if this was our re-step trap.
    /// </summary>
    public static bool TryHandleTrap(nint gregsBase)
    {
        if (!_enabled || !_restepping)
        {
            return false;
        }

        var g = (byte*)gregsBase;
        *(ulong*)(g + GregEfl) &= ~TrapFlag;
        _restepping = false;
        Interlocked.Decrement(ref _activeResteps);

        // A target store just retired: record it only if it wrote a non-zero value.
        if (_pendingAddr != 0)
        {
            var newVal = *(ulong*)(_pendingAddr & ~7UL);
            if (newVal != 0)
            {
                var slot = Interlocked.Increment(ref _recWrite) - 1;
                if (slot - Volatile.Read(ref _recFlush) < RecCap)
                {
                    ref var rec = ref _recs[slot % RecCap];
                    rec.Rip = _pendingRip;
                    rec.Addr = _pendingAddr;
                    rec.Before = 0;
                    rec.Seq = Interlocked.Increment(ref _recSeq);
                }

                if (Interlocked.Increment(ref _catches) >= _maxCatches)
                {
                    Volatile.Write(ref _disarmed, 1);
                }
            }

            _pendingAddr = 0;
            _pendingRip = 0;
        }

        if (Volatile.Read(ref _disarmed) == 0)
        {
            if (Mprotect((nint)_page, 0x1000, ProtRead) == 0)
            {
                Volatile.Write(ref _armed, 1);
            }
        }

        return true;
    }

    private static bool TryParse(string? value, out ulong target, out long maxCatches)
    {
        target = 0;
        maxCatches = 1;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var span = parts[0].AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        if (!ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out target))
        {
            return false;
        }

        if (parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mc) && mc > 0)
        {
            maxCatches = mc;
        }

        return true;
    }
}
