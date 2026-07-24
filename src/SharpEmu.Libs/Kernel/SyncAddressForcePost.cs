// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Diagnostic: a signal-free "force-post" for stuck sync-on-address (futex) semaphores.
/// Enabled via <c>SHARPEMU_FORCE_POST="0xADDR[:n][,0xADDR2[:n]...]"</c> (n = posts per
/// attempt, default 1), with <c>SHARPEMU_FORCE_POST_AFTER_MS</c> (delay before the first
/// attempt, default 15000 — let the level-load wedge settle) and
/// <c>SHARPEMU_FORCE_POST_INTERVAL_MS</c> (repeat interval; 0 = once). Inert when unset.
///
/// A dedicated background host thread replicates the guest producer half of a counting
/// semaphore for each configured address: it commit-gates + atomically increments the
/// watched count word (the word IS the guest memory at the sync-address —
/// <see cref="KernelSyncOnAddressCompatExports.SyncOnAddressWait"/> blocks while
/// <c>*addr == pattern</c>, and writing the word alone never releases a parked waiter) and
/// then calls <see cref="KernelSyncOnAddressCompatExports.SignalAddressWaiters"/> (which
/// wakes cooperative + host-parked waiters and self-drives them via the scheduler's Pump).
///
/// This is the "force-post differential": if force-posting a starved semaphore makes the
/// pipeline advance, the wedge is a wake-delivery/scheduler race; if the woken consumer just
/// finds an empty queue and re-parks (no advance), the producer never enqueued work and the
/// break is upstream. It does NOT drive guest threads directly (that would violate the
/// scheduler's single-owner invariant) — it only posts, exactly like a guest producer would.
/// </summary>
public static unsafe class SyncAddressForcePost
{
    private readonly record struct Target(ulong Address, int Count);

    private static readonly Target[] _targets = Parse(
        Environment.GetEnvironmentVariable("SHARPEMU_FORCE_POST"));

    private static readonly int _afterMs = ParseInt(
        Environment.GetEnvironmentVariable("SHARPEMU_FORCE_POST_AFTER_MS"), 15000);

    private static readonly int _intervalMs = ParseInt(
        Environment.GetEnvironmentVariable("SHARPEMU_FORCE_POST_INTERVAL_MS"), 0);

    private static readonly bool _enabled = _targets.Length != 0;

    private static int _started;

    public static bool Enabled => _enabled;

    /// <summary>Idempotently starts the background poster on first use. Cheap no-op when
    /// disabled or already running.</summary>
    public static void EnsureStarted()
    {
        if (!_enabled || Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(PostLoop)
        {
            IsBackground = true,
            Name = "SyncAddressForcePost",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
        Console.Error.WriteLine(
            $"[FORCEPOST] armed {_targets.Length} target(s), afterMs={_afterMs}, intervalMs={_intervalMs}");
    }

    private static void PostLoop()
    {
        Thread.Sleep(_afterMs);
        do
        {
            foreach (var target in _targets)
            {
                PostOne(target);
            }

            if (_intervalMs > 0)
            {
                Thread.Sleep(_intervalMs);
            }
        }
        while (_intervalMs > 0);
    }

    private static void PostOne(Target target)
    {
        var address = target.Address;

        // A host thread must not dereference a reserved-but-uncommitted guest page (it aborts
        // the process, unlike a guest-thread lazy-commit fault). Commit-gate first, mirroring
        // MemPollWatch. Guest window is 0x4_0000_0000..0x9_0000_0000, and the count word the
        // waiter compares is a uint32 at the sync-address.
        if (address < 0x0000000400000000UL || address >= 0x0000000900000000UL || (address & 0x3UL) != 0)
        {
            Console.Error.WriteLine($"[FORCEPOST] addr=0x{address:X16} skipped (out-of-range/misaligned)");
            return;
        }

        if (HostMemory.Query((void*)address, out var info) == 0 || info.State != HostMemory.MEM_COMMIT)
        {
            Console.Error.WriteLine($"[FORCEPOST] addr=0x{address:X16} skipped (uncommitted)");
            return;
        }

        // Replicate a counting-semaphore producer: xadd [count],+n then wake. Incrementing the
        // watched word makes the woken consumer's own post-wait re-acquire observe a token, so a
        // genuinely wake-starved waiter can proceed (rather than re-parking on the stale count).
        var before = *(int*)address;
        var after = Interlocked.Add(ref *(int*)address, target.Count);
        var woke = KernelSyncOnAddressCompatExports.SignalAddressWaiters(address, target.Count);

        Console.Error.WriteLine(
            $"[FORCEPOST] addr=0x{address:X16} wordBefore=0x{before:X8} wordAfter=0x{after:X8} " +
            $"posts={target.Count} cooperative_woke={woke}");
    }

    private static int ParseInt(string? value, int fallback)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : fallback;
    }

    private static Target[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var targets = new List<Target>();
        foreach (var token in value.Split(
                     [',', ';', ' ', '\t'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var addrSpan = parts[0].AsSpan();
            if (addrSpan.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                addrSpan = addrSpan[2..];
            }

            if (!ulong.TryParse(addrSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
            {
                continue;
            }

            var count = 1;
            if (parts.Length > 1 &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) &&
                parsedCount > 0)
            {
                count = parsedCount;
            }

            targets.Add(new Target(address, count));
        }

        return targets.ToArray();
    }
}
