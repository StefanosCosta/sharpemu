// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

/// <summary>
/// <see cref="GuestWriteRipWatch.WarmUp"/> pre-JITs the SIGSEGV write-fault path so
/// none of it compiles inside a signal frame on a cooperative guest worker (the
/// "UnmanagedCallersOnly method from managed code" fail-fast). It drives a synthetic
/// fault on a private scratch page, so its one hard invariant is that it leaves no
/// residue behind: no watch armed, so a subsequent real run starts clean and the
/// import-loop re-arm pass has nothing spurious to flush or re-protect.
/// </summary>
public sealed class GuestWriteRipWatchWarmUpTests
{
    [Fact]
    public void WarmUpRunsWithoutThrowingAndLeavesNoWatchArmed()
    {
        // No static SHARPEMU_WATCH_WRITE_RIP is set under test, so the only way the
        // watch could report itself enabled after WarmUp is a dynamic watch the
        // synthetic fault failed to roll back.
        Assert.False(GuestWriteRipWatch.Enabled);

        GuestWriteRipWatch.WarmUp();

        Assert.False(GuestWriteRipWatch.Enabled);

        // Idempotent: a second warmup (e.g. a re-init path) must also leave it clean.
        GuestWriteRipWatch.WarmUp();
        Assert.False(GuestWriteRipWatch.Enabled);

        // The managed re-arm/flush pass must be a no-op with nothing armed and no
        // record queued, i.e. it must not throw or print a leftover synthetic fault.
        GuestWriteRipWatch.ArmAndFlush();
        Assert.False(GuestWriteRipWatch.Enabled);
    }
}
