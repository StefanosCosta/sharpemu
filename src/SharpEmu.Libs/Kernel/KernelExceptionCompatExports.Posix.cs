// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// POSIX signal surface (sigaction/signal + pthread_kill/thr_kill) layered on top
// of the same handler registry and asynchronous delivery path that the native
// sceKernelInstallExceptionHandler/sceKernelRaiseException exports use. IL2CPP's
// stop-the-world collector suspends threads through the POSIX API rather than the
// sce* one: a coordinator thread pthread_kill()s every other runtime thread with
// SIGUSR1 (30), counts the successful sends, then waits on a semaphore for that
// many acknowledgements; each signalled thread runs its installed SIGUSR1 handler,
// reaches a GC safepoint, and posts the semaphore. Without real delivery the send
// is a silent no-op, no thread ever acknowledges, and the whole app hangs after
// its first frame. Routing these exports into TryRaiseGuestException makes the
// target thread actually run its handler (queued to its next HLE boundary when it
// is running guest code), which is what closes the handshake.
public static partial class KernelExceptionCompatExports
{
    // Real SCE kernel result codes, as observed by the guest's libScePosix
    // pthread_kill wrapper and tested by the IL2CPP coordinator. These are NOT the
    // same numbering as SharpEmu's internal OrbisGen2Result enum, so they are
    // spelled out here. The coordinator only counts a send as a thread that must
    // acknowledge when the wrapper returns 0; ESRCH/EINVAL make it skip that
    // thread, so a send we cannot actually deliver MUST report one of those rather
    // than success (otherwise it waits forever for an acknowledgement).
    private const int ScePosixSuccess = 0;
    private const int ScePosixEsrch = unchecked((int)0x80020003);
    private const int ScePosixEinval = unchecked((int)0x80020016);

    // FreeBSD reserves signals 1..NSIG-1 (NSIG == 32). Handler pointer sentinels
    // SIG_DFL (0) and SIG_IGN (1) carry no user callback to deliver.
    private const int MaxSignalNumber = 31;
    private const ulong SigDfl = 0;
    private const ulong SigIgn = 1;

    [SysAbiExport(
        Nid = "KiJEPEWRyUY",
        ExportName = "sigaction",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Sigaction(CpuContext ctx) => SigactionCore(ctx);

    [SysAbiExport(
        Nid = "UDCI-WazohQ",
        ExportName = "_sigaction",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SigactionUnderscore(CpuContext ctx) => SigactionCore(ctx);

    [SysAbiExport(
        Nid = "VADc3MNQ3cM",
        ExportName = "signal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Signal(CpuContext ctx)
    {
        var signum = unchecked((int)ctx[CpuRegister.Rdi]);
        var handler = ctx[CpuRegister.Rsi];
        if (signum is <= 0 or > MaxSignalNumber)
        {
            // signal() reports failure with SIG_ERR ((void*)-1).
            ctx[CpuRegister.Rax] = unchecked((ulong)-1L);
            return ScePosixEinval;
        }

        var previous = RegisterSignalHandler(signum, handler);
        ctx[CpuRegister.Rax] = previous;
        return ScePosixSuccess;
    }

    [SysAbiExport(
        Nid = "yH-uQW3LbX0",
        ExportName = "pthread_kill",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadKill(CpuContext ctx) =>
        DeliverThreadSignal(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "1wA1-Z+abRE",
        ExportName = "_pthread_kill",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadKillUnderscore(CpuContext ctx) =>
        DeliverThreadSignal(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "nDnzdVIRC3w",
        ExportName = "thr_kill",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int ThrKill(CpuContext ctx) =>
        DeliverThreadSignal(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "310KVHv7P14",
        ExportName = "_thr_kill",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int ThrKillUnderscore(CpuContext ctx) =>
        DeliverThreadSignal(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    private static int SigactionCore(CpuContext ctx)
    {
        var signum = unchecked((int)ctx[CpuRegister.Rdi]);
        var actPtr = ctx[CpuRegister.Rsi];
        var oactPtr = ctx[CpuRegister.Rdx];
        if (signum is <= 0 or > MaxSignalNumber)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong previous;
        lock (_gate)
        {
            _installedHandlers.TryGetValue(signum, out previous);
        }

        // struct sigaction begins with the handler pointer (the sa_handler /
        // sa_sigaction union). That is the only field this layer models, so the
        // old-action struct only needs its handler slot populated for the common
        // save/restore idiom to round-trip.
        if (oactPtr != 0)
        {
            ctx.TryWriteUInt64(oactPtr, previous);
        }

        if (actPtr != 0)
        {
            if (!ctx.TryReadUInt64(actPtr, out var handler))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            RegisterSignalHandler(signum, handler);
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Records (or clears) the process-wide handler for a signal and returns the
    // previous handler pointer. SIG_DFL/SIG_IGN clear the entry: there is no guest
    // callback to invoke, so a later delivery falls through to the "no handler"
    // path instead of jumping to 0/1.
    private static ulong RegisterSignalHandler(int signum, ulong handler)
    {
        lock (_gate)
        {
            _installedHandlers.TryGetValue(signum, out var previous);
            if (handler is SigDfl or SigIgn)
            {
                _installedHandlers.Remove(signum);
            }
            else
            {
                _installedHandlers[signum] = handler;
            }

            return previous;
        }
    }

    private static int DeliverThreadSignal(CpuContext ctx, ulong threadHandle, int signum)
    {
        if (threadHandle == 0 || signum is < 0 or > MaxSignalNumber)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)ScePosixEinval);
            return ScePosixEinval;
        }

        // Signal 0 is the POSIX existence probe: no delivery, just report the
        // thread is reachable (we cannot cheaply distinguish, so assume it is).
        if (signum == 0)
        {
            ctx[CpuRegister.Rax] = ScePosixSuccess;
            return ScePosixSuccess;
        }

        ulong handler;
        lock (_gate)
        {
            _installedHandlers.TryGetValue(signum, out handler);
        }

        // No installed handler means nothing would acknowledge this signal. The
        // caller (e.g. IL2CPP's suspend coordinator) treats a successful send as a
        // thread it must wait to acknowledge, so reporting success here would strand
        // it forever. Report ESRCH so it skips this target instead.
        if (handler == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)ScePosixEsrch);
            return ScePosixEsrch;
        }

        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is null ||
            !scheduler.TryRaiseGuestException(ctx, threadHandle, handler, signum, out _))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)ScePosixEsrch);
            return ScePosixEsrch;
        }

        // If the target is host-parked in an interruptible wait, wake it so it
        // returns to an import boundary and runs the just-queued handler.
        KernelPthreadCompatExports.InterruptHostParkedThreadForSignal(threadHandle);
        ctx[CpuRegister.Rax] = ScePosixSuccess;
        return ScePosixSuccess;
    }
}
