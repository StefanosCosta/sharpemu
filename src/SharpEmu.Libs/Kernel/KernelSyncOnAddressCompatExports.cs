// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// libkernel's low-level "wait while *addr == pattern" / "wake waiters on addr" pair —
/// the same futex-family contract as Linux FUTEX_WAIT/WAKE, Windows WaitOnAddress, and
/// FreeBSD's UMTX_OP_WAIT_UINT/WAKE (Orbis's kernel is FreeBSD-derived). Used directly by
/// compiled C++ mutex/condvar fast paths (observed via a disassembled call site: an atomic
/// refcount xadd feeds a contended/uncontended branch, and only the contended path zeroes
/// rsi/rdx/rcx and calls this NID — matching the classic futex-mutex algorithm where the
/// "expected" compare value is whatever was just observed in the fast path). No SDK header
/// for these two NIDs ships in any publicly available toolchain, so the exact signature was
/// derived empirically rather than from documentation: rdi=addr, rsi=pattern (uint32,
/// compared against *addr), rdx=timeout pointer (NULL observed at every call site in this
/// game, matching this file's sibling sceKernelWaitSema/WaitEventFlag's IN/OUT
/// pointer-to-usec, NULL=infinite convention) for Wait; rdi=addr, rsi=count for Wake.
/// </summary>
public static class KernelSyncOnAddressCompatExports
{
    private static readonly ConcurrentDictionary<ulong, object> _gates = new();

    private static object GetGate(ulong address) => _gates.GetOrAdd(address, static _ => new object());

    private static string GetWakeKey(ulong address) => $"sceKernelSyncOnAddressWait:{address:X16}";

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var pattern = unchecked((uint)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];

        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!KernelMemoryCompatExports.TryReadUInt32Compat(ctx, address, out var current))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (current != pattern)
        {
            if (_traceSyncAddr) TraceSyncAddr($"wait-eagain addr=0x{address:X16} pattern=0x{pattern:X8} current=0x{current:X8} {FormatCallSite(ctx)}");
            // The compare word already moved past the caller's observed value between
            // the guest's fast-path read and this call — matches the standard futex
            // contract of returning EAGAIN rather than blocking, so a racing Wake can
            // never be lost.
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
        }

        if (_traceSyncAddr) TraceSyncAddr($"wait-block addr=0x{address:X16} pattern=0x{pattern:X8} timeout={(timeoutAddress == 0 ? "infinite" : "finite")} {FormatCallSite(ctx)}");

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !KernelMemoryCompatExports.TryReadUInt32Compat(ctx, timeoutAddress, out timeoutUsec))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var wakeKey = GetWakeKey(address);
        var deadline = timeoutAddress != 0
            ? GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMicroseconds(timeoutUsec))
            : 0;

        // A raw futex wake carries no condition to re-check (unlike a semaphore's
        // token count): being invoked at all means a real Wake targeted this key.
        var woken = false;
        bool WakePredicate()
        {
            woken = true;
            return true;
        }

        int ResumeWait()
        {
            if (_traceSyncAddr) TraceSyncAddr($"wait-cooperative-resume addr=0x{address:X16} woken={woken}");
            return (int)(woken ? OrbisGen2Result.ORBIS_GEN2_OK : OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelSyncOnAddressWait",
                wakeKey,
                ResumeWait,
                WakePredicate,
                deadline))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // Not a guest thread (or no scheduler): fall back to a host-thread wait so the
        // semantics still hold on non-cooperative callers.
        return WaitOnHostThread(ctx, address, timeoutAddress, timeoutUsec);
    }

    private static int WaitOnHostThread(CpuContext ctx, ulong address, ulong timeoutAddress, uint timeoutUsec)
    {
        var gate = GetGate(address);
        var deadlineMs = timeoutAddress != 0
            ? Environment.TickCount64 + Math.Max(1L, timeoutUsec / 1000L)
            : long.MaxValue;
        lock (gate)
        {
            var remaining = deadlineMs - Environment.TickCount64;
            if (timeoutAddress != 0 && remaining <= 0)
            {
                if (_traceSyncAddr) TraceSyncAddr($"wait-host-timeout-immediate addr=0x{address:X16}");
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
            }

            var pulsed = Monitor.Wait(gate, remaining <= 0 ? Timeout.Infinite : (int)Math.Min(remaining, int.MaxValue));
            if (!pulsed)
            {
                if (_traceSyncAddr) TraceSyncAddr($"wait-host-timeout addr=0x{address:X16}");
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
            }
        }

        if (_traceSyncAddr) TraceSyncAddr($"wait-host-wake addr=0x{address:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var count = unchecked((int)ctx[CpuRegister.Rsi]);

        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // A negative count is the conventional "wake everyone" sentinel (mirroring
        // sceKernelSignalSema's unbounded-signal handling elsewhere in this file's
        // sibling exports).
        var maxCount = count < 0 ? int.MaxValue : count;
        var woke = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetWakeKey(address), maxCount);

        if (_gates.TryGetValue(address, out var gate))
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        }

        if (_traceSyncAddr) TraceSyncAddr($"wake addr=0x{address:X16} count={count} cooperative_woken={woke} {FormatCallSite(ctx)}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }

    // Temporary diagnostic (SHARPEMU_LOG_SYNCADDR=1), mirroring KernelSemaphoreCompatExports'
    // SHARPEMU_LOG_SEMA trace exactly, to find which addresses are contended and whether
    // anything ever wakes them.
    private static readonly bool _traceSyncAddr =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SYNCADDR"), "1", StringComparison.Ordinal);

    private static void TraceSyncAddr(string message)
    {
        if (!_traceSyncAddr)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] syncaddr.{message}");
    }

    private static string FormatCallSite(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var returnAddress);
        return $"guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} ret=0x{returnAddress:X16}";
    }
}
