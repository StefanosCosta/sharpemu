// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// A thread host-parked in WaitSemaphoreOnHostThread / SyncOnAddress WaitOnHostThread
// (the primordial/external thread, which cannot park cooperatively) must observe an
// asynchronously-queued guest exception — e.g. IL2CPP's stop-the-world SIGUSR1 — and
// run it IN PLACE without spuriously returning from the wait, then re-enter the wait.
// Before this fix such a signal was queued but never delivered, deadlocking the GC
// coordinator. These tests drive the two host-park loops through a scheduler stub that
// reports/consumes a pending exception, and assert (a) delivery happens during the
// wait and (b) the wait only completes on a real wake, never spuriously.
//
// Joins the GuestThreadScheduler collection because it mutates the process-wide
// GuestThreadExecution.Scheduler static; each test uses distinct sema handles / futex
// addresses so it does not race the other Kernel wait-primitive test classes.
[Collection(GuestThreadSchedulerStateCollection.Name)]
public sealed class KernelHostParkSignalDeliveryTests
{
    private const ulong MemoryBase = 0x30_0000_0000;

    [Fact]
    public async Task WaitSema_HostPark_DeliversQueuedSignalInPlace_ThenReturnsOnlyOnSignal()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var setupContext = new CpuContext(memory, Generation.Gen5);
        var handle = CreateZeroCountSemaphore(memory, setupContext, MemoryBase + 0x10, MemoryBase + 0x40);

        var scheduler = new HostParkScheduler { PendingForCurrentThread = true };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var waitContext = new CpuContext(memory, Generation.Gen5);
            waitContext[CpuRegister.Rdi] = handle;
            waitContext[CpuRegister.Rsi] = 1;   // need one token
            waitContext[CpuRegister.Rdx] = 0;   // infinite

            var waitTask = Task.Run(() => KernelSemaphoreCompatExports.KernelWaitSema(waitContext));

            // The host-park loop should deliver the queued signal in place within a
            // couple of its <=100ms poll iterations.
            Assert.True(await SpinUntilAsync(() => scheduler.DeliverInPlaceCount >= 1, TimeSpan.FromSeconds(5)),
                "queued guest exception was not delivered to the host-parked WaitSema");

            // Delivery must NOT have returned the wait: the token was never produced.
            await Task.Delay(150);
            Assert.False(waitTask.IsCompleted, "WaitSema returned spuriously after signal delivery (no token existed)");

