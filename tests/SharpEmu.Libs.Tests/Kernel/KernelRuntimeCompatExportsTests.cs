// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelGetTscFrequency must describe the same clock that sceKernelReadTsc returns. ReadTsc
// only returns the CPU's RDTSC when the host RDTSC reader is available (64-bit Windows) and
// otherwise falls back to the QPC-based Stopwatch, so the frequency selection has to follow suit.
public sealed class KernelRuntimeCompatExportsTests
{
    private static KernelRuntimeCompatExports.TryGetFrequency Yields(ulong hz) =>
        (out ulong frequencyHz) =>
        {
            frequencyHz = hz;
            return true;
        };

    private static readonly KernelRuntimeCompatExports.TryGetFrequency Fails =
        (out ulong frequencyHz) =>
        {
            frequencyHz = 0;
            return false;
        };

    [Fact]
    public void WithoutHostRdtsc_ReportsStopwatchFrequency_NotHardwareTsc()
    {
        // Regression: on Linux/macOS ReadTsc returns the Stopwatch counter, so the reported
        // frequency must be the Stopwatch's, never the CPU's much larger hardware TSC frequency.
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Fact]
    public void WithHostRdtsc_PrefersCalibratedFrequency()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(2_400_000_000UL, frequencyHz);
        Assert.Equal("calibrated-rdtsc", source);
    }

    [Fact]
    public void WithHostRdtsc_FallsBackToCpuid_WhenCalibrationFails()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(3_000_000_000UL, frequencyHz);
        Assert.Equal("cpuid", source);
    }

    [Fact]
    public void WithHostRdtsc_UsesStopwatch_WhenRdtscFrequencyUnknown()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnvOverride_Wins_WhenSane(bool rdtscAvailable)
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable,
            overrideHzText: "1500000000",
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(1_500_000_000UL, frequencyHz);
        Assert.Equal("env", source);
    }

    [Fact]
    public void EnvOverride_BelowMinimum_IsIgnored()
    {
        // 500 kHz is below the sanity floor, so it is dropped; with rdtsc unavailable the
        // hardware-TSC path is gated off and the Stopwatch frequency is used.
        var (frequencyHz, _) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: "500000",
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
    }

    [Fact]
    public void NonPositiveStopwatchFrequency_FallsBackToDefault()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 0);

        Assert.Equal(10_000_000UL, frequencyHz); // DefaultKernelTscFrequency
        Assert.Equal("qpc", source);
    }

    // A time a calendar can express is the time the zone lookup uses, so every
    // ordinary conversion keeps the exact instant the guest asked about.
    [Theory]
    [InlineData(0L)]
    [InlineData(1_800_000_000L)]
    [InlineData(-62135596800L)]
    [InlineData(253402300799L)]
    public void TimesADateCanExpressAreLeftAlone(long seconds)
    {
        Assert.Equal(
            seconds,
            KernelRuntimeCompatExports.ClampToRepresentableUnixSeconds(seconds));
    }

    [Theory]
    [InlineData(26076015555344804L, 253402300799L)]
    [InlineData(long.MaxValue, 253402300799L)]
    [InlineData(-62135596801L, -62135596800L)]
    [InlineData(long.MinValue, -62135596800L)]
    public void TimesNoDateCanExpressSaturate(long seconds, long expected)
    {
        Assert.Equal(
            expected,
            KernelRuntimeCompatExports.ClampToRepresentableUnixSeconds(seconds));
    }

    // Regression (metal_slug_tactics): the guest libc normalises a time by
    // stepping it an hour at a time until local -> utc -> local round-trips.
    // Out past year 9999 every step has to keep resolving to the same zone
    // offset, or the round trip never agrees and the loop never terminates -
    // the game spent millions of calls there and never booted.
    [Fact]
    public void HourStepsPastTheCalendarResolveToOneZoneOffset()
    {
        const long farFuture = 26076015555344804L;
        var clamped = KernelRuntimeCompatExports.ClampToRepresentableUnixSeconds(farFuture);

        Assert.Equal(
            clamped,
            KernelRuntimeCompatExports.ClampToRepresentableUnixSeconds(farFuture - 3600));
        Assert.Equal(
            clamped,
            KernelRuntimeCompatExports.ClampToRepresentableUnixSeconds(farFuture + 3600));
    }

    // The stated reading of an ambiguous local time is the caller's to make, so a
    // conversion that was told "standard time" must not answer with a daylight shift.
    [Theory]
    [InlineData(1_800_000_000L)]  // mid-year, daylight saving active where observed
    [InlineData(1_700_000_000L)]  // late in the year, standard time where observed
    public void StandardTimeRequestNeverReportsADaylightShift(long seconds)
    {
        Assert.Equal(0, KernelRuntimeCompatExports.ResolveLocalZone(seconds, 0).DstSeconds);
    }

    [Fact]
    public void DaylightRequestAddsItsShiftToStandardTime()
    {
        const long midYear = 1_800_000_000L;
        var standard = KernelRuntimeCompatExports.ResolveLocalZone(midYear, 0);
        var daylight = KernelRuntimeCompatExports.ResolveLocalZone(midYear, 1);

        Assert.Equal(
            standard.OffsetSeconds + daylight.DstSeconds,
            daylight.OffsetSeconds);
    }
}

