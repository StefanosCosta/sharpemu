// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests.LibcStdio;

// A guest path under no mount prefix resolves to "" (KernelMemoryCompatExports default-deny).
// Handing that to FileStream throws ArgumentException, which used to escape the export's narrow
// catch filter into DispatchImport's catch-all; that returns an SCE error code in rax, and a
// guest reading it as a FILE* sails past its null check and later dereferences garbage.
public sealed class FopenUnresolvablePathTests
{
    private const ulong GuestBase = 0x0000_0009_0000_0000UL;
    private const ulong PathAddress = GuestBase;
    private const ulong ModeAddress = GuestBase + 0x100;

    // Absolute and matched by no built-in mount, so resolution is empty regardless of which
    // SHARPEMU_*_DIR roots the test host happens to have set.
    private const string UnresolvableGuestPath = "/system/unmapped/boot.config";

    [Fact]
    public void Fopen_UnresolvableGuestPath_ReturnsNullHandleAndNotFound()
    {
        var context = CreateContext("r");

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Fopen_UnresolvableGuestPath_ForWrite_DoesNotThrow()
    {
        // The write path also runs Path.GetDirectoryName/CreateDirectory before FileStream,
        // so it needs the same guard.
        var context = CreateContext("w");

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Freopen_UnresolvableGuestPath_ReturnsNullHandleAndNotFound()
    {
        var context = CreateContext("r");
        context[CpuRegister.Rdx] = 0x1000; // any non-zero FILE* to rebind

        var result = LibcStdioExports.Freopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext(string mode)
    {
        var memory = new FakeCpuMemory(GuestBase, 0x1000);
        memory.WriteCString(PathAddress, UnresolvableGuestPath);
        memory.WriteCString(ModeAddress, mode);

        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;
        return context;
    }
}
