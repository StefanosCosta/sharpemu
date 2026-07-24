// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Covers the host-shutdown safe-point primitive that lets a guest thread spinning
// on a non-blocking import (e.g. an FMOD audio worker re-calling sceKernelSignalSema)
// wind out to the host-exit sentinel so teardown can reach guest-thread quiescence.
// The DispatchImport entry gate itself needs a live native trampoline, so these tests
// exercise TryForceGuestExitToHostStub(shutdown: true) directly via the same reflection
// harness pattern as ImportTrampolineAbiTests, driving the thread-static active-execution
// state that the redirect reads.
public sealed class ForcedGuestExitOnShutdownTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ReturnSlotAddress = MemoryBase + 0x200;
    private const ulong Sentinel = 0x8_0000_0000; // >= 65536, the primitive's floor.

    [Fact]
    public unsafe void ForceGuestExit_Shutdown_PatchesReturnSlotAndSetsFlag()
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        var argPack = Marshal.AllocHGlobal(128);
        try
        {
            new Span<byte>((void*)argPack, 128).Clear();

            // Make this test thread look like the active execution thread with a
            // patchable guest return slot, so TryPatchActiveGuestReturnSlot succeeds.
            SetThreadStatic("_activeExecutionBackend", backend);
            SetThreadStatic("_activeCpuContext", context);
            SetThreadStatic("_activeEntryReturnSentinelRip", Sentinel);
            SetThreadStatic("_activeGuestReturnSlotAddress", ReturnSlotAddress);
            SetThreadStatic("_activeForcedGuestExit", false);

            var redirected = InvokeForceGuestExit(backend, argPack, shutdown: true);

            Assert.True(redirected);
            // The arg-pack return address and the guest return slot both point at the
            // host-exit sentinel now, so the trampoline ret unwinds the slice.
            Assert.Equal(unchecked((long)Sentinel), Marshal.ReadInt64(argPack + 96));
            Assert.True(context.TryReadUInt64(ReturnSlotAddress, out var patched));
            Assert.Equal(Sentinel, patched);
            Assert.True(GetThreadStaticBool("_activeForcedGuestExit"));
        }
        finally
        {
            Marshal.FreeHGlobal(argPack);
            ResetThreadStatics();
        }
    }

    [Fact]
    public unsafe void ForceGuestExit_NoActiveReturnSlot_FailsSafeWithoutRedirect()
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));

        var argPack = Marshal.AllocHGlobal(128);
        try
        {
            new Span<byte>((void*)argPack, 128).Clear();

            // No active execution thread: ActiveEntryReturnSentinelRip falls back to the
            // zeroed instance field (< 65536), so the redirect must decline rather than
            // patch a bogus slot. This is the teardown-already-progressed fail-safe.
            ResetThreadStatics();

            var redirected = InvokeForceGuestExit(backend, argPack, shutdown: true);

            Assert.False(redirected);
            Assert.Equal(0L, Marshal.ReadInt64(argPack + 96));
            Assert.False(GetThreadStaticBool("_activeForcedGuestExit"));
        }
        finally
        {
            Marshal.FreeHGlobal(argPack);
            ResetThreadStatics();
        }
    }

    private static bool InvokeForceGuestExit(DirectExecutionBackend backend, nint argPack, bool shutdown)
    {
        var method = typeof(DirectExecutionBackend).GetMethod(
            "TryForceGuestExitToHostStub",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(backend, [argPack, 1234L, 0xDEADBEEFUL, "TESTNID00aa", shutdown])!;
    }

    private static void SetThreadStatic(string name, object? value)
    {
        var field = typeof(DirectExecutionBackend).GetField(
            name,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(null, value);
    }

    private static bool GetThreadStaticBool(string name)
    {
        var field = typeof(DirectExecutionBackend).GetField(
            name,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (bool)field.GetValue(null)!;
    }

    // Thread-static state is per-thread, but xUnit reuses threads across tests, so
    // clear it after each case to avoid leaking a fake active-execution backend.
    private static void ResetThreadStatics()
    {
        SetThreadStatic("_activeExecutionBackend", null);
        SetThreadStatic("_activeCpuContext", null);
        SetThreadStatic("_activeEntryReturnSentinelRip", 0UL);
        SetThreadStatic("_activeGuestReturnSlotAddress", 0UL);
        SetThreadStatic("_activeForcedGuestExit", false);
    }
}
