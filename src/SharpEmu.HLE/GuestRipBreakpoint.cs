// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE;

/// <summary>
/// Diagnostic: a software breakpoint that captures the guest register file at an
/// arbitrary guest RIP, including on the primordial/external thread that the
/// cooperative --debug-server pause cannot reliably stop. Enabled via
/// <c>SHARPEMU_BP_RIP=&lt;addr[,addr...]&gt;</c> (optionally
/// <c>SHARPEMU_BP_MAX=&lt;n&gt;</c>, default 32 total captures).
///
/// Each target byte is overwritten with a single-byte <c>INT3</c> (0xCC) — the
/// smallest possible patch, so unlike a multi-byte fault-load patch it never
/// corrupts the adjacent instruction. When the guest executes it the CPU raises
/// SIGTRAP synchronously (unavoidable, on whatever thread hit it); the POSIX
/// signal handler hands the registers here, we snapshot them into a preallocated
/// ring, restore the original byte, rewind RIP so the real instruction
/// re-executes, and disarm. The managed <see cref="ArmAndFlush"/> pass (driven
/// from the import dispatch loop) prints captures and re-arms, so a hot site is
/// sampled once per import cycle until the capture budget is spent.
///
/// Linux-only (uses mprotect + the Linux gregs layout); a no-op elsewhere. Guest
/// memory is identity-mapped, so target and dereferenced addresses are read/written
/// directly as host pointers.
///
/// LIMITATION: reliable only on a cold/single-threaded call site (e.g. a specific
/// call instruction reached by one thread). Breakpointing a HOT function reached by
/// many guest threads/cores at once faults the process — restoring the byte on one
/// core while another is mid-fetch is cross-modifying code without serialization, so
/// a core can execute a garbage instruction. Such sites need a hardware breakpoint
/// (DR0-3), not a patch.
/// </summary>
public static unsafe class GuestRipBreakpoint
{
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int ProtExec = 0x4;
    private const ulong PageMask = 0xFFFUL;
    private const int RecordCapacity = 256;
    private const byte Int3 = 0xCC;

    private struct Capture
    {
        public ulong Rip, Rax, Rbx, Rcx, Rdx, Rsi, Rdi, Rbp, Rsp, R12, R13, R14, R15;
        public long Sequence;
    }

    // How to advance past the overwritten instruction. Emulatable prologue kinds
    // keep the INT3 armed forever and emulate the instruction (no byte restore =>
    // no cross-modifying-code hazard => safe on HOT, multi-threaded functions).
    // OneShot restores the original byte and rewinds (cold single-threaded sites
    // only). See the class remarks.
    private enum EmulateKind
    {
        OneShot = 0,        // unknown instruction: restore byte + rewind, disarm
        PushRbp,            // 0x55
        Endbr64,            // F3 0F 1E FA
        CallMemRaxDisp8,    // FF 50 <disp8> = call qword [rax+disp8]
    }

    private sealed class Breakpoint
    {
        public ulong Address;
        public ulong PageStart;
        public byte OriginalByte;
        public EmulateKind Kind;
        public int Saved;         // 1 = OriginalByte captured
        public int PageWritable;  // 1 = page mprotect'd RWX
        public int Armed;         // 1 = INT3 currently written
        public int Fired;         // 1 = OneShot breakpoint already consumed
    }

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    private static readonly Breakpoint[] _breakpoints = Parse(
        Environment.GetEnvironmentVariable("SHARPEMU_BP_RIP"));

    private static readonly long _budget = ParseBudget(
        Environment.GetEnvironmentVariable("SHARPEMU_BP_MAX"));

    // SHARPEMU_BP_WATCH_OFFSET=<hex>: on each capture, arm a dynamic write-watch on
    // [base (+ deref) + offset], to find the code that writes a field discovered at
    // the breakpoint. -1 = disabled.
    //   SHARPEMU_BP_WATCH_BASE=<reg>  which captured register is the base (default rdi).
    //   SHARPEMU_BP_WATCH_DEREF=1     watch [[base]+offset] instead of [base+offset]
    //                                 (e.g. base=deque item, [base]=descriptor).
    private static readonly long _watchOffset = ParseWatchOffset(
        Environment.GetEnvironmentVariable("SHARPEMU_BP_WATCH_OFFSET"));

    private static readonly int _watchBaseReg = ParseWatchBaseReg(
        Environment.GetEnvironmentVariable("SHARPEMU_BP_WATCH_BASE"));

    private static readonly bool _watchDeref = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_BP_WATCH_DEREF"), "1", StringComparison.Ordinal);

