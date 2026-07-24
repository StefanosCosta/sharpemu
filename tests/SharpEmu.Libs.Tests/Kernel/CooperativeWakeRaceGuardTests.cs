// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Regression guard for the cooperative sync-on-address lost-wakeup race. A real
// SyncOnAddressWake can fire from another host thread (an I/O/AMPR completion, or another
// guest thread running on its own per-thread runner) AFTER a cooperative waiter's pre-block
// generation snapshot but BEFORE its block is key-matchable in WakeBlockedThreads
// (State==Blocked && HasBlockedContinuation && BlockWakeKey). That wake matches no registered
// waiter yet, wakes nobody, and is dropped - but it bumps the address's wake generation. The
// cooperative WakePredicate's park-time self-probe consults WakeRacedSince against its snapshot
// so the dropped wake is honoured (the thread re-readies) instead of being stranded forever.
// This was the Unity job-worker wake-starvation that wedged IL2CPP level loads.
[Collection(KernelSyncOnAddressCompatStateCollection.Name)]
public sealed class CooperativeWakeRaceGuardTests
{
    // Distinct from the other Kernel wait-primitive test classes' addresses; the wake
    // generation is a process-wide per-address static, so each class uses its own address.
    private const ulong Address = 0x2_0000_0000 + 0x200;

    [Fact]
    public void WakeRacedSince_IsFalse_WhenNoWakeArrivedSinceSnapshot()
    {
        var observed = KernelSyncOnAddressCompatExports.CurrentGenerationForTest(Address);

        // No wake between snapshot and probe -> the self-probe must park (return to caller as
        // "not woken"), waiting for WakeBlockedThreads' later key-matched call.
        Assert.False(KernelSyncOnAddressCompatExports.WakeRacedSince(Address, observed));
    }

    [Fact]
    public void WakeRacedSince_IsTrue_AfterAWakeThatMatchedNoWaiter()
    {
        // Snapshot exactly as a cooperative waiter does, just before it registers its block.
        var observed = KernelSyncOnAddressCompatExports.CurrentGenerationForTest(Address);

        // The racing wake: a producer posts before this waiter is key-matchable, so it wakes
        // nobody (returns 0) - the classic dropped wake this guard must recover.
        var woke = KernelSyncOnAddressCompatExports.SignalAddressWaiters(Address);
        Assert.Equal(0, woke);

        // The park-time self-probe must now observe the raced wake and refuse to park,
        // closing the lost-wakeup window.
        Assert.True(KernelSyncOnAddressCompatExports.WakeRacedSince(Address, observed));
    }
}
