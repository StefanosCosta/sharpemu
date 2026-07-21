// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

/// <summary>
/// Covers HostMemory.Protect's POSIX backend (SharpEmu.HLE.HostMemory, the
/// static class PhysicalVirtualMemory's default IHostMemory actually delegates
/// to) for ranges spanning multiple separately-allocated but adjacent
/// reservations - the same class of bug fixed for sceKernelMprotect.
/// </summary>
public sealed unsafe class HostMemoryProtectTests
{
    private const int ProtNone = 0x0;
    private const int MapPrivate = 0x02;
    private static readonly int MapAnon = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
    private static readonly nint MapFailed = -1;

    [Fact]
    public void ProtectSucceedsAcrossTwoAdjacentlyAllocatedRegions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pageSize = (nuint)Environment.SystemPageSize;
        var regionSize = pageSize * 2;

        // Reserve two separate but adjacent HostMemory.Alloc regions, reproducing
        // "two adjacent sceKernelMmap reservations" without relying on the OS
        // placing independent allocations back-to-back on its own.
        AllocateTwoAdjacentRegions(regionSize, out var firstBase, out var secondBase);

        try
        {
            // The requested range spans both regions (starts in the first,
            // ends in the second) - before the fix this returned false.
            var protectSize = (nuint)regionSize * 2;
            var succeeded = HostMemory.Protect(firstBase, protectSize, HostMemory.PAGE_READONLY, out _);

            Assert.True(succeeded);

            // Confirm the new protection was actually recorded against BOTH
            // covering regions, not just the first one touched by the walk.
            HostMemory.Query(firstBase, out var firstInfo);
            HostMemory.Query(secondBase, out var secondInfo);
            Assert.Equal(HostMemory.PAGE_READONLY, firstInfo.Protect);
            Assert.Equal(HostMemory.PAGE_READONLY, secondInfo.Protect);
        }
        finally
        {
            HostMemory.Free(firstBase, 0, HostMemory.MEM_RELEASE);
            HostMemory.Free(secondBase, 0, HostMemory.MEM_RELEASE);
        }
    }

    [Fact]
    public void ProtectFailsAcrossAGenuineGapBetweenRegions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pageSize = (nuint)Environment.SystemPageSize;
        var regionSize = pageSize * 2;

        // Allocate only the first and third thirds, leaving the middle third
        // as a genuine, never-mapped gap.
        AllocateTwoRegions(regionSize, secondOffset: regionSize * 2, out var firstBase, out var thirdBase);

        try
        {
            var protectSize = (nuint)regionSize * 3;
            var succeeded = HostMemory.Protect(firstBase, protectSize, HostMemory.PAGE_READONLY, out _);

            Assert.False(succeeded);

            // The failed call must not have mutated either real region's
            // protection (all-or-nothing, matching the existing single-region
            // failure semantics).
            HostMemory.Query(firstBase, out var firstInfo);
            Assert.Equal(HostMemory.PAGE_READWRITE, firstInfo.Protect);
        }
        finally
        {
            HostMemory.Free(firstBase, 0, HostMemory.MEM_RELEASE);
            HostMemory.Free(thirdBase, 0, HostMemory.MEM_RELEASE);
        }
    }

    private static void AllocateTwoAdjacentRegions(nuint regionSize, out void* firstBase, out void* secondBase) =>
        AllocateTwoRegions(regionSize, secondOffset: regionSize, out firstBase, out secondBase);

    // Reserves two separate HostMemory.Alloc regions at deterministic addresses
    // (adjacent, or separated by a gap when secondOffset > regionSize). Between
    // picking a free hole and re-reserving it, another thread's mmap (test
    // parallelism, the GC, or the JIT) can steal the hole, so retry with a fresh
    // hole until both reservations land where requested rather than asserting on
    // a single racy attempt.
    private static void AllocateTwoRegions(
        nuint regionSize,
        nuint secondOffset,
        out void* firstBase,
        out void* secondBase)
    {
        var holeSize = secondOffset + regionSize;
        for (var attempt = 0; ; attempt++)
        {
            var hole = mmap(0, holeSize, ProtNone, MapPrivate | MapAnon, -1, 0);
            Assert.NotEqual(MapFailed, hole);
            Assert.Equal(0, munmap(hole, holeSize));

            var first = (void*)hole;
            var second = (void*)((ulong)hole + secondOffset);
            var firstResult = HostMemory.Alloc(
                first, regionSize, HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);
            var secondResult = HostMemory.Alloc(
                second, regionSize, HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE);

            if ((nint)firstResult == (nint)first && (nint)secondResult == (nint)second)
            {
                firstBase = first;
                secondBase = second;
                return;
            }

            // The hole was stolen between munmap and reservation; release
            // whatever did land and try again with a fresh hole.
            if ((nint)firstResult != 0)
            {
                HostMemory.Free(firstResult, 0, HostMemory.MEM_RELEASE);
            }

            if ((nint)secondResult != 0)
            {
                HostMemory.Free(secondResult, 0, HostMemory.MEM_RELEASE);
            }

            Assert.True(attempt < 64, "Could not reserve two adjacent host regions after repeated attempts.");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(nint addr, nuint length);
}
