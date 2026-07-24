// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Diagnostic: identifies the guest instruction (RIP) that writes to a specific
/// guest address, for direct (non-HLE) guest stores that the import-boundary
/// value poller (SHARPEMU_TRACE_WRITE_ADDRS) cannot attribute. Enabled via
/// <c>SHARPEMU_WATCH_WRITE_RIP=&lt;addr[,addr...]&gt;</c>. Each watched address'
/// page is write-protected; a guest store faults, the POSIX signal handler
/// records the faulting RIP + address + the word's pre-write value into a
/// preallocated ring (signal-safe scalar writes only), restores write access so
/// the store completes, and a managed re-arm/flush pass (driven from the import
/// dispatch loop) prints the captured records and re-protects the page to catch
/// the next writer. Because the page is briefly writable between a caught fault
/// and the next re-arm, this samples writers rather than catching every store -
/// sufficient to find which code last wrote a value that later goes frozen.
/// Linux-only (uses mprotect); a no-op elsewhere.
/// </summary>
public static unsafe class GuestWriteRipWatch
{
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const ulong PageMask = 0xFFFUL;
    private const int RecordCapacity = 256;

    private struct WriteRecord
    {
        public ulong Rip;
        public ulong FaultAddress;
        public ulong ValueBefore;
        public long Sequence;
    }

    private sealed class Watch
    {
        public ulong Address;      // exact watched word (8-byte)
        public ulong PageStart;
        public ulong PageEnd;
        public int Armed;          // 1 = page protected read-only
        public int LoggedArm;
        public long FaultCount;    // total faults observed on this page
    }

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    private static readonly Watch[] _watches = ParseWatches(
        Environment.GetEnvironmentVariable("SHARPEMU_WATCH_WRITE_RIP"));

    // Runtime-armed watches (e.g. from GuestRipBreakpoint on a captured element's
    // work-pointer). Fixed slots so ArmDynamic is signal/alloc-safe. Enabled the
    // moment the first one is armed, even if no static SHARPEMU_WATCH_WRITE_RIP.
    private const int DynamicCapacity = 32;
    private static readonly Watch[] _dynamicWatches = CreateDynamicSlots();
    private static int _dynamicCount;

    private static Watch[] CreateDynamicSlots()
    {
        var slots = new Watch[DynamicCapacity];
        for (var i = 0; i < slots.Length; i++)
        {
            slots[i] = new Watch();
        }

        return slots;
    }

    private static readonly bool _writeWatchCapable = !OperatingSystem.IsWindows();
    private static readonly bool _enabled = _writeWatchCapable && _watches.Length != 0;

    // Preallocated ring; the signal handler only writes scalar fields + a
    // published index, never allocates or locks.
    private static readonly WriteRecord[] _records = new WriteRecord[RecordCapacity];
    private static long _writeSequence;
    private static int _recordWriteIndex;
    private static int _recordFlushIndex;

    public static bool Enabled => _enabled || Volatile.Read(ref _dynamicCount) > 0;

    /// <summary>
    /// Arm a watch on <paramref name="address"/> discovered at runtime (from a
    /// breakpoint capture). Signal/alloc-safe: fixed slots, mprotect only, no
    /// locks. De-dupes by page so watching many objects on one page costs one
    /// slot. Returns false if the table is full or the page is not yet mapped.
    /// </summary>
    public static bool ArmDynamic(ulong address)
    {
        if (!_writeWatchCapable || address == 0)
        {
            return false;
        }

        var pageStart = address & ~PageMask;
        var existing = Volatile.Read(ref _dynamicCount);
        for (var i = 0; i < existing; i++)
        {
            var w = _dynamicWatches[i];
            if (w != null && w.PageStart == pageStart)
            {
                return true; // already watching this page
            }
        }

        if (existing >= DynamicCapacity)
        {
            return false;
        }

        if (Mprotect((nint)pageStart, 0x1000, ProtRead) != 0)
        {
            return false; // page not mapped yet
        }

        var slot = Interlocked.Increment(ref _dynamicCount) - 1;
        if (slot >= DynamicCapacity)
        {
            return false;
        }

        var watch = _dynamicWatches[slot];
        watch.Address = address;
        watch.PageStart = pageStart;
        watch.PageEnd = pageStart + 0x1000UL;
        Volatile.Write(ref watch.Armed, 1);
        return true;
    }

