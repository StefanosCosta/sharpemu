// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    /// <summary>
    /// Bridges the compat layer's validated host-memory access (libc-malloc'd
    /// buffers and other committed host ranges the guest reaches but that are
    /// not in the region table) to the Core memory implementation via
    /// <see cref="IExternalHostMemoryAccessor"/>. Registered on the guest memory
    /// so <c>ctx.Memory</c> transparently serves those ranges for every caller,
    /// instead of each syscall reaching for a parallel fallback helper. Nested so
    /// it can call the private, already-range-validating host accessors.
    /// </summary>
    public sealed class ExternalHostMemoryAccessor : IExternalHostMemoryAccessor
    {
        public static readonly ExternalHostMemoryAccessor Instance = new();

        private ExternalHostMemoryAccessor()
        {
        }

        public bool TryReadExternal(ulong address, Span<byte> destination) =>
            TryReadHostMemory(address, destination);

        public bool TryWriteExternal(ulong address, ReadOnlySpan<byte> source) =>
            TryWriteHostMemory(address, source);
    }
}
