// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.HLE;

/// <summary>
/// Diagnostic: a signal-free guest execution logger. Enabled via
/// <c>SHARPEMU_GUEST_HOOK=&lt;addr[,addr...]&gt;</c> (optionally
/// <c>SHARPEMU_GUEST_HOOK_MAX=&lt;n&gt;</c>, default 32 total captures). The CPU backend
/// plants an <c>E9 rel32</c> jmp detour at each address into a per-hook trampoline that
/// runs in ordinary preemptive guest context and calls <see cref="Capture"/> with a
/// pointer to a register snapshot frame it built on the stack.
///
/// Unlike <see cref="GuestRipBreakpoint"/> (which captures from a SIGTRAP handler), this
/// path never uses a POSIX signal: the trampoline reaches managed code by a normal
/// <c>call</c>, exactly like an import trampoline. That is the whole point — the
/// INT3/single-step breakpoints crash on continuation-resumed cold guest threads with
/// "Invalid Program: attempted to call an UnmanagedCallersOnly method from managed code"
/// (a JIT-inside-a-signal-frame / cooperative-mode-reentry abort), which cannot happen
/// here. No warmup is required: <see cref="Capture"/> JITs lazily in ordinary managed
/// context on first hit, just as import dispatch did.
///
/// The detour is installed once, single-threaded, before the target pages go hot, and
/// never mutated again — so it is safe even on hot, multi-threaded code (no
/// cross-modifying-code hazard, unlike a re-armed INT3).
/// </summary>
public static unsafe class GuestExecLogger
{
    private const int RecordCapacity = 256;

    // Offsets into the register snapshot frame the CPU-backend trampoline builds
    // (snapshotPtr = frame-low). MUST stay in lockstep with CreateGuestHookTrampoline's
    // push order in DirectExecutionBackend.cs. The frame is (from low address up):
    // r15,r14,r13,r12,r11,r10,r9,r8,rdi,rsi,rbp,rbx,rdx,rcx,rax,rflags — 16 * 8 bytes,
    // pushed below a 0x100 red-zone-skip; the original guest rsp is snapshotPtr + 0x180.
    private const int R15Off = 0x00;
    private const int R14Off = 0x08;
    private const int R13Off = 0x10;
    private const int R12Off = 0x18;
    private const int R11Off = 0x20;
    private const int R10Off = 0x28;
    private const int R9Off = 0x30;
    private const int R8Off = 0x38;
    private const int RdiOff = 0x40;
    private const int RsiOff = 0x48;
    private const int RbpOff = 0x50;
    private const int RbxOff = 0x58;
    private const int RdxOff = 0x60;
    private const int RcxOff = 0x68;
    private const int RaxOff = 0x70;
    private const int RflagsOff = 0x78;

    /// <summary>Distance from the snapshot frame-low to the original guest RSP.</summary>
    public const int GuestRspFromFrameLow = 0x180;

    private struct HookRecord
    {
        public ulong Rip, Rax, Rbx, Rcx, Rdx, Rsi, Rdi, Rbp, Rsp, R8, R9, R10, R11, R12, R13, R14, R15, Rflags;
        public long Sequence;
    }

    private static readonly ulong[] _addresses = Parse(
        Environment.GetEnvironmentVariable("SHARPEMU_GUEST_HOOK"));

    private static readonly long _budget = ParseBudget(
        Environment.GetEnvironmentVariable("SHARPEMU_GUEST_HOOK_MAX"));

    private static readonly bool _enabled = _addresses.Length != 0;

    private static readonly HookRecord[] _records = new HookRecord[RecordCapacity];
    private static long _sequence;
    private static long _captured;
    private static int _recordWriteIndex;
    private static int _recordFlushIndex;

    public static bool Enabled => _enabled;

    /// <summary>The parsed hook addresses, in the order the trampolines index them.</summary>
    public static IReadOnlyList<ulong> Addresses => _addresses;

