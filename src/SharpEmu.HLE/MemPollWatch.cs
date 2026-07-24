// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.HLE;

/// <summary>
/// Diagnostic: a signal-free memory write-watch. Enabled via
/// <c>SHARPEMU_MEM_POLL=&lt;addr[,addr...]&gt;</c> (optionally <c>SHARPEMU_MEM_POLL_US=&lt;n&gt;</c>,
/// default 20). A dedicated background host thread reads each guest address (as a u64)
/// in a tight loop and logs every value change (<c>init-&gt;v</c>, then <c>old-&gt;new</c>) with a
/// millisecond timestamp.
///
/// Guest x86-64 code runs directly on the host CPU, so an individual store cannot be
/// trapped without a page fault (the SIGSEGV write-watch crashes on continuation-resumed
/// guest threads) or a hardware watchpoint (empirically does not fire on guest stores in
/// this setup). This poll-based watch avoids both: it never installs a signal handler and
/// never patches code — it simply samples the value. It cannot name the writing instruction,
/// but it decisively answers "does this field ever change, to what, and roughly when" — e.g.
/// whether a transient (later-freed) container's count is ever written non-zero.
///
/// Guest memory is identity-mapped (host VA == guest address) and the guest heap arenas are
/// large committed regions, so reading a since-freed object's address does not fault; the
/// read is range-guarded to the guest window regardless. Reads race with guest writes; a torn
/// read is harmless for change detection (a 0-&gt;N transition is still observed on the next pass).
/// </summary>
public static unsafe class MemPollWatch
{
    private static readonly ulong[] _addresses = Parse(
        Environment.GetEnvironmentVariable("SHARPEMU_MEM_POLL"));

    // SHARPEMU_MEM_POLL_CHAIN="off1,off2,...,offN": follow a pointer chain from a base that is
    // published live at runtime (see PublishBase) to track a TRANSIENT object whose absolute
    // address is not stable across runs. Each pass: ptr=base; ptr=[ptr+off_i] for i<N-1 (deref,
    // commit-guarded); watch (ptr + off_N) as a u64 WITHOUT dereferencing. Re-resolved every
    // pass so the watch follows the object as it is constructed and freed.
    private static readonly ulong[] _chain = Parse(
        Environment.GetEnvironmentVariable("SHARPEMU_MEM_POLL_CHAIN"));

    private static long _base; // published live via PublishBase (0 = not yet known)

    private static readonly int _intervalSpin = ParseIntervalSpin(
        Environment.GetEnvironmentVariable("SHARPEMU_MEM_POLL_US"));

    private static readonly bool _enabled = _addresses.Length != 0 || _chain.Length != 0;

    // SHARPEMU_MEM_POLL_PVM=1: read watched addresses through the emulator's memory
    // abstraction (ICpuMemory.TryRead) instead of raw host pointers. This is the ONLY way to
    // observe DIRECT-MEMORY regions (sceKernelMapDirectMemory pools): raw pointers +
    // HostMemory.Query report them as uncommitted so the default path skips them, yet the
    // renderer/shader-eval read them fine via this same abstraction. Also enables 4-byte reads
    // for u32 counters at 4-aligned addresses that the 8-aligned raw path rejects.
    private static readonly bool _usePvm = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_MEM_POLL_PVM"), "1", StringComparison.Ordinal);

    private static volatile ICpuMemory? _guestMemory;

    private static Thread? _thread;
    private static volatile bool _stop;

    public static bool Enabled => _enabled;

    /// <summary>Register the guest memory abstraction so SHARPEMU_MEM_POLL_PVM reads can resolve
    /// direct-memory regions. Safe to call repeatedly (e.g. from AGC submit setup).</summary>
    public static void AttachGuestMemory(ICpuMemory memory) => _guestMemory = memory;

