// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Optional fallback consulted by the guest memory implementation when an
/// access misses every tracked guest region. Some guest-reachable memory lives
/// at host addresses outside the region table - notably libc <c>malloc</c>
/// allocations, which the compat layer creates with <c>Marshal.AllocHGlobal</c>
/// and tracks separately. Registering an accessor lets the single
/// <see cref="ICpuMemory"/> path serve those ranges transparently, so every
/// caller (read/write/pread/recv/...) is malloc-safe without opting in, instead
/// of each syscall having to reach for a parallel fallback helper.
/// Implementations must validate the range themselves and copy nothing (return
/// false) for anything they do not own.
/// </summary>
public interface IExternalHostMemoryAccessor
{
    bool TryReadExternal(ulong address, Span<byte> destination);

    bool TryWriteExternal(ulong address, ReadOnlySpan<byte> source);
}
