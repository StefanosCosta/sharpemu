// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// The cooperative sync-on-address WakePredicate is probed speculatively and WITHOUT a key match
// - once right after the block registers (RunGuestThread's exit handler) and again after guest
// exception delivery (RestoreInterruptedGuestThread). It must therefore be a pure, idempotent
// condition test. These guard both halves of that contract:
//
//   * it must WAKE when the watched word no longer equals the pattern, even though nobody posted
//     a wake. That is the primitive's actual "wait while *addr == pattern" contract, and it is the
//     only thing that rescues a producer which published the watched value but skipped its wake
//     because its waiter-bookkeeping raced this thread's registration. These waits are infinite
//     (Unity's Baselib semaphores pass a null timeout), so without it such a waiter is stranded
//     forever with its condition already satisfied.
//   * it must KEEP PARKING, on every repeated invocation, while the word still matches and no
//     wake has raced. An earlier revision counted invocations and returned true unconditionally
//     from the second onwards, which released those speculative probes spuriously.
[Collection(KernelSyncOnAddressCompatStateCollection.Name)]
public sealed class SyncOnAddressSpeculativeWakeTests
{
    // The wake generation is a process-wide per-address static, so this class keeps its own
    // address space away from the other sync-on-address test classes.
    private const ulong MemoryBase = 0x3_0000_0000;

    [Fact]
    public void TryWake_Wakes_WhenWatchedWordNoLongerMatchesPattern()
    {
        const ulong address = MemoryBase + 0x100;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var waiter = ParkWaiter(memory, address, handle: 0x5001, out _);

        // The producer publishes the value this waiter is watching but posts NO wake, so nothing
        // bumps the generation. Only re-reading the word can rescue it.
        memory.TryWrite(address, BitConverter.GetBytes(1u));

        Assert.True(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Resume());
    }

    [Fact]
    public void TryWake_KeepsParking_WhenWatchedWordStillMatchesPattern()
    {
        const ulong address = MemoryBase + 0x200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var waiter = ParkWaiter(memory, address, handle: 0x5002, out _);

        // Repeated speculative probes with an unchanged word and no raced wake must all park.
        // The second call is the one that regressed under the old invocation-counting predicate.
        Assert.False(waiter.TryWake());
        Assert.False(waiter.TryWake());
        Assert.False(waiter.TryWake());
    }

    [Fact]
    public void TryWake_StillHonoursARacedWake_WhenWatchedWordIsUnchanged()
    {
        const ulong address = MemoryBase + 0x300;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var waiter = ParkWaiter(memory, address, handle: 0x5003, out _);

        // The classic register-vs-wake drop: a real wake lands before this waiter is
        // key-matchable, so it wakes nobody - but it bumps the generation, which the predicate
        // must still honour without the watched word moving at all.
        Assert.Equal(0, KernelSyncOnAddressCompatExports.SignalAddressWaiters(address));

        Assert.True(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Resume());
    }

    [Fact]
    public void TryWake_KeepsParking_WhenWatchedAddressBecomesUnreadable()
    {
        const ulong address = MemoryBase + 0x400;
        var backing = new FakeCpuMemory(MemoryBase, 0x1000);
        var memory = new RevocableCpuMemory(backing);
        var waiter = ParkWaiter(memory, address, handle: 0x5004, out _);

        // Address-space teardown can unmap the watched page underneath a parked waiter. A failed
        // read is "no evidence the condition changed", so it must park rather than fault.
        memory.Revoked = true;

        Assert.False(waiter.TryWake());
    }

    // Drives the real export far enough to hand back the real WakePredicate: SyncOnAddressWait
    // parks via RequestCurrentThreadBlock, and TryConsumeCurrentThreadBlock returns the waiter
    // the scheduler would have stored.
    private static IGuestThreadBlockWaiter ParkWaiter(
        ICpuMemory memory,
        ulong address,
        ulong handle,
        out int waitResult)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(handle);
        try
        {
            memory.TryWrite(address, BitConverter.GetBytes(0u));

            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = address;
            context[CpuRegister.Rsi] = 0; // pattern
            context[CpuRegister.Rdx] = 0; // null timeout => infinite, as Baselib passes

            waitResult = KernelSyncOnAddressCompatExports.SyncOnAddressWait(context);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waitResult);

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out IGuestThreadBlockWaiter? waiter,
                out var deadline));
            Assert.NotNull(waiter);
            Assert.Equal(0, deadline); // infinite: nothing will ever time it out
            return waiter;
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    // Not an ICpuMemoryWrapper: the predicate's unwrap must leave it in place so the revoked
    // read is actually exercised.
    private sealed class RevocableCpuMemory(ICpuMemory inner) : ICpuMemory
    {
        public bool Revoked { get; set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            !Revoked && inner.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            !Revoked && inner.TryWrite(virtualAddress, source);
    }
}
