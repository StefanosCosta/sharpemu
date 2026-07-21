// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// PhysicalVirtualMemory is the host-backed (identity-mapped) implementation.
// Reserve-only regions (> 4 GiB, non-executable) defer commit until first
// access; TryAllocateGuestMemory serves a first-fit free-list with coalescing.
// These tests pin that behaviour through fake IHostMemory implementations.
public sealed class PhysicalVirtualMemoryTests
{
    // 1. Lazy commit: a reserve-only region has its pages committed on demand
    //    when read; freshly committed pages read as zero.
    [Fact]
    public void LazyReadCommitsPageOnDemandAndReadsZero()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        // > 4 GiB, non-executable -> reserve-only with lazy commit.
        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        Assert.NotEqual(0UL, address);

        // Discard the priming commits AllocateAt issues up front; we want to
        // observe the on-demand commit triggered by the read itself.
        host.CommitCalls.Clear();

        var buffer = new byte[1];
        Assert.True(memory.TryRead(address, buffer));
        Assert.Equal(0, buffer[0]);

        // The touched page (page-aligned to `address`) was committed on demand.
        var page = address & ~0xFFFUL;
        Assert.Equal([(page, 0x1000UL, HostPageProtection.ReadWrite)], host.CommitCalls);
    }

    [Fact]
    public void RepeatedLazyReadUsesCommittedRangeCache()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        host.CommitCalls.Clear();

        Span<byte> buffer = stackalloc byte[16];
        Assert.True(memory.TryRead(address + 0x100, buffer));
        var queryCallsAfterFirstRead = host.QueryCalls;
        Assert.True(memory.TryRead(address + 0x108, buffer[..8]));

        Assert.Equal(queryCallsAfterFirstRead, host.QueryCalls);
        Assert.Single(host.CommitCalls);
    }

    [Fact]
    public void TryCopyHandlesOverlappingIdentityMappedRanges()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        Assert.True(memory.TryWrite(address, new byte[] { 1, 2, 3, 4, 5, 6 }));

        Assert.True(memory.TryCopy(address + 2, address, 4));

        Span<byte> result = stackalloc byte[6];
        Assert.True(memory.TryRead(address, result));
        Assert.Equal(new byte[] { 1, 2, 1, 2, 3, 4 }, result.ToArray());
    }

    [Fact]
    public void RepeatedTryCopyKeepsSourceAndDestinationCommitRangesCached()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        var source = address + 0x100;
        var destination = address + 0x1100;
        Assert.True(memory.TryWrite(source, new byte[] { 1, 2, 3, 4 }));
        Assert.True(memory.TryWrite(destination, new byte[4]));

        host.CommitCalls.Clear();
        Assert.True(memory.TryCopy(destination, source, 4));
        var queryCallsAfterFirstCopy = host.QueryCalls;
        Assert.True(memory.TryCopy(destination, source, 4));

        Assert.Equal(queryCallsAfterFirstCopy, host.QueryCalls);
    }

    // 2. Reserve-only region: GetPointer commits the page before returning it,
    //    so callers receive a valid (non-null) pointer. An unmapped address yields null.
    [Fact]
    public unsafe void GetPointerOnReserveOnlyRegionCommitsAndReturnsValidPointer()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        host.CommitCalls.Clear();

        var pointer = memory.GetPointer(address + 0x123);
        Assert.NotEqual(0UL, (ulong)pointer);
        Assert.Equal(address + 0x123, (ulong)pointer);

        var page = (address + 0x123) & ~0xFFFUL;
        Assert.Equal([(page, 0x1000UL, HostPageProtection.ReadWrite)], host.CommitCalls);
    }

    [Fact]
    public unsafe void GetPointerOnUnmappedAddressReturnsNull()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);

        Assert.Equal(0UL, (ulong)memory.GetPointer(0x0001_0000));
    }

    // 3. Free-list reuse: a freed range is served back by first-fit allocation,
    //    preferring the lowest fitting free range over the larger trailing span.
    [Fact]
    public void FreedRangeIsReusedByFirstFitAllocation()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x4000, 0x1000, out var second));
        Assert.NotEqual(first, second);
        Assert.True(memory.TryFreeGuestMemory(first));

        // A smaller allocation must reuse first's freed slot (lowest fitting range),
        // not the larger trailing free range.
        Assert.True(memory.TryAllocateGuestMemory(0x2000, 0x1000, out var reused));
        Assert.Equal(first, reused);
    }

    // 4. Coalescing: freeing the middle of three adjacent ranges merges both the
    //    left and right free neighbours in a single TryFreeGuestMemory call,
    //    restoring the full span for subsequent first-fit reuse.
    [Fact]
    public void FreeingMiddleRangeCoalescesBothNeighbours()
    {
        using var memory = new PhysicalVirtualMemory(new FakeHostMemory());

        // Three adjacent 0x1000 allocations: offsets 0x1000, 0x2000, 0x3000.
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var first));
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var second));
        Assert.True(memory.TryAllocateGuestMemory(0x1000, 0x1000, out var third));

        // Free the outer ranges first, leaving two separate free ranges.
        Assert.True(memory.TryFreeGuestMemory(first));
        Assert.True(memory.TryFreeGuestMemory(third));

        // Freeing the middle range must coalesce both neighbours at once.
        Assert.True(memory.TryFreeGuestMemory(second));

        // The whole arena is now one coalesced free range; a full-arena allocation
        // reuses first's base address.
        Assert.True(memory.TryAllocateGuestMemory(0x000F_F000, 0x1000, out var coalesced));
        Assert.Equal(first, coalesced);
    }

    // 4b. External host-memory fallback: when an access misses every tracked
    //     region (e.g. a libc-malloc'd buffer living at a host address outside
    //     the region table), ctx.Memory routes to the registered
    //     IExternalHostMemoryAccessor so every caller is malloc-safe. With no
    //     accessor set, a miss must still return false (behaviour preserved),
    //     and a normal mapped access must never consult the accessor.
    [Fact]
    public void TryReadWriteFallBackToExternalAccessorOnRegionMiss()
    {
        var host = new FakeHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var accessor = new StubExternalHostMemory(baseAddress: 0x0000_5000_0000_0000, size: 0x1000);
        memory.ExternalHostMemory = accessor;

        // 0x5000_0000_0000 is in no tracked region -> routes to the accessor.
        Assert.True(memory.TryWrite(0x0000_5000_0000_0010, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
        Assert.Equal(1, accessor.WriteCalls);

        Span<byte> read = stackalloc byte[4];
        Assert.True(memory.TryRead(0x0000_5000_0000_0010, read));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, read.ToArray());
        Assert.True(memory.TryCompare(0x0000_5000_0000_0010, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
        Assert.False(memory.TryCompare(0x0000_5000_0000_0010, new byte[] { 0, 0, 0, 0 }));
    }

    [Fact]
    public void RegionMissReturnsFalseWithNoExternalAccessor()
    {
        var host = new FakeHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        // No ExternalHostMemory set -> behaviour preserved: a miss is false.
        Assert.False(memory.TryRead(0x0000_5000_0000_0010, new byte[4]));
        Assert.False(memory.TryWrite(0x0000_5000_0000_0010, new byte[4]));
        Assert.False(memory.TryCompare(0x0000_5000_0000_0010, new byte[4]));
    }

    [Fact]
    public void MappedAccessNeverConsultsExternalAccessor()
    {
        using var host = new LazyZeroedHostMemory();
        using var memory = new PhysicalVirtualMemory(host);
        var accessor = new StubExternalHostMemory(baseAddress: 0x0000_5000_0000_0000, size: 0x1000);
        memory.ExternalHostMemory = accessor;

        var address = memory.AllocateAt(0, (4UL << 30) + 0x1000, executable: false);
        Assert.True(memory.TryWrite(address, new byte[] { 1, 2, 3, 4 }));
        Span<byte> read = stackalloc byte[4];
        Assert.True(memory.TryRead(address, read));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, read.ToArray());

        // The mapped region satisfied every access; the fallback was never used.
        Assert.Equal(0, accessor.ReadCalls);
        Assert.Equal(0, accessor.WriteCalls);
    }

    // 5. Region-spanning access: TryRead/TryWrite/TryCompare/TryCopy must succeed
    //    across the boundary between two (or more) separately-allocated but
    //    adjacent regions - AllocateAt/Map never coalesce regions, and each
    //    sceKernelMmap-style guest allocation becomes its own MemoryRegion, so a
    //    single guest memcpy/memmove routinely spans several of these in practice.
    [Fact]
    public void TryReadSucceedsAcrossTwoAdjacentRegions()
    {
        using var host = new AdjacentRegionHostMemory(0x4000);
        using var memory = new PhysicalVirtualMemory(host);

        var firstRegion = memory.AllocateAt(0, 0x1000, executable: false);
        var secondRegion = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(firstRegion + 0x1000, secondRegion);

        Assert.True(memory.TryWrite(firstRegion + 0xFFC, new byte[] { 1, 2, 3, 4 }));
        Assert.True(memory.TryWrite(secondRegion, new byte[] { 5, 6, 7, 8 }));

        Span<byte> result = stackalloc byte[8];
        Assert.True(memory.TryRead(firstRegion + 0xFFC, result));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, result.ToArray());
    }

    [Fact]
    public void TryWriteSucceedsAcrossTwoAdjacentRegions()
    {
        using var host = new AdjacentRegionHostMemory(0x4000);
        using var memory = new PhysicalVirtualMemory(host);

        var firstRegion = memory.AllocateAt(0, 0x1000, executable: false);
        var secondRegion = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(firstRegion + 0x1000, secondRegion);

        Assert.True(memory.TryWrite(firstRegion + 0xFFE, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }));

        Span<byte> firstHalf = stackalloc byte[2];
        Span<byte> secondHalf = stackalloc byte[2];
        Assert.True(memory.TryRead(firstRegion + 0xFFE, firstHalf));
        Assert.True(memory.TryRead(secondRegion, secondHalf));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, firstHalf.ToArray());
        Assert.Equal(new byte[] { 0xCC, 0xDD }, secondHalf.ToArray());
    }

    [Fact]
    public void TryCompareAcrossTwoAdjacentRegionsDetectsMatchAndMismatch()
    {
        using var host = new AdjacentRegionHostMemory(0x4000);
        using var memory = new PhysicalVirtualMemory(host);

        var firstRegion = memory.AllocateAt(0, 0x1000, executable: false);
        var secondRegion = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(firstRegion + 0x1000, secondRegion);

        Assert.True(memory.TryWrite(firstRegion + 0xFFE, new byte[] { 1, 2, 3, 4 }));

        Assert.True(memory.TryCompare(firstRegion + 0xFFE, new byte[] { 1, 2, 3, 4 }));
        Assert.False(memory.TryCompare(firstRegion + 0xFFE, new byte[] { 1, 2, 3, 5 }));
    }

    [Fact]
    public void TryCopySucceedsWithSourceAndDestinationSpanningDifferentBoundaries()
    {
        using var host = new AdjacentRegionHostMemory(0x6000);
        using var memory = new PhysicalVirtualMemory(host);

        // Three adjacent regions. Source spans the first boundary (region 1/2);
        // destination spans the second boundary (region 2/3) at a different
        // offset, so the two sides' split points don't line up.
        var region1 = memory.AllocateAt(0, 0x1000, executable: false);
        var region2 = memory.AllocateAt(0, 0x1000, executable: false);
        var region3 = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(region1 + 0x1000, region2);
        Assert.Equal(region2 + 0x1000, region3);

        var source = region1 + 0xFFC;
        var destination = region2 + 0xFFE;
        Assert.True(memory.TryWrite(source, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        Assert.True(memory.TryCopy(destination, source, 8));

        Span<byte> result = stackalloc byte[8];
        Assert.True(memory.TryRead(destination, result));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, result.ToArray());
    }

    [Fact]
    public void TryReadSucceedsAcrossThreeAdjacentRegions()
    {
        using var host = new AdjacentRegionHostMemory(0x6000);
        using var memory = new PhysicalVirtualMemory(host);

        var region1 = memory.AllocateAt(0, 0x1000, executable: false);
        var region2 = memory.AllocateAt(0, 0x1000, executable: false);
        var region3 = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(region1 + 0x1000, region2);
        Assert.Equal(region2 + 0x1000, region3);

        var address = region1 + 0xFF8;
        var payload = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.True(memory.TryWrite(address, payload));

        Span<byte> result = stackalloc byte[16];
        Assert.True(memory.TryRead(address, result));
        Assert.Equal(payload, result.ToArray());
    }

    [Fact]
    public void TryReadAcrossGapBetweenRegionsFailsWithoutPartialMutation()
    {
        using var host = new AdjacentRegionHostMemory(0x6000);
        using var memory = new PhysicalVirtualMemory(host);

        var firstRegion = memory.AllocateAt(0, 0x1000, executable: false);
        host.SkipBytes(0x1000); // leave a genuine, never-allocated gap
        var thirdRegion = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(firstRegion + 0x2000, thirdRegion);

        // A sentinel entirely inside firstRegion (does not span the gap),
        // used to prove the failed cross-gap read below didn't corrupt it.
        Assert.True(memory.TryWrite(firstRegion + 0xFF0, new byte[] { 9, 9, 9, 9 }));

        Span<byte> result = stackalloc byte[4];
        result.Fill(0xAA);
        // Genuinely spans from firstRegion into the never-allocated gap.
        Assert.False(memory.TryRead(firstRegion + 0xFFE, result));

        // The destination buffer must be untouched on failure - all-or-nothing.
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, result.ToArray());

        // The unrelated sentinel bytes must also be unaffected.
        Span<byte> sentinel = stackalloc byte[4];
        Assert.True(memory.TryRead(firstRegion + 0xFF0, sentinel));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, sentinel.ToArray());
    }

    [Fact]
    public void TryWriteAcrossGapBetweenRegionsFailsWithoutPartialMutation()
    {
        using var host = new AdjacentRegionHostMemory(0x6000);
        using var memory = new PhysicalVirtualMemory(host);

        var firstRegion = memory.AllocateAt(0, 0x1000, executable: false);
        host.SkipBytes(0x1000);
        var thirdRegion = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(firstRegion + 0x2000, thirdRegion);

        // A sentinel entirely inside firstRegion (does not span the gap).
        Assert.True(memory.TryWrite(firstRegion + 0xFF0, new byte[] { 1, 2, 3, 4 }));

        // Genuinely spans from firstRegion into the never-allocated gap.
        Assert.False(memory.TryWrite(firstRegion + 0xFFE, new byte[] { 9, 9, 9, 9 }));

        Span<byte> result = stackalloc byte[4];
        Assert.True(memory.TryRead(firstRegion + 0xFF0, result));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.ToArray());
    }

    [Fact]
    public void TryCompareAcrossGapBetweenRegionsFails()
    {
        using var host = new AdjacentRegionHostMemory(0x6000);
        using var memory = new PhysicalVirtualMemory(host);

        var firstRegion = memory.AllocateAt(0, 0x1000, executable: false);
        host.SkipBytes(0x1000);
        var thirdRegion = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(firstRegion + 0x2000, thirdRegion);

        Assert.True(memory.TryWrite(firstRegion + 0xFF0, new byte[] { 1, 2, 3, 4 }));

        // Genuinely spans from firstRegion into the never-allocated gap.
        Assert.False(memory.TryCompare(firstRegion + 0xFFE, new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void TryCopyAcrossGapBetweenRegionsFails()
    {
        using var host = new AdjacentRegionHostMemory(0x8000);
        using var memory = new PhysicalVirtualMemory(host);

        var source = memory.AllocateAt(0, 0x1000, executable: false);
        var destinationRegion1 = memory.AllocateAt(0, 0x1000, executable: false);
        host.SkipBytes(0x1000);
        var destinationRegion3 = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(destinationRegion1 + 0x2000, destinationRegion3);

        Assert.True(memory.TryWrite(source, new byte[] { 1, 2, 3, 4 }));
        // A sentinel entirely inside destinationRegion1 (does not span the gap).
        Assert.True(memory.TryWrite(destinationRegion1 + 0xFF0, new byte[] { 9, 9, 9, 9 }));

        // The destination range genuinely spans the gap between
        // destinationRegion1 and destinationRegion3 - the copy must fail and
        // leave the destination untouched.
        Assert.False(memory.TryCopy(destinationRegion1 + 0xFFE, source, 4));

        Span<byte> result = stackalloc byte[4];
        Assert.True(memory.TryRead(destinationRegion1 + 0xFF0, result));
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, result.ToArray());
    }

    [Fact]
    public void TryWriteAcrossTwoRegionsElevatesOnlyTheReadOnlyRegionAndRestoresItAfterward()
    {
        using var host = new AdjacentRegionHostMemory(0x4000);
        using var memory = new PhysicalVirtualMemory(host);

        // Map the first region read-only (no Write flag) via the ELF-loader
        // path, which populates _pageProtections - the only way an ordinary
        // AllocateAt-created region's pages can fail CanWriteWithoutProtectionChange.
        // The second region is a plain, fully-writable AllocateAt region.
        // Map requires an exact placement address up front (unlike AllocateAt's
        // desiredAddress=0 "let the host pick" convention), so peek the host's
        // next hand-out address before mapping.
        var firstRegion = host.NextAddress;
        memory.Map(firstRegion, 0x1000, 0, ReadOnlySpan<byte>.Empty, ProgramHeaderFlags.Read);
        var secondRegion = memory.AllocateAt(0, 0x1000, executable: false);
        Assert.Equal(firstRegion + 0x1000, secondRegion);

        var protectCallsBefore = host.ProtectCalls.Count;
        Assert.True(memory.TryWrite(firstRegion + 0xFFE, new byte[] { 1, 2, 3, 4 }));

        // The write across the boundary required temporarily elevating the
        // read-only region's protection (at least one Protect call beyond
        // whatever Map itself already issued).
        Assert.True(host.ProtectCalls.Count > protectCallsBefore);

        Span<byte> result = stackalloc byte[4];
        Assert.True(memory.TryRead(firstRegion + 0xFFE, result));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.ToArray());

        // The elevation must have been restored - a second write to the SAME
        // now-still-read-only region (not spanning the boundary this time)
        // must go through the exclusive path again, proving the fast path
        // still sees it as non-writable.
        Assert.True(memory.TryWrite(firstRegion + 0x10, new byte[] { 5, 6, 7, 8 }));
    }

    /// <summary>
    /// Host memory backed by one real, zero-initialised block. Successive
    /// Allocate/Reserve calls hand out consecutive sub-ranges of that block
    /// (ignoring the requested address, matching LazyZeroedHostMemory's style),
    /// so consecutive PhysicalVirtualMemory.AllocateAt/Map calls land back-to-back
    /// in _regions - reproducing "two adjacent but separately-allocated
    /// reservations" (e.g. two adjacent sceKernelMmap calls) deterministically.
    /// All allocations here are well under the reserve-only threshold (4 GiB),
    /// so IsReservedOnly is always false and EnsureRangeCommitted no-ops.
    /// </summary>
    private sealed unsafe class AdjacentRegionHostMemory : IHostMemory, IDisposable
    {
        private readonly void* _allocation;
        private readonly ulong _base;
        private ulong _cursor;
        private bool _freed;

        public AdjacentRegionHostMemory(ulong totalSize)
        {
            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)(totalSize + 0xFFF));
            _base = ((ulong)_allocation + 0xFFF) & ~0xFFFUL;
            _cursor = _base;
        }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> ProtectCalls { get; } = [];

        /// <summary>Peeks the address the next desiredAddress=0 Allocate/Reserve call will hand out.</summary>
        public ulong NextAddress => _cursor;

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection)
        {
            // Honor an exact non-zero placement request (needed by Map, which
            // computes the write address from its own virtualAddress parameter
            // rather than from whatever this call returns); a desiredAddress of
            // 0 means "hand out the next successive cursor address" instead.
            var address = desiredAddress != 0 ? desiredAddress : _cursor;
            _cursor = address + size;
            return address;
        }

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        /// <summary>Advances the cursor without allocating it, opening a gap before the next call.</summary>
        public void SkipBytes(ulong size) => _cursor += size;

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => true;

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            ProtectCalls.Add((address, size, protection));
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            if (!_freed)
            {
                System.Runtime.InteropServices.NativeMemory.Free(_allocation);
                _freed = true;
            }
        }
    }

    /// <summary>
    /// Host memory backed by a single real, zero-initialised page. Reserve/Allocate
    /// report the page-aligned buffer address so lazy-commit read paths can actually
    /// dereference the returned pointer. Query always reports Reserved, so
    /// EnsureRangeCommitted issues a Commit on first access.
    /// </summary>
    private sealed unsafe class LazyZeroedHostMemory : IHostMemory, IDisposable
    {
        private readonly void* _allocation;
        private readonly ulong _address;
        private bool _freed;

        public LazyZeroedHostMemory()
        {
            _allocation = System.Runtime.InteropServices.NativeMemory.AllocZeroed(0x3000);
            _address = ((ulong)_allocation + 0xFFF) & ~0xFFFUL;
        }

        public List<(ulong Address, ulong Size, HostPageProtection Protection)> CommitCalls { get; } = [];

        public int QueryCalls { get; private set; }

        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) => _address;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) => _address;

        public bool Commit(ulong address, ulong size, HostPageProtection protection)
        {
            CommitCalls.Add((address, size, protection));
            return true;
        }

        public bool Free(ulong address)
        {
            // The real buffer is released in Dispose; keep Free a no-op so
            // PhysicalVirtualMemory.Clear does not double-free it.
            return true;
        }

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            QueryCalls++;
            var pageAddress = address & ~0xFFFUL;
            info = new HostRegionInfo(
                pageAddress,
                pageAddress,
                0x1000,
                HostRegionState.Reserved,
                0,
                HostPageProtection.NoAccess,
                0,
                0);
            return true;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }

        public void Dispose()
        {
            if (!_freed)
            {
                System.Runtime.InteropServices.NativeMemory.Free(_allocation);
                _freed = true;
            }
        }
    }

    // Minimal host memory for free-list tests: Allocate honours the desired
    // address (or a fallback), everything else succeeds as a no-op. The guest
    // allocation arena never dereferences, so no real backing is required.
    private sealed class FakeHostMemory : IHostMemory
    {
        public ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            desiredAddress != 0 ? desiredAddress : 0x00007000_0000_0000;

        public ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection) =>
            Allocate(desiredAddress, size, protection);

        public bool Commit(ulong address, ulong size, HostPageProtection protection) => true;

        public bool Free(ulong address) => true;

        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection)
        {
            rawOldProtection = 0;
            return true;
        }

        public bool Query(ulong address, out HostRegionInfo info)
        {
            info = default;
            return false;
        }

        public void FlushInstructionCache(ulong address, ulong size)
        {
        }
    }

    /// <summary>
    /// Stand-in for the libc-heap/host-memory fallback: backs a fixed guest
    /// address window with a real managed buffer, so PhysicalVirtualMemory's
    /// external-accessor path can be exercised without a real host allocation at
    /// that address. Counts calls so tests can assert the fallback is used only
    /// on a region miss.
    /// </summary>
    private sealed class StubExternalHostMemory : IExternalHostMemoryAccessor
    {
        private readonly ulong _base;
        private readonly byte[] _storage;

        public StubExternalHostMemory(ulong baseAddress, int size)
        {
            _base = baseAddress;
            _storage = new byte[size];
        }

        public int ReadCalls { get; private set; }

        public int WriteCalls { get; private set; }

        public bool TryReadExternal(ulong address, Span<byte> destination)
        {
            ReadCalls++;
            if (!TryResolve(address, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWriteExternal(ulong address, ReadOnlySpan<byte> source)
        {
            WriteCalls++;
            if (!TryResolve(address, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < _base)
            {
                return false;
            }

            var start = address - _base;
            if (start > (ulong)_storage.Length || (ulong)length > (ulong)_storage.Length - start)
            {
                return false;
            }

            offset = (int)start;
            return true;
        }
    }
}
