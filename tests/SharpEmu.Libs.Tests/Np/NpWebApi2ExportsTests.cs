// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

// The push-event handle/filter lifecycle: CreateHandle mints a tracked handle, CreateFilter binds
// a filter to a valid handle (and rejects an unknown one), and DeleteHandle frees a handle exactly
// once. NpWebApi2Exports holds module-global state, so keep every case inside this single class
// (xUnit runs a class's methods sequentially) and assert only relative properties, never absolute ids.
public sealed class NpWebApi2ExportsTests
{
    private const int NpWebApi2ErrorInvalidArgument = unchecked((int)0x80553402);

    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    // Mint a valid push-event handle the way a guest does: initialize the library (yielding a
    // library-context id) then create a push-event handle under it.
    private int CreateValidPushEventHandle()
    {
        _ctx[CpuRegister.Rdi] = 1;       // httpContextId > 0
        _ctx[CpuRegister.Rsi] = 0x1000;  // poolSize > 0
        var libraryContextId = NpWebApi2Exports.NpWebApi2Initialize(_ctx);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)libraryContextId);
        return NpWebApi2Exports.NpWebApi2InitializeAlt(_ctx); // sceNpWebApi2PushEventCreateHandle
    }

    // Mint a valid user context: initialize the library, then create a user context under it.
    private int CreateValidUserContext()
    {
        _ctx[CpuRegister.Rdi] = 1;       // httpContextId > 0
        _ctx[CpuRegister.Rsi] = 0x1000;  // poolSize > 0
        var libraryContextId = NpWebApi2Exports.NpWebApi2Initialize(_ctx);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)libraryContextId);
        _ctx[CpuRegister.Rsi] = 0;       // a valid (non -1) userId
        return NpWebApi2Exports.NpWebApi2CreateUserContext(_ctx);
    }

    [Fact]
    public void CreateFilter_WithValidHandle_ReturnsPositiveFilterId()
    {
        var handle = CreateValidPushEventHandle();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)handle);
        var filterId = NpWebApi2Exports.NpWebApi2PushEventCreateFilter(_ctx);

        Assert.True(filterId > 0, $"expected a positive filter id, got {filterId}");
    }

    [Fact]
    public void CreateFilter_WithUnknownHandle_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = int.MaxValue; // an id the monotonic counter never mints

        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventCreateFilter(_ctx));
    }

    [Fact]
    public void DeleteHandle_FreesAValidHandleExactlyOnce()
    {
        var handle = CreateValidPushEventHandle();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)handle);
        Assert.Equal(0, NpWebApi2Exports.NpWebApi2PushEventDeleteHandle(_ctx));

        // A second delete of the now-freed handle must be rejected.
        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)handle);
        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventDeleteHandle(_ctx));
    }

    [Fact]
    public void DeleteHandle_WithUnknownHandle_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = int.MaxValue - 1;

        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventDeleteHandle(_ctx));
    }

    [Fact]
    public void RegisterCallback_WithValidArguments_ReturnsPositiveCallbackId()
    {
        var userContext = CreateValidUserContext();
        var handle = CreateValidPushEventHandle();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)userContext);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)(uint)handle);
        _ctx[CpuRegister.Rdx] = 0x1000; // non-null callback function pointer

        var callbackId = NpWebApi2Exports.NpWebApi2PushEventRegisterCallback(_ctx);

        Assert.True(callbackId > 0, $"expected a positive callback id, got {callbackId}");
    }

    [Fact]
    public void RegisterCallback_WithUnknownHandle_ReturnsInvalidArgument()
    {
        var userContext = CreateValidUserContext();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)userContext);
        _ctx[CpuRegister.Rsi] = int.MaxValue; // an id the monotonic counter never mints
        _ctx[CpuRegister.Rdx] = 0x1000;

        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventRegisterCallback(_ctx));
    }

    [Fact]
    public void RegisterCallback_WithNullCallback_ReturnsInvalidArgument()
    {
        var userContext = CreateValidUserContext();
        var handle = CreateValidPushEventHandle();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)userContext);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)(uint)handle);
        _ctx[CpuRegister.Rdx] = 0; // null callback function pointer

        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventRegisterCallback(_ctx));
    }

    [Fact]
    public void RegisterCallback_WithUnknownUserContext_ReturnsInvalidArgument()
    {
        var handle = CreateValidPushEventHandle();

        _ctx[CpuRegister.Rdi] = int.MaxValue; // an id CreateUserContext never mints
        _ctx[CpuRegister.Rsi] = unchecked((ulong)(uint)handle);
        _ctx[CpuRegister.Rdx] = 0x1000;

        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventRegisterCallback(_ctx));
    }

    [Fact]
    public void UnregisterCallback_FreesARegisteredCallbackExactlyOnce()
    {
        var userContext = CreateValidUserContext();
        var handle = CreateValidPushEventHandle();

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)userContext);
        _ctx[CpuRegister.Rsi] = unchecked((ulong)(uint)handle);
        _ctx[CpuRegister.Rdx] = 0x1000;
        var callbackId = NpWebApi2Exports.NpWebApi2PushEventRegisterCallback(_ctx);

        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)callbackId);
        Assert.Equal(0, NpWebApi2Exports.NpWebApi2PushEventUnregisterCallback(_ctx));

        // A second unregister of the now-freed callback must be rejected.
        _ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)callbackId);
        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventUnregisterCallback(_ctx));
    }

    [Fact]
    public void UnregisterCallback_WithUnknownId_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rdi] = int.MaxValue - 2;

        Assert.Equal(
            NpWebApi2ErrorInvalidArgument,
            NpWebApi2Exports.NpWebApi2PushEventUnregisterCallback(_ctx));
    }
}
