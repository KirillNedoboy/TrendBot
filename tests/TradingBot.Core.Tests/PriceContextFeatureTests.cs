using System.Reflection;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class PriceContextFeatureTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InvalidAndUnsupportedConfigurationReturnsFeatureLocalUnknownWithoutThrowing()
    {
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: -1, pivotRight: int.MaxValue, atrPeriod: 0,
            centerKind: (PriceContextChannelCenterKind)99,
            widthKind: (PriceContextChannelWidthKind)77,
            htfMovingAverageKind: (PriceContextMovingAverageKind)66,
            htfConfirmationCount: 4);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input([Bar(0, 10m, 11m, 9m, 10m)], [], []), Cursor(1, 0), configuration);

        Assert.Equal("price-context-v2", result.Configuration.AlgorithmVersion!.Value);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            result.ConfirmedPivots.State);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration, result.Channel.State);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration, result.LocalTrend.State);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration, result.HtfContext.State);
        Assert.Contains("-1", result.ConfirmedPivots.Reasons);
        Assert.Contains("77", result.Channel.Reasons);
        Assert.All(result.ConfirmedPivots.Pivots, static pivot => Assert.NotNull(pivot));
        Assert.True(result.HtfContext.PendingCount >= 0);
    }

    [Fact]
    public void PivotIdentificationRemainsReadyWhenAtrBufferConfigurationIsMissing()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 8m, 9m, 5m, 10m),
            Bar(2, 9m, 10m, 7m, 10m),
            Bar(3, 10m, 11m, 8m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: null, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);

        Assert.Equal(PriceContextFeatureState.Ready, result.ConfirmedPivots.State);
        PriceContextPivotEvidence low = Assert.Single(result.ConfirmedPivots.Pivots,
            pivot => pivot.Kind == PriceContextPivotKind.Low);
        Assert.Null(low.AtrAtConfirmation);
        Assert.Null(low.FrozenBuffer);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            result.StructuralLevels.State);
        Assert.Null(result.StructuralLevels.Support);
    }

    [Fact]
    public void ChannelUsesAtrWidthAndDoesNotUseDonchianRange()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 20m, 0.1m, 10m),
            Bar(1, 11m, 12m, 10m, 10m),
            Bar(2, 12m, 13m, 11m, 10m),
            Bar(3, 13m, 14m, 12m, 10m),
            Bar(4, 14m, 15m, 13m, 10m)
        ];
        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(5, 4), Configuration(
                pivotLeft: 1, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
                channelWidthPeriod: 2, channelWidthMultiplier: 2m,
                htfFastPeriod: 1, htfSlowPeriod: 2));

        Assert.Equal(PriceContextFeatureState.Ready, result.Channel.State);
        Assert.NotEqual(result.Channel.Width, result.Channel.DonchianWidth);
        Assert.Equal(result.Channel.HalfWidth * 2m, result.Channel.Width);
        Assert.True(result.Channel.HalfWidth < result.Channel.DonchianWidth);
    }

    [Fact]
    public void Hlc3SmoothingAndPopulationStandardDeviationAreExplicit()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 12m, 8m, 10m),
            Bar(1, 12m, 14m, 10m, 10m),
            Bar(2, 14m, 16m, 12m, 10m),
            Bar(3, 16m, 18m, 14m, 10m)
        ];
        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), Configuration(
                centerKind: PriceContextChannelCenterKind.Hlc3Smoothed,
                centerPeriod: 2, hlc3SmoothingKind: PriceContextMovingAverageKind.Rma,
                widthKind: PriceContextChannelWidthKind.StandardDeviation,
                channelWidthPeriod: 2, channelWidthMultiplier: 2m,
                atrPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2));

        Assert.Equal(PriceContextFeatureState.Ready, result.Channel.State);
        Assert.Equal(14.25m, result.Channel.Center);
        Assert.Equal(12.5m, result.Channel.PriorCenter);
        Assert.Equal(1m, result.Channel.StandardDeviation);
        Assert.Equal(2m, result.Channel.HalfWidth);
        Assert.Equal(4m, result.Channel.Width);
        Assert.Null(result.Channel.Atr);
    }

    [Fact]
    public void StandardDeviationChannelDoesNotDependOnUnusedAtrSmoothingEnum()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 12m, 8m, 10m), Bar(1, 12m, 14m, 10m, 10m),
            Bar(2, 14m, 16m, 12m, 10m), Bar(3, 16m, 18m, 14m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            atrMode: (PriceContextAtrMode)99,
            hlc3SmoothingKind: (PriceContextMovingAverageKind)88,
            widthKind: PriceContextChannelWidthKind.StandardDeviation,
            centerPeriod: 2, channelWidthPeriod: 2,
            htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);

        Assert.Equal(PriceContextFeatureState.Ready, result.Channel.State);
        Assert.Equal(1m, result.Channel.StandardDeviation);
    }

    [Fact]
    public void ConfirmedPivotRequiresStrictNeighborsAndDelayedAvailability()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 8m, 9m, 5m, 10m),
            Bar(2, 9m, 10m, 7m, 10m),
            Bar(3, 10m, 11m, 8m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult beforeRight = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(2, 1), configuration);
        Assert.DoesNotContain(beforeRight.ConfirmedPivots.Pivots,
            pivot => pivot.Kind == PriceContextPivotKind.Low);

        PriceContextFeatureResult afterRight = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);
        PriceContextPivotEvidence low = Assert.Single(afterRight.ConfirmedPivots.Pivots,
            pivot => pivot.Kind == PriceContextPivotKind.Low);
        Assert.Equal(Time(2).Value.AddMinutes(1), low.ClosedBarTime.Value);
        Assert.Equal(Time(3), low.ConfirmationTime);
        Assert.Equal(2, low.ConfirmationSequence);
        Assert.Equal(1, low.PivotLeft);
        Assert.Equal(1, low.PivotRight);

        ScreenerBar[] plateau =
        [Bar(0, 10m, 11m, 9m, 10m), Bar(1, 8m, 9m, 5m, 10m),
            Bar(2, 9m, 10m, 5m, 10m), Bar(3, 10m, 11m, 8m, 10m)];
        PriceContextFeatureResult plateauResult = PriceContextFeatureCalculator.Calculate(
            Input(plateau, [], []), Cursor(4, 3), configuration);
        Assert.DoesNotContain(plateauResult.ConfirmedPivots.Pivots,
            pivot => pivot.Kind == PriceContextPivotKind.Low);
    }

    [Fact]
    public void PivotMustHaveTheConfiguredNumberOfLeftAndRightNeighbors()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 11m, 1m, 10m), Bar(1, 10m, 11m, 2m, 10m),
            Bar(2, 10m, 11m, 3m, 10m), Bar(3, 10m, 11m, 4m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 2, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);

        Assert.DoesNotContain(result.ConfirmedPivots.Pivots,
            pivot => pivot.Kind == PriceContextPivotKind.Low);
    }

    [Fact]
    public void StructuralLevelIsUnknownWarmupWhenAtrWasUnavailableAtPivotConfirmation()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 11m, 9m, 10m), Bar(1, 8m, 9m, 5m, 10m),
            Bar(2, 9m, 10m, 7m, 10m), Bar(3, 10m, 11m, 8m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 5, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);

        Assert.Equal(PriceContextFeatureState.UnknownWarmup, result.StructuralLevels.State);
        Assert.Null(result.StructuralLevels.Support);
    }

    [Fact]
    public void ConfirmedPivotsRemainAvailableWhenLevelBufferConfigurationIsMissing()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 11m, 9m, 10m), Bar(1, 8m, 9m, 5m, 10m),
            Bar(2, 9m, 10m, 7m, 10m), Bar(3, 10m, 11m, 8m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: null, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);

        Assert.Equal(PriceContextFeatureState.Ready, result.ConfirmedPivots.State);
        Assert.Contains(result.ConfirmedPivots.Pivots,
            pivot => pivot.Kind == PriceContextPivotKind.Low);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            result.StructuralLevels.State);
    }

    [Fact]
    public void LateBarIsAppendedAtAvailabilityInsteadOfReorderedIntoThePast()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 11m, 9m, 10m, availabilityOffsetMinutes: 5),
            Bar(1, 20m, 21m, 19m, 10m),
            Bar(2, 30m, 31m, 29m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
            widthKind: PriceContextChannelWidthKind.StandardDeviation,
            channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(Time(6), 2), configuration);

        Assert.Equal(PriceContextFeatureState.Ready, result.Channel.State);
        Assert.Equal(15m, result.Channel.Center);
        Assert.Equal(result.LocalTrend.CurrentBar,
            result.Channel.Provenance[^1]);
    }

    [Fact]
    public void BreaksUseCloseOnlyStrictBoundaryAndFreezeAtrBufferAcrossRoleReversal()
    {
        ScreenerBar[] setup =
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 8m, 9m, 5m, 10m),
            Bar(2, 9m, 10m, 7m, 10m),
            Bar(3, 8m, 20m, 7m, 10m),
            Bar(4, 8m, 10m, 6m, 10m),
            Bar(5, 8m, 8m, 4m, 10m),
            Bar(6, 8m, 9m, 7m, 10m),
            Bar(7, 8m, 9m, 7m, 10m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 2, levelBufferMultiplier: 0.1m,
            centerPeriod: 2, channelWidthPeriod: 2, htfFastPeriod: 1,
            htfSlowPeriod: 2);

        PriceContextFeatureResult equality = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);
        PriceContextStructuralLevel support = Assert.IsType<PriceContextStructuralLevel>(
            equality.StructuralLevels.Support);
        decimal boundary = support.Anchor - support.FrozenBuffer;
        Assert.Equal(PriceContextLevelRole.Support, support.Role);

        ScreenerBar wickOnly = Bar(4, boundary + 1m, boundary + 4m, boundary - 4m, 10m);
        PriceContextFeatureResult wickResult = PriceContextFeatureCalculator.Calculate(
            Input(setup[..4].Append(wickOnly), [], []), Cursor(5, 4), configuration);
        Assert.Equal(PriceContextLevelRole.Support, wickResult.StructuralLevels.Support!.Role);

        ScreenerBar strictBreak = Bar(5, boundary - 1m, boundary + 3m, boundary - 3m, 10m);
        PriceContextFeatureResult breakResult = PriceContextFeatureCalculator.Calculate(
            Input(setup[..5].Append(strictBreak), [], []), Cursor(6, 5), configuration);
        PriceContextStructuralLevel reversed = Assert.IsType<PriceContextStructuralLevel>(
            breakResult.StructuralLevels.Resistance);
        Assert.Equal(PriceContextLevelRole.Resistance, reversed.Role);
        Assert.Equal(support.Anchor, reversed.Anchor);
        Assert.Equal(support.FrozenBuffer, reversed.FrozenBuffer);
        Assert.Equal(support.Pivot, reversed.Pivot);
    }

    [Fact]
    public void LocalTrendUsesCurrentAndPriorCenterAndCurrentClose()
    {
        ScreenerBar[] setup =
        [Bar(0, 10m, 11m, 9m, 10m), Bar(1, 12m, 13m, 11m, 10m),
            Bar(2, 14m, 15m, 13m, 10m), Bar(3, 16m, 17m, 15m, 10m)];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), Cursor(4, 3), configuration);
        Assert.Equal(PriceContextTrendDirection.Bull, result.LocalTrend.Direction);
        Assert.True(result.LocalTrend.CurrentCenter > result.LocalTrend.PriorCenter);

        ScreenerBar belowCenter = Bar(3, 13m, 17m, 9m, 10m);
        PriceContextFeatureResult neutral = PriceContextFeatureCalculator.Calculate(
            Input(setup[..3].Append(belowCenter), [], []), Cursor(4, 3), configuration);
        Assert.Equal(PriceContextTrendDirection.Neutral, neutral.LocalTrend.Direction);
    }

    [Fact]
    public void HtfConfirmationCountsDistinctClosesAndResetsOnChangeOrUnknown()
    {
        ScreenerBar[] htf =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
            HtfBar(2, 14m, 15m, 13m), HtfBar(3, 16m, 17m, 15m),
            HtfBar(4, 18m, 19m, 17m), HtfBar(5, 20m, 21m, 19m)
        ];
        ScreenerBar[] setup = [Bar(0, 10m, 11m, 9m, 10m), Bar(1, 11m, 12m, 10m, 10m),
            Bar(2, 12m, 13m, 11m, 10m)];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 2, htfSlowPeriod: 3,
            htfConfirmationCount: 2);

        PriceContextFeatureResult first = PriceContextFeatureCalculator.Calculate(
            Input(setup, htf, []), Cursor(Time(30), 5), configuration);
        Assert.Equal(PriceContextHtfRegime.Bull, first.HtfContext.ConfirmedRegime);
        Assert.Equal(0, first.HtfContext.PendingCount);

        PriceContextFeatureResult duplicate = PriceContextFeatureCalculator.Calculate(
            Input(setup, htf.Append(htf[^1]), []), Cursor(Time(30), 5), configuration);
        Assert.Equal(first.HtfContext.ConfirmedRegime, duplicate.HtfContext.ConfirmedRegime);
        Assert.Equal(first.HtfContext.PendingCount, duplicate.HtfContext.PendingCount);
        Assert.Contains("duplicate", duplicate.HtfContext.Reasons);

        ScreenerBar[] change = htf[..5]
            .Select((bar, index) => index == 3
                ? HtfBar(3, 16m, 30m, 15m)
                : bar)
            .Append(HtfBar(5, 8m, 21m, 7m))
            .ToArray();
        PriceContextFeatureResult changed = PriceContextFeatureCalculator.Calculate(
            Input(setup, change, []), Cursor(Time(30), 5), configuration);
        Assert.Equal(PriceContextHtfCandidate.Bear, changed.HtfContext.PendingCandidate);
        Assert.Equal(1, changed.HtfContext.PendingCount);
    }

    [Fact]
    public void HtfConfirmationCountsDistinctBarsEvenWhenClosePriceRepeats()
    {
        ScreenerBar[] htf =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
            HtfBar(2, 14m, 15m, 13m), HtfBar(3, 14m, 15m, 13m),
            HtfBar(4, 14m, 15m, 13m), HtfBar(5, 14m, 15m, 13m)
        ];
        ScreenerBar[] setup = [Bar(0, 10m, 11m, 9m, 10m), Bar(1, 11m, 12m, 10m, 10m),
            Bar(2, 12m, 13m, 11m, 10m)];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
            channelWidthPeriod: 2, htfFastPeriod: 2, htfSlowPeriod: 3,
            htfConfirmationCount: 3);

        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, htf, []), Cursor(Time(30), 5), configuration);

        Assert.Equal(PriceContextHtfRegime.Bull, result.HtfContext.ConfirmedRegime);
        Assert.DoesNotContain("duplicate", result.HtfContext.Reasons);
    }

    [Fact]
    public void ExactPriceVolumeAndV2ReplayArePermutationAndSerializationStable()
    {
        ScreenerBar[] setup =
        [Bar(0, 10m, 11m, 9m, 10m), Bar(1, 12m, 13m, 10m, 20m),
            Bar(2, 14m, 15m, 11m, 30m), Bar(3, 13m, 16m, 12m, 40m),
            Bar(4, 18m, 19m, 15m, 50m)];
        PriceVolumeObservation[] trades =
        [PriceVolume(0, 10m, 1m), PriceVolume(1, 12m, 2m),
            PriceVolume(2, 14m, 3m), PriceVolume(3, 13m, 4m), PriceVolume(4, 18m, 5m)];
        PriceContextFeatureConfiguration configuration = Configuration(
            pivotLeft: 1, pivotRight: 1, atrPeriod: 2, centerPeriod: 2,
            channelWidthPeriod: 2, vwapWindow: 3, pocWindow: 3, pocBinSize: 1m,
            htfFastPeriod: 1, htfSlowPeriod: 2);
        CausalEventCursor cursor = Cursor(5, 4);

        PriceContextFeatureResult first = PriceContextFeatureCalculator.Calculate(
            Input(setup, setup, trades), cursor, configuration);
        PriceContextFeatureResult permuted = PriceContextFeatureCalculator.Calculate(
            Input(setup.Reverse(), setup.Reverse(), trades.Reverse()), cursor, configuration);

        Assert.Equal(first, permuted);
        Assert.Equal(184m / 12m, first.Vwap.Value);
        string json = CoreJsonSerializer.Serialize(first);
        Assert.Equal(json, CoreJsonSerializer.Serialize(
            CoreJsonSerializer.Deserialize<PriceContextFeatureResult>(json)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<PriceContextFeatureResult>(
            json[..^1] + ",\"unexpected\":true}"));
    }

    [Fact]
    public void V2ContractsExposeReadOnlyPublicProperties()
    {
        Type[] types =
        [typeof(PriceContextFeatureConfiguration), typeof(PriceContextFeatureInput),
            typeof(PriceContextConfirmedPivotEvidence), typeof(PriceContextConfirmedPivots),
            typeof(PriceContextStructuralLevel), typeof(PriceContextStructuralLevels),
            typeof(PriceContextChannel), typeof(PriceContextLocalTrend),
            typeof(PriceContextHtfContext), typeof(PriceContextFeatureResult)];
        Assert.All(types, static type => Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.Null(property.SetMethod)));
    }

    private static PriceContextFeatureConfiguration Configuration(
        string setupTimeframe = "1m", string htfTimeframe = "5m", int? pivotLeft = 1,
        int? pivotRight = 1, int? atrPeriod = 2,
        PriceContextAtrMode atrMode = PriceContextAtrMode.Sma,
        PriceContextChannelCenterKind centerKind = PriceContextChannelCenterKind.Ema,
        int? centerPeriod = 2, PriceContextMovingAverageKind? hlc3SmoothingKind = null,
        PriceContextChannelWidthKind widthKind = PriceContextChannelWidthKind.Atr,
        int? channelWidthPeriod = 2, decimal? channelWidthMultiplier = 1m,
        decimal? levelBufferMultiplier = 1m, int? htfFastPeriod = 2,
        int? htfSlowPeriod = 3,
        PriceContextMovingAverageKind htfMovingAverageKind = PriceContextMovingAverageKind.Ema,
        int? htfConfirmationCount = 2, int? vwapWindow = null, int? pocWindow = null,
        decimal? pocBinSize = null) => new(
            setupTimeframe, htfTimeframe, new PriceContextFeatureVersion("price-context-v2"),
            pivotLeft, pivotRight, atrPeriod, atrMode, centerKind, centerPeriod,
            hlc3SmoothingKind, widthKind, channelWidthPeriod, channelWidthMultiplier,
            levelBufferMultiplier, htfFastPeriod, htfSlowPeriod, htfMovingAverageKind,
            htfConfirmationCount, vwapWindow, pocWindow, pocBinSize);

    private static PriceContextFeatureInput Input(IEnumerable<ScreenerBar> setup,
        IEnumerable<ScreenerBar> htf, IEnumerable<PriceVolumeObservation> trades) => new(
        "feed-a", Instrument, "1m", Time(0), setup, "5m", Time(0), htf, trades);

    private static ScreenerBar Bar(int index, decimal close, decimal high, decimal low,
        decimal volume, int availabilityOffsetMinutes = 0)
    {
        UtcTimestamp eventTime = Time(index);
        Bar bar = new($"bar-{index}-{close}-{high}-{low}", Instrument, eventTime, eventTime,
            Time(index + 1), new Price(Math.Max(low, close - 1m)), new Price(high),
            new Price(low), new Price(close), new Quantity(volume));
        return new ScreenerBar("feed-a", bar, volume,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)),
            new UtcTimestamp(eventTime.Value.AddMinutes(availabilityOffsetMinutes)), index);
    }

    private static ScreenerBar HtfBar(int index, decimal close, decimal high, decimal low)
    {
        UtcTimestamp eventTime = new(BaseTime.AddMinutes(index * 5));
        Bar bar = new($"htf-{index}-{close}", Instrument, eventTime, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(5)),
            new Price(Math.Max(low, close - 1m)), new Price(high), new Price(low),
            new Price(close), new Quantity(10m));
        return new ScreenerBar("feed-a", bar, 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)),
            new UtcTimestamp(eventTime.Value.AddMinutes(5)), index);
    }

    private static PriceVolumeObservation PriceVolume(int index, decimal price,
        decimal volume)
    {
        UtcTimestamp eventTime = Time(index);
        return new PriceVolumeObservation("feed-a", Instrument, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime, index,
            new Price(price), new Quantity(volume));
    }

    private static CausalEventCursor Cursor(int index, long sequence) =>
        new(Time(index), sequence);

    private static CausalEventCursor Cursor(UtcTimestamp asOf, long sequence) =>
        new(asOf, sequence);

    private static UtcTimestamp Time(int index) =>
        new(BaseTime.AddMinutes(index));
}
