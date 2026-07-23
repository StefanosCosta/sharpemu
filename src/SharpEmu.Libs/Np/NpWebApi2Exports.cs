// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpWebApi2Exports
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private static int _initialized;
    private static int _nextLibraryContextHandle;
    private static int _nextPushEventHandle;
    private static int _nextPushEventFilter;
    private static int _nextUserContextHandle = 1000;
    private static int _nextCallbackHandle;
    private static readonly object _contextGate = new();
    private static readonly HashSet<int> _libraryContexts = [];
    private static readonly HashSet<int> _pushEventHandles = [];
    private static readonly HashSet<int> _pushEventFilters = [];
    private static readonly HashSet<int> _userContexts = [];
    private static readonly HashSet<int> _callbackHandles = [];

    [SysAbiExport(
        Nid = "+o9816YQhqQ",
        ExportName = "sceNpWebApi2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Initialize(CpuContext ctx)
    {
        var httpContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var poolSize = ctx[CpuRegister.Rsi];

        if (httpContextId <= 0 || poolSize == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var libraryContextId = CreateLibraryContextId();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init", httpContextId, poolSize);
        return ctx.SetReturn(libraryContextId);
    }

    [SysAbiExport(
        Nid = "WV1GwM32NgY",
        ExportName = "sceNpWebApi2PushEventCreateHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2InitializeAlt(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var handle = CreatePushEventHandle();
        Interlocked.Exchange(ref _initialized, 1);
        TraceNpWebApi2("init-alt", libraryContextId, 0);
        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        Nid = "sk54bi6FtYM",
        ExportName = "sceNpWebApi2CreateUserContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2CreateUserContext(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var userId = unchecked((int)ctx[CpuRegister.Rsi]);

        TraceNpWebApi2(
            "create-user-context",
            libraryContextId,
            unchecked((uint)userId));

        if (Volatile.Read(ref _initialized) == 0 ||
            !IsValidLibraryContextId(libraryContextId) ||
            userId == -1)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var userContextId = Interlocked.Increment(ref _nextUserContextHandle);
        lock (_contextGate)
        {
            _userContexts.Add(userContextId);
        }

        return ctx.SetReturn(userContextId);
    }

    // Register a push-event filter under a previously created push-event handle
    // (the game narrows which realtime PSN notifications it wants). No PSN backend
    // is emulated, so accept a valid handle and hand back a tracked filter id the
    // caller can later free; reject an unknown handle. Returning the filter id (not
    // 0) matches the SDK, which yields a positive filter handle on success.
    [SysAbiExport(
        Nid = "MsaFhR+lPE4",
        ExportName = "sceNpWebApi2PushEventCreateFilter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventCreateFilter(CpuContext ctx)
    {
        var pushEventHandle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidPushEventHandle(pushEventHandle))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var filterId = CreatePushEventFilter();
        TraceNpWebApi2("create-filter", pushEventHandle, unchecked((uint)filterId));
        return ctx.SetReturn(filterId);
    }

    // Free a push-event handle created by sceNpWebApi2PushEventCreateHandle. Reject
    // an unknown/already-freed handle so the game's teardown bookkeeping stays honest.
    [SysAbiExport(
        Nid = "fIATVMo4Y1w",
        ExportName = "sceNpWebApi2PushEventDeleteHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventDeleteHandle(CpuContext ctx)
    {
        var pushEventHandle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!RemovePushEventHandle(pushEventHandle))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        TraceNpWebApi2("delete-handle", pushEventHandle, 0);
        return ctx.SetReturn(0);
    }

    // Register a realtime push-event callback against a user context and a push-event
    // handle. No PSN backend is emulated, so the callback is never invoked; we still
    // validate the user context, the push-event handle, and a non-null callback pointer,
    // then hand back a tracked positive callback id the game later frees via
    // UnregisterCallback (the SDK yields a callback id on success, not 0).
    [SysAbiExport(
        Nid = "fY3QqeNkF8k",
        ExportName = "sceNpWebApi2PushEventRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventRegisterCallback(CpuContext ctx)
    {
        var userContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var pushEventHandle = unchecked((int)ctx[CpuRegister.Rsi]);
        var callbackFunction = ctx[CpuRegister.Rdx];

        if (!IsValidUserContextId(userContextId) ||
            !IsValidPushEventHandle(pushEventHandle) ||
            callbackFunction == 0)
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        var callbackId = CreateCallbackHandle();
        TraceNpWebApi2("register-callback", pushEventHandle, unchecked((uint)callbackId));
        return ctx.SetReturn(callbackId);
    }

    // Free a callback registered by sceNpWebApi2PushEventRegisterCallback. Reject an
    // unknown/already-freed id so the game's teardown bookkeeping stays honest.
    [SysAbiExport(
        Nid = "hOnIlcGrO6g",
        ExportName = "sceNpWebApi2PushEventUnregisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2PushEventUnregisterCallback(CpuContext ctx)
    {
        var callbackId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!RemoveCallbackHandle(callbackId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        TraceNpWebApi2("unregister-callback", callbackId, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "bEvXpcEk200",
        ExportName = "sceNpWebApi2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpWebApi2")]
    public static int NpWebApi2Terminate(CpuContext ctx)
    {
        var libraryContextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsValidLibraryContextId(libraryContextId))
        {
            return ctx.SetReturn(NpWebApi2ErrorInvalidArgument);
        }

        RemoveLibraryContextId(libraryContextId);
        TraceNpWebApi2("term", libraryContextId, 0);
        return ctx.SetReturn(0);
    }

    private static int CreateLibraryContextId()
    {
        var handle = Interlocked.Increment(ref _nextLibraryContextHandle);
        lock (_contextGate)
        {
            _libraryContexts.Add(handle);
        }

        return handle;
    }

    private static int CreatePushEventHandle()
    {
        var handle = Interlocked.Increment(ref _nextPushEventHandle);
        lock (_contextGate)
        {
            _pushEventHandles.Add(handle);
        }

        return handle;
    }

    private static bool IsValidPushEventHandle(int pushEventHandle)
    {
        if (pushEventHandle <= 0)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _pushEventHandles.Contains(pushEventHandle);
        }
    }

    private static bool RemovePushEventHandle(int pushEventHandle)
    {
        lock (_contextGate)
        {
            return _pushEventHandles.Remove(pushEventHandle);
        }
    }

    private static int CreatePushEventFilter()
    {
        var filterId = Interlocked.Increment(ref _nextPushEventFilter);
        lock (_contextGate)
        {
            _pushEventFilters.Add(filterId);
        }

        return filterId;
    }

    private static int CreateCallbackHandle()
    {
        var callbackId = Interlocked.Increment(ref _nextCallbackHandle);
        lock (_contextGate)
        {
            _callbackHandles.Add(callbackId);
        }

        return callbackId;
    }

    private static bool RemoveCallbackHandle(int callbackId)
    {
        lock (_contextGate)
        {
            return _callbackHandles.Remove(callbackId);
        }
    }

    private static bool IsValidUserContextId(int userContextId)
    {
        if (userContextId <= 0)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _userContexts.Contains(userContextId);
        }
    }

    private static bool IsValidLibraryContextId(int libraryContextId)
    {
        if (libraryContextId <= 0 || libraryContextId >= 0x8000)
        {
            return false;
        }

        lock (_contextGate)
        {
            return _libraryContexts.Contains(libraryContextId);
        }
    }

    private static void RemoveLibraryContextId(int libraryContextId)
    {
        lock (_contextGate)
        {
            _libraryContexts.Remove(libraryContextId);
            if (_libraryContexts.Count == 0)
            {
                Interlocked.Exchange(ref _initialized, 0);
            }
        }
    }

    private static void TraceNpWebApi2(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP_WEB_API2"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] npwebapi2.{operation} id={id} arg0=0x{arg0:X16} initialized={Volatile.Read(ref _initialized)}");
    }
}
