// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Iced.Intel;

namespace SharpEmu.Core.Cpu.Disasm;

public readonly struct DecodedInst
{
    public DecodedInst(
        ulong rip,
        int length,
        string text,
        string mnemonic,
        FlowControl flowControl,
        ulong? nearBranchTarget,
        ulong? memoryAddress,
        bool isRipRelative,
        byte[] bytes)
    {
        Rip = rip;
        Length = length;
        Text = text;
        Mnemonic = mnemonic;
        FlowControl = flowControl;
        NearBranchTarget = nearBranchTarget;
        MemoryAddress = memoryAddress;
        IsRipRelative = isRipRelative;
        Bytes = bytes;
    }

    public ulong Rip { get; }

    public int Length { get; }

    public string Text { get; }

    public string Mnemonic { get; }

    public FlowControl FlowControl { get; }

    public ulong? NearBranchTarget { get; }

    public ulong? MemoryAddress { get; }

    /// <summary>
    /// True when the instruction has a RIP-relative memory operand (<c>[rip+disp]</c>).
    /// Such an instruction is position-dependent: relocating its bytes verbatim to a
    /// different address changes what it references, so a code-detour relocator must
    /// reject it (or fix up the displacement) rather than copy it.
    /// </summary>
    public bool IsRipRelative { get; }

    public byte[] Bytes { get; }
}
