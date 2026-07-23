// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// _is_signal_return(pc) asks whether a return address is a kernel signal-return trampoline.
// SharpEmu never injects a guest-visible signal-trampoline frame, so no pc is ever a signal
// return: the export must report 0 (ordinary frame) for every input.
public sealed class IsSignalReturnTests
{
    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    [Theory]
    [InlineData(0x0000000000000000UL)]
    [InlineData(0x000075F0FD0D20C5UL)] // the pc from the subnautica.log probe
    [InlineData(0xFFFFFFFFFFFFFFFFUL)]
    public void ReportsNotASignalFrame_ForEveryPc(ulong pc)
    {
        _ctx[CpuRegister.Rdi] = pc;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelRuntimeCompatExports.IsSignalReturn(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }
}