    /// <summary>
    /// Managed-context pass (call from the import dispatch loop): protect any
    /// watched page whose backing memory now exists but is not yet armed, and
    /// re-protect pages that were unprotected by a caught fault. Also prints any
    /// captured write records. Cheap when nothing is pending.
    /// </summary>
    public static void ArmAndFlush()
    {
        if (!Enabled)
        {
            return;
        }

        // Flush captured records first so the value-before reads printed here
        // reflect what the signal handler saw. Console I/O here means this must
        // only run from ordinary managed context, never a signal frame - use
        // Arm() (below) from allocation/signal-reachable paths instead.
        var flushIndex = Volatile.Read(ref _recordFlushIndex);
        var writeIndex = Volatile.Read(ref _recordWriteIndex);
        while (flushIndex != writeIndex)
        {
            ref var record = ref _records[flushIndex % RecordCapacity];
            Console.Error.WriteLine(
                $"[WT][RIP] seq={record.Sequence} write to 0x{record.FaultAddress:X16} " +
                $"from rip=0x{record.Rip:X16} value_before=0x{record.ValueBefore:X16}");
            flushIndex++;
        }
        Volatile.Write(ref _recordFlushIndex, flushIndex);

        for (var index = 0; index < _watches.Length; index++)
        {
            var watch = _watches[index];

            // mprotect fails (ENOMEM) until the heap page backing the watched
            // address is actually mapped; just retry on the next pass. The page
            // may already have been armed by the signal-safe Arm() at map time -
            // report that too (LoggedArm de-dupes with Arm()'s silent arming).
            if (Volatile.Read(ref watch.Armed) == 0 &&
                Mprotect((nint)watch.PageStart, (nuint)(watch.PageEnd - watch.PageStart), ProtRead) == 0)
            {
                Volatile.Write(ref watch.Armed, 1);
            }

            if (Volatile.Read(ref watch.Armed) != 0 &&
                Interlocked.Exchange(ref watch.LoggedArm, 1) == 0)
            {
                Console.Error.WriteLine(
                    $"[WT][RIP] armed watch on 0x{watch.Address:X16} (page 0x{watch.PageStart:X16}) " +
                    $"faults_seen={Volatile.Read(ref watch.FaultCount)}");
            }
        }

        // Re-protect dynamic watches that a caught fault unprotected, so the next
        // write to that page is sampled too (persistent sampling of the producer).
        var dynamicCount = Volatile.Read(ref _dynamicCount);
        for (var index = 0; index < dynamicCount && index < DynamicCapacity; index++)
        {
            var watch = _dynamicWatches[index];
            if (watch.PageStart != 0 &&
                Volatile.Read(ref watch.Armed) == 0 &&
                Mprotect((nint)watch.PageStart, 0x1000, ProtRead) == 0)
            {
                Volatile.Write(ref watch.Armed, 1);
            }
        }
    }

    /// <summary>
    /// Signal- and allocation-path-safe: (re-)protect any unarmed watched page
    /// whose backing memory now exists. No allocation, no locks, no Console I/O,
    /// so it is safe to call synchronously the instant a page is mapped (before
    /// the guest resumes and performs its first store, which the import-boundary
    /// re-arm pass would otherwise race).
    /// </summary>
    public static void Arm()
    {
        if (!_enabled)
        {
            return;
        }

        for (var index = 0; index < _watches.Length; index++)
        {
            var watch = _watches[index];
            if (Volatile.Read(ref watch.Armed) == 0 &&
                Mprotect((nint)watch.PageStart, (nuint)(watch.PageEnd - watch.PageStart), ProtRead) == 0)
            {
                Volatile.Write(ref watch.Armed, 1);
            }
        }
    }

