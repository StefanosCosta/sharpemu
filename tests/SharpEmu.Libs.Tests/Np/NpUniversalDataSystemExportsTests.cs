// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

// sceNpUniversalDataSystemCreateEventPropertyObject initializes a caller-owned property-object
// buffer with a tracked non-zero marker so the EventPropertyObjectSet* setters (which probe it as
// a readable pointer) succeed. Cover the happy path plus the null- and bad-pointer rejections.
public sealed class NpUniversalDataSystemExportsTests
{
    private const int NpUniversalDataSystemErrorInvalidArgument = unchecked((int)0x80553102);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ObjectAddress = MemoryBase + 0x100;

    private readonly CpuContext _ctx = new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    [Fact]
    public void CreateEventPropertyObject_InitializesBufferAndReturnsSuccess()
    {
        _ctx[CpuRegister.Rdi] = 1;             // event (unused by the marker write)
        _ctx[CpuRegister.Rsi] = ObjectAddress; // out property-object buffer

        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyObject(_ctx));

        // The buffer must be left non-zero so later Set* readability probes see an initialized object.
        Assert.True(_ctx.TryReadInt32(ObjectAddress, out var marker));
        Assert.NotEqual(0, marker);
    }

    [Fact]
    public void CreateEventPropertyObject_WithNullPointer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(
            NpUniversalDataSystemErrorInvalidArgument,
            NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyObject(_ctx));
    }

    [Fact]
    public void CreateEventPropertyObject_WithUnmappedPointer_ReturnsMemoryFault()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0x9000_0000; // below the fake region base -> unwritable

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyObject(_ctx));
    }

    [Fact]
    public void DestroyEventPropertyObject_OnACreatedObject_ReturnsSuccess()
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = ObjectAddress;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemCreateEventPropertyObject(_ctx));

        _ctx[CpuRegister.Rsi] = ObjectAddress; // free the same object the create wrote
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyObject(_ctx));
    }

    [Fact]
    public void DestroyEventPropertyObject_IsBestEffortForNullAndUnmappedPointers()
    {
        // Teardown must never fault the game: a null or unreadable object still returns success.
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyObject(_ctx));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0x9000_0000; // below the fake region base -> unreadable
        Assert.Equal(0, NpUniversalDataSystemExports.NpUniversalDataSystemDestroyEventPropertyObject(_ctx));
    }
}
