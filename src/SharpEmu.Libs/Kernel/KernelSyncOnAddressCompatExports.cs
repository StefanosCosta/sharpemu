// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
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

    // Bumped by SyncOnAddressWake before it pulses the gate. WaitOnHostThread captures the
    // generation before it can yield and rechecks it under the same lock instead of trusting
    // Monitor.Wait's return value alone: a bare Monitor.PulseAll only reaches a thread that is
    // *already* inside Monitor.Wait at that exact instant, so a wake landing in the window
    // between a host-thread caller deciding to block and actually entering the wait (e.g.
    // while it's still computing its own timeout) would otherwise vanish with no persisted
    // effect - a classic missed-wakeup, empirically confirmed this session (a real Wake call
    // observed for a contended address, but the waiting host thread kept timing out and
    // retrying forever regardless).
    private static readonly ConcurrentDictionary<ulong, long> _wakeGenerations = new();

    private static long CurrentGeneration(ulong address) =>
        _wakeGenerations.TryGetValue(address, out var generation) ? generation : 0;

    // A raw futex carries no re-checkable condition, so a cooperative waiter's park-time
    // self-probe uses the wake generation to detect a SyncOnAddressWake that fired between its
    // pre-block snapshot and its block becoming key-matchable in WakeBlockedThreads - a wake
    // that matched no registered waiter yet and was therefore dropped. Used by the cooperative
    // WakePredicate; exposed internally for regression tests of that lost-wakeup guard.
    internal static bool WakeRacedSince(ulong address, long observedGeneration) =>
        CurrentGeneration(address) != observedGeneration;

    // Test-only: read the current wake generation so a test can snapshot it, post a wake with
    // no registered waiter (the racing wake), and assert WakeRacedSince observes it.
    internal static long CurrentGenerationForTest(ulong address) => CurrentGeneration(address);

    private static object GetGate(ulong address) => _gates.GetOrAdd(address, static _ => new object());

    private static string GetWakeKey(ulong address) => $"sceKernelSyncOnAddressWait:{address:X16}";

    // Peels per-thread CpuContext memory decorators (TrackedCpuMemory and friends) down to the
    // shared address space, so a parked waiter's word can be sampled from any host thread without
    // touching that thread's diagnostics. Loops because decorators can nest.
    private static ICpuMemory UnwrapMemory(ICpuMemory memory)
    {
        while (memory is ICpuMemoryWrapper wrapper)
        {
            var inner = wrapper.Inner;
            if (ReferenceEquals(inner, memory) || inner is null)
            {
                break;
            }

            memory = inner;
        }

        return memory;
    }

    // Deliberately failure-tolerant: the watched page can be unmapped underneath a parked waiter
    // (address-space teardown). A failed read simply means "no evidence the condition changed",
    // so the caller keeps parking rather than faulting.
    private static bool TryReadWatchedWord(ICpuMemory memory, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static int _watchedWordWakeCount;

    // Every one of these is a producer that published the value a waiter was watching and then
    // failed to post the matching wake - i.e. a genuine missed wakeup that this re-read rescued.
    // Logged unconditionally (not behind SHARPEMU_LOG_SYNCADDR) but rate-limited, so the rescue
    // stays visible instead of silently papering the defect over.
    private static void ReportWatchedWordWake(ulong address, uint pattern, uint latest)
    {
        var count = Interlocked.Increment(ref _watchedWordWakeCount);
        if (count > 8 && (count & (count - 1)) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][WARN] syncaddr.watched_word_wake addr=0x{address:X16} " +
            $"pattern=0x{pattern:X8} latest=0x{latest:X8} count={count} " +
            "(condition satisfied with no wake posted - rescued a missed wakeup)");
    }

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        // Lazily arms the SHARPEMU_FORCE_POST diagnostic once the guest starts using
        // sync-on-address (the scheduler is up by then); cheap no-op when unset/already armed.
        SyncAddressForcePost.EnsureStarted();

        var address = ctx[CpuRegister.Rdi];
        var pattern = unchecked((uint)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];

        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Snapshot the wake generation BEFORE reading *addr, so it covers the entire window
        // from here through block registration and the park-time recheck, for BOTH the
        // cooperative and host-thread paths. Any SyncOnAddressWake/SignalAddressWaiters on
        // this address after this point bumps the generation (see _wakeGenerations), which
        // both paths recheck to avoid a lost wakeup that races block registration.
        var observedGeneration = CurrentGeneration(address);

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

        // Hoisted once, here, because WakePredicate below can be invoked from any host thread
        // (a wake posted by an I/O completion, or the scheduler's speculative self-probe) long
        // after this call returns. ctx.Memory is this guest thread's per-thread TrackedCpuMemory,
        // which records LastFailure on a failed read; reading through it from a foreign thread
        // would corrupt the parked thread's own fault diagnostics. The unwrapped memory is the
        // process-wide, reader-locked address space, which is what we actually want to sample.
        var watchedMemory = UnwrapMemory(ctx.Memory);

        // A raw futex wake carries no re-checkable condition of its own (unlike a semaphore's
        // token count or an event flag's bit pattern), so this predicate reconstructs one. It is
        // evaluated identically on EVERY invocation and must stay pure and idempotent, because
        // the scheduler probes it speculatively and without a key match: once synchronously right
        // after registering a block (RunGuestThread's exit handler) and again after delivering a
        // guest exception (RestoreInterruptedGuestThread). An earlier revision counted
        // invocations and returned true unconditionally from the second onwards, which made
        // both of those probes release the waiter spuriously.
        //
        // Two independent reasons to stop parking, checked in this order:
        //
        // 1. The wake generation moved. A real SyncOnAddressWake can fire from another host
        //    thread (an I/O/AMPR completion posting SignalAddressWaiters, or another guest thread
        //    on its own per-thread runner) in the window between this wait's *addr read and the
        //    block becoming key-matchable in WakeBlockedThreads (which requires State==Blocked &&
        //    HasBlockedContinuation && BlockWakeKey). Such a wake matches no thread yet and is
        //    dropped - the classic register-vs-wake lost wakeup. It does bump the generation
        //    first (SignalAddressWaiters), so comparing against the pre-block snapshot honours
        //    it. Keeping this branch first and unconditional means every key-matched wake behaves
        //    exactly as it did before.
        //
        // 2. The watched word no longer equals the pattern. This is the primitive's actual
        //    contract - "wait while *addr == pattern" - and it covers the one case a generation
        //    bump cannot: a producer that publishes the value this waiter is watching but skips
        //    its wake entirely, because its own waiter-bookkeeping raced this thread's
        //    registration. Such a producer posts nothing, so nothing bumps the generation, and on
        //    an infinite wait (the norm for Unity's Baselib semaphores, which pass a null timeout)
        //    the waiter would be stranded forever with its condition already satisfied. SharpEmu
        //    widens that window far beyond hardware, because blocking is continuation-capture at
        //    the import boundary: the guest must unwind out through the native stub to
        //    RunGuestThread before State=Blocked publishes. Re-reading here is always a legal
        //    wake - a futex caller must re-check its own condition and re-park regardless - and
        //    it cannot degenerate into a spin, because a waiter that re-parks passes the value it
        //    just read as its new pattern.
        var woken = false;
        bool WakePredicate()
        {
            if (WakeRacedSince(address, observedGeneration))
            {
                woken = true;
                return true;
            }

            if (TryReadWatchedWord(watchedMemory, address, out var latest) && latest != pattern)
            {
                ReportWatchedWordWake(address, pattern, latest);
                woken = true;
                return true;
            }

            return false;
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
        return WaitOnHostThread(ctx, address, timeoutAddress, timeoutUsec, observedGeneration);
    }

    private static int WaitOnHostThread(CpuContext ctx, ulong address, ulong timeoutAddress, uint timeoutUsec, long observedGeneration)
    {
        var gate = GetGate(address);
        var deadlineMs = timeoutAddress != 0
            ? Environment.TickCount64 + Math.Max(1L, timeoutUsec / 1000L)
            : long.MaxValue;
        var scheduler = GuestThreadExecution.Scheduler;
        // Explicit Monitor.Enter/Exit (not lock{}) so the gate can be released while a
        // queued async signal handler runs in place on this host thread.
        Monitor.Enter(gate);
        try
        {
            // Rechecked every iteration under the same lock SyncOnAddressWake pulses under,
            // instead of trusting Monitor.Wait's pulsed/timed-out return value alone - closes
            // the lost-wakeup window described on _wakeGenerations.
            while (CurrentGeneration(address) == observedGeneration)
            {
                var remaining = deadlineMs - Environment.TickCount64;
                if (timeoutAddress != 0 && remaining <= 0)
                {
                    if (_traceSyncAddr) TraceSyncAddr($"wait-host-timeout addr=0x{address:X16}");
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                }

                // Deliver a queued async guest exception (IL2CPP stop-the-world SIGUSR1)
                // in place without returning from the futex wait; release the gate first
                // (the handler parks on the GC's ResumeSemaphore for the whole cycle).
                if (scheduler?.HasPendingGuestExceptionForCurrentThread() == true)
                {
                    Monitor.Exit(gate);
                    try
                    {
                        scheduler.TryDeliverPendingGuestExceptionInPlace(ctx);
                    }
                    finally
                    {
                        Monitor.Enter(gate);
                    }

                    continue;
                }

                // Bounded (<=100ms) so the pending-exception poll above runs even for an
                // otherwise-infinite wait, which SyncOnAddressWake would not pulse.
                Monitor.Wait(gate, (int)Math.Min(remaining <= 0 ? long.MaxValue : remaining, 100));
            }
        }
        finally
        {
            Monitor.Exit(gate);
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
        var woke = SignalAddressWaiters(address, maxCount);

        if (_traceSyncAddr) TraceSyncAddr($"wake addr=0x{address:X16} count={count} cooperative_woken={woke} {FormatCallSite(ctx)}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    /// <summary>
    /// Posts the producer half of the sync-on-address (futex) protocol: wakes any thread
    /// parked in <see cref="SyncOnAddressWait"/> on <paramref name="address"/>. Extracted from
    /// <see cref="SyncOnAddressWake"/> so I/O-completion paths that write a value a waiter is
    /// polling (an AMPR write-address completion, an AIO result store) can post the matching wake
    /// — writing the watched memory alone never releases a parked waiter. Spurious wakes are
    /// harmless: the waiter re-checks its pattern and re-parks. A zero address is a no-op.
    /// </summary>
    internal static int SignalAddressWaiters(ulong address, int maxCount = int.MaxValue)
    {
        if (address == 0)
        {
            return 0;
        }

        // Bump the wake generation BEFORE waking/pulsing anyone. The cooperative waiter's
        // park-time self-probe (SyncOnAddressWait's WakePredicate invocation #1) decides whether
        // to park by comparing CurrentGeneration(address) against the generation it snapshotted
        // at wait entry. If the bump happened AFTER WakeBlockedThreads, a waiter that registered
        // its block and ran that self-probe in the window between WakeBlockedThreads (which missed
        // it — not yet registered) and this bump would observe the stale generation, park, and
        // miss the wake entirely (recovering only on its next timeout). Bumping first closes that
        // register-vs-wake window for BOTH the cooperative self-probe and the host-thread
        // generation recheck (which likewise only needs the bump to precede the pulse).
        _wakeGenerations.AddOrUpdate(address, 1, static (_, current) => current + 1);

        var woke = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetWakeKey(address), maxCount) ?? 0;

        if (_gates.TryGetValue(address, out var gate))
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        }

        return woke;
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