    /// <summary>
    /// Trampoline entry point (via the CPU backend's Win64 gateway). Records the register
    /// snapshot into the ring. Runs in ordinary managed context on the runner thread's host
    /// stack, so it may allocate/JIT freely. Bounded by the capture budget.
    /// </summary>
    public static void Capture(int hookIndex, nint snapshotPtr)
    {
        if (!_enabled || (uint)hookIndex >= (uint)_addresses.Length)
        {
            return;
        }

        if (Volatile.Read(ref _captured) >= _budget)
        {
            return;
        }

        var f = (byte*)snapshotPtr;
        var slot = Interlocked.Increment(ref _recordWriteIndex) - 1;
        ref var record = ref _records[(slot % RecordCapacity + RecordCapacity) % RecordCapacity];
        record.Rip = _addresses[hookIndex];
        record.R15 = *(ulong*)(f + R15Off);
        record.R14 = *(ulong*)(f + R14Off);
        record.R13 = *(ulong*)(f + R13Off);
        record.R12 = *(ulong*)(f + R12Off);
        record.R11 = *(ulong*)(f + R11Off);
        record.R10 = *(ulong*)(f + R10Off);
        record.R9 = *(ulong*)(f + R9Off);
        record.R8 = *(ulong*)(f + R8Off);
        record.Rdi = *(ulong*)(f + RdiOff);
        record.Rsi = *(ulong*)(f + RsiOff);
        record.Rbp = *(ulong*)(f + RbpOff);
        record.Rbx = *(ulong*)(f + RbxOff);
        record.Rdx = *(ulong*)(f + RdxOff);
        record.Rcx = *(ulong*)(f + RcxOff);
        record.Rax = *(ulong*)(f + RaxOff);
        record.Rflags = *(ulong*)(f + RflagsOff);
        record.Rsp = (ulong)snapshotPtr + GuestRspFromFrameLow;
        record.Sequence = Interlocked.Increment(ref _sequence);
        Interlocked.Increment(ref _captured);

        // Convention: the first hook address publishes its rdi as the base for
        // MemPollWatch's SHARPEMU_MEM_POLL_CHAIN resolution (e.g. hook Perform entry so the
        // op becomes the base for an op->container->entry->srcObj chain). Cheap no-op otherwise.
        if (hookIndex == 0)
        {
            MemPollWatch.PublishBase(record.Rdi);
        }
    }

    /// <summary>
    /// Managed-context pass (call from the import dispatch loop): print pending captures.
    /// Flush-only — the detour is permanent and never re-armed.
    /// </summary>
    public static void ArmAndFlush()
    {
        if (!_enabled)
        {
            return;
        }

        var flushIndex = Volatile.Read(ref _recordFlushIndex);
        var writeIndex = Volatile.Read(ref _recordWriteIndex);
        while (flushIndex != writeIndex)
        {
            ref var record = ref _records[(flushIndex % RecordCapacity + RecordCapacity) % RecordCapacity];
            // Common vtable-call derefs the caller usually wants: [rax+0x18] (dispatched
            // method when rax is a vtable), [rdi] (receiver vtable), and the two op fields
            // the .resS investigation watches. Range-checked so a garbage register never faults.
            var raxMethod = SafeReadU64(record.Rax + 0x18);
            var rdiVtable = SafeReadU64(record.Rdi);
            var caller = SafeReadStack(record.Rsp);

            var frames = new System.Text.StringBuilder();
            var fp = record.Rbp;
            for (var depth = 0; depth < 8; depth++)
            {
                var ret = SafeReadStack(fp + 8);
                var next = SafeReadStack(fp);
                if (ret == 0)
                {
                    break;
                }

                frames.Append($" 0x{ret:X}");
                if (next <= fp || next == 0)
                {
                    break;
                }

                fp = next;
            }

            Console.Error.WriteLine(
                $"[HOOK] seq={record.Sequence} rip=0x{record.Rip:X16} " +
                $"rax=0x{record.Rax:X16} rbx=0x{record.Rbx:X16} rcx=0x{record.Rcx:X16} rdx=0x{record.Rdx:X16} " +
                $"rsi=0x{record.Rsi:X16} rdi=0x{record.Rdi:X16} rbp=0x{record.Rbp:X16} rsp=0x{record.Rsp:X16} " +
                $"r8=0x{record.R8:X16} r9=0x{record.R9:X16} r10=0x{record.R10:X16} r11=0x{record.R11:X16} " +
                $"r12=0x{record.R12:X16} r13=0x{record.R13:X16} r14=0x{record.R14:X16} r15=0x{record.R15:X16} " +
                $"rflags=0x{record.Rflags:X16} | caller=0x{caller:X16} [rax+0x18]=0x{raxMethod:X16} [rdi]=0x{rdiVtable:X16}" +
                $"\n      stack:{frames}");
            flushIndex++;
        }

        Volatile.Write(ref _recordFlushIndex, flushIndex);
    }

    private static ulong SafeReadU64(ulong address)
    {
        if (address < 0x0000000400000000UL || address >= 0x0000000900000000UL || (address & 0x7UL) != 0)
        {
            return 0;
        }

        return *(ulong*)address;
    }

    private static ulong SafeReadStack(ulong address)
    {
        if (address < 0x0000600000000000UL || address >= 0x0000800000000000UL || (address & 0x7UL) != 0)
        {
            return 0;
        }

        return *(ulong*)address;
    }

    private static long ParseBudget(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return 32;
    }

    private static ulong[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var addresses = new List<ulong>();
        foreach (var token in value.Split(
                     [',', ';', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var span = token.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                addresses.Add(address);
            }
        }

        return addresses.ToArray();
    }
}