// The guest libc's mktime drives both conversion directions and refuses to accept a
// result until they describe the zone identically; these cover what it reads back.
// Everything here is asserted as an agreement between the two exports rather than
// against absolute offsets, so it holds in any host timezone.
public sealed class KernelTimezoneConversionTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong LocalTimeAddress = MemoryBase + 0x100;
    private const ulong UtcTimeAddress = MemoryBase + 0x120;
    private const ulong TimesecAddress = MemoryBase + 0x140;
    private const ulong DstSecondsAddress = MemoryBase + 0x160;
    private const int TimesecSize = 16;
    private const int WestSecondsOffset = 8;
    private const int DstSecondsOffset = 12;

    // mktime hands its own tm_isdst straight through; -1 is "you decide".
    private const int IsDstUnknown = -1;

    private static (FakeCpuMemory Memory, CpuContext Context) NewGuest()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static byte[] ReadTimesec(FakeCpuMemory memory)
    {
        var timesec = new byte[TimesecSize];
        Assert.True(memory.TryRead(TimesecAddress, timesec));
        return timesec;
    }

    private static void FillTimesecWithJunk(FakeCpuMemory memory)
    {
        Span<byte> junk = stackalloc byte[TimesecSize];
        junk.Fill(0xCC);
        Assert.True(memory.TryWrite(TimesecAddress, junk));
    }

    private static long LocalToUtc(FakeCpuMemory memory, CpuContext context, long local, int isDst)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)local);
        context[CpuRegister.Rsi] = unchecked((ulong)(long)isDst);
        context[CpuRegister.Rdx] = UtcTimeAddress;
        context[CpuRegister.Rcx] = TimesecAddress;
        context[CpuRegister.R8] = DstSecondsAddress;

        Assert.Equal(0, KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context));
        Assert.True(context.TryReadUInt64(UtcTimeAddress, out var utc));
        return unchecked((long)utc);
    }

    private static long UtcToLocal(FakeCpuMemory memory, CpuContext context, long utc)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)utc);
        context[CpuRegister.Rsi] = LocalTimeAddress;
        context[CpuRegister.Rdx] = TimesecAddress;
        context[CpuRegister.Rcx] = 0;

        Assert.Equal(0, KernelRuntimeCompatExports.KernelConvertUtcToLocaltime(context));
        Assert.True(context.TryReadUInt64(LocalTimeAddress, out var local));
        return unchecked((long)local);
    }

    // Regression: this direction used to write two int32s at the front of the
    // structure and never touch the two fields the guest actually reads, leaving
    // them as whatever the caller's stack held.
    [Fact]
    public void LocalToUtcFillsEveryTimesecField()
    {
        var (memory, context) = NewGuest();
        FillTimesecWithJunk(memory);

        LocalToUtc(memory, context, 1_800_000_000L, IsDstUnknown);

        Assert.DoesNotContain((byte)0xCC, ReadTimesec(memory));
    }

    [Theory]
    [InlineData(1_800_000_000L)]          // daylight saving active where observed
    [InlineData(1_700_000_000L)]          // standard time where observed
    [InlineData(26076015555344804L)]      // past year 9999, where the guest's search walks
    public void BothDirectionsDescribeTheZoneIdentically(long local)
    {
        var (memory, context) = NewGuest();

        var utc = LocalToUtc(memory, context, local, IsDstUnknown);
        var fromLocal = ReadTimesec(memory);

        var roundTripped = UtcToLocal(memory, context, utc);
        var fromUtc = ReadTimesec(memory);

        // The comparison mktime makes before it will accept the conversion.
        Assert.Equal(
            BitConverter.ToUInt32(fromLocal, WestSecondsOffset),
            BitConverter.ToUInt32(fromUtc, WestSecondsOffset));
        Assert.Equal(
            BitConverter.ToUInt32(fromLocal, DstSecondsOffset),
            BitConverter.ToUInt32(fromUtc, DstSecondsOffset));
        // ... and the round trip it checks next.
        Assert.Equal(local, roundTripped);
    }

    [Fact]
    public void TimesecCarriesTheUtcInstantForBothDirections()
    {
        var (memory, context) = NewGuest();

        var utc = LocalToUtc(memory, context, 1_800_000_000L, IsDstUnknown);
        Assert.Equal(utc, BitConverter.ToInt64(ReadTimesec(memory), 0));

        UtcToLocal(memory, context, utc);
        Assert.Equal(utc, BitConverter.ToInt64(ReadTimesec(memory), 0));
    }

    // The out-parameter is a plain int slot: mktime's sits in the four bytes
    // directly below the time it round-trips, so a wider write corrupts it.
    [Fact]
    public void DaylightSecondsOutParameterIsThirtyTwoBits()
    {
        var (memory, context) = NewGuest();
        Span<byte> junk = stackalloc byte[8];
        junk.Fill(0xCC);
        Assert.True(memory.TryWrite(DstSecondsAddress, junk));

        LocalToUtc(memory, context, 1_800_000_000L, IsDstUnknown);

        var slot = new byte[8];
        Assert.True(memory.TryRead(DstSecondsAddress, slot));
        Assert.Equal(new byte[] { 0xCC, 0xCC, 0xCC, 0xCC }, slot[4..]);
    }

    [Fact]
    public void ConversionsRejectAMissingOutputPointer()
    {
        var (_, context) = NewGuest();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 0;
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = 0;

        Assert.NotEqual(0, KernelRuntimeCompatExports.KernelConvertUtcToLocaltime(context));
        Assert.NotEqual(0, KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context));
    }
}
