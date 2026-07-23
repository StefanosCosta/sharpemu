// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Regression: the /temp0 scratch mount root must be provisioned on resolution,
// matching the sibling resolvers (/download0, /hostapp, /devlog/app). Without it,
// the non-recursive sceKernelMkdir(/temp0/<subdir>) failed with NOT_FOUND because
// its parent host directory never existed, while the same call under /download0
// succeeded. Unity/IL2CPP titles scratch into /temp0, so this asymmetry is guest-visible.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelTemp0ProvisioningTests : IDisposable
{
    private const string Temp0VariableName = "SHARPEMU_TEMP0_DIR";

    private readonly string? _previousTemp0Dir;
    private readonly string _temp0Root;

    public KernelTemp0ProvisioningTests()
    {
        _previousTemp0Dir = Environment.GetEnvironmentVariable(Temp0VariableName);
        _temp0Root = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-temp0-{Guid.NewGuid():N}");
        // Point the configured branch at a path that does NOT yet exist on disk.
        Environment.SetEnvironmentVariable(Temp0VariableName, _temp0Root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Temp0VariableName, _previousTemp0Dir);
        if (Directory.Exists(_temp0Root))
        {
            Directory.Delete(_temp0Root, recursive: true);
        }
    }

    [Fact]
    public void ResolveGuestPath_Temp0Root_CreatesHostDirectory()
    {
        Assert.False(Directory.Exists(_temp0Root));

        var hostPath = KernelMemoryCompatExports.ResolveGuestPath("/temp0");

        Assert.Equal(Path.GetFullPath(_temp0Root), Path.GetFullPath(hostPath));
        Assert.True(Directory.Exists(hostPath));
    }

    [Fact]
    public void ResolveGuestPath_Temp0Subdirectory_HasProvisionedParent()
    {
        // The failing case: mkdir /temp0/<subdir> is non-recursive, so its parent
        // (the temp0 root) must already exist by the time the guest path resolves.
        var hostSubPath = KernelMemoryCompatExports.ResolveGuestPath("/temp0/scratch");

        var parent = Path.GetDirectoryName(hostSubPath);
        Assert.False(string.IsNullOrEmpty(parent));
        Assert.True(Directory.Exists(parent));
    }
}
