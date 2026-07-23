// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Disasm;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Covers the decode/validate half of the signal-free guest execution logger
// (SHARPEMU_GUEST_HOOK). GuestHookRelocator.TryPlan decides how many whole
// instructions an E9 rel32 detour clobbers and whether they are safe to relocate
// verbatim into the trampoline. v1 accepts only position-independent straight-line
// instructions and rejects everything else (the trampoline emitter never mis-relocates
// a rip-relative operand or a relative branch).
public sealed class GuestHookRelocatorTests
{
    private const ulong SiteRip = 0x800B01640UL;

    [Fact]
    public void FunctionPrologue_ClobbersWholeInstructions_Verbatim()
    {
        // push rbp (1) ; mov rbp,rsp (3) ; push r15 (2) -> 4 < 5 pulls in the third.
        byte[] site = [0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56];
        Assert.True(GuestHookRelocator.TryPlan(site, SiteRip, out var clobbered, out var reloc, out var reject));
        Assert.Null(reject);
        Assert.Equal(6, clobbered);
        Assert.Equal(new byte[] { 0x55, 0x48, 0x89, 0xE5, 0x41, 0x57 }, reloc);
    }

    [Fact]
    public void RbpRelativeCompare_IsPositionIndependent_Verbatim()
    {
        // cmp qword [rbp-0x1e0], 0  (the .resS submit gate at 0x800B02819) — 8 bytes, rbp-relative.
        byte[] site = [0x48, 0x83, 0xBD, 0x20, 0xFE, 0xFF, 0xFF, 0x00, 0x90, 0x90];
        Assert.True(GuestHookRelocator.TryPlan(site, SiteRip, out var clobbered, out var reloc, out var reject));
        Assert.Null(reject);
        Assert.Equal(8, clobbered);
        Assert.Equal(new byte[] { 0x48, 0x83, 0xBD, 0x20, 0xFE, 0xFF, 0xFF, 0x00 }, reloc);
    }

    [Fact]
    public void RipRelativeLoad_IsRejected()
    {
        // mov rax, [rip+0x12345] — position-dependent, must not be copied verbatim.
        byte[] site = [0x48, 0x8B, 0x05, 0x45, 0x23, 0x01, 0x00, 0x90, 0x90, 0x90];
        Assert.False(GuestHookRelocator.TryPlan(site, SiteRip, out _, out _, out var reject));
        Assert.Equal("rip-relative", reject);
    }

    [Fact]
    public void RelativeCall_IsRejected()
    {
        // call rel32 — an IP-relative branch; verbatim relocation would call the wrong target.
        byte[] site = [0xE8, 0x00, 0x10, 0x00, 0x00, 0x90, 0x90, 0x90];
        Assert.False(GuestHookRelocator.TryPlan(site, SiteRip, out _, out _, out var reject));
        Assert.NotNull(reject);
    }

    [Fact]
    public void ShortConditionalBranch_IsRejected()
    {
        // je rel8 — relative branch inside the clobber window.
        byte[] site = [0x74, 0x10, 0x90, 0x90, 0x90, 0x90];
        Assert.False(GuestHookRelocator.TryPlan(site, SiteRip, out _, out _, out var reject));
        Assert.NotNull(reject);
    }

    [Fact]
    public void IndirectCall_IsRejected_InV1()
    {
        // call qword [rax+0x28] — an indirect call (the .resS submit call shape). v1 does not
        // relocate calls; it rejects rather than mis-handle the pushed return address.
        byte[] site = [0xFF, 0x50, 0x28, 0x90, 0x90, 0x90];
        Assert.False(GuestHookRelocator.TryPlan(site, SiteRip, out _, out _, out var reject));
        Assert.NotNull(reject);
    }

    [Fact]
    public void TruncatedInstruction_ReportsInsufficientBytes()
    {
        // A lone REX byte with no room to complete the instruction.
        byte[] site = [0x48];
        Assert.False(GuestHookRelocator.TryPlan(site, SiteRip, out _, out _, out var reject));
        Assert.NotNull(reject);
    }
}
