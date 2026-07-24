// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

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

    // SHARPEMU_HOOK_WATCH="expr;expr;...": extra values dumped per record. An expr is a
    // register name followed by any sequence of "+HEX" (add) or "@" (dereference, u64,
    // range-guarded). e.g. "r13+8@" = *(r13+8); "rax@+20@" = *(*(rax)+0x20). Diagnostic
    // only; inert when unset.
    private static readonly string[] _watch = ParseWatch(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_WATCH"));

    // SHARPEMU_HOOK_ARM_WRITE=1 arms a signal-free-planted dynamic write-watch
    // (GuestWriteRipWatch.ArmDynamic) at a runtime-resolved address each time a
    // hook fires, so the code that later writes a per-run heap field can be caught
    // by RIP. The address is resolved as: base = <reg>; base += PREOFF; if DEREF
    // base = *(base); watch (base + OFF). This closes the gap that INT3-based
    // GuestRipBreakpoint (which aborts on this game's continuation-resumed threads)
    // left for arming a write-watch from a signal-free E9 hook. Diagnostic; inert
    // when unset. Example (find who writes item+0x48 where a Perform hook has
    // rdi=item): SHARPEMU_HOOK_ARM_WRITE=1 HOOK=0x800AC9840 BASE=rdi OFF=0x48; or
    // subobj+8 = *(item+0xa8)+8: PREOFF=0xa8 DEREF=1 OFF=8.
    private static readonly bool _armWriteEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ARM_WRITE"), "1", StringComparison.Ordinal);

    /// <summary>
    /// True when SHARPEMU_HOOK_ARM_WRITE will plant a dynamic <see cref="GuestWriteRipWatch"/>
    /// at runtime. Known at startup (unlike <see cref="GuestWriteRipWatch.Enabled"/>, which
    /// only flips once the first watch is armed), so the signal-path warmup can pre-JIT the
    /// continuation-resume chain before any cooperative-worker store faults.
    /// </summary>
    public static bool WriteWatchArmingEnabled => _armWriteEnabled;

    private static readonly string _armWriteBaseReg =
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ARM_WRITE_BASE") is { Length: > 0 } reg ? reg : "rdi";

    private static readonly ulong _armWritePreOff = ParseHexOffset(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ARM_WRITE_PREOFF"));

    private static readonly ulong _armWriteOff = ParseHexOffset(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ARM_WRITE_OFF"));

    private static readonly bool _armWriteDeref = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ARM_WRITE_DEREF"), "1", StringComparison.Ordinal);

    // SHARPEMU_HOOK_ACC=1: object-keyed accumulator. At each hook hit it computes an object
    // KEY (SHARPEMU_HOOK_ACC_KEY expr, default "rdi"; same expr syntax as SHARPEMU_HOOK_WATCH,
    // e.g. "rbx+1f0@@" to key by a vector element pointer) and, optionally, a FIELD
    // (SHARPEMU_HOOK_ACC_FIELD, e.g. "rdi+48@" for a state word). It tracks per-key hit count
    // and the field's first/last value, flagging keys whose field NEVER equals
    // SHARPEMU_HOOK_ACC_DONE (hex). Purpose: turn a nondeterministic object churn into a
    // deterministic "these object(s) were checked N times and never reached done" — the
    // object-keyed integrate-failure finder for the load-wedge cohort. Runs on EVERY hit
    // (budget-independent, unlike the record ring). Inert when unset. Example (cat_quest
    // isDone `[item+0x48]==2` at 0x800ACDED0, key=item ptr in rdi):
    //   SHARPEMU_GUEST_HOOK=0x800ACDED0 SHARPEMU_HOOK_ACC=1 HOOK_ACC_KEY=rdi
    //   SHARPEMU_HOOK_ACC_FIELD=rdi+48@ SHARPEMU_HOOK_ACC_DONE=2
    private static readonly bool _accEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ACC"), "1", StringComparison.Ordinal);

    private static readonly string _accKeyExpr =
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ACC_KEY") is { Length: > 0 } k ? k : "rdi";

    private static readonly string? _accFieldExpr =
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ACC_FIELD") is { Length: > 0 } fexpr ? fexpr : null;

    private static readonly bool _accHasDone =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ACC_DONE"));

    private static readonly ulong _accDone = ParseHexOffset(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ACC_DONE"));

    // SHARPEMU_HOOK_ACC_MASK: AND-mask applied to the field before recording/comparing. The `@`
    // deref reads a full u64, but state fields are commonly int32 — set MASK=0xFFFFFFFF so
    // `done=2` matches a 32-bit state of 2 regardless of the adjacent 4 bytes. Default: all-ones.
    private static readonly ulong _accMask = TryParseHexOr(
        Environment.GetEnvironmentVariable("SHARPEMU_HOOK_ACC_MASK"), ulong.MaxValue);

    private const int AccCapacity = 16384;

    private sealed class AccEntry
    {
        public long Count;
        public ulong FirstField;
        public ulong LastField;
        public int SawField;   // 1 once a field value was recorded
        public int EverDone;   // 1 if the field ever equalled _accDone
        public long FirstSequence;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, AccEntry> _acc = new();
    private static long _accFlushTick;

    private static readonly HookRecord[] _records = new HookRecord[RecordCapacity];
    private static long _sequence;
    private static long _captured;
    private static int _armLogCount;
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

        // Build the register snapshot into a local first, so the object-keyed accumulator
        // (SHARPEMU_HOOK_ACC) can run on EVERY hit, independent of the bounded record ring.
        var f = (byte*)snapshotPtr;
        HookRecord snap = default;
        snap.Rip = _addresses[hookIndex];
        snap.R15 = *(ulong*)(f + R15Off);
        snap.R14 = *(ulong*)(f + R14Off);
        snap.R13 = *(ulong*)(f + R13Off);
        snap.R12 = *(ulong*)(f + R12Off);
        snap.R11 = *(ulong*)(f + R11Off);
        snap.R10 = *(ulong*)(f + R10Off);
        snap.R9 = *(ulong*)(f + R9Off);
        snap.R8 = *(ulong*)(f + R8Off);
        snap.Rdi = *(ulong*)(f + RdiOff);
        snap.Rsi = *(ulong*)(f + RsiOff);
        snap.Rbp = *(ulong*)(f + RbpOff);
        snap.Rbx = *(ulong*)(f + RbxOff);
        snap.Rdx = *(ulong*)(f + RdxOff);
        snap.Rcx = *(ulong*)(f + RcxOff);
        snap.Rax = *(ulong*)(f + RaxOff);
        snap.Rflags = *(ulong*)(f + RflagsOff);
        snap.Rsp = (ulong)snapshotPtr + GuestRspFromFrameLow;

        if (_accEnabled)
        {
            AccumulateObject(ref snap);
        }

        if (Volatile.Read(ref _captured) >= _budget)
        {
            return;
        }

        var slot = Interlocked.Increment(ref _recordWriteIndex) - 1;
        ref var record = ref _records[(slot % RecordCapacity + RecordCapacity) % RecordCapacity];
        record = snap;
        record.Sequence = Interlocked.Increment(ref _sequence);
        Interlocked.Increment(ref _captured);

        // Convention: the first hook address publishes its rdi as the base for
        // MemPollWatch's SHARPEMU_MEM_POLL_CHAIN resolution (e.g. hook Perform entry so the
        // op becomes the base for an op->container->entry->srcObj chain). Cheap no-op otherwise.
        if (hookIndex == 0)
        {
            MemPollWatch.PublishBase(record.Rdi);
        }

        // Arm a dynamic write-watch at a runtime-resolved address so the code that
        // later writes a per-run heap field is caught by RIP (GuestWriteRipWatch is
        // enabled the moment the first ArmDynamic succeeds). Signal-free: this runs
        // in ordinary managed context on the runner thread, and ArmDynamic is
        // alloc/lock-free (fixed slots + mprotect), de-duping by page.
        if (_armWriteEnabled)
        {
            var arm = GetReg(ref record, _armWriteBaseReg.AsSpan()) + _armWritePreOff;
            if (_armWriteDeref)
            {
                arm = SafeReadU64(arm);
            }

            if (arm != 0)
            {
                var target = arm + _armWriteOff;
                var ok = GuestWriteRipWatch.ArmDynamic(target);
                if (Interlocked.Increment(ref _armLogCount) <= 8)
                {
                    Console.Error.WriteLine(
                        $"[HOOK][ARMWRITE] hook#{hookIndex} base({_armWriteBaseReg})=0x{GetReg(ref record, _armWriteBaseReg.AsSpan()):X} " +
                        $"-> watch 0x{target:X16} armed={ok}");
                }
            }
        }
    }

    /// <summary>
    /// Pre-JIT the GuestWriteRipWatch fault-handler path when SHARPEMU_HOOK_ARM_WRITE
    /// is active, so no signal-frame method is cold when a caught store faults (the
    /// classic JIT-in-signal-frame fail-fast). GuestRipBreakpoint.WarmUp only warms
    /// these when the INT3 breakpoint is enabled; the signal-free hook needs its own.
    /// Called from WarmUpPosixSignalPath; a no-op when the feature is off.
    /// </summary>
    public static void WarmUp()
    {
        if (!_armWriteEnabled)
        {
            return;
        }

        var w = typeof(GuestWriteRipWatch);
        foreach (var name in new[] { "ArmDynamic", "TryHandleWriteFault" })
        {
            var m = w.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }
    }

    private static ulong ParseHexOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static ulong TryParseHexOr(string? value, ulong fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    // Object-keyed accumulator update (budget-independent). Keys by SHARPEMU_HOOK_ACC_KEY and
    // tracks SHARPEMU_HOOK_ACC_FIELD per key; alloc/lock-safe enough for the hot path (one
    // ConcurrentDictionary GetOrAdd + scalar interlocked writes). Caps distinct keys so a
    // runaway key expression can't grow unbounded.
    private static void AccumulateObject(ref HookRecord rec)
    {
        var key = EvalWatch(ref rec, _accKeyExpr);
        if (key == 0)
        {
            return;
        }

        if (!_acc.TryGetValue(key, out var entry))
        {
            if (_acc.Count >= AccCapacity)
            {
                return; // full; ignore new keys rather than grow unbounded
            }

            entry = _acc.GetOrAdd(key, static _ => new AccEntry());
        }

        var count = Interlocked.Increment(ref entry.Count);
        if (_accFieldExpr is not null)
        {
            var field = EvalWatch(ref rec, _accFieldExpr) & _accMask;
            entry.LastField = field;
            if (Interlocked.Exchange(ref entry.SawField, 1) == 0)
            {
                entry.FirstField = field;
                entry.FirstSequence = Interlocked.Increment(ref _sequence);
            }

            if (_accHasDone && field == _accDone)
            {
                Volatile.Write(ref entry.EverDone, 1);
            }
        }

        _ = count;
    }

    /// <summary>
    /// Prints the object-keyed accumulator's "never reached done" offenders (or the top keys
    /// by hit count when no done-value is set). Called rate-limited from <see cref="ArmAndFlush"/>
    /// so the last report before the run is killed shows the persistently-stuck object(s).
    /// </summary>
    private static void ReportAccumulator(bool force)
    {
        if (!_accEnabled)
        {
            return;
        }

        // Rate-limit: report roughly every 4096 flush passes (or when forced).
        if (!force && (Interlocked.Increment(ref _accFlushTick) & 0xFFF) != 0)
        {
            return;
        }

        var offenders = new List<KeyValuePair<ulong, AccEntry>>();
        foreach (var kv in _acc)
        {
            // With a done-value: an offender is a key that was seen with a field but never done.
            // Without: report every key (top by count below).
            if (!_accHasDone || (Volatile.Read(ref kv.Value.SawField) == 1 && Volatile.Read(ref kv.Value.EverDone) == 0))
            {
                offenders.Add(kv);
            }
        }

        offenders.Sort(static (a, b) => b.Value.Count.CompareTo(a.Value.Count));

        var shown = 0;
        Console.Error.WriteLine(
            $"[HOOK][ACC] distinct_keys={_acc.Count} " +
            (_accHasDone ? $"never_reached_done(0x{_accDone:X})={offenders.Count}" : "top_keys") +
            $" key='{_accKeyExpr}' field='{_accFieldExpr ?? "(none)"}'");
        foreach (var kv in offenders)
        {
            if (shown++ >= 16)
            {
                break;
            }

            Console.Error.WriteLine(
                $"[HOOK][ACC]   key=0x{kv.Key:X16} hits={Volatile.Read(ref kv.Value.Count)} " +
                $"field:first=0x{kv.Value.FirstField:X} last=0x{kv.Value.LastField:X} " +
                $"everDone={Volatile.Read(ref kv.Value.EverDone)} firstSeq={kv.Value.FirstSequence}");
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

        ReportAccumulator(force: false);

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

            var watch = new System.Text.StringBuilder();
            for (var w = 0; w < _watch.Length; w++)
            {
                watch.Append($" {_watch[w]}=0x{EvalWatch(ref record, _watch[w]):X}");
            }

            Console.Error.WriteLine(
                $"[HOOK] seq={record.Sequence} rip=0x{record.Rip:X16} " +
                $"rax=0x{record.Rax:X16} rbx=0x{record.Rbx:X16} rcx=0x{record.Rcx:X16} rdx=0x{record.Rdx:X16} " +
                $"rsi=0x{record.Rsi:X16} rdi=0x{record.Rdi:X16} rbp=0x{record.Rbp:X16} rsp=0x{record.Rsp:X16} " +
                $"r8=0x{record.R8:X16} r9=0x{record.R9:X16} r10=0x{record.R10:X16} r11=0x{record.R11:X16} " +
                $"r12=0x{record.R12:X16} r13=0x{record.R13:X16} r14=0x{record.R14:X16} r15=0x{record.R15:X16} " +
                $"rflags=0x{record.Rflags:X16} | caller=0x{caller:X16} [rax+0x18]=0x{raxMethod:X16} [rdi]=0x{rdiVtable:X16}" +
                (watch.Length != 0 ? $" |{watch}" : string.Empty) +
                $"\n      stack:{frames}");
            flushIndex++;
        }

        Volatile.Write(ref _recordFlushIndex, flushIndex);
    }

    private static ulong SafeReadU64(ulong address)
    {
        if ((address & 0x7UL) != 0)
        {
            return 0;
        }

        // Two bands are always guest-mapped and are read raw: the image/heap window, and the
        // high band holding guest thread stacks and the dispatcher stubs
        // (CpuDispatcher/DirectExecutionBackend place those at 0x6FFD..0x6FFF on POSIX and
        // 0x7FFD..0x7FFF on Windows). The old fixed 4 GB..36 GB window excluded that high band
        // entirely, so every stack-relative deref — the common case for a hook watch on a
        // by-reference argument — silently read back as 0.
        var stackBandBase = OperatingSystem.IsWindows() ? 0x7FFD_0000_0000UL : 0x6FFD_0000_0000UL;
        var stackBandEnd = stackBandBase + 0x3_0000_0000UL;
        var raw = (address >= 0x0000000400000000UL && address < 0x0000000900000000UL)
            || (address >= stackBandBase && address < stackBandEnd);
        if (raw)
        {
            return *(ulong*)address;
        }

        // Everything else — notably the low mappings AllocateMappedGuestAddress hands out from
        // 16 MB up, where IL2CPP's managed heap lands — is gated on a real commit query, since a
        // host-thread fault on an unmapped page would abort the process.
        if (address < 0x1000UL ||
            HostMemory.Query((void*)address, out var info) == 0 ||
            info.State != HostMemory.MEM_COMMIT)
        {
            return 0;
        }

        return *(ulong*)address;
    }

    // Evaluate a SHARPEMU_HOOK_WATCH expression against a captured record: a register name
    // followed by "+HEX" (add) or "@" (dereference u64, range-guarded) tokens, left to right.
    private static ulong EvalWatch(ref HookRecord record, string expr)
    {
        var span = expr.AsSpan();
        var i = 0;
        while (i < span.Length && (char.IsLetterOrDigit(span[i])))
        {
            i++;
        }

        var value = GetReg(ref record, span[..i]);
        while (i < span.Length)
        {
            var c = span[i];
            if (c == '@')
            {
                value = SafeReadU64(value);
                i++;
            }
            else if (c == '+')
            {
                var j = i + 1;
                while (j < span.Length && Uri.IsHexDigit(span[j]))
                {
                    j++;
                }

                if (ulong.TryParse(span[(i + 1)..j], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var add))
                {
                    value += add;
                }

                i = j;
            }
            else
            {
                i++;
            }
        }

        return value;
    }

    private static ulong GetReg(ref HookRecord record, ReadOnlySpan<char> name)
    {
        if (name.Equals("rax", StringComparison.OrdinalIgnoreCase)) return record.Rax;
        if (name.Equals("rbx", StringComparison.OrdinalIgnoreCase)) return record.Rbx;
        if (name.Equals("rcx", StringComparison.OrdinalIgnoreCase)) return record.Rcx;
        if (name.Equals("rdx", StringComparison.OrdinalIgnoreCase)) return record.Rdx;
        if (name.Equals("rsi", StringComparison.OrdinalIgnoreCase)) return record.Rsi;
        if (name.Equals("rdi", StringComparison.OrdinalIgnoreCase)) return record.Rdi;
        if (name.Equals("rbp", StringComparison.OrdinalIgnoreCase)) return record.Rbp;
        if (name.Equals("rsp", StringComparison.OrdinalIgnoreCase)) return record.Rsp;
        if (name.Equals("r8", StringComparison.OrdinalIgnoreCase)) return record.R8;
        if (name.Equals("r9", StringComparison.OrdinalIgnoreCase)) return record.R9;
        if (name.Equals("r10", StringComparison.OrdinalIgnoreCase)) return record.R10;
        if (name.Equals("r11", StringComparison.OrdinalIgnoreCase)) return record.R11;
        if (name.Equals("r12", StringComparison.OrdinalIgnoreCase)) return record.R12;
        if (name.Equals("r13", StringComparison.OrdinalIgnoreCase)) return record.R13;
        if (name.Equals("r14", StringComparison.OrdinalIgnoreCase)) return record.R14;
        if (name.Equals("r15", StringComparison.OrdinalIgnoreCase)) return record.R15;
        if (name.Equals("rip", StringComparison.OrdinalIgnoreCase)) return record.Rip;
        return 0;
    }

    private static string[] ParseWatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(
            [';', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
