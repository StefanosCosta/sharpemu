// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceSysmoduleGetModuleInfoForUnwind shares its core (and 0x130-byte struct) with the libKernel
// twin sceKernelGetModuleInfoForUnwind. These cover the argument-validation paths that need no
// registered module; the happy path (a resolved module + filled struct) is exercised by the
// libKernel variant's own coverage since both delegate to the same GetModuleInfoForUnwindCore.
public sealed class SysmoduleGetModuleInfoForUnwindTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong OutInfoAddress = MemoryBase + 0x100;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public SysmoduleGetModuleInfoForUnwindTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void NullOutInfoPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = 0x8_0000_0000; // queried address (unused on this path)
        _ctx[CpuRegister.Rsi] = 0;             // flags
        _ctx[CpuRegister.Rdx] = 0;             // out-info pointer

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.SysmoduleGetModuleInfoForUnwind(_ctx));
    }

    [Fact]
    public void FlagsOutOfRange_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = 0x8_0000_0000;
        _ctx[CpuRegister.Rsi] = 5; // valid flags are [0, 3)
        _ctx[CpuRegister.Rdx] = OutInfoAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.SysmoduleGetModuleInfoForUnwind(_ctx));
    }

    [Fact]
    public void CallerStructTooSmall_ReturnsInvalidArgument()
    {
        _ctx.TryWriteUInt64(OutInfoAddress, 0x10); // st_size below the required 0x130
        _ctx[CpuRegister.Rdi] = 0x8_0000_0000;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = OutInfoAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.SysmoduleGetModuleInfoForUnwind(_ctx));
    }

    [Fact]
    public void NoModuleAtAddress_ReturnsNotFound()
    {
        _ctx.TryWriteUInt64(OutInfoAddress, 0x130); // valid st_size
        _ctx[CpuRegister.Rdi] = 0xFFFF_FFFF_0000;   // an address no module claims
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = OutInfoAddress;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelRuntimeCompatExports.SysmoduleGetModuleInfoForUnwind(_ctx));
    }
}
