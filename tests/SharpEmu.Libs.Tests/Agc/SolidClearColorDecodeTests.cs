// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

/// <summary>
/// Covers <see cref="AgcExports.DecodeSolidClearColor"/>, which recovers the
/// constant colour a procedural fullscreen-clear pixel shader exports to MRT0.
/// The regression these guard against: caveman_ninja's constant-white fill
/// (VMovB32 v0 &lt;- 0x3C003C00 packed fp16, Compressed export) previously decoded
/// to black because only InitialScalarRegisters[0] was inspected.
/// </summary>
public sealed class SolidClearColorDecodeTests
{
    [Fact]
    public void CompressedFp16Immediate_DecodesWhite()
    {
        // VMovB32 v0 <- 0x3C003C00 (1.0h, 1.0h); Exp mrt0 <- v0,v0,v0,v0 (Compressed).
        var state = PixelState(
            0x605040900,
            MovImmediate(0, 0x3C003C00),
            Export(target: 0, compressed: true, 0, 0, 0, 0),
            EndProgram());

        AssertColor((1f, 1f, 1f, 1f), AgcExports.DecodeSolidClearColor(state, EmptyEvaluation()));
    }

    [Fact]
    public void CompressedFp16Immediate_DecodesPerChannelNotAHardcode()
    {
        // v0 packs (R=1.0h low, G=0 high); v1 packs (B=0 low, A=1.0h high).
        // Compressed export reads Sources[0]->(R,G), Sources[1]->(B,A).
        var state = PixelState(
            0x100,
            MovImmediate(0, 0x0000_3C00),
            MovImmediate(1, 0x3C00_0000),
            Export(target: 0, compressed: true, 0, 1, 0, 1),
            EndProgram());

        AssertColor((1f, 0f, 0f, 1f), AgcExports.DecodeSolidClearColor(state, EmptyEvaluation()));
    }

    [Fact]
    public void Fp32Immediate_DecodesWhite()
    {
        // VMovB32 v0 <- 0x3F800000 (1.0f); uncompressed Exp mrt0 <- v0,v0,v0,v0.
        var state = PixelState(
            0x200,
            MovImmediate(0, 0x3F80_0000),
            Export(target: 0, compressed: false, 0, 0, 0, 0),
            EndProgram());

        AssertColor((1f, 1f, 1f, 1f), AgcExports.DecodeSolidClearColor(state, EmptyEvaluation()));
    }

    [Fact]
    public void ScalarSourcedExport_PreservesTitleClearBehaviour()
    {
        // Title-clear pattern: the colour arrives via a scalar the evaluator folded
        // into the initial SGPR file (VMovB32 v0 <- s0). Must match the legacy
        // InitialScalarRegisters[0] fp32 decode.
        var state = PixelState(
            0x808E88000,
            MovScalar(0, 0),
            Export(target: 0, compressed: false, 0, 0, 0, 0),
            EndProgram());
        var evaluation = EvaluationWithScalars(BitConverter.SingleToUInt32Bits(0.25f));

        AssertColor((0.25f, 0.25f, 0.25f, 0.25f),
            AgcExports.DecodeSolidClearColor(state, evaluation));
    }

    [Fact]
    public void UnresolvableExport_FallsBackToScalarRegister()
    {
        // Export reads v5, which no instruction writes: the IR trace fails and the
        // decoder falls back to the InitialScalarRegisters[0] fp32 path.
        var state = PixelState(
            0x300,
            Export(target: 0, compressed: false, 5, 5, 5, 5),
            EndProgram());
        var evaluation = EvaluationWithScalars(BitConverter.SingleToUInt32Bits(0.5f));

        AssertColor((0.5f, 0.5f, 0.5f, 0.5f),
            AgcExports.DecodeSolidClearColor(state, evaluation));
    }

    [Fact]
    public void UnresolvableExport_WithNoScalars_DefaultsToWhite()
    {
        var state = PixelState(
            0x400,
            Export(target: 0, compressed: false, 5, 5, 5, 5),
            EndProgram());

        AssertColor((1f, 1f, 1f, 1f), AgcExports.DecodeSolidClearColor(state, EmptyEvaluation()));
    }

    private static Gen5ShaderState PixelState(
        ulong address,
        params Gen5ShaderInstruction[] instructions) =>
        new(new Gen5ShaderProgram(address, instructions), [], null);

    private static Gen5ShaderEvaluation EmptyEvaluation() =>
        new([], [], [], []);

    private static Gen5ShaderEvaluation EvaluationWithScalars(params uint[] scalars) =>
        new(scalars, scalars, [], []);

    private static Gen5ShaderInstruction MovImmediate(uint vectorRegister, uint literal) =>
        new(
            0,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [],
            [new Gen5Operand(Gen5OperandKind.LiteralConstant, literal)],
            [Gen5Operand.Vector(vectorRegister)],
            null);

    private static Gen5ShaderInstruction MovScalar(uint vectorRegister, uint scalarRegister) =>
        new(
            0,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [],
            [Gen5Operand.Scalar(scalarRegister)],
            [Gen5Operand.Vector(vectorRegister)],
            null);

    private static Gen5ShaderInstruction Export(
        uint target,
        bool compressed,
        uint x,
        uint y,
        uint z,
        uint w) =>
        new(
            0,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [Gen5Operand.Vector(x), Gen5Operand.Vector(y), Gen5Operand.Vector(z), Gen5Operand.Vector(w)],
            [],
            new Gen5ExportControl(target, 0xF, compressed, true, false));

    private static Gen5ShaderInstruction EndProgram() =>
        new(0, Gen5ShaderEncoding.Sopp, "SEndpgm", [], [], [], null);

    private static void AssertColor(
        (float Red, float Green, float Blue, float Alpha) expected,
        (float Red, float Green, float Blue, float Alpha) actual)
    {
        Assert.Equal(expected.Red, actual.Red, 3);
        Assert.Equal(expected.Green, actual.Green, 3);
        Assert.Equal(expected.Blue, actual.Blue, 3);
        Assert.Equal(expected.Alpha, actual.Alpha, 3);
    }
}
