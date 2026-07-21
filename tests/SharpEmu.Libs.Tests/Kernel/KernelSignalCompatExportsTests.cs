// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Serializes the test classes that mutate the process-wide
// GuestThreadExecution.Scheduler static (and, for signals, the shared
// _installedHandlers registry) so xUnit's per-class parallelism cannot race them.
[CollectionDefinition(GuestThreadSchedulerStateCollection.Name, DisableParallelization = true)]
public sealed class GuestThreadSchedulerStateCollection
{
    public const string Name = "GuestThreadSchedulerState";
}

// Covers the POSIX signal surface that IL2CPP's stop-the-world uses: sigaction/
// signal register a handler into the shared exception-handler registry, and
// pthread_kill/thr_kill route into IGuestThreadScheduler.TryRaiseGuestException so
// the target thread runs that handler. Each test uses a distinct signal number
// because the handler registry is process-wide static state, and restores it to
// SIG_DFL afterwards so nothing leaks between tests.
[Collection(GuestThreadSchedulerStateCollection.Name)]
public sealed class KernelSignalCompatExportsTests
{
    private const int ScePosixEsrch = unchecked((int)0x80020003);
    private const ulong ThreadHandle = 0x7000_0000_1234_5678;

    [Fact]
    public void PthreadKill_WithHandlerInstalledBySigaction_DeliversToTargetThread()
    {
        const int signum = 7;
        const ulong memoryBase = 0x10_0000_0000;
        const ulong actAddress = memoryBase + 0x100;
        const ulong handlerAddress = 0x8000_0000UL;

        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new RecordingScheduler { RaiseResult = true };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            // struct sigaction begins with the handler pointer.
            Assert.True(context.TryWriteUInt64(actAddress, handlerAddress));
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = actAddress;
            context[CpuRegister.Rdx] = 0;
            Assert.Equal(0, KernelExceptionCompatExports.Sigaction(context));

            context[CpuRegister.Rdi] = ThreadHandle;
            context[CpuRegister.Rsi] = signum;
            var result = KernelExceptionCompatExports.PthreadKill(context);

            Assert.Equal(0, result);
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.Equal(1, scheduler.RaiseCount);
            Assert.Equal(ThreadHandle, scheduler.LastThreadHandle);
            Assert.Equal(handlerAddress, scheduler.LastHandler);
            Assert.Equal(signum, scheduler.LastExceptionType);
        }
        finally
        {
            ClearHandler(context, memory, signum);
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public void PthreadKill_WithNoHandlerInstalled_ReturnsEsrchAndDoesNotDeliver()
    {
        const int signum = 9;
        var memory = new RecordingCpuMemory(0x11_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new RecordingScheduler { RaiseResult = true };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            context[CpuRegister.Rdi] = ThreadHandle;
            context[CpuRegister.Rsi] = signum;
            var result = KernelExceptionCompatExports.PthreadKill(context);

            // No installed handler means nothing would acknowledge the signal, so
            // reporting success would strand a suspend coordinator forever.
            Assert.Equal(ScePosixEsrch, result);
            Assert.Equal(unchecked((ulong)ScePosixEsrch), context[CpuRegister.Rax]);
            Assert.Equal(0, scheduler.RaiseCount);
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public void PthreadKill_WhenSchedulerCannotDeliver_ReturnsEsrch()
    {
        const int signum = 11;
        const ulong memoryBase = 0x12_0000_0000;
        const ulong actAddress = memoryBase + 0x100;

        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new RecordingScheduler { RaiseResult = false };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            Assert.True(context.TryWriteUInt64(actAddress, 0x8000_0000UL));
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = actAddress;
            context[CpuRegister.Rdx] = 0;
            KernelExceptionCompatExports.Sigaction(context);

            context[CpuRegister.Rdi] = ThreadHandle;
            context[CpuRegister.Rsi] = signum;
            var result = KernelExceptionCompatExports.PthreadKill(context);

            Assert.Equal(ScePosixEsrch, result);
            Assert.Equal(1, scheduler.RaiseCount);
        }
        finally
        {
            ClearHandler(context, memory, signum);
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public void Sigaction_WritesPreviousHandlerToOldActionStruct()
    {
        const int signum = 13;
        const ulong memoryBase = 0x13_0000_0000;
        const ulong actAddress = memoryBase + 0x100;
        const ulong oactAddress = memoryBase + 0x200;
        const ulong firstHandler = 0x8000_1000UL;
        const ulong secondHandler = 0x8000_2000UL;

        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        try
        {
            Assert.True(context.TryWriteUInt64(actAddress, firstHandler));
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = actAddress;
            context[CpuRegister.Rdx] = 0;
            KernelExceptionCompatExports.Sigaction(context);

            Assert.True(context.TryWriteUInt64(actAddress, secondHandler));
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = actAddress;
            context[CpuRegister.Rdx] = oactAddress;
            KernelExceptionCompatExports.Sigaction(context);

            Assert.True(context.TryReadUInt64(oactAddress, out var savedHandler));
            Assert.Equal(firstHandler, savedHandler);
        }
        finally
        {
            ClearHandler(context, memory, signum);
        }
    }

    [Fact]
    public void Signal_ReturnsPreviousHandlerAndRegistersNew()
    {
        const int signum = 15;
        const ulong firstHandler = 0x8000_3000UL;
        const ulong secondHandler = 0x8000_4000UL;

        var memory = new RecordingCpuMemory(0x14_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new RecordingScheduler { RaiseResult = true };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = firstHandler;
            KernelExceptionCompatExports.Signal(context);

            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = secondHandler;
            KernelExceptionCompatExports.Signal(context);
            Assert.Equal(firstHandler, context[CpuRegister.Rax]);

            // The latest handler is the one delivered.
            context[CpuRegister.Rdi] = ThreadHandle;
            context[CpuRegister.Rsi] = signum;
            KernelExceptionCompatExports.PthreadKill(context);
            Assert.Equal(secondHandler, scheduler.LastHandler);
        }
        finally
        {
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = 0; // SIG_DFL clears it
            KernelExceptionCompatExports.Signal(context);
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public void Sigaction_WithSigIgn_ClearsInstalledHandler()
    {
        const int signum = 17;
        const ulong memoryBase = 0x15_0000_0000;
        const ulong actAddress = memoryBase + 0x100;

        var memory = new RecordingCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var scheduler = new RecordingScheduler { RaiseResult = true };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            Assert.True(context.TryWriteUInt64(actAddress, 0x8000_5000UL));
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = actAddress;
            context[CpuRegister.Rdx] = 0;
            KernelExceptionCompatExports.Sigaction(context);

            // SIG_IGN (1) clears the handler.
            Assert.True(context.TryWriteUInt64(actAddress, 1UL));
            context[CpuRegister.Rdi] = signum;
            context[CpuRegister.Rsi] = actAddress;
            context[CpuRegister.Rdx] = 0;
            KernelExceptionCompatExports.Sigaction(context);

            context[CpuRegister.Rdi] = ThreadHandle;
            context[CpuRegister.Rsi] = signum;
            var result = KernelExceptionCompatExports.PthreadKill(context);

            Assert.Equal(ScePosixEsrch, result);
            Assert.Equal(0, scheduler.RaiseCount);
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    private static void ClearHandler(CpuContext context, RecordingCpuMemory memory, int signum)
    {
        // Restore process-wide state to SIG_DFL so the static registry does not
        // leak an installed handler into other tests.
        var scratch = memory.BaseAddress + 0x10;
        if (context.TryWriteUInt64(scratch, 0))
        {
            context[CpuRegister.Rdi] = (ulong)signum;
            context[CpuRegister.Rsi] = scratch;
            context[CpuRegister.Rdx] = 0;
            KernelExceptionCompatExports.Sigaction(context);
        }
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        public bool RaiseResult { get; set; }

        public int RaiseCount { get; private set; }

        public ulong LastThreadHandle { get; private set; }

        public ulong LastHandler { get; private set; }

        public int LastExceptionType { get; private set; }

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = null;
            return false;
        }

        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error)
        {
            returnValue = 0;
            error = null;
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => Array.Empty<GuestThreadSnapshot>();

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = null;
            return false;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            RaiseCount++;
            LastThreadHandle = threadHandle;
            LastHandler = handler;
            LastExceptionType = exceptionType;
            error = RaiseResult ? null : "test: delivery refused";
            return RaiseResult;
        }
    }

    private sealed class RecordingCpuMemory : ICpuMemory
    {
        private readonly byte[] _storage;

        public RecordingCpuMemory(ulong baseAddress, int size)
        {
            BaseAddress = baseAddress;
            _storage = new byte[size];
        }

        public ulong BaseAddress { get; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < BaseAddress)
            {
                return false;
            }

            var relative = virtualAddress - BaseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
