// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPresentGeometryTests
{
    // Titles composite their frame with a rectangle-list draw: three corners,
    // the fourth derived by the hardware. Mapping it to a triangle list left
    // half of every full-screen composite unwritten (black).
    [Fact]
    public void RectListDrawsAsAFourCornerStrip()
    {
        Assert.Equal(
            PrimitiveTopology.TriangleStrip,
            VulkanVideoPresenter.GetPrimitiveTopology(GuestPrimitiveType.RectList));
        Assert.Equal(
            4u,
            VulkanVideoPresenter.GetDrawVertexCount(
                GuestPrimitiveType.RectList,
                vertexCount: 3,
                indexBuffer: null));
    }

    // An indexed rect list draws the indices it was given; only the implicit
    // fourth corner of an auto-index draw is synthesised.
    [Fact]
    public void IndexedRectListKeepsItsIndexCount()
    {
        var indexBuffer = new GuestIndexBuffer([], 0, Is32Bit: false, Pooled: false);
        Assert.Equal(
            6u,
            VulkanVideoPresenter.GetDrawVertexCount(
                GuestPrimitiveType.RectList,
                vertexCount: 6,
                indexBuffer));
    }

    [Theory]
    [InlineData(1u, PrimitiveTopology.PointList)]
    [InlineData(2u, PrimitiveTopology.LineList)]
    [InlineData(3u, PrimitiveTopology.LineStrip)]
    [InlineData(4u, PrimitiveTopology.TriangleList)]
    [InlineData(5u, PrimitiveTopology.TriangleFan)]
    [InlineData(6u, PrimitiveTopology.TriangleStrip)]
    public void OrdinaryPrimitivesKeepTheirTopology(
        uint primitiveType,
        PrimitiveTopology expected)
    {
        Assert.Equal(expected, VulkanVideoPresenter.GetPrimitiveTopology(primitiveType));
    }

    // The six-vertex two-triangle quad is the other way titles composite a
    // full screen; its vertex count must pass through untouched.
    [Fact]
    public void TriangleListVertexCountIsUnchanged()
    {
        Assert.Equal(
            6u,
            VulkanVideoPresenter.GetDrawVertexCount(4, vertexCount: 6, indexBuffer: null));
    }
}