    public static void Start()
    {
        if (!_enabled || _thread is not null)
        {
            return;
        }

        _thread = new Thread(PollLoop)
        {
            IsBackground = true,
            Name = "MemPollWatch",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
        Console.Error.WriteLine(
            $"[MEMPOLL] watching {_addresses.Length} address(es), spin={_intervalSpin}, pvm={_usePvm}");
    }

    public static void Stop() => _stop = true;

    /// <summary>Publish the live base pointer for SHARPEMU_MEM_POLL_CHAIN resolution (e.g. the op
    /// captured at a Perform-entry hook). Cheap; safe to call repeatedly.</summary>
    public static void PublishBase(ulong basePointer) => Volatile.Write(ref _base, (long)basePointer);

    // Resolve the SHARPEMU_MEM_POLL_CHAIN from the published base into a final watch address, or
    // 0 if any hop is not yet resolvable (base unknown, null pointer, or uncommitted page).
    private static bool TryResolveChain(out ulong watchAddress)
    {
        watchAddress = 0;
        var ptr = (ulong)Volatile.Read(ref _base);
        if (ptr == 0 || _chain.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < _chain.Length - 1; i++)
        {
            var at = ptr + _chain[i];
            if (!TryReadCommitted(at, out ptr) || ptr == 0)
            {
                return false;
            }
        }

        watchAddress = ptr + _chain[^1];
        return (watchAddress & 0x7UL) == 0;
    }

    private static bool TryReadCommitted(ulong address, out ulong value)
    {
        value = 0;

        var mem = _guestMemory;
        if (_usePvm && mem is not null)
        {
            // Read through the emulator's memory abstraction so DIRECT-MEMORY regions resolve.
            // PVM.TryRead validates safely (returns false, never aborts), so the address range is
            // relaxed to also cover low GPU/video-memory buffers (e.g. compute outputs / scanouts)
            // that fall below the CPU guest window. 8-byte read when 8-aligned; otherwise a 4-byte
            // read (u32 counters/CBs are commonly at 4-aligned addresses the raw path rejects).
            if (address < 0x1000UL || (address & 0x3UL) != 0)
            {
                return false;
            }

            Span<byte> buf = stackalloc byte[8];
            if ((address & 0x7UL) == 0)
            {
                if (!mem.TryRead(address, buf))
                {
                    return false;
                }

                value = BitConverter.ToUInt64(buf);
            }
            else
            {
                if (!mem.TryRead(address, buf[..4]))
                {
                    return false;
                }

                value = BitConverter.ToUInt32(buf[..4]);
            }

            return true;
        }

        // Raw path: strict guest window + 8-byte alignment; a host-thread fault on a
        // reserved-but-uncommitted page aborts the process, so VirtualQuery-gate first.
        if (address < 0x0000000400000000UL || address >= 0x0000000900000000UL || (address & 0x7UL) != 0)
        {
            return false;
        }

        if (HostMemory.Query((void*)address, out var info) == 0 || info.State != HostMemory.MEM_COMMIT)
        {
            return false;
        }

        value = *(ulong*)address;
        return true;
    }

    private static void PollLoop()
    {
        var last = new ulong[_addresses.Length];
        var seen = new bool[_addresses.Length];
        // TryReadCommitted gates each read: raw pointers are VirtualQuery-checked for
        // MEM_COMMIT (a host-thread fault on a reserved-but-uncommitted guest page aborts the
        // process), and the PVM path returns false until the region resolves.
        var sw = Stopwatch.StartNew();
        var chainSeen = false;
        var chainLast = 0UL;
        var chainLastAddr = 0UL;

        while (!_stop)
        {
            var anyPending = false;

            if (_chain.Length != 0)
            {
                if (TryResolveChain(out var chainAddr) && TryReadCommitted(chainAddr, out var chainVal))
                {
                    if (!chainSeen || chainVal != chainLast || chainAddr != chainLastAddr)
                    {
                        Console.Error.WriteLine(
                            $"[MEMPOLL] t={sw.ElapsedMilliseconds}ms chain->0x{chainAddr:X} " +
                            $"{(chainSeen ? $"0x{chainLast:X16}" : "init")} -> 0x{chainVal:X16}");
                        chainLast = chainVal;
                        chainLastAddr = chainAddr;
                        chainSeen = true;
                    }
                }
                else
                {
                    anyPending = true; // base/chain not resolvable yet
                }
            }

            for (var i = 0; i < _addresses.Length; i++)
            {
                if (!TryReadCommitted(_addresses[i], out var value))
                {
                    anyPending = true; // not yet readable (uncommitted / unresolved region)
                    continue;
                }

                if (!seen[i] || value != last[i])
                {
                    Console.Error.WriteLine(
                        $"[MEMPOLL] t={sw.ElapsedMilliseconds}ms addr=0x{_addresses[i]:X} " +
                        $"{(seen[i] ? $"0x{last[i]:X16}" : "init")} -> 0x{value:X16}");
                    last[i] = value;
                    seen[i] = true;
                }
            }

            // While any watched page is still unmapped, poll slowly (VirtualQuery contends the
            // vmem region lock with guest allocations). Once all are committed, spin tightly to
            // catch short-lived transitions.
            if (anyPending)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(_intervalSpin);
            }
        }
    }

    private static int ParseIntervalSpin(string? value)
    {
        // SHARPEMU_MEM_POLL_US is advisory; map to a SpinWait iteration count (~tens of ns each).
        // Default keeps the poll extremely tight to catch short-lived transitions.
        if (!string.IsNullOrWhiteSpace(value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) &&
            us > 0)
        {
            return Math.Clamp(us * 20, 20, 200000);
        }

        return 400;
    }

    private static ulong[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var addresses = new List<ulong>();
        foreach (var token in value.Split(
                     [',', ';', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var span = token.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                addresses.Add(address);
            }
        }

        return addresses.ToArray();
    }
}
