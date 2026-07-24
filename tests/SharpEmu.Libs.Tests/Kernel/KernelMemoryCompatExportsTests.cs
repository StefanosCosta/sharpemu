// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(KernelMemoryCompatStateCollection.Name, DisableParallelization = true)]
public sealed class KernelMemoryCompatStateCollection
{
    public const string Name = "KernelMemoryCompatState";
}

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryCompatExportsTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;
    private const ulong AllocationOutAddress = GuestMemoryBase + 0x100;
    private const ulong SpanStartOutAddress = GuestMemoryBase + 0x108;
    private const ulong SpanSizeOutAddress = GuestMemoryBase + 0x110;

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixOpen_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/il2cpp.usym");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0; // O_RDONLY

        var result = KernelMemoryCompatExports.PosixOpen(context);

        // A libc open() failure must be -1, not the raw 0x8002xxxx sentinel the
        // guest would otherwise store as a valid fd and later dereference. Guest
        // libc's File::Open only compares the low 32 bits against -1 (cmp eax,-1);
        // the raw Orbis result (e.g. ORBIS_GEN2_ERROR_NOT_FOUND = 0x80020002) never
        // equals that, so a missing file used to be read back as success with a
        // bogus fd - which regressed deep in IL2CPP metadata symbol loading.
        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void KernelOpenUnderscore_RandomDeviceReturnsValidFd()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/dev/urandom");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0; // O_RDONLY

        var result = KernelMemoryCompatExports.KernelOpenUnderscore(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(ulong.MaxValue, context[CpuRegister.Rax]);
        Assert.True(context[CpuRegister.Rax] > 2); // past stdin/stdout/stderr

        KernelMemoryCompatExports.KernelCloseUnderscore(context);
    }

    [Theory]
    [InlineData("/dev/random")]
    [InlineData("/dev/srandom")]
    public void KernelOpenUnderscore_RandomDeviceAliasesReturnValidFd(string devicePath)
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, devicePath);
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0;

        var result = KernelMemoryCompatExports.KernelOpenUnderscore(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(ulong.MaxValue, context[CpuRegister.Rax]);

        KernelMemoryCompatExports.KernelCloseUnderscore(context);
    }

    [Fact]
    public void KernelReadUnderscore_RandomDeviceReturnsNonZeroRandomBytes()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong bufferAddress = memoryBase + 0x200;
        const int length = 64;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/dev/urandom");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelOpenUnderscore(context));
        var fd = context[CpuRegister.Rax];

        context[CpuRegister.Rdi] = fd;
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = length;
        var readResult = KernelMemoryCompatExports.KernelReadUnderscore(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, readResult);
        Assert.Equal((ulong)length, context[CpuRegister.Rax]);

        Span<byte> buffer = stackalloc byte[length];
        Assert.True(memory.TryRead(bufferAddress, buffer));
        // Not a randomness test, just confirms real bytes came back rather
        // than an untouched zero-filled buffer.
        Assert.Contains(buffer.ToArray(), b => b != 0);

        context[CpuRegister.Rdi] = fd;
        KernelMemoryCompatExports.KernelCloseUnderscore(context);
    }

    [Fact]
    public void KernelOpenUnderscore_RandomDeviceRepeatOpenGetsIndependentFds()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/dev/urandom");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelOpenUnderscore(context));
        var firstFd = context[CpuRegister.Rax];

        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelOpenUnderscore(context));
        var secondFd = context[CpuRegister.Rax];

        Assert.NotEqual(firstFd, secondFd);

        context[CpuRegister.Rdi] = firstFd;
        KernelMemoryCompatExports.KernelCloseUnderscore(context);
        context[CpuRegister.Rdi] = secondFd;
        KernelMemoryCompatExports.KernelCloseUnderscore(context);
    }

    [Fact]
    public void PosixFstat_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // the not-found sentinel misused as an fd
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixFstat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixClose_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd

        var result = KernelMemoryCompatExports.PosixClose(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixRead_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x40;

        var result = KernelMemoryCompatExports.PosixRead(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixWrite_BadDescriptorReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong bufferAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(bufferAddress, "payload");
        context[CpuRegister.Rdi] = 0x80020002; // never-opened / sentinel fd
        context[CpuRegister.Rsi] = bufferAddress;
        context[CpuRegister.Rdx] = 0x7;

        var result = KernelMemoryCompatExports.PosixWrite(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_FragmentedRangeReturnsLargestAlignedSpan()
    {
        const ulong firstAllocationStart = 0x0020_0000;
        const ulong firstAllocationLength = 0x0020_0000;
        const ulong secondAllocationStart = 0x00C0_0000;
        const ulong secondAllocationLength = 0x0040_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, firstAllocationStart, firstAllocationLength);
            AllocateDirectMemory(context, secondAllocationStart, secondAllocationLength);

            QueryAvailableDirectMemory(context, 0, 0x0100_0000, 0x4000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0040_0000UL, spanStart);
            Assert.Equal(0x0080_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, firstAllocationStart, firstAllocationLength);
            ReleaseDirectMemory(context, secondAllocationStart, secondAllocationLength);
        }
    }

    [Fact]
    public void AvailableDirectMemorySize_AppliesAlignmentBeforeComparingSpans()
    {
        const ulong allocationStart = 0x0070_0000;
        const ulong allocationLength = 0x0010_0000;
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);

        try
        {
            AllocateDirectMemory(context, allocationStart, allocationLength);

            QueryAvailableDirectMemory(context, 0x0010_0000, 0x00C0_0000, 0x0040_0000);

            Assert.True(context.TryReadUInt64(SpanStartOutAddress, out var spanStart));
            Assert.True(context.TryReadUInt64(SpanSizeOutAddress, out var spanSize));
            Assert.Equal(0x0080_0000UL, spanStart);
            Assert.Equal(0x0040_0000UL, spanSize);
        }
        finally
        {
            ReleaseDirectMemory(context, allocationStart, allocationLength);
        }
    }

    private static void AllocateDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = start + length;
        context[CpuRegister.Rdx] = length;
        context[CpuRegister.Rcx] = 0x4000;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = AllocationOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(AllocationOutAddress, out var allocatedAddress));
        Assert.Equal(start, allocatedAddress);
    }

    private static void QueryAvailableDirectMemory(
        CpuContext context,
        ulong searchStart,
        ulong searchEnd,
        ulong alignment)
    {
        context[CpuRegister.Rdi] = searchStart;
        context[CpuRegister.Rsi] = searchEnd;
        context[CpuRegister.Rdx] = alignment;
        context[CpuRegister.Rcx] = SpanStartOutAddress;
        context[CpuRegister.R8] = SpanSizeOutAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAvailableDirectMemorySize(context));
    }

    private static void ReleaseDirectMemory(CpuContext context, ulong start, ulong length)
    {
        context[CpuRegister.Rdi] = start;
        context[CpuRegister.Rsi] = length;

        Assert.Equal(0, KernelMemoryCompatExports.KernelReleaseDirectMemory(context));
    }

    [Fact]
    public void MapNamedFlexibleMemory_NullInOutPointerReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03; // CPU read|write
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong inOutAddress = memoryBase + 0x100;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(inOutAddress, BitConverter.GetBytes(0UL));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void MapNamedFlexibleMemory_UnreadableInOutPointerReturnsMemoryFault()
    {
        // The in-out pointer points outside the FakeCpuMemory backing store, so
        // the first TryReadUInt64 must fail before any reservation is attempted.
        const ulong memoryBase = 0x1_0000_0000;
        const ulong unreachableInOut = memoryBase + 0x10_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unreachableInOut;
        context[CpuRegister.Rsi] = 0x1000;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0;

        var result = KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
    }

    [Fact]
    public void VirtualQuery_PreservesReservationPastFixedCommitAtSameBase()
    {
        const ulong memoryBase = 0x12_0000_0000;
        const ulong reservedLength = 0x1_0000;
        const ulong committedLength = 0x2000;
        const ulong inOutAddress = memoryBase + 0x1_7000;
        const ulong infoAddress = memoryBase + 0x1_8000;
        var memory = new FakeCpuMemory(memoryBase, 0x2_0000);
        var context = new CpuContext(memory, Generation.Gen5);

        KernelMemoryCompatExports.RegisterReservedVirtualRange(memoryBase, reservedLength);
        Assert.True(memory.TryWrite(inOutAddress, BitConverter.GetBytes(memoryBase)));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = committedLength;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = 0x10; // fixed mapping

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(context));

        context[CpuRegister.Rdi] = memoryBase + 0x8000;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = infoAddress;
        context[CpuRegister.Rcx] = 0x48;

        Assert.Equal(0, KernelMemoryCompatExports.KernelVirtualQuery(context));
        Assert.True(context.TryReadUInt64(infoAddress, out var regionStart));
        Assert.True(context.TryReadUInt64(infoAddress + 8, out var regionEnd));
        Assert.Equal(memoryBase + committedLength, regionStart);
        Assert.Equal(memoryBase + reservedLength, regionEnd);
    }

    [Fact]
    public void Mprotect_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_ZeroLengthReturnsInvalidArgument()
    {
        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = memoryBase;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Mprotect_UnmappedRangeReturnsNotFound()
    {
        // A plausible guest address that FakeCpuMemory does not back and that
        // has no host reservation. TryProtectHostRange calls VirtualProtect,
        // which fails on an unmapped range, yielding NOT_FOUND rather than
        // mutating protection or throwing.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;
        context[CpuRegister.Rdx] = 0x03;

        var result = KernelMemoryCompatExports.KernelMprotect(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void Munmap_ZeroAddressReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_OverflowRangeReturnsInvalidArgument()
    {
        // address + length would overflow; KernelMunmap guards this explicitly
        // before touching any region accounting.
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = ulong.MaxValue - 0x10;
        context[CpuRegister.Rsi] = 0x20;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void Munmap_UnmappedRangeReturnsNotFound()
    {
        // No flexible region is registered at this address and FakeCpuMemory
        // does not back it, so both physicallyBacked and removedRegions are
        // empty and the export reports NOT_FOUND.
        const ulong unmappedAddress = 0x2_0000_0000;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unmappedAddress;
        context[CpuRegister.Rsi] = 0x4000;

        var result = KernelMemoryCompatExports.KernelMunmap(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void OperatorNew_ReturnsWritableAddressOfRequestedSize()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 64;

        var result = KernelMemoryCompatExports.OperatorNew(context);
        var address = context[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(0UL, address);

        // The allocation comes from KernelMemoryCompatExports' own Marshal.AllocHGlobal
        // -backed heap, a real host pointer distinct from FakeCpuMemory's simulated guest
        // range, so verify it directly via Marshal rather than memory.TryRead/TryWrite.
        var pointer = unchecked((nint)address);
        Marshal.WriteByte(pointer, 63, 0xAB);
        Assert.Equal(0xAB, Marshal.ReadByte(pointer, 63));

        KernelMemoryCompatExports.Free(context);
    }

    [Fact]
    public void OperatorNewArray_BehavesLikeOperatorNewForTheSameSize()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 128;

        var result = KernelMemoryCompatExports.OperatorNewArray(context);
        var address = context[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(0UL, address);

        var pointer = unchecked((nint)address);
        Marshal.WriteByte(pointer, 127, 0xCD);
        Assert.Equal(0xCD, Marshal.ReadByte(pointer, 127));

        context[CpuRegister.Rdi] = address;
        KernelMemoryCompatExports.OperatorDeleteArray(context);
    }

    [Fact]
    public void OperatorDelete_NullPointerIsNoOp()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;

        var result = KernelMemoryCompatExports.OperatorDelete(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
    }

    [Fact]
    public void OperatorDeleteSized_IgnoresSizeArgumentAndFreesTheAllocation()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 32;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.OperatorNew(context));
        var address = context[CpuRegister.Rax];

        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 999; // deliberately mismatched size, must still free correctly
        var deleteResult = KernelMemoryCompatExports.OperatorDeleteSized(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, deleteResult);

        // Allocating again should succeed cleanly, confirming the heap's internal
        // bookkeeping for the freed slot wasn't corrupted by the mismatched size.
        context[CpuRegister.Rdi] = 32;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.OperatorNew(context));
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        KernelMemoryCompatExports.Free(context);
    }

    [Fact]
    public void OperatorDeleteArraySized_FreesAnOperatorNewArrayAllocation()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 48;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.OperatorNewArray(context));
        var address = context[CpuRegister.Rax];

        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = 48;
        var deleteResult = KernelMemoryCompatExports.OperatorDeleteArraySized(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, deleteResult);
    }

    [Fact]
    public void ReallocAlign_NullPointerAllocatesFreshAlignedBlock()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 64;
        context[CpuRegister.Rdx] = 0x40;

        var result = KernelMemoryCompatExports.ReallocAlign(context);
        var address = context[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(0UL, address);
        Assert.Equal(0UL, address % 0x40);

        var pointer = unchecked((nint)address);
        Marshal.WriteByte(pointer, 63, 0xAB);
        Assert.Equal(0xAB, Marshal.ReadByte(pointer, 63));

        context[CpuRegister.Rdi] = address;
        KernelMemoryCompatExports.Free(context);
    }

    [Fact]
    public void ReallocAlign_GrowsExistingAllocationPreservingContentsAndAlignment()
    {
        // Mirrors the guest call shape that used to crash: a dynamic array's
        // growth path calls reallocalign(old_ptr, bigger_size, alignment) and
        // then unconditionally writes through the returned pointer. Before this
        // export existed, the unresolved-import fallback left RAX at 0 and that
        // write faulted (metal_slug, Access Violation at guest RIP 0x800812114).
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 32;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Malloc(context));
        var originalAddress = context[CpuRegister.Rax];
        var originalPointer = unchecked((nint)originalAddress);
        Marshal.WriteByte(originalPointer, 0, 0x42);
        Marshal.WriteByte(originalPointer, 31, 0x99);

        context[CpuRegister.Rdi] = originalAddress;
        context[CpuRegister.Rsi] = 128;
        context[CpuRegister.Rdx] = 0x40;

        var result = KernelMemoryCompatExports.ReallocAlign(context);
        var resizedAddress = context[CpuRegister.Rax];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(0UL, resizedAddress);
        Assert.Equal(0UL, resizedAddress % 0x40);

        var resizedPointer = unchecked((nint)resizedAddress);
        Assert.Equal(0x42, Marshal.ReadByte(resizedPointer, 0));
        Assert.Equal(0x99, Marshal.ReadByte(resizedPointer, 31));

        context[CpuRegister.Rdi] = resizedAddress;
        KernelMemoryCompatExports.Free(context);
    }
}