    private static readonly bool _enabled = !OperatingSystem.IsWindows() && _breakpoints.Length != 0;

    private static readonly Capture[] _records = new Capture[RecordCapacity];
    private static long _sequence;
    private static long _captured;
    private static int _recordWriteIndex;
    private static int _recordFlushIndex;

    public static bool Enabled => _enabled;

    /// <summary>
    /// Pre-JIT the SIGTRAP hit path so none of it compiles inside the signal frame
    /// (a cold signal-path method there manifests as the fatal "attempted to call a
    /// UnmanagedCallersOnly method from managed code" JIT-in-signal-frame abort — see
    /// <see cref="GuestSingleStepTracer.WarmUp"/>). The existing synthetic-SIGTRAP
    /// warmup enters <see cref="TryHandleTrap"/> with a fake RIP=0, so only its
    /// no-match branch is JITted; its match / emulate / callee branches are not. Called
    /// once, outside signal context, from WarmUpPosixSignalPath. Unlike the tracer this
    /// is the breakpoint's ONLY warmup — it has no synthetic-fault coverage of its hit
    /// path at all.
    /// </summary>
    public static void WarmUp()
    {
        if (!_enabled)
        {
            return;
        }

        var t = typeof(GuestRipBreakpoint);
        foreach (var name in new[]
                 {
                     nameof(TryHandleTrap), nameof(ArmAndFlush),
                     "SafeReadU64", "SafeReadStack", "SelectRegister", "ClassifyPrologue",
                 })
        {
            var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }

        // The capture can arm a dynamic write-watch, whose ArmDynamic runs in the
        // signal frame and whose TryHandleWriteFault runs in the SIGSEGV frame — warm
        // both so the write-watch follow-up (SHARPEMU_BP_WATCH_*) is also signal-safe.
        var w = typeof(GuestWriteRipWatch);
        foreach (var name in new[] { "ArmDynamic", "TryHandleWriteFault" })
        {
            var m = w.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }

        // Warm the mprotect P/Invoke stub used from the byte-restore path (a scratch
        // page kept RW then released is a harmless no-op).
        var scratch = NativeMemory.AllocZeroed(0x1000);
        _ = Mprotect((nint)scratch, 0x1000, ProtRead | ProtWrite);
        NativeMemory.Free(scratch);
    }

