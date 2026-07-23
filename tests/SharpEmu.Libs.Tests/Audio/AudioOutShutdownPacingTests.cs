// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

// On host shutdown the audio ports must stay registered (with their host backend
// released) rather than being dropped. Dropping them let sceAudioOutOutput return
// instantly with no pacing, which removed the backpressure that drains a guest audio
// engine's ring buffer - so FMOD's producer semaphore saturated and its thread
// busy-spun on sceKernelSignalSema forever. Keeping the port routes Output through
// the null-backend PaceSilence path, preserving that backpressure during teardown.
[Collection(nameof(AudioOutStateCollection))]
public sealed class AudioOutShutdownPacingTests
{
    [Fact]
    public void ShutdownAllPorts_RetainsPortWithBackendReleased()
    {
        ResetAudioOutState();
        try
        {
            var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = 0;        // userId
            context[CpuRegister.Rsi] = 0;        // type
            context[CpuRegister.Rcx] = 256;      // bufferLength (frames)
            context[CpuRegister.R8] = 48000;     // frequency
            context[CpuRegister.R9] = 1;         // format 1 = stereo s16

            var handle = AudioOutExports.AudioOutOpen(context);
            Assert.True(handle > 0);

            var ports = GetPorts();
            Assert.True(ports.Contains(handle));

            AudioOutExports.ShutdownAllPorts();

            // The port is still registered (not removed) and its backend was released,
            // so a straggler sceAudioOutOutput takes the null-backend pacing path.
            Assert.True(ports.Contains(handle));
            Assert.Null(GetPortBackend(ports[handle]!));
            Assert.True(GetShutdownFlag());
        }
        finally
        {
            ResetAudioOutState();
        }
    }

    private static IDictionary GetPorts()
    {
        var field = typeof(AudioOutExports).GetField(
            "Ports",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (IDictionary)field.GetValue(null)!;
    }

    private static object? GetPortBackend(object port)
    {
        var property = port.GetType().GetProperty(
            "Backend",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return property.GetValue(port);
    }

    private static bool GetShutdownFlag()
    {
        var field = typeof(AudioOutExports).GetField(
            "_shutdown",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (bool)field.GetValue(null)!;
    }

    private static void ResetAudioOutState()
    {
        GetPorts().Clear();
        typeof(AudioOutExports)
            .GetField("_shutdown", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, false);
    }
}

[CollectionDefinition(nameof(AudioOutStateCollection), DisableParallelization = true)]
public sealed class AudioOutStateCollection
{
}
