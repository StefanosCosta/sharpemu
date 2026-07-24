// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.HLE;

/// <summary>
/// Diagnostic: a guest single-step / branch execution tracer for Linux x86-64.
/// Captures the exact RIP sequence a guest function executes so two runs (a
/// working input vs a stuck one) can be diffed to find where control flow
/// diverges - the way into heavily-inlined guest code that static disassembly
/// cannot follow.
///
/// Enabled via <c>SHARPEMU_TRACE_SS=&lt;armAddrHex&gt;[,&lt;loHex&gt;-&lt;hiHex&gt;][,&lt;maxSteps&gt;]</c>
/// (output file <c>SHARPEMU_TRACE_SS_OUT</c>, default <c>sharpemu_ss_trace.bin</c> -
/// a stream of little-endian u64 RIPs). An INT3 is planted at <c>armAddr</c>; the
/// first thread to hit it flips into single-step mode by setting the x86 Trap Flag
/// (EFLAGS bit 8) in the signal mcontext, and every subsequent instruction faults
/// SIGTRAP. RIPs inside the window <c>[lo,hi)</c> are recorded. When execution
/// leaves the window via a CALL we do NOT single-step the callee: TF is cleared, a
/// one-shot INT3 is planted at the return address (which lies inside the window),
/// the callee runs at full speed, and TF is re-armed when the return fires. So the
/// trace stays at the traced function's own instruction granularity - callees
/// collapse to a single return RIP.
///
/// The processor clears TF on entry to the debug (#DB) handler, so TF must be
/// re-asserted on EVERY continue-stepping path. Failing to keep the two invariant
/// (<c>_steppingActive</c> set iff TF is intended set on return) makes a stray trap
/// fall through to the generic fault path and abort the process; every stop path
/// clears TF unconditionally.
///
/// Linux-only (hardcodes the Linux gregset offsets RIP=128/RSP=120/EFLAGS=136);
/// a no-op elsewhere. Guest memory is identity-mapped, so code/stack addresses are
/// host pointers. Reliable only because the traced loader runs on a single guest
/// thread (same cross-modifying-code caveat as <see cref="GuestRipBreakpoint"/>).
/// </summary>
public static unsafe class GuestSingleStepTracer
{
    private const byte Int3 = 0xCC;
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int ProtExec = 0x4;
    private const ulong PageMask = 0xFFFUL;

    // Linux gregset byte offsets relative to GetPosixRegisterBase(ucontext).
    private const int GregRip = 128;
    private const int GregRsp = 120;
    private const int GregEfl = 136;
    private const ulong TrapFlag = 0x100;

