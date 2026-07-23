// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;

namespace SharpEmu.Core.Cpu.Disasm;

/// <summary>
/// Pure decode/validate helper for the signal-free guest execution logger
/// (<c>SHARPEMU_GUEST_HOOK</c>). Planting an <c>E9 rel32</c> jmp detour at a guest
/// address overwrites (clobbers) whatever whole instructions the 5-byte jmp covers;
/// the trampoline must later re-execute a faithful copy of those clobbered
/// instructions before returning control. That verbatim copy is only sound when the
/// clobbered instructions are <b>position-independent</b> — running the same bytes at
/// the trampoline's address must behave identically to running them at the original
/// site.
///
/// v1 scope: position-independent straight-line instructions only. Any RIP-relative
/// operand, any relative branch/call, or any non-straight-line control flow
/// (call/ret/jmp/jcc/int) is rejected outright (the caller skips that hook and patches
/// nothing) rather than mis-relocated. Displacement/branch fix-up is a documented v2
/// extension. This keeps the relocator small and auditable, and covers the intended
/// sites (function prologues <c>push rbp; mov rbp,rsp</c>, and rbp-relative gates such
/// as <c>cmp qword [rbp-0x1e0],0</c>).
/// </summary>
public static class GuestHookRelocator
{
    /// <summary>Minimum bytes an <c>E9 rel32</c> detour overwrites.</summary>
    public const int MinPatchBytes = 5;

    /// <summary>
    /// Decodes whole instructions at <paramref name="siteRip"/> until at least
    /// <see cref="MinPatchBytes"/> bytes are covered, validating each is safe to relocate
    /// verbatim. On success, <paramref name="clobberedLen"/> is the total covered length
    /// (always a whole number of instructions) and <paramref name="relocatedBytes"/> is the
    /// verbatim copy of those clobbered instructions (to be re-executed by the trampoline).
    /// On rejection, returns false with a human-readable <paramref name="reject"/> reason and
    /// no bytes should be patched.
    /// </summary>
    public static bool TryPlan(
        ReadOnlySpan<byte> site,
        ulong siteRip,
        out int clobberedLen,
        out byte[] relocatedBytes,
        out string? reject)
    {
        clobberedLen = 0;
        relocatedBytes = [];
        reject = null;

        var buffer = new List<byte>(MinPatchBytes * 2);
        var total = 0;
        while (total < MinPatchBytes)
        {
            if (total >= site.Length)
            {
                reject = "insufficient-bytes";
                return false;
            }

            if (!IcedDecoder.TryDecode(siteRip + (ulong)total, site[total..], out var inst) || inst.Length <= 0)
            {
                reject = "decode-failed";
                return false;
            }

            if (inst.Bytes.Length < inst.Length)
            {
                // The decode window ran out before the full instruction — the caller must
                // supply more bytes; never copy a truncated instruction.
                reject = "insufficient-bytes";
                return false;
            }

            // Only straight-line, position-independent instructions may be copied verbatim.
            if (inst.FlowControl != FlowControl.Next)
            {
                reject = $"control-flow:{inst.FlowControl}";
                return false;
            }

            if (inst.IsRipRelative)
            {
                reject = "rip-relative";
                return false;
            }

            if (inst.NearBranchTarget is not null)
            {
                // Belt-and-suspenders: a relative branch/call is already non-Next above,
                // but guard explicitly in case any straight-line form carries a near branch.
                reject = "relative-branch";
                return false;
            }

            buffer.AddRange(inst.Bytes.AsSpan(0, inst.Length));
            total += inst.Length;
        }

        clobberedLen = total;
        relocatedBytes = buffer.ToArray();
        return true;
    }
}
