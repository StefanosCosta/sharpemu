// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelSyncOnAddressWait/Wake share a process-wide static gate dictionary keyed by
// guest address; parallel test execution across other Kernel test classes is fine since
// each test below uses its own address, but disable intra-class parallelization to keep
// the blocking tests' timing assumptions predictable.
[CollectionDefinition(KernelSyncOnAddressCompatStateCollection.Name, DisableParallelization = true)]
public sealed class KernelSyncOnAddressCompatStateCollection
{
    public const string Name = "KernelSyncOnAddressCompatState";
}

[Collection(KernelSyncOnAddressCompatStateCollection.Name)]
public sealed class KernelSyncOnAddressCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void Wait_ZeroAddress_ReturnsInvalidArgument()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;

        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Wait_UnreadableAddress_ReturnsMemoryFault()
    {
        const ulong unmappedAddress = 0x2_0000_0000;
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;

        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void Wait_ValueAlreadyChangedFromPattern_ReturnsTryAgainWithoutBlocking()
    {
        const ulong address = MemoryBase + 0x100;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(address, BitConverter.GetBytes(7u));
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 0; // pattern the caller expected, no longer matches

        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN, result);
    }

    [Fact]
    public void Wait_TimesOutWhenNeverWoken()
    {
        const ulong address = MemoryBase + 0x110;
        const ulong timeoutAddress = MemoryBase + 0x118;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(address, BitConverter.GetBytes(0u));
        memory.TryWrite(timeoutAddress, BitConverter.GetBytes(1_000u)); // 1ms
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = timeoutAddress;

        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWait(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, result);
    }

    [Fact]
    public void Wake_ZeroAddress_ReturnsInvalidArgument()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0;

        var result = KernelSyncOnAddressCompatExports.SyncOnAddressWake(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public async Task Wake_UnblocksAHostThreadWaiter()
    {
        // Outside a guest thread (no scheduler registered), RequestCurrentThreadBlock
        // returns false and Wait falls back to a real host-thread Monitor.Wait — this
        // exercises that fallback path directly, mirroring the semaphore module's
        // equivalent WaitSemaphoreOnHostThread fallback.
        const ulong address = MemoryBase + 0x120;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var waitContext = new CpuContext(memory, Generation.Gen5);
        var wakeContext = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(address, BitConverter.GetBytes(0u));
        waitContext[CpuRegister.Rdi] = address;
        waitContext[CpuRegister.Rsi] = 0;

        var waitTask = Task.Run(() => KernelSyncOnAddressCompatExports.SyncOnAddressWait(waitContext));

        // Give the background thread a chance to enter Monitor.Wait before waking it;
        // a Wake that arrives first would otherwise have nothing to signal.
        await Task.Delay(50);
        wakeContext[CpuRegister.Rdi] = address;
        wakeContext[CpuRegister.Rsi] = 1;
        var wakeResult = KernelSyncOnAddressCompatExports.SyncOnAddressWake(wakeContext);

        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5))) == waitTask;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, wakeResult);
        Assert.True(completed, "SyncOnAddressWait did not return after SyncOnAddressWake.");
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await waitTask);
    }
}