    // open(2) flags / mode (Linux x86-64).
    private const int OWronly = 0x1;
    private const int OCreat = 0x40;
    private const int OTrunc = 0x200;
    private const int Mode0644 = 0x1A4;

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int Mprotect(nint address, nuint length, int protection);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte* path, int flags, int mode);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int fd, void* buffer, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    // ---- configuration (parsed once, no allocation in the signal path) ----
    private static readonly bool _linux = !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();
    private static readonly ulong _armAddr;
    private static readonly ulong _lo;
    private static readonly ulong _hi;
    private static readonly long _maxSteps;
    private static readonly string _outPath;
    private static readonly bool _enabled;

    // ---- shared state (single-writer: only the one stepping thread appends) ----
    private static readonly int _fd;
    private static readonly ulong* _buf;
    private const int BufCapacity = 4096; // 32 KiB; flushed on full + at stop
    private static int _bufPos;
    private static long _recorded;
    private static int _stopped;
    private static int _closed;

    // Diagnostics (signal-safe counters, printed from ArmAndFlush).
    private static long _armFires;
    private static long _stepTraps;
    private static long _stepOvers;
    private static long _stops;
    private static long _diagPrinted;
    private static readonly bool _noTf =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SS_NOTF"), "1", StringComparison.Ordinal);

    // SHARPEMU_TRACE_SS_SKIP=N: let the first N arm-INT3 hits pass through untraced
    // (restore + re-arm), so a later invocation of the armed function is traced -
    // useful when the first call early-outs.
    private static long _skipRemaining =
        long.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SS_SKIP"), out var sk) && sk > 0 ? sk : 0;
    private static long _armHits;

    // arm-INT3 descriptor (planted once from ArmAndFlush, restored in the handler).
    private static readonly ulong _armPageStart;
    private static byte _armOriginalByte;
    private static int _armSaved;
    private static int _armPageWritable;
    private static int _armArmed;
    private static int _armFired;

    // ---- per-thread stepping state (the loader is single-threaded, so in practice
    // only one thread ever sets these; [ThreadStatic] keeps a stray second thread
    // that enters the armed function from corrupting the tracer thread's machine). ----
    [ThreadStatic] private static bool _steppingActive;
    [ThreadStatic] private static bool _stepOverActive;
    [ThreadStatic] private static ulong _stepOverRetAddr;
    [ThreadStatic] private static byte _stepOverOrigByte;

    public static bool Enabled => _enabled;

    /// <summary>
    /// Pre-JIT the signal-path methods so none compiles inside a signal frame (a
    /// cold signal path there manifests as "call a UnmanagedCallersOnly method from
    /// managed code" / a fatal JIT-in-signal-frame). <see cref="TryHandleTrap"/>
    /// itself is warmed by the existing synthetic-SIGTRAP warmup, but its callees
    /// are not reached by that fake trap, so warm them explicitly. Called once,
    /// outside signal context, from WarmUpPosixSignalPath.
    /// </summary>
    public static void WarmUp()
    {
        if (!_enabled)
        {
            return;
        }

        var t = typeof(GuestSingleStepTracer);
        foreach (var name in new[] { nameof(TryHandleTrap), "Record", "Stop", "TryPlantReturnBp", "SafeReadStack" })
        {
            var m = t.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m != null)
            {
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
        }

        // Warm the libc P/Invoke stubs used from the signal handler (0-byte write is
        // a no-op syscall; mprotect a scratch page then release it).
        _ = Write(_fd, _buf, 0);
        var scratch = NativeMemory.AllocZeroed(0x1000);
        _ = Mprotect((nint)scratch, 0x1000, ProtRead | ProtWrite);
        NativeMemory.Free(scratch);
    }

    static GuestSingleStepTracer()
    {
        if (_linux &&
            TryParseConfig(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SS"),
                out _armAddr, out _lo, out _hi, out _maxSteps))
        {
            _armPageStart = _armAddr & ~PageMask;
            _outPath = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SS_OUT") ?? "sharpemu_ss_trace.bin";

            var pathBytes = Encoding.UTF8.GetBytes(_outPath + "\0");
            fixed (byte* p = pathBytes)
            {
                _fd = Open(p, OWronly | OCreat | OTrunc, Mode0644);
            }

            _buf = (ulong*)NativeMemory.AllocZeroed((nuint)BufCapacity * sizeof(ulong));
            _enabled = _fd >= 0 && _buf != null;
        }
        else
        {
            _outPath = string.Empty;
            _fd = -1;
        }
    }

    /// <summary>
    /// Managed-context pass (call beside <see cref="GuestRipBreakpoint.ArmAndFlush"/>
    /// from the import loop): plant the arm-INT3 once its page is mapped, and after
    /// tracing stops close the output and print a one-line summary. All buffer writes
    /// happen in the signal handler (single writer); this never touches the buffer,
    /// so no cross-thread race on <c>_bufPos</c>.
    /// </summary>
    public static void ArmAndFlush()
    {
        if (!_enabled)
        {
            return;
        }

        // Live diagnostics so a crash mid-trace still shows how far it got.
        var hits = Volatile.Read(ref _armHits);
        if (hits != Volatile.Read(ref _diagPrinted))
        {
            Volatile.Write(ref _diagPrinted, hits);
            Console.Error.WriteLine(
                $"[SS][diag] armHits={hits} skipLeft={Volatile.Read(ref _skipRemaining)} " +
                $"armFires={Volatile.Read(ref _armFires)} stepTraps={Volatile.Read(ref _stepTraps)} " +
                $"stepOvers={Volatile.Read(ref _stepOvers)} stops={Volatile.Read(ref _stops)} " +
                $"recorded={Volatile.Read(ref _recorded)}");
        }

        if (Volatile.Read(ref _stopped) == 1)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _ = Close(_fd);
                Console.Error.WriteLine(
                    $"[SS] trace complete: {Volatile.Read(ref _recorded)} steps -> {_outPath} " +
                    $"(armFires={Volatile.Read(ref _armFires)} stepTraps={Volatile.Read(ref _stepTraps)} " +
                    $"stepOvers={Volatile.Read(ref _stepOvers)})");
            }

            return;
        }

        if (Volatile.Read(ref _armFired) != 0 || Volatile.Read(ref _armArmed) != 0)
        {
            return;
        }

        if (_armPageWritable == 0)
        {
            // Fails (ENOMEM) until the loader's code page is mapped; retry next pass.
            if (Mprotect((nint)_armPageStart, 0x1000, ProtRead | ProtWrite | ProtExec) != 0)
            {
                return;
            }

            _armPageWritable = 1;
        }

        if (_armSaved == 0)
        {
            _armOriginalByte = *(byte*)_armAddr;
            _armSaved = 1;
        }

        *(byte*)_armAddr = Int3;
        Volatile.Write(ref _armArmed, 1);
    }

    /// <summary>
    /// Signal-handler entry for SIGTRAP. Reads/writes RIP/RSP/EFLAGS in place through
    /// the Linux gregset base. Returns true if the trap was an arm-INT3 hit, a
    /// step-over return, or an active single-step (consumed); false when idle so a
    /// genuine <see cref="GuestRipBreakpoint"/> INT3 still reaches its handler. Must
    /// not allocate, lock, or write to the console.
    /// </summary>
    public static bool TryHandleTrap(nint gregsBase)
    {
        if (!_enabled)
        {
            return false;
        }

        var g = (byte*)gregsBase;
        var trapRip = *(ulong*)(g + GregRip);

        // Concurrency guard: ANOTHER thread can trap on the arm-INT3 in the tiny
        // window before the first thread restores the byte (the arm site is not
        // guaranteed single-threaded). Consume it - the byte is already restored,
        // so rewinding re-executes the real instruction. Never fall through (that
        // would chain to the default SIGTRAP handler and abort the process).
        // Gate on !_steppingActive so the STEPPING thread's own single-step trap
        // right after a 1-byte instruction at _armAddr (trapRip-1 == _armAddr) is
        // NOT mistaken for a concurrent hit (that would rewind and re-execute
        // forever -> stack overflow).
        if (!_steppingActive && Volatile.Read(ref _armFired) != 0 && trapRip - 1 == _armAddr)
        {
            *(ulong*)(g + GregRip) = _armAddr;
            return true;
        }

        // Case A: the arm-INT3 fired - enter single-step mode.
        if (!_steppingActive && Volatile.Read(ref _armArmed) != 0 && trapRip - 1 == _armAddr)
        {
            Interlocked.Increment(ref _armHits);
            *(byte*)_armAddr = _armOriginalByte;   // restore the real byte
            *(ulong*)(g + GregRip) = _armAddr;     // rewind so the real instr runs

            if (Volatile.Read(ref _skipRemaining) > 0)
            {
                // Let this invocation run untraced; ArmAndFlush re-plants the INT3
                // (armFired stays 0) so the next invocation is caught.
                Interlocked.Decrement(ref _skipRemaining);
                Volatile.Write(ref _armArmed, 0);
                return true;
            }

            Volatile.Write(ref _armArmed, 0);
            Volatile.Write(ref _armFired, 1);
            Interlocked.Increment(ref _armFires);
            if (_noTf)
            {
                // Diagnostic: arm becomes an inert one-shot (no single-stepping) to
                // isolate whether TF single-stepping is what crashes.
                Volatile.Write(ref _stopped, 1);
                return true;
            }

            _steppingActive = true;
            *(ulong*)(g + GregEfl) |= TrapFlag;    // arm single-step
            return true;
        }

        // Case B: a step-over return-INT3 fired - resume single-stepping.
        if (_stepOverActive && trapRip - 1 == _stepOverRetAddr)
        {
            *(byte*)_stepOverRetAddr = _stepOverOrigByte;   // restore the real byte
            _stepOverActive = false;
            *(ulong*)(g + GregRip) = _stepOverRetAddr;      // rewind
            Record(_stepOverRetAddr);
            *(ulong*)(g + GregEfl) |= TrapFlag;             // re-arm single-step
            return true;
        }

        // Case C: an ordinary single-step trap (TRAP_TRACE). The CPU already
        // advanced RIP to the next instruction and cleared TF, so we must re-assert
        // TF to keep stepping.
        if (_steppingActive && !_stepOverActive)
        {
            Interlocked.Increment(ref _stepTraps);
            var rip = trapRip;
            Record(rip);

            if (Volatile.Read(ref _recorded) >= _maxSteps)
            {
                Stop(g);
                return true;
            }

            if (rip >= _lo && rip < _hi)
            {
                // Still inside the window - keep stepping.
                *(ulong*)(g + GregEfl) |= TrapFlag;
                return true;
            }

            // Left the window (EITHER direction - callees can be at higher OR lower
            // addresses than the traced function). Distinguish a CALL from a genuine
            // return by the stack top: a CALL just pushed a return address that lies
            // INSIDE the window; a ret to the caller did not. On a CALL, step over
            // the callee at full speed (a one-shot INT3 at the return address
            // re-arms stepping); otherwise the function returned - stop.
            var rsp = *(ulong*)(g + GregRsp);
            var ret = SafeReadStack(rsp);
            if (ret >= _lo && ret < _hi && TryPlantReturnBp(ret))
            {
                *(ulong*)(g + GregEfl) &= ~TrapFlag;   // run the callee at full speed
                _stepOverActive = true;
                _stepOverRetAddr = ret;
                Interlocked.Increment(ref _stepOvers);
                return true;
            }

            // Genuine return to the caller (or unclassifiable jump) - stop.
            Stop(g);
            return true;
        }

        return false;
    }

    private static void Record(ulong rip)
    {
        _buf[_bufPos++] = rip;
        _recorded++;
        if (_bufPos >= BufCapacity)
        {
            _ = Write(_fd, _buf, (nuint)BufCapacity * sizeof(ulong));
            _bufPos = 0;
        }
    }

    private static void Stop(byte* g)
    {
        *(ulong*)(g + GregEfl) &= ~TrapFlag;   // TF must be clear on every stop path
        _steppingActive = false;
        _stepOverActive = false;
        if (_bufPos > 0)
        {
            _ = Write(_fd, _buf, (nuint)_bufPos * sizeof(ulong));   // write(2) is signal-safe
            _bufPos = 0;
        }

        Interlocked.Increment(ref _stops);
        Volatile.Write(ref _stopped, 1);
    }

    // Plant a one-shot INT3 at the CALL's return address so single-stepping resumes
    // when the callee returns. The traced thread is the only one executing this code
    // (single-threaded invariant), and the byte is at the caller's return site - not
    // in the running callee - so there is no concurrent fetch of the patched byte.
    private static bool TryPlantReturnBp(ulong ret)
    {
        var pageStart = ret & ~PageMask;
        if (Mprotect((nint)pageStart, 0x1000, ProtRead | ProtWrite | ProtExec) != 0)
        {
            return false;
        }

        _stepOverOrigByte = *(byte*)ret;
        *(byte*)ret = Int3;
        return true;
    }

    // Read a value off a guest thread stack (host-mmap'd high). Only ever called
    // with a live rsp, so the wide window is safe; a bogus rsp returns 0 (-> stop).
    private static ulong SafeReadStack(ulong address)
    {
        if (address < 0x0000600000000000UL || address >= 0x0000800000000000UL || (address & 0x7UL) != 0)
        {
            return 0;
        }

        return *(ulong*)address;
    }

    private static bool TryParseConfig(string? value, out ulong armAddr, out ulong lo, out ulong hi, out long maxSteps)
    {
        armAddr = 0;
        lo = 0;
        hi = 0;
        maxSteps = 5_000_000;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // <armAddr>[,<lo>-<hi>][,<maxSteps>]
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !TryParseHex(parts[0], out armAddr))
        {
            return false;
        }

        lo = armAddr;
        hi = (armAddr & ~PageMask) + 0x8000; // default window: generous single-function span

        for (var i = 1; i < parts.Length; i++)
        {
            var dash = parts[i].IndexOf('-');
            if (dash > 0)
            {
                if (TryParseHex(parts[i][..dash], out var pLo) && TryParseHex(parts[i][(dash + 1)..], out var pHi) && pHi > pLo)
                {
                    lo = pLo;
                    hi = pHi;
                }
            }
            else if (long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pMax) && pMax > 0)
            {
                maxSteps = pMax;
            }
        }

        return true;
    }

    private static bool TryParseHex(string s, out ulong value)
    {
        var span = s.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
