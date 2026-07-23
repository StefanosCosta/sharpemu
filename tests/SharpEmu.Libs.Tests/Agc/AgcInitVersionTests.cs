// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// sceAgcInit validates the register-defaults version against a fixed allow-list. The defaults blob is
// version-independent, so every acknowledged SDK generation must be accepted rather than rejected as
// INVALID_ARGUMENT. Subnautica uses version 9 (once between 8 and 10); Cat Quest 3 uses version 12,
// which fell into an 11-12 gap in the allow-list and wrongly rejected AGC/GPU init.
public sealed class AgcInitVersionTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x2000;
    private const ulong StateAddress = BaseAddress + 0x100;

    [Theory]
    [InlineData(7u)]
    [InlineData(8u)]
    [InlineData(9u)]
    [InlineData(10u)]
    [InlineData(11u)]
    [InlineData(12u)]
    [InlineData(13u)]
    public void Init_SupportedVersion_ReturnsOk(uint version)
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = StateAddress;
        ctx[CpuRegister.Rsi] = version;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.Init(ctx));
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(9999u)]
    public void Init_UnsupportedVersion_ReturnsInvalidArgument(uint version)
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = StateAddress;
        ctx[CpuRegister.Rsi] = version;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, AgcExports.Init(ctx));
    }

    [Fact]
    public void Init_NullState_ReturnsInvalidArgument()
    {
        var ctx = NewContext();
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 9u;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, AgcExports.Init(ctx));
    }

    private static CpuContext NewContext() =>
        new(new FakeCpuMemory(BaseAddress, MemorySize), Generation.Gen5);
}