    /// <summary>
    /// Pre-JIT the entire signal-reachable fault-handler path before any POSIX
    /// handler is installed, mirroring <see cref="GuestImageWriteTracker.WarmUp"/>.
    /// A cold managed method entered from a SIGSEGV frame on a cooperative guest
    /// worker JITs inside the signal frame while the thread's GC-mode/stack-walk
    /// bookkeeping is inconsistent, and the runtime fail-fasts ("attempted to call
    /// an UnmanagedCallersOnly method from managed code"). Driving one synthetic
    /// fault on a private page-aligned scratch page compiles
    /// <see cref="TryHandleWriteFault"/>/<c>TryHandleForWatch</c>/
    /// <see cref="ArmDynamic"/> and warms the <c>mprotect</c> marshalling stub in
    /// ordinary managed context; the arm/flush entrypoints are prepared too.
    /// Leaves no watch armed and no record queued. No-op where mprotect is absent.
    /// </summary>
    public static void WarmUp()
    {
        if (!_writeWatchCapable)
        {
            return;
        }

        // Snapshot the ring and dynamic-table cursors so the synthetic fault
        // leaves no residue: no live watch on a freed page, no spurious record for
        // ArmAndFlush to print on the first real pass.
        var savedDynamicCount = Volatile.Read(ref _dynamicCount);
        var savedWriteIndex = Volatile.Read(ref _recordWriteIndex);
        var savedFlushIndex = Volatile.Read(ref _recordFlushIndex);
        var savedSequence = Volatile.Read(ref _writeSequence);

        // Own a full host page so mprotecting our 4 KiB watch page read-only
        // touches only this scratch, never a page shared with the managed heap.
        // Host pages are 16 KiB on Apple Silicon (the emulator runs 4 KiB under
        // Rosetta, but this warmup can run on a bare host); align to the largest so
        // the kernel's length rounding stays inside memory we own.
        var scratch = NativeMemory.AlignedAlloc(0x4000, 0x4000);
        try
        {
            var address = (ulong)scratch;
            if (ArmDynamic(address))
            {
                // TryHandleForWatch restores the page to read/write, so it is safe
                // to free below.
                _ = TryHandleWriteFault(address, 0);
            }
        }
        finally
        {
            NativeMemory.AlignedFree(scratch);

            // Roll back the synthetic activity so a real run starts clean.
            if (savedDynamicCount >= 0 && savedDynamicCount < DynamicCapacity)
            {
                var watch = _dynamicWatches[savedDynamicCount];
                Volatile.Write(ref watch.Armed, 0);
                watch.PageStart = 0;
                watch.PageEnd = 0;
                watch.Address = 0;
                watch.FaultCount = 0;
                watch.LoggedArm = 0;
            }

            Volatile.Write(ref _dynamicCount, savedDynamicCount);
            Volatile.Write(ref _recordWriteIndex, savedWriteIndex);
            Volatile.Write(ref _recordFlushIndex, savedFlushIndex);
            Volatile.Write(ref _writeSequence, savedSequence);
        }

        // Guarantee every signal-reachable method is compiled even if the synthetic
        // arm above did not fire (e.g. mprotect refused the scratch page), and warm
        // the managed-context arm/flush entrypoints the tool also reaches.
        foreach (var name in new[] { "TryHandleWriteFault", "TryHandleForWatch", "ArmDynamic", "Arm", "ArmAndFlush" })
        {
            var method = typeof(GuestWriteRipWatch).GetMethod(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
        }
    }

    /// <summary>
    /// Signal-handler entry. If <paramref name="faultAddress"/> lies on a
    /// watched, armed page, record the writer's RIP + the word's current value,
    /// restore write access so the store can complete, and return true. Must not
    /// allocate or take managed locks.
    /// </summary>
    public static bool TryHandleWriteFault(ulong faultAddress, ulong rip)
    {
        if (!Enabled || faultAddress == 0)
        {
            return false;
        }

        for (var index = 0; index < _watches.Length; index++)
        {
            if (TryHandleForWatch(_watches[index], faultAddress, rip))
            {
                return true;
            }
        }

        var dynamicCount = Volatile.Read(ref _dynamicCount);
        for (var index = 0; index < dynamicCount && index < DynamicCapacity; index++)
        {
            if (TryHandleForWatch(_dynamicWatches[index], faultAddress, rip))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryHandleForWatch(Watch watch, ulong faultAddress, ulong rip)
    {
        if (watch.PageStart == 0 || faultAddress < watch.PageStart || faultAddress >= watch.PageEnd)
        {
            return false;
        }

        Interlocked.Increment(ref watch.FaultCount);
        if (Volatile.Read(ref watch.Armed) == 0)
        {
            // Another watch on the same page already unprotected it; nothing to
            // do but let the store proceed.
            return true;
        }

        var slot = Interlocked.Increment(ref _recordWriteIndex) - 1;
        ref var record = ref _records[slot % RecordCapacity];
        record.Rip = rip;
        record.FaultAddress = faultAddress;
        // Aligned read stays inside the watched page (never crosses into a
        // possibly-unmapped next page from a signal frame).
        record.ValueBefore = *(ulong*)(faultAddress & ~7UL);
        record.Sequence = Interlocked.Increment(ref _writeSequence);

        // Restore write access so the faulting store completes. The managed
        // ArmAndFlush pass re-protects to catch the next writer.
        Volatile.Write(ref watch.Armed, 0);
        _ = Mprotect(
            (nint)watch.PageStart,
            (nuint)(watch.PageEnd - watch.PageStart),
            ProtRead | ProtWrite);
        return true;
    }

    private static Watch[] ParseWatches(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || OperatingSystem.IsWindows())
        {
            return [];
        }

        var watches = new List<Watch>();
        foreach (var token in value.Split(
                     [',', ';', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var span = token.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                continue;
            }

            var pageStart = address & ~PageMask;
            watches.Add(new Watch
            {
                Address = address,
                PageStart = pageStart,
                PageEnd = pageStart + 0x1000UL,
            });
        }

        return watches.ToArray();
    }
}