    /// <summary>
    /// Managed-context pass (call from the import dispatch loop): print pending
    /// captures, then (re-)arm each breakpoint whose page is mapped, until the
    /// total capture budget is spent. Console I/O here means managed-context only.
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
            ref var record = ref _records[flushIndex % RecordCapacity];
            // Deref the two pointers the caller usually wants at a vtable-call
            // site: [rax+0x18] (the dispatched method when rax is a vtable) and
            // [rdi] (the receiver's own vtable). Range-checked so a garbage
            // register never faults the flush.
            var raxMethod = SafeReadU64(record.Rax + 0x18);
            var rdiVtable = SafeReadU64(record.Rdi);
            var rdi58 = SafeReadU64(record.Rdi + 0x58);
            var rdi11c = SafeReadU64(record.Rdi + 0x118);
            // [rsp] at a function entry (before its own push rbp) is the caller's
            // return address - the direct handle on who invoked this function.
            // Guest thread stacks are host-mmap'd high (~0x00006FFF.. / 0x7800..),
            // outside SafeReadU64's guest-heap window, so read them with a
            // stack-aware guard. rsp is always a live, mapped pointer.
            var caller = SafeReadStack(record.Rsp);
            // Walk the saved-rbp chain to recover the call stack. At a function
            // entry (before its own `push rbp`) rbp is still the CALLER's frame
            // base, so frame 0's return is [rsp] and each subsequent return is
            // [rbp+8] with rbp advancing to [rbp]. Bounded + guarded so a bogus
            // rbp can never fault the flush.
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
                    break; // stack grows down; a non-increasing fp means chain end/garbage
                }

                fp = next;
            }

            Console.Error.WriteLine(
                $"[BP] seq={record.Sequence} rip=0x{record.Rip:X16} " +
                $"rax=0x{record.Rax:X16} rbx=0x{record.Rbx:X16} rcx=0x{record.Rcx:X16} rdx=0x{record.Rdx:X16} " +
                $"rsi=0x{record.Rsi:X16} rdi=0x{record.Rdi:X16} rbp=0x{record.Rbp:X16} rsp=0x{record.Rsp:X16} " +
                $"r12=0x{record.R12:X16} r13=0x{record.R13:X16} r14=0x{record.R14:X16} r15=0x{record.R15:X16} " +
                $"| caller=0x{caller:X16} [rax+0x18]=0x{raxMethod:X16} [rdi]=0x{rdiVtable:X16} [rdi+0x58]=0x{rdi58:X16} [rdi+0x118]=0x{rdi11c:X16}" +
                $"\n      stack:{frames}");
            flushIndex++;
        }
        Volatile.Write(ref _recordFlushIndex, flushIndex);

        if (Volatile.Read(ref _captured) >= _budget)
        {
            return;
        }

        for (var index = 0; index < _breakpoints.Length; index++)
        {
            var breakpoint = _breakpoints[index];
            // An emulatable breakpoint (PushRbp/Endbr64) stays armed forever - it
            // emulates the overwritten instruction instead of restoring the byte,
            // so it is safe on hot, multi-threaded code and needs no re-arm. A
            // OneShot breakpoint restores + rewinds once (cold sites only) and is
            // never re-armed (re-patching a live page faults the CLR signal path).
            if (Volatile.Read(ref breakpoint.Armed) != 0 || Volatile.Read(ref breakpoint.Fired) != 0)
            {
                continue;
            }

            if (breakpoint.PageWritable == 0)
            {
                // Fails (ENOMEM) until the code page is mapped; retry next pass.
                if (Mprotect((nint)breakpoint.PageStart, 0x1000, ProtRead | ProtWrite | ProtExec) != 0)
                {
                    continue;
                }

                breakpoint.PageWritable = 1;
            }

            if (breakpoint.Saved == 0)
            {
                breakpoint.OriginalByte = *(byte*)breakpoint.Address;
                breakpoint.Kind = ClassifyPrologue(breakpoint.Address);
                breakpoint.Saved = 1;
            }

            *(byte*)breakpoint.Address = Int3;
            Volatile.Write(ref breakpoint.Armed, 1);
        }
    }

    /// <summary>
    /// Signal-handler entry. If <paramref name="trapRip"/> (the RIP the INT3
    /// faulted at, i.e. breakpoint address + 1) matches an armed breakpoint,
    /// snapshot the registers, restore the original byte, and return true with
    /// <paramref name="rewindRip"/> set to the breakpoint address so the caller
    /// can rewind RIP and re-execute the real instruction. Must not allocate or
    /// take managed locks.
    /// </summary>
    public static bool TryHandleTrap(
        ulong trapRip,
        ulong rax, ulong rbx, ulong rcx, ulong rdx,
        ulong rsi, ulong rdi, ulong rbp, ulong rsp,
        ulong r12, ulong r13, ulong r14, ulong r15,
        out ulong newRip, out ulong newRsp)
    {
        newRip = 0;
        newRsp = rsp;
        if (!_enabled)
        {
            return false;
        }

        var breakpointAddress = trapRip - 1;
        for (var index = 0; index < _breakpoints.Length; index++)
        {
            var breakpoint = _breakpoints[index];
            if (breakpoint.Address != breakpointAddress)
            {
                continue;
            }

            // A trap at a known breakpoint address must ALWAYS be consumed - never
            // fall through to the fault path, which would abort. Capture up to the
            // budget (bounded by _captured); racing threads are serialised only by
            // the Interlocked ring index.
            if (Volatile.Read(ref _captured) < _budget)
            {
                var slot = Interlocked.Increment(ref _recordWriteIndex) - 1;
                ref var record = ref _records[slot % RecordCapacity];
                record.Rip = breakpointAddress;
                record.Rax = rax; record.Rbx = rbx; record.Rcx = rcx; record.Rdx = rdx;
                record.Rsi = rsi; record.Rdi = rdi; record.Rbp = rbp; record.Rsp = rsp;
                record.R12 = r12; record.R13 = r13; record.R14 = r14; record.R15 = r15;
                record.Sequence = Interlocked.Increment(ref _sequence);
                Interlocked.Increment(ref _captured);

                // Optionally arm a write-watch on [base(+deref)+offset] to catch the
                // code that later writes the field discovered at this breakpoint.
                if (_watchOffset >= 0)
                {
                    var baseValue = SelectRegister(_watchBaseReg, rax, rbx, rcx, rdx, rsi, rdi, rbp, r12, r13, r14, r15);
                    if (_watchDeref)
                    {
                        baseValue = SafeReadU64(baseValue);
                    }

                    if (baseValue != 0)
                    {
                        _ = GuestWriteRipWatch.ArmDynamic(baseValue + (ulong)_watchOffset);
                    }
                }
            }

            switch (breakpoint.Kind)
            {
                case EmulateKind.PushRbp:
                    // Emulate `push rbp` and step over it; INT3 stays armed (no
                    // byte restore => safe on hot multi-threaded code).
                    newRsp = rsp - 8;
                    *(ulong*)newRsp = rbp;
                    newRip = breakpointAddress + 1;
                    break;

                case EmulateKind.Endbr64:
                    // `endbr64` (F3 0F 1E FA) has no architectural effect here.
                    newRip = breakpointAddress + 4;
                    break;

                case EmulateKind.CallMemRaxDisp8:
                    // `call qword [rax+disp8]` (FF 50 disp8): push return addr,
                    // jump to the call target. disp8 lives at addr+2 (only byte0
                    // was overwritten by INT3). INT3 stays armed.
                    {
                        var disp = (long)(sbyte)*(byte*)(breakpointAddress + 2);
                        var target = *(ulong*)(rax + (ulong)disp);
                        newRsp = rsp - 8;
                        *(ulong*)newRsp = breakpointAddress + 3; // return address
                        newRip = target;
                    }
                    break;

                default:
                    // OneShot: restore the real byte and rewind so it re-executes;
                    // disarm (never re-armed). Only the winner restores; others
                    // just rewind to the (now restored) instruction.
                    newRip = breakpointAddress;
                    if (Interlocked.Exchange(ref breakpoint.Armed, 0) == 1)
                    {
                        Volatile.Write(ref breakpoint.Fired, 1);
                        *(byte*)breakpoint.Address = breakpoint.OriginalByte;
                    }
                    break;
            }

            return true;
        }

        return false;
    }

    // Read the first bytes of the target instruction to pick an emulation strategy.
    // Only function-prologue shapes that are trivial and side-effect-precise to
    // emulate keep the INT3 armed (hot-code-safe); anything else falls back to a
    // cold-only one-shot restore.
    private static EmulateKind ClassifyPrologue(ulong address)
    {
        var b0 = *(byte*)address;
        if (b0 == 0x55)
        {
            return EmulateKind.PushRbp;
        }

        if (b0 == 0xF3 &&
            *(byte*)(address + 1) == 0x0F &&
            *(byte*)(address + 2) == 0x1E &&
            *(byte*)(address + 3) == 0xFA)
        {
            return EmulateKind.Endbr64;
        }

        // call qword [rax+disp8]: FF /2 with modrm 0x50 (mod=01, reg=010, rm=rax).
        if (b0 == 0xFF && *(byte*)(address + 1) == 0x50)
        {
            return EmulateKind.CallMemRaxDisp8;
        }

        return EmulateKind.OneShot;
    }

    private static ulong SafeReadU64(ulong address)
    {
        // Guest heap/code live in this identity-mapped window; anything else is a
        // garbage register we must not dereference from the flush path.
        if (address < 0x0000000400000000UL || address >= 0x0000000900000000UL || (address & 0x7UL) != 0)
        {
            return 0;
        }

        return *(ulong*)address;
    }

    // Read a value off a guest thread stack (host-mmap'd high, e.g. 0x00006FFF.. or
    // 0x00007800..). Only ever called with a live rsp/rbp, which is guaranteed
    // mapped, so the wide window is safe here where a garbage-register deref would
    // not be.
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

    private static long ParseWatchOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return -1;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? (long)parsed
            : -1;
    }

    // Register index used by SelectRegister below.
    private static int ParseWatchBaseReg(string? value) => (value?.Trim().ToLowerInvariant()) switch
    {
        "rax" => 0, "rbx" => 1, "rcx" => 2, "rdx" => 3, "rsi" => 4, "rdi" => 5,
        "rbp" => 6, "r12" => 7, "r13" => 8, "r14" => 9, "r15" => 10,
        _ => 5, // default rdi
    };

    private static ulong SelectRegister(
        int idx,
        ulong rax, ulong rbx, ulong rcx, ulong rdx, ulong rsi, ulong rdi,
        ulong rbp, ulong r12, ulong r13, ulong r14, ulong r15) => idx switch
    {
        0 => rax, 1 => rbx, 2 => rcx, 3 => rdx, 4 => rsi, 5 => rdi,
        6 => rbp, 7 => r12, 8 => r13, 9 => r14, 10 => r15,
        _ => rdi,
    };

    private static Breakpoint[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || OperatingSystem.IsWindows())
        {
            return [];
        }

        var breakpoints = new List<Breakpoint>();
        foreach (var token in value.Split(
                     [',', ';', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var span = token.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                continue;
            }

            breakpoints.Add(new Breakpoint
            {
                Address = address,
                PageStart = address & ~PageMask,
            });
        }

        return breakpoints.ToArray();
    }
}
