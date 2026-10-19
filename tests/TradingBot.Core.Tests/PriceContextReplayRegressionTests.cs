using System.Globalization;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class PriceContextReplayRegressionTests
{
    private static readonly InstrumentId Instrument = new("ETHUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 2, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BatchCalculationMatchesSequentialPrefixReplayAtEveryCursor()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 12m, 13m, 10m),
            SetupBar(2, 11m, 12m, 9m), SetupBar(3, 14m, 15m, 12m),
            SetupBar(4, 13m, 14m, 11m), SetupBar(5, 16m, 17m, 14m)
        ];
        ScreenerBar[] htf =
        [
            HtfBar(0, 100m, 102m, 98m), HtfBar(1, 98m, 100m, 96m),
            HtfBar(2, 103m, 105m, 101m)
        ];
        PriceVolumeObservation[] trades =
        [Trade(0, 10m, 1m), Trade(1, 12m, 2m), Trade(2, 11m, 3m),
            Trade(3, 14m, 4m), Trade(4, 13m, 5m), Trade(5, 16m, 6m)];
        PriceContextFeatureConfiguration configuration = Configuration(
            htfFastPeriod: 1, htfSlowPeriod: 2, vwapWindow: 3, pocWindow: 3,
            pocBinSize: 1m);

        for (int index = 0; index < setup.Length; index++)
        {
            UtcTimestamp asOf = setup[index].CloseTime;
            CausalEventCursor cursor = new(asOf, 10_000);
            PriceContextFeatureInput batchInput = Input(setup, htf, trades);
            PriceContextFeatureInput prefixInput = Input(
                setup[..(index + 1)],
                htf.Where(bar => bar.CloseTime.Value <= asOf.Value),
                trades.Where(trade => trade.EventTime.Value <= asOf.Value));

            PriceContextFeatureResult batch = PriceContextFeatureCalculator.Calculate(
                batchInput, cursor, configuration);
            PriceContextFeatureResult prefix = PriceContextFeatureCalculator.Calculate(
                prefixInput, cursor, configuration);

            AssertEquivalentFeatureValues(batch, prefix);
        }
    }

    [Fact]
    public void FutureBarsAndTradesCannotChangeEarlierValuesOrConfirmedStates()
    {
        ScreenerBar[] historic =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 8m, 9m, 5m),
            SetupBar(2, 9m, 10m, 7m), SetupBar(3, 12m, 13m, 10m),
            SetupBar(4, 11m, 12m, 9m)
        ];
        ScreenerBar[] htf =
        [HtfBar(0, 100m, 102m, 98m), HtfBar(1, 98m, 100m, 96m)];
        PriceVolumeObservation[] trades = [Trade(0, 10m, 1m), Trade(1, 8m, 2m),
            Trade(2, 9m, 3m)];
        PriceContextFeatureConfiguration configuration = Configuration(
            htfFastPeriod: 1, htfSlowPeriod: 2, vwapWindow: 2, pocWindow: 2,
            pocBinSize: 1m);
        CausalEventCursor cursor = new(historic[^1].CloseTime, 10_000);

        PriceContextFeatureResult baseline = PriceContextFeatureCalculator.Calculate(
            Input(historic, htf, trades), cursor, configuration);
        PriceContextFeatureResult withFuture = PriceContextFeatureCalculator.Calculate(
            Input(
                historic.Concat([SetupBar(8, 900m, 901m, 899m)]),
                htf.Concat([HtfBar(4, 900m, 902m, 898m)]),
                trades.Concat([Trade(8, 900m, 900m)])),
            cursor, configuration);

        AssertEquivalentFeatureValues(baseline, withFuture);
        Assert.Contains("future", withFuture.Quality.Reasons);
    }

    [Fact]
    public void StrictPivotAppearsOnlyAfterRightWindowAndRetainsWindowEvidence()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 8m, 9m, 5m),
            SetupBar(2, 9m, 10m, 7m), SetupBar(3, 10m, 11m, 8m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration();

        PriceContextFeatureResult beforeRight = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), new CausalEventCursor(Time(2), 10_000), configuration);
        Assert.DoesNotContain(beforeRight.ConfirmedPivots.Pivots,
            static pivot => pivot.Kind == PriceContextPivotKind.Low);

        PriceContextFeatureResult afterRight = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), new CausalEventCursor(Time(3), 10_000), configuration);
        PriceContextPivotEvidence low = Assert.Single(afterRight.ConfirmedPivots.Pivots,
            static pivot => pivot.Kind == PriceContextPivotKind.Low);
        Assert.Equal(5m, low.Anchor);
        Assert.Equal(Time(3), low.ConfirmationTime);
        Assert.Equal(2, low.ConfirmationSequence);
        Assert.Equal(3, low.PivotWindow.Count);
        Assert.Equal(setup[2], low.PivotWindow[^1]);
        Assert.Equal(setup[1], low.PivotBar);
    }

    [Fact]
    public void EveryCenterAndWidthChoiceUsesItsExplicitFormula()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 12m, 13m, 11m),
            SetupBar(2, 14m, 15m, 13m), SetupBar(3, 16m, 17m, 15m)
        ];
        PriceContextChannelCenterKind[] centers =
        [PriceContextChannelCenterKind.Ema, PriceContextChannelCenterKind.Rma,
            PriceContextChannelCenterKind.Hlc3Smoothed];
        PriceContextMovingAverageKind[] hlc3Smoothing =
        [PriceContextMovingAverageKind.Ema, PriceContextMovingAverageKind.Rma];

        foreach (PriceContextChannelCenterKind center in centers)
        {
            PriceContextMovingAverageKind? smoothing = center ==
                PriceContextChannelCenterKind.Hlc3Smoothed
                ? hlc3Smoothing[0]
                : null;
            PriceContextFeatureResult atr = PriceContextFeatureCalculator.Calculate(
                Input(setup, [], []), new CausalEventCursor(Time(4), 10_000),
                Configuration(centerKind: center, hlc3SmoothingKind: smoothing,
                    widthKind: PriceContextChannelWidthKind.Atr,
                    channelWidthMultiplier: 2m));

            Assert.Equal(PriceContextFeatureState.Ready, atr.Channel.State);
            Assert.Equal(center == PriceContextChannelCenterKind.Rma ? 14.25m : 15m,
                atr.Channel.Center);
            Assert.Equal(3m, atr.Channel.Atr);
            Assert.Equal(6m, atr.Channel.HalfWidth);
        }

        foreach (PriceContextMovingAverageKind smoothing in hlc3Smoothing)
        {
            PriceContextFeatureResult standardDeviation =
                PriceContextFeatureCalculator.Calculate(
                    Input(setup, [], []), new CausalEventCursor(Time(4), 10_000),
                    Configuration(centerKind: PriceContextChannelCenterKind.Hlc3Smoothed,
                        hlc3SmoothingKind: smoothing,
                        widthKind: PriceContextChannelWidthKind.StandardDeviation,
                        channelWidthMultiplier: 2m));

            Assert.Equal(PriceContextFeatureState.Ready,
                standardDeviation.Channel.State);
            Assert.Equal(smoothing == PriceContextMovingAverageKind.Rma
                ? 14.25m : 15m, standardDeviation.Channel.Center);
            Assert.Equal(1m, standardDeviation.Channel.StandardDeviation);
            Assert.Equal(2m, standardDeviation.Channel.HalfWidth);
            Assert.Equal(4m, standardDeviation.Channel.Width);
            Assert.Null(standardDeviation.Channel.Atr);
        }
    }

    [Fact]
    public void FrozenAtrBufferHonorsEqualityWicksAndRoleReversal()
    {
        ScreenerBar[] initial =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 8m, 9m, 5m),
            SetupBar(2, 9m, 10m, 7m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            levelBufferMultiplier: 0.1m);
        PriceContextFeatureResult confirmed = PriceContextFeatureCalculator.Calculate(
            Input(initial, [], []), new CausalEventCursor(Time(3), 10_000),
            configuration);
        PriceContextStructuralLevel support = Assert.IsType<PriceContextStructuralLevel>(
            confirmed.StructuralLevels.Support);
        Assert.Equal(5m, support.Anchor);
        Assert.Equal(0.4m, support.FrozenBuffer);
        decimal boundary = support.Anchor - support.FrozenBuffer;

        PriceContextFeatureResult equality = PriceContextFeatureCalculator.Calculate(
            Input(initial.Append(SetupBar(3, boundary, 8m, 2m)), [], []),
            new CausalEventCursor(Time(4), 10_000), configuration);
        Assert.Equal(PriceContextLevelRole.Support,
            equality.StructuralLevels.Support!.Role);

        PriceContextFeatureResult wick = PriceContextFeatureCalculator.Calculate(
            Input(initial.Append(SetupBar(3, boundary, 8m, 1m)), [], []),
            new CausalEventCursor(Time(4), 10_000), configuration);
        Assert.Equal(PriceContextLevelRole.Support, wick.StructuralLevels.Support!.Role);

        PriceContextFeatureResult broken = PriceContextFeatureCalculator.Calculate(
            Input(initial.Concat([
                SetupBar(3, boundary, 8m, 2m),
                SetupBar(4, boundary - 0.1m, 8m, 4m)]), [], []),
            new CausalEventCursor(Time(5), 10_000), configuration);
        PriceContextStructuralLevel reversal = Assert.IsType<PriceContextStructuralLevel>(
            broken.StructuralLevels.Resistance);
        Assert.Equal(PriceContextLevelRole.Resistance, reversal.Role);
        Assert.Equal(support.Anchor, reversal.Anchor);
        Assert.Equal(support.FrozenBuffer, reversal.FrozenBuffer);
        Assert.Equal(support.Pivot, reversal.Pivot);
    }

    [Fact]
    public void HtfConfirmationCountsDistinctBarsAndResetsCandidateAndUnknown()
    {
        ScreenerBar[] bull =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
            HtfBar(2, 12m, 13m, 11m), HtfBar(3, 14m, 15m, 13m),
            HtfBar(4, 14m, 15m, 13m), HtfBar(5, 14m, 15m, 13m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration(
            htfFastPeriod: 2, htfSlowPeriod: 3, htfConfirmationCount: 3);

        PriceContextFeatureResult confirmed = PriceContextFeatureCalculator.Calculate(
            Input([], bull, []), new CausalEventCursor(
                new UtcTimestamp(HtfTime(5).Value.AddMinutes(5)), 10_000),
            configuration);
        Assert.Equal(PriceContextHtfRegime.Bull,
            confirmed.HtfContext.ConfirmedRegime);
        Assert.Equal(0, confirmed.HtfContext.PendingCount);

        PriceContextFeatureResult duplicate = PriceContextFeatureCalculator.Calculate(
            Input([], bull.Concat([bull[^1]]), []),
            new CausalEventCursor(new UtcTimestamp(HtfTime(5).Value.AddMinutes(5)),
                10_000), configuration);
        Assert.Equal(confirmed.HtfContext.ConfirmedRegime,
            duplicate.HtfContext.ConfirmedRegime);
        Assert.Equal(confirmed.HtfContext.PendingCount,
            duplicate.HtfContext.PendingCount);
        Assert.Contains("duplicate", duplicate.HtfContext.Reasons);

        ScreenerBar[] changed = bull[..5]
            .Select((bar, index) => index == 3
                ? HtfBar(3, 14m, 30m, 13m)
                : bar)
            .Append(HtfBar(5, 8m, 21m, 7m))
            .ToArray();
        PriceContextFeatureResult candidateChanged =
            PriceContextFeatureCalculator.Calculate(
                Input([], changed, []),
                new CausalEventCursor(new UtcTimestamp(HtfTime(5).Value.AddMinutes(5)),
                    10_000), configuration);
        Assert.Equal(PriceContextHtfCandidate.Bear,
            candidateChanged.HtfContext.PendingCandidate);
        Assert.Equal(1, candidateChanged.HtfContext.PendingCount);

        PriceContextFeatureResult duplicatePending =
            PriceContextFeatureCalculator.Calculate(
                Input([], changed.Concat([changed[^1]]), []),
                new CausalEventCursor(new UtcTimestamp(HtfTime(5).Value.AddMinutes(5)),
                    10_000), configuration);
        Assert.Equal(PriceContextHtfCandidate.Bear,
            duplicatePending.HtfContext.PendingCandidate);
        Assert.Equal(1, duplicatePending.HtfContext.PendingCount);
        Assert.Contains("duplicate", duplicatePending.HtfContext.Reasons);

        ScreenerBar[] unknownThenBull =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 12m, 13m, 10m),
            HtfBar(2, 14m, 15m, 11m), HtfBar(3, 16m, 17m, 12m),
            HtfBar(4, 18m, 19m, 5m), HtfBar(5, 20m, 21m, 7m),
            HtfBar(6, 22m, 23m, 8m)
        ];
        PriceContextFeatureResult reset = PriceContextFeatureCalculator.Calculate(
            Input([], unknownThenBull, []),
            new CausalEventCursor(new UtcTimestamp(HtfTime(6).Value.AddMinutes(5)),
                10_000),
            Configuration(htfFastPeriod: 1, htfSlowPeriod: 2,
                htfConfirmationCount: 2));
        Assert.Equal(PriceContextHtfCandidate.Bull,
            reset.HtfContext.PendingCandidate);
        Assert.Equal(1, reset.HtfContext.PendingCount);

        PriceContextFeatureResult unknown = PriceContextFeatureCalculator.Calculate(
            Input([], unknownThenBull, []),
            new CausalEventCursor(new UtcTimestamp(HtfTime(5).Value.AddMinutes(5)),
                10_000),
            Configuration(htfFastPeriod: 1, htfSlowPeriod: 2,
                htfConfirmationCount: 2));
        Assert.Equal(PriceContextFeatureState.UnknownWarmup, unknown.HtfContext.State);
        Assert.Null(unknown.HtfContext.PendingCandidate);
        Assert.Equal(0, unknown.HtfContext.PendingCount);
    }

    [Fact]
    public void LateAvailabilityIsOrderedAtAvailabilityAndDoesNotRewriteEarlierBars()
    {
        ScreenerBar[] late =
        [
            SetupBar(0, 10m, 11m, 9m, availabilityDelayMinutes: 4),
            SetupBar(1, 20m, 21m, 19m), SetupBar(2, 30m, 31m, 29m)
        ];
        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(late, [], []), new CausalEventCursor(Time(6), 10_000),
            Configuration(widthKind: PriceContextChannelWidthKind.StandardDeviation,
                channelWidthPeriod: 2, htfFastPeriod: 1, htfSlowPeriod: 2));

        Assert.Equal(PriceContextFeatureState.Ready, result.Channel.State);
        Assert.Equal(15m, result.Channel.Center);
        Assert.Equal(late[0], result.LocalTrend.CurrentBar);
        Assert.Equal(late[0], result.Channel.Provenance[^1]);
    }

    [Fact]
    public void PopulationStandardDeviationUsesDeterministicDecimalSqrt()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 1m, 2m, 0.5m), SetupBar(1, 1m, 2m, 0.5m),
            SetupBar(2, 4m, 5m, 3m)
        ];
        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), new CausalEventCursor(Time(3), 10_000),
            Configuration(widthKind: PriceContextChannelWidthKind.StandardDeviation,
                channelWidthPeriod: 3, channelWidthMultiplier: 1m));

        Assert.Equal(PriceContextFeatureState.Ready, result.Channel.State);
        Assert.Equal(1.4142135623730950488016887242m,
            result.Channel.StandardDeviation);
        Assert.Equal(result.Channel.StandardDeviation, result.Channel.HalfWidth);
    }

    [Fact]
    public void DuplicateAndConflictQualityAreExplicitAndDeterministic()
    {
        ScreenerBar[] clean =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 12m, 13m, 10m),
            SetupBar(2, 14m, 15m, 11m), SetupBar(3, 16m, 17m, 13m)
        ];
        PriceContextFeatureConfiguration configuration = Configuration();
        PriceContextFeatureResult baseline = PriceContextFeatureCalculator.Calculate(
            Input(clean, [], []), new CausalEventCursor(Time(4), 10_000),
            configuration);
        PriceContextFeatureResult duplicate = PriceContextFeatureCalculator.Calculate(
            Input(clean[..2].Concat([clean[1], clean[2], clean[3]]), [], []),
            new CausalEventCursor(Time(4), 10_000), configuration);

        Assert.Equal(baseline.Channel.Center, duplicate.Channel.Center);
        Assert.Equal(baseline.Channel.Width, duplicate.Channel.Width);
        Assert.Contains("duplicate", duplicate.Quality.Reasons);

        ScreenerBar conflictBar = SetupBar(1, 99m, 100m, 98m);
        PriceContextFeatureResult conflict = PriceContextFeatureCalculator.Calculate(
            Input([clean[0], clean[1], conflictBar, clean[2], clean[3]], [], []),
            new CausalEventCursor(Time(4), 10_000), configuration);
        Assert.Equal(PriceContextFeatureState.UnknownQuality, conflict.Channel.State);
        Assert.Equal(PriceContextFeatureState.UnknownQuality,
            conflict.StructuralLevels.State);
        Assert.Contains("conflict", conflict.Quality.Reasons);
    }

    [Fact]
    public void InvalidOversizedWarmupAndOverflowInputsRemainFeatureLocal()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 12m, 13m, 10m),
            SetupBar(2, 14m, 15m, 11m)
        ];
        PriceContextFeatureConfiguration invalid = Configuration(
            pivotLeft: -1, pivotRight: int.MaxValue,
            centerKind: (PriceContextChannelCenterKind)77,
            widthKind: (PriceContextChannelWidthKind)66,
            htfMovingAverageKind: (PriceContextMovingAverageKind)55,
            htfConfirmationCount: 4);
        PriceContextFeatureResult invalidResult = PriceContextFeatureCalculator.Calculate(
            Input(setup, [], []), new CausalEventCursor(Time(3), 10_000), invalid);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            invalidResult.ConfirmedPivots.State);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            invalidResult.Channel.State);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            invalidResult.HtfContext.State);
        Assert.Contains("77", invalidResult.Channel.Reasons);

        PriceContextFeatureResult warmup = PriceContextFeatureCalculator.Calculate(
            Input([setup[0]], [], []), new CausalEventCursor(Time(1), 10_000),
            Configuration());
        Assert.Equal(PriceContextFeatureState.UnknownWarmup,
            warmup.ConfirmedPivots.State);
        Assert.Equal(PriceContextFeatureState.UnknownWarmup, warmup.Channel.State);

        ScreenerBar[] overflowing =
        [
            SetupBar(0, decimal.MaxValue, decimal.MaxValue, 1m),
            SetupBar(1, decimal.MaxValue, decimal.MaxValue, 1m),
            SetupBar(2, decimal.MaxValue, decimal.MaxValue, 1m)
        ];
        PriceContextFeatureResult overflow = PriceContextFeatureCalculator.Calculate(
            Input(overflowing, [], []), new CausalEventCursor(Time(3), 10_000),
            Configuration());
        Assert.Equal(PriceContextFeatureState.UnknownOverflow,
            overflow.Channel.State);
    }

    [Fact]
    public void PermutationCultureAndJsonRoundTripRemainStable()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m), SetupBar(1, 12m, 13m, 10m),
            SetupBar(2, 11m, 12m, 9m), SetupBar(3, 14m, 15m, 12m)
        ];
        ScreenerBar[] htf =
        [HtfBar(0, 100m, 102m, 98m), HtfBar(1, 98m, 100m, 96m),
            HtfBar(2, 103m, 105m, 101m)];
        PriceVolumeObservation[] trades =
        [Trade(0, 10m, 1m), Trade(1, 12m, 2m), Trade(2, 11m, 3m)];
        PriceContextFeatureConfiguration configuration = Configuration(
            vwapWindow: 3, pocWindow: 3, pocBinSize: 1m,
            htfFastPeriod: 1, htfSlowPeriod: 2);
        CausalEventCursor cursor = new(Time(4), 10_000);

        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            PriceContextFeatureResult first = PriceContextFeatureCalculator.Calculate(
                Input(setup, htf, trades), cursor, configuration);
            PriceContextFeatureResult permuted = PriceContextFeatureCalculator.Calculate(
                Input(setup.Reverse(), htf.Reverse(), trades.Reverse()), cursor,
                configuration);
            string firstJson = CoreJsonSerializer.Serialize(first);
            Assert.Equal(firstJson, CoreJsonSerializer.Serialize(permuted));
            Assert.Equal(firstJson, CoreJsonSerializer.Serialize(
                CoreJsonSerializer.Deserialize<PriceContextFeatureResult>(firstJson)));
            Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<
                PriceContextFeatureResult>(firstJson[..^1]
                    + ",\"unexpected\":true}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void AssertEquivalentFeatureValues(PriceContextFeatureResult expected,
        PriceContextFeatureResult actual)
    {
        Assert.Equal(expected.ConfirmedPivots.State, actual.ConfirmedPivots.State);
        Assert.Equal(expected.ConfirmedPivots.Pivots, actual.ConfirmedPivots.Pivots);
        Assert.Equal(expected.StructuralLevels.State, actual.StructuralLevels.State);
        Assert.Equal(expected.StructuralLevels.Support, actual.StructuralLevels.Support);
        Assert.Equal(expected.StructuralLevels.Resistance, actual.StructuralLevels.Resistance);
        Assert.Equal(expected.Channel.State, actual.Channel.State);
        Assert.Equal(expected.Channel.Center, actual.Channel.Center);
        Assert.Equal(expected.Channel.PriorCenter, actual.Channel.PriorCenter);
        Assert.Equal(expected.Channel.Width, actual.Channel.Width);
        Assert.Equal(expected.Channel.Upper, actual.Channel.Upper);
        Assert.Equal(expected.Channel.Lower, actual.Channel.Lower);
        Assert.Equal(expected.LocalTrend.State, actual.LocalTrend.State);
        Assert.Equal(expected.LocalTrend.Direction, actual.LocalTrend.Direction);
        Assert.Equal(expected.LocalTrend.CurrentCenter, actual.LocalTrend.CurrentCenter);
        Assert.Equal(expected.LocalTrend.PriorCenter, actual.LocalTrend.PriorCenter);
        Assert.Equal(expected.HtfContext.State, actual.HtfContext.State);
        Assert.Equal(expected.HtfContext.PendingCandidate,
            actual.HtfContext.PendingCandidate);
        Assert.Equal(expected.HtfContext.PendingCount, actual.HtfContext.PendingCount);
        Assert.Equal(expected.HtfContext.ConfirmedRegime,
            actual.HtfContext.ConfirmedRegime);
        Assert.Equal(expected.Vwap.State, actual.Vwap.State);
        Assert.Equal(expected.Vwap.Value, actual.Vwap.Value);
        Assert.Equal(expected.Poc.State, actual.Poc.State);
        Assert.Equal(expected.Poc.Value, actual.Poc.Value);
    }

    private static PriceContextFeatureConfiguration Configuration(
        int? pivotLeft = 1, int? pivotRight = 1, int? atrPeriod = 2,
        PriceContextAtrMode atrMode = PriceContextAtrMode.Sma,
        PriceContextChannelCenterKind centerKind = PriceContextChannelCenterKind.Ema,
        int? centerPeriod = 2,
        PriceContextMovingAverageKind? hlc3SmoothingKind = null,
        PriceContextChannelWidthKind widthKind = PriceContextChannelWidthKind.Atr,
        int? channelWidthPeriod = 2, decimal? channelWidthMultiplier = 1m,
        decimal? levelBufferMultiplier = 0.1m, int? htfFastPeriod = 2,
        int? htfSlowPeriod = 3,
        PriceContextMovingAverageKind htfMovingAverageKind =
            PriceContextMovingAverageKind.Ema,
        int? htfConfirmationCount = 2, int? vwapWindow = null,
        int? pocWindow = null, decimal? pocBinSize = null) => new(
            "1m", "5m", new PriceContextFeatureVersion("price-context-v2"),
            pivotLeft, pivotRight, atrPeriod, atrMode, centerKind, centerPeriod,
            hlc3SmoothingKind, widthKind, channelWidthPeriod, channelWidthMultiplier,
            levelBufferMultiplier, htfFastPeriod, htfSlowPeriod, htfMovingAverageKind,
            htfConfirmationCount, vwapWindow, pocWindow, pocBinSize);

    private static PriceContextFeatureInput Input(IEnumerable<ScreenerBar> setup,
        IEnumerable<ScreenerBar> htf, IEnumerable<PriceVolumeObservation> trades) => new(
        "replay-feed", Instrument, "1m", Time(0), setup, "5m", HtfTime(0), htf,
        trades);

    private static ScreenerBar SetupBar(int index, decimal close, decimal high,
        decimal low, int availabilityDelayMinutes = 0) => Bar(
            Time(index).Value, $"setup-{index}-{close}-{high}-{low}", close, high, low,
            index, availabilityDelayMinutes);

    private static ScreenerBar HtfBar(int index, decimal close, decimal high,
        decimal low, int availabilityDelayMinutes = 0) => Bar(
            HtfTime(index).Value, $"htf-{index}-{close}-{high}-{low}", close, high, low,
            100 + index, availabilityDelayMinutes);

    private static ScreenerBar Bar(DateTimeOffset eventTime, string eventId,
        decimal close, decimal high, decimal low, long stableSequence,
        int availabilityDelayMinutes)
    {
        DateTimeOffset closeTime = eventTime.AddMinutes(1);
        if (eventId.StartsWith("htf-", StringComparison.Ordinal))
        {
            closeTime = eventTime.AddMinutes(5);
        }

        Bar bar = new(eventId, Instrument, new UtcTimestamp(eventTime),
            new UtcTimestamp(eventTime), new UtcTimestamp(closeTime),
            new Price(Math.Max(low, close - 1m)), new Price(high), new Price(low),
            new Price(close), new Quantity(10m));
        return new ScreenerBar("replay-feed", bar, 10m,
            new UtcTimestamp(eventTime.AddMinutes(-1)),
            new UtcTimestamp(eventTime.AddMinutes(availabilityDelayMinutes)),
            stableSequence);
    }

    private static PriceVolumeObservation Trade(int index, decimal price,
        decimal volume) => new("replay-feed", Instrument, Time(index),
            new UtcTimestamp(Time(index).Value.AddMinutes(-1)),
            Time(index), 200 + index, new Price(price), new Quantity(volume));

    private static UtcTimestamp Time(int index) =>
        new(BaseTime.AddMinutes(index));

    private static UtcTimestamp HtfTime(int index) =>
        new(BaseTime.AddMinutes(index * 5));
}
