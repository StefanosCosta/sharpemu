// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
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

    private static readonly bool _enabled = !OperatingSystem.IsWindows() && _watches.Length != 0;

    // Preallocated ring; the signal handler only writes scalar fields + a
    // published index, never allocates or locks.
    private static readonly WriteRecord[] _records = new WriteRecord[RecordCapacity];
    private static long _writeSequence;
    private static int _recordWriteIndex;
    private static int _recordFlushIndex;

    public static bool Enabled => _enabled;

    /// <summary>
    /// Managed-context pass (call from the import dispatch loop): protect any
    /// watched page whose backing memory now exists but is not yet armed, and
    /// re-protect pages that were unprotected by a caught fault. Also prints any
    /// captured write records. Cheap when nothing is pending.
    /// </summary>
    public static void ArmAndFlush()
    {
        if (!_enabled)
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
    /// Signal-handler entry. If <paramref name="faultAddress"/> lies on a
    /// watched, armed page, record the writer's RIP + the word's current value,
    /// restore write access so the store can complete, and return true. Must not
    /// allocate or take managed locks.
    /// </summary>
    public static bool TryHandleWriteFault(ulong faultAddress, ulong rip)
    {
        if (!_enabled || faultAddress == 0)
        {
            return false;
        }

        for (var index = 0; index < _watches.Length; index++)
        {
            var watch = _watches[index];
            if (faultAddress < watch.PageStart || faultAddress >= watch.PageEnd)
            {
                continue;
            }

            Interlocked.Increment(ref watch.FaultCount);
            if (Volatile.Read(ref watch.Armed) == 0)
            {
                // Another watch on the same page already unprotected it; nothing
                // to do but let the store proceed.
                return true;
            }

            // Reserve a ring slot. Racing signal deliveries on different host
            // threads are serialised by the per-thread handler depth guard in
            // practice, but Interlocked keeps the index publication safe.
            var slot = Interlocked.Increment(ref _recordWriteIndex) - 1;
            ref var record = ref _records[slot % RecordCapacity];
            record.Rip = rip;
            record.FaultAddress = faultAddress;
            record.ValueBefore = *(ulong*)watch.Address;
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

        return false;
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