            // A real token release is the only thing that may complete the wait.
            KernelSemaphoreCompatExports.KernelSignalSema(new CpuContext(memory, Generation.Gen5), handle, 1);

            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))) == waitTask;
            Assert.True(completed, "WaitSema did not return after SignalSema.");
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await waitTask);
            Assert.Equal(1, scheduler.DeliverInPlaceCount); // delivered exactly once
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public async Task WaitSema_HostPark_NoPendingSignal_DoesNotDeliver_AndWakesNormally()
    {
        var memory = new FakeCpuMemory(MemoryBase + 0x1000, 0x1000);
        var setupContext = new CpuContext(memory, Generation.Gen5);
        var handle = CreateZeroCountSemaphore(memory, setupContext, MemoryBase + 0x1010, MemoryBase + 0x1040);

        var scheduler = new HostParkScheduler { PendingForCurrentThread = false };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var waitContext = new CpuContext(memory, Generation.Gen5);
            waitContext[CpuRegister.Rdi] = handle;
            waitContext[CpuRegister.Rsi] = 1;
            waitContext[CpuRegister.Rdx] = 0;

            var waitTask = Task.Run(() => KernelSemaphoreCompatExports.KernelWaitSema(waitContext));
            await Task.Delay(150);
            Assert.False(waitTask.IsCompleted, "WaitSema should still be blocked with no token and no signal.");

            KernelSemaphoreCompatExports.KernelSignalSema(new CpuContext(memory, Generation.Gen5), handle, 1);

            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))) == waitTask;
            Assert.True(completed, "WaitSema did not return after SignalSema (gate-restructure regression).");
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await waitTask);
            Assert.Equal(0, scheduler.DeliverInPlaceCount);
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public async Task SyncOnAddress_HostPark_DeliversQueuedSignalInPlace_ThenWakesOnWake()
    {
        const ulong address = MemoryBase + 0x2000;
        var memory = new FakeCpuMemory(MemoryBase + 0x2000, 0x1000);
        memory.TryWrite(address, BitConverter.GetBytes(0u));

        var scheduler = new HostParkScheduler { PendingForCurrentThread = true };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var waitContext = new CpuContext(memory, Generation.Gen5);
            waitContext[CpuRegister.Rdi] = address;
            waitContext[CpuRegister.Rsi] = 0;   // pattern matches *addr, so it parks
            waitContext[CpuRegister.Rdx] = 0;   // infinite

            var waitTask = Task.Run(() => KernelSyncOnAddressCompatExports.SyncOnAddressWait(waitContext));

            Assert.True(await SpinUntilAsync(() => scheduler.DeliverInPlaceCount >= 1, TimeSpan.FromSeconds(5)),
                "queued guest exception was not delivered to the host-parked SyncOnAddressWait");

            await Task.Delay(150);
            Assert.False(waitTask.IsCompleted, "SyncOnAddressWait returned spuriously after signal delivery.");

            var wakeContext = new CpuContext(memory, Generation.Gen5);
            wakeContext[CpuRegister.Rdi] = address;
            wakeContext[CpuRegister.Rsi] = 1;
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(wakeContext);

            var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))) == waitTask;
            Assert.True(completed, "SyncOnAddressWait did not return after SyncOnAddressWake.");
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await waitTask);
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    [Fact]
    public async Task SyncOnAddress_InfiniteWait_ObservesPendingSignalWithinPollInterval()
    {
        // Guards that the infinite-wait Monitor.Wait is bounded: pre-fix it slept
        // ~int.MaxValue ms and would never observe a queued signal without a wake.
        const ulong address = MemoryBase + 0x3000;
        var memory = new FakeCpuMemory(MemoryBase + 0x3000, 0x1000);
        memory.TryWrite(address, BitConverter.GetBytes(0u));

        var scheduler = new HostParkScheduler { PendingForCurrentThread = true };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var waitContext = new CpuContext(memory, Generation.Gen5);
            waitContext[CpuRegister.Rdi] = address;
            waitContext[CpuRegister.Rsi] = 0;
            waitContext[CpuRegister.Rdx] = 0; // infinite

            _ = Task.Run(() => KernelSyncOnAddressCompatExports.SyncOnAddressWait(waitContext));

            Assert.True(await SpinUntilAsync(() => scheduler.DeliverInPlaceCount >= 1, TimeSpan.FromSeconds(2)),
                "infinite SyncOnAddressWait did not poll pending exceptions within the bounded interval");

            // Release it so the test thread does not leak a blocked wait.
            var wakeContext = new CpuContext(memory, Generation.Gen5);
            wakeContext[CpuRegister.Rdi] = address;
            wakeContext[CpuRegister.Rsi] = 1;
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(wakeContext);
        }
        finally
        {
            GuestThreadExecution.Scheduler = null;
        }
    }

    private static uint CreateZeroCountSemaphore(FakeCpuMemory memory, CpuContext ctx, ulong semaAddress, ulong nameAddress)
    {
        memory.TryWrite(nameAddress, Encoding.ASCII.GetBytes("test-sema\0"));
        ctx[CpuRegister.Rdi] = semaAddress;
        ctx[CpuRegister.Rsi] = nameAddress;
        ctx[CpuRegister.Rdx] = 0;              // attr
        ctx[CpuRegister.Rcx] = 0;              // initial count
        ctx[CpuRegister.R8] = 0x7FFF_FFFF;     // max count
        ctx[CpuRegister.R9] = 0;               // no option
        var result = KernelSemaphoreCompatExports.KernelCreateSema(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);

        var handleBytes = new byte[4];
        Assert.True(memory.TryRead(semaAddress, handleBytes));
        return BitConverter.ToUInt32(handleBytes);
    }

    private static async Task<bool> SpinUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    // Minimal scheduler stub: reports a settable "current thread has a queued signal"
    // flag and consumes it once when the host-park loop delivers in place.
    private sealed class HostParkScheduler : IGuestThreadScheduler
    {
        private volatile bool _pending;
        private int _deliverInPlaceCount;

        public bool PendingForCurrentThread
        {
            get => _pending;
            set => _pending = value;
        }

        public int DeliverInPlaceCount => Volatile.Read(ref _deliverInPlaceCount);

        public bool HasPendingGuestExceptionForCurrentThread() => _pending;

        public bool TryDeliverPendingGuestExceptionInPlace(CpuContext currentContext)
        {
            if (!_pending)
            {
                return false;
            }

            _pending = false; // handler "ran"; the wait must now re-check its condition
            Interlocked.Increment(ref _deliverInPlaceCount);
            return true;
        }

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
            error = null;
            return false;
        }
    }
}
