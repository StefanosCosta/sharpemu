// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// An I/O completion (an AMPR write-address record, an AIO result store) writes the value a
// sceKernelSyncOnAddressWait waiter is polling. Writing that memory alone never releases a parked
// futex waiter — the producer must also post the wake. These tests cover SignalAddressWaiters, the
// shared helper the completion paths now call, so a parked waiter is actually woken.
[Collection(KernelSyncOnAddressCompatStateCollection.Name)]
public sealed class SyncOnAddressWakeOnCompletionTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public async Task SignalAddressWaiters_ReleasesHostThreadWaiter()
    {
        // Same host-thread fallback the completion paths hit outside a guest thread: a waiter parks
        // in Monitor.Wait, and the completion-side SignalAddressWaiters call must release it.
        const ulong address = MemoryBase + 0x130;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var waitContext = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(address, BitConverter.GetBytes(0u));
        waitContext[CpuRegister.Rdi] = address;
        waitContext[CpuRegister.Rsi] = 0;

        var waitTask = Task.Run(() => KernelSyncOnAddressCompatExports.SyncOnAddressWait(waitContext));

        // Let the waiter reach Monitor.Wait before the completion posts its wake.
        await Task.Delay(50);
        KernelSyncOnAddressCompatExports.SignalAddressWaiters(address);

        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))) == waitTask;

        Assert.True(completed, "SyncOnAddressWait did not return after a completion-path SignalAddressWaiters.");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await waitTask);
    }

    [Fact]
    public void SignalAddressWaiters_ZeroAddress_IsNoOp()
    {
        // Completion paths pass whatever address the record/request carried; a zero address must be
        // a harmless no-op (no waiters, no throw) rather than a special case each caller guards.
        var woke = KernelSyncOnAddressCompatExports.SignalAddressWaiters(0);

        Assert.Equal(0, woke);
    }

    [Fact]
    public void SignalAddressWaiters_NoWaiters_ReturnsZero()
    {
        // A completion for an address nobody is parked on wakes nothing, but still records the
        // generation bump so a subsequent waiter's recheck stays correct.
        const ulong address = MemoryBase + 0x140;

        var woke = KernelSyncOnAddressCompatExports.SignalAddressWaiters(address);

        Assert.Equal(0, woke);
    }
}
