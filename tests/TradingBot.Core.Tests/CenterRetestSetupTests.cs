using System.Reflection;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CenterRetestSetupTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] WarmupReasons = ["warmup"];
    private static readonly string[] VwapNotConfiguredReasons = ["vwap-not-configured"];
    private static readonly string[] PocNotConfiguredReasons = ["poc-not-configured"];

    [Fact]
    public void LongCenterRetestBecomesReadyWhenAllSpecifiedConditionsPass()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 9m, 11m, 9m, 10m),
            SetupBar(1, 11m, 13m, 11m, 12m),
            SetupBar(2, 13m, 15m, 13m, 14m),
            SetupBar(3, 15m, 17m, 10m, 16m)
        ];
        ScreenerBar[] htf =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
            HtfBar(2, 12m, 13m, 11m), HtfBar(3, 14m, 15m, 13m),
            HtfBar(4, 14m, 15m, 13m), HtfBar(5, 14m, 15m, 13m)
        ];

        (PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality) =
            CalculateContext(setup, htf, new CausalEventCursor(Time(30), 10_000));

        Assert.Equal(PriceContextFeatureState.Ready, priceContext.Channel.State);
        Assert.Equal(15m, priceContext.Channel.Center);
        Assert.Equal(13m, priceContext.Channel.PriorCenter);
        Assert.Equal(PriceContextTrendDirection.Bull, priceContext.LocalTrend.Direction);
        Assert.Equal(PriceContextHtfRegime.Bull, priceContext.HtfContext.ConfirmedRegime);
        Assert.False(quality.IsReady);

        CenterRetestSetupResult result = Evaluate(priceContext, quality);

        Assert.Equal(CenterRetestSetupResult.CurrentAlgorithmVersion, result.AlgorithmVersion);
        Assert.Equal(CenterRetestSetupState.Ready, result.Long.State);
        Assert.Equal(Direction.Long, result.Long.Direction);
        Assert.True(result.Long.IsReady);
        Assert.True(result.Long.Conditions.AllConditionsPassed);
        Assert.True(result.Long.Conditions.HtfContextMatches);
        Assert.True(result.Long.Conditions.LocalTrendMatches);
        Assert.True(result.Long.Conditions.CenterTouched);
        Assert.True(result.Long.Conditions.OpenHoldsCenter);
        Assert.True(result.Long.Conditions.CloseHoldsCenter);
        Assert.True(result.Long.Conditions.CandleDirectionMatches);
        Assert.False(result.Long.Conditions.ClosedThroughCenter);
        Assert.True(result.Long.Conditions.BarConfirmed);
        Assert.True(result.Long.Conditions.DataQualityPassed);
        Assert.Equal(15m, result.Long.ChannelCenter);
        Assert.Equal(PriceContextTrendDirection.Bull, result.Long.LocalTrendDirection);
        Assert.Equal(PriceContextHtfRegime.Bull, result.Long.ConfirmedHtfRegime);
        Assert.Equal(priceContext.LocalTrend.CurrentBar, result.Long.SignalBar);
        Assert.Equal(CenterRetestSetupState.NoSetup, result.Short.State);
        Assert.Contains("htf-context-not-bear", result.Short.Reasons);
        Assert.True(result.IsReady);
    }

    [Fact]
    public void ShortCenterRetestUsesMirroredRulesAndLocalBias()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 19m, 21m, 19m, 20m),
            SetupBar(1, 17m, 19m, 17m, 18m),
            SetupBar(2, 15m, 17m, 15m, 16m),
            SetupBar(3, 15m, 20m, 13m, 14m)
        ];
        ScreenerBar[] htf =
        [
            HtfBar(0, 20m, 21m, 19m), HtfBar(1, 22m, 23m, 21m),
            HtfBar(2, 18m, 19m, 17m), HtfBar(3, 16m, 17m, 15m),
            HtfBar(4, 16m, 17m, 15m), HtfBar(5, 16m, 17m, 15m)
        ];

        (PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality) =
            CalculateContext(setup, htf, new CausalEventCursor(Time(30), 10_000));

        Assert.Equal(15m, priceContext.Channel.Center);
        Assert.Equal(17m, priceContext.Channel.PriorCenter);
        Assert.Equal(PriceContextTrendDirection.Bear, priceContext.LocalTrend.Direction);
        Assert.Equal(PriceContextHtfRegime.Bear, priceContext.HtfContext.ConfirmedRegime);

        CenterRetestSetupResult result = Evaluate(priceContext, quality);

        Assert.Equal(CenterRetestSetupState.Ready, result.Short.State);
        Assert.Equal(Direction.Short, result.Short.Direction);
        Assert.True(result.Short.Conditions.AllConditionsPassed);
        Assert.True(result.Short.Conditions.CenterTouched);
        Assert.True(result.Short.Conditions.OpenHoldsCenter);
        Assert.True(result.Short.Conditions.CloseHoldsCenter);
        Assert.True(result.Short.Conditions.CandleDirectionMatches);
        Assert.Equal(15m, result.Short.ChannelCenter);
        Assert.Equal(PriceContextTrendDirection.Bear, result.Short.LocalTrendDirection);
        Assert.Equal(PriceContextHtfRegime.Bear, result.Short.ConfirmedHtfRegime);
        Assert.Equal(CenterRetestSetupState.NoSetup, result.Long.State);
        Assert.Contains("htf-context-not-bull", result.Long.Reasons);
        Assert.True(result.IsReady);
    }

    [Fact]
    public void ShortCenterRetestRequiresMirroredLocalBias()
    {
        PriceContextFeatureResult context = ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bull,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 99m, high: 106m, low: 98m, close: 98m);

        CenterRetestSetupResult result = EvaluateManual(context);

        Assert.Equal(CenterRetestSetupState.NoSetup, result.Short.State);
        Assert.Contains("local-trend-not-bear", result.Short.Reasons);
        Assert.True(result.Short.Conditions.HtfContextMatches);
        Assert.True(result.Short.Conditions.CenterTouched);
        Assert.True(result.Short.Conditions.OpenHoldsCenter);
        Assert.True(result.Short.Conditions.CloseHoldsCenter);
        Assert.True(result.Short.Conditions.CandleDirectionMatches);
        Assert.False(result.Short.Conditions.AllConditionsPassed);
    }

    [Fact]
    public void ConditionFailuresReturnNoSetupWithStableReasons()
    {
        PriceContextFeatureResult noTouch = ManualContext(
            open: 101m, high: 103m, low: 101m, close: 102m);
        CenterRetestSetupResult noTouchResult = EvaluateManual(noTouch);
        Assert.Equal(CenterRetestSetupState.NoSetup, noTouchResult.Long.State);
        Assert.False(noTouchResult.Long.Conditions.CenterTouched);
        Assert.Contains("center-not-touched", noTouchResult.Long.Reasons);

        PriceContextFeatureResult openFailure = ManualContext(
            open: 99m, high: 102m, low: 94m, close: 101m);
        CenterRetestSetupResult openFailureResult = EvaluateManual(openFailure);
        Assert.Equal(CenterRetestSetupState.NoSetup, openFailureResult.Long.State);
        Assert.False(openFailureResult.Long.Conditions.OpenHoldsCenter);
        Assert.Contains("open-not-at-or-above-center",
            openFailureResult.Long.Reasons);

        PriceContextFeatureResult candleFailure = ManualContext(
            open: 101m, high: 102m, low: 94m, close: 100m);
        CenterRetestSetupResult candleFailureResult = EvaluateManual(candleFailure);
        Assert.Equal(CenterRetestSetupState.NoSetup, candleFailureResult.Long.State);
        Assert.False(candleFailureResult.Long.Conditions.CandleDirectionMatches);
        Assert.False(candleFailureResult.Long.Conditions.ClosedThroughCenter);
        Assert.Contains("candle-not-bullish", candleFailureResult.Long.Reasons);

        CenterRetestSetupResult inclusiveTouch = EvaluateManual(ManualContext(
            open: 100m, high: 102m, low: 100m, close: 101m));
        Assert.Equal(CenterRetestSetupState.Ready, inclusiveTouch.Long.State);
        Assert.True(inclusiveTouch.Long.Conditions.CenterTouched);

        CenterRetestSetupResult equality = EvaluateManual(ManualContext(
            open: 100m, high: 102m, low: 94m, close: 100m));
        Assert.Equal(CenterRetestSetupState.NoSetup, equality.Long.State);
        Assert.False(equality.Long.Conditions.ClosedThroughCenter);
        Assert.Contains("candle-not-bullish", equality.Long.Reasons);

        PriceContextFeatureResult shortOpenFailure = ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 100m, high: 106m, low: 98m, close: 98m);
        CenterRetestSetupResult shortOpenFailureResult = EvaluateManual(shortOpenFailure);
        Assert.Equal(CenterRetestSetupState.NoSetup,
            shortOpenFailureResult.Short.State);
        Assert.Contains("open-not-at-or-below-center",
            shortOpenFailureResult.Short.Reasons);

        PriceContextFeatureResult shortCandleFailure = ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 98m, high: 106m, low: 98m, close: 99m);
        CenterRetestSetupResult shortCandleFailureResult =
            EvaluateManual(shortCandleFailure);
        Assert.Equal(CenterRetestSetupState.NoSetup,
            shortCandleFailureResult.Short.State);
        Assert.Contains("candle-not-bearish", shortCandleFailureResult.Short.Reasons);

        CenterRetestSetupResult inclusiveShortTouch = EvaluateManual(ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 99m, high: 99m, low: 98m, close: 98m));
        Assert.Equal(CenterRetestSetupState.Ready, inclusiveShortTouch.Short.State);
        Assert.True(inclusiveShortTouch.Short.Conditions.CenterTouched);
    }

    [Fact]
    public void CloseThroughCenterIsInvalidatedWhileEqualityIsNot()
    {
        CenterRetestSetupResult longBreak = EvaluateManual(ManualContext(
            open: 100m, high: 102m, low: 94m, close: 99m));
        Assert.Equal(CenterRetestSetupState.Invalidated, longBreak.Long.State);
        Assert.True(longBreak.Long.IsInvalidated);
        Assert.False(longBreak.Long.Conditions.CloseHoldsCenter);
        Assert.True(longBreak.Long.Conditions.ClosedThroughCenter);
        Assert.Contains("closed-through-center", longBreak.Long.Reasons);
        Assert.DoesNotContain("close-not-at-or-above-center", longBreak.Long.Reasons);

        CenterRetestSetupResult longEquality = EvaluateManual(ManualContext(
            open: 100m, high: 102m, low: 94m, close: 100m));
        Assert.Equal(CenterRetestSetupState.NoSetup, longEquality.Long.State);
        Assert.False(longEquality.Long.Conditions.ClosedThroughCenter);

        CenterRetestSetupResult shortBreak = EvaluateManual(ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 99m, high: 106m, low: 98m, close: 100m));
        Assert.Equal(CenterRetestSetupState.Invalidated, shortBreak.Short.State);
        Assert.True(shortBreak.Short.Conditions.ClosedThroughCenter);
        Assert.Contains("closed-through-center", shortBreak.Short.Reasons);

        CenterRetestSetupResult shortEquality = EvaluateManual(ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 99m, high: 106m, low: 98m, close: 99m));
        Assert.Equal(CenterRetestSetupState.NoSetup, shortEquality.Short.State);
        Assert.False(shortEquality.Short.Conditions.ClosedThroughCenter);
    }

    [Fact]
    public void CenterRetestHasNoMinimumWidthGate()
    {
        PriceContextFeatureResult context = ManualContext(
            lower: 100m, upper: 100m, halfWidth: 0m, width: 0m,
            open: 100m, high: 102m, low: 94m, close: 101m);

        CenterRetestSetupResult result = EvaluateManual(context);

        Assert.Equal(CenterRetestSetupState.Ready, result.Long.State);
        Assert.Equal(100m, result.Long.ChannelCenter);
        Assert.True(result.Long.Conditions.AllConditionsPassed);
    }

    [Fact]
    public void MissingOrUnsupportedConfigurationFailsClosed()
    {
        PriceContextFeatureResult context = ManualContext();
        FeatureQualityEvaluation quality = ManualQuality(context);

        CenterRetestSetupResult missing = CenterRetestSetupEvaluator.Evaluate(
            context, quality, new CenterRetestSetupConfiguration());
        Assert.Equal(CenterRetestSetupState.UnknownConfiguration, missing.Long.State);
        Assert.Equal(CenterRetestSetupState.UnknownConfiguration, missing.Short.State);
        Assert.Contains("configuration-algorithm-version-missing",
            missing.Long.Reasons);

        CenterRetestSetupResult unsupportedVersion = CenterRetestSetupEvaluator.Evaluate(
            context, quality, new CenterRetestSetupConfiguration(
                new CenterRetestSetupVersion("center-retest-v2")));
        Assert.Equal(CenterRetestSetupState.UnknownConfiguration,
            unsupportedVersion.Long.State);
        Assert.Contains("configuration-algorithm-version-unsupported",
            unsupportedVersion.Long.Reasons);

        PriceContextFeatureResult wrongContextVersion = ManualContext(
            priceContextVersion: "price-context-v3");
        CenterRetestSetupResult wrongVersion = EvaluateManual(wrongContextVersion);
        Assert.Equal(CenterRetestSetupState.UnknownConfiguration,
            wrongVersion.Long.State);
        Assert.Contains("price-context-algorithm-version-unsupported",
            wrongVersion.Long.Reasons);
    }

    [Fact]
    public void DataQualityGateRequiresOnlyChannelTrendAndHtfEvidence()
    {
        PriceContextFeatureResult context = ManualContext();
        FeatureQualityEvaluation quality = ManualQuality(context);

        Assert.False(quality.IsReady);
        Assert.Contains(quality.Features, feature =>
            feature.Kind == FeatureQualityFeatureKind.PriceContextVwap && !feature.IsReady);
        Assert.Equal(3, quality.Features.Count(feature =>
            feature.Kind is FeatureQualityFeatureKind.PriceContextChannel
                or FeatureQualityFeatureKind.PriceContextLocalTrend
                or FeatureQualityFeatureKind.PriceContextHtfContext));

        CenterRetestSetupResult result = Evaluate(context, quality);

        Assert.Equal(CenterRetestSetupState.Ready, result.Long.State);
        Assert.True(result.Long.Conditions.DataQualityPassed);
    }

    [Fact]
    public void MissingDuplicateStaleOrMismatchedQualityEvidenceBlocksReadiness()
    {
        PriceContextFeatureResult context = ManualContext();

        AssertUnknownQuality(context, ManualQuality(context, includeChannel: false),
            "quality-evidence-missing:channel");
        AssertUnknownQuality(context, ManualQuality(context, duplicateChannel: true),
            "quality-evidence-duplicate:channel");
        AssertUnknownQuality(context, ManualQuality(context, channelReady: false),
            "quality-evidence-not-ready:channel");
        AssertUnknownQuality(context, ManualQuality(context,
                channelFormula: "price-context-v1"),
            "quality-evidence-formula-mismatch:channel");
        AssertUnknownQuality(context, ManualQuality(context,
                channelOriginalState: "UnknownWarmup"),
            "quality-evidence-state-mismatch:channel");
        AssertUnknownQuality(context, ManualQuality(context,
                channelSeries: FeatureQualityInputSeries.Htf),
            "quality-evidence-series-mismatch:channel");

        FeatureQualityEvaluation wrongCursor = ManualQuality(context,
            cursor: new CausalEventCursor(Time(7), 10));
        Assert.Throws<ArgumentException>(() => Evaluate(context, wrongCursor));
    }

    [Fact]
    public void SignalBarMustBeConfirmedAndEligibleAtTheCausalCursor()
    {
        PriceContextFeatureResult ineligible = ManualContext(availabilityOffsetMinutes: 5);
        CenterRetestSetupResult ineligibleResult = EvaluateManual(ineligible);

        Assert.Equal(CenterRetestSetupState.UnknownInput, ineligibleResult.Long.State);
        Assert.False(ineligibleResult.Long.Conditions.BarConfirmed);
        Assert.Contains("signal-bar-not-eligible", ineligibleResult.Long.Reasons);

        ScreenerBar otherBar = ManualBar(101m, 103m, 99m, 102m);
        PriceContextFeatureResult mismatchedProvenance = ManualContext(
            channelProvenanceBar: otherBar);
        CenterRetestSetupResult mismatchResult = EvaluateManual(mismatchedProvenance);

        Assert.Equal(CenterRetestSetupState.UnknownQuality, mismatchResult.Long.State);
        Assert.False(mismatchResult.Long.Conditions.BarConfirmed);
        Assert.Contains("signal-bar-not-latest-channel-bar",
            mismatchResult.Long.Reasons);
    }

    [Fact]
    public void FutureDataCannotMakeAnEarlierCursorReady()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 9m, 11m, 9m, 10m),
            SetupBar(1, 11m, 13m, 11m, 12m),
            SetupBar(2, 13m, 15m, 13m, 14m),
            SetupBar(3, 15m, 17m, 10m, 16m)
        ];
        ScreenerBar[] htf =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
            HtfBar(2, 12m, 13m, 11m), HtfBar(3, 14m, 15m, 13m),
            HtfBar(4, 14m, 15m, 13m), HtfBar(5, 14m, 15m, 13m)
        ];
        CausalEventCursor earlyCursor = new(Time(3), 10_000);

        (PriceContextFeatureResult earlyWithFuture, FeatureQualityEvaluation earlyQuality) =
            CalculateContext(setup, htf, earlyCursor);
        (PriceContextFeatureResult earlyTruncated, _) =
            CalculateContext(setup[..3], Array.Empty<ScreenerBar>(), earlyCursor);
        (PriceContextFeatureResult late, FeatureQualityEvaluation lateQuality) =
            CalculateContext(setup, htf, new CausalEventCursor(Time(30), 10_000));

        Assert.Contains("future", earlyWithFuture.Quality.Reasons);
        Assert.Equal(earlyTruncated.Channel.Center, earlyWithFuture.Channel.Center);
        Assert.Equal(earlyTruncated.Channel.PriorCenter,
            earlyWithFuture.Channel.PriorCenter);
        Assert.Equal(earlyTruncated.LocalTrend.Direction,
            earlyWithFuture.LocalTrend.Direction);

        CenterRetestSetupResult earlyResult = Evaluate(earlyWithFuture, earlyQuality);
        Assert.Equal(CenterRetestSetupState.UnknownWarmup, earlyResult.Long.State);
        Assert.NotEqual(CenterRetestSetupState.Ready, earlyResult.Short.State);

        CenterRetestSetupResult lateResult = Evaluate(late, lateQuality);
        Assert.Equal(CenterRetestSetupState.Ready, lateResult.Long.State);
    }

    [Fact]
    public void StrictJsonRoundTripAndImmutableEvidenceArePreserved()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 9m, 11m, 9m, 10m),
            SetupBar(1, 11m, 13m, 11m, 12m),
            SetupBar(2, 13m, 15m, 13m, 14m),
            SetupBar(3, 15m, 17m, 10m, 16m)
        ];
        ScreenerBar[] htf =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
            HtfBar(2, 12m, 13m, 11m), HtfBar(3, 14m, 15m, 13m),
            HtfBar(4, 14m, 15m, 13m), HtfBar(5, 14m, 15m, 13m)
        ];
        (PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality) =
            CalculateContext(setup, htf, new CausalEventCursor(Time(30), 10_000));
        CenterRetestSetupResult result = Evaluate(priceContext, quality);

        string json = CoreJsonSerializer.Serialize(result);
        CenterRetestSetupResult? restored =
            CoreJsonSerializer.Deserialize<CenterRetestSetupResult>(json);
        Assert.Equal(result, restored);
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<CenterRetestSetupResult>(
                json[..^1] + ",\"unexpected\":true}"));

        Type[] contracts =
        [
            typeof(CenterRetestSetupVersion),
            typeof(CenterRetestSetupConfiguration),
            typeof(CenterRetestConditionEvidence),
            typeof(CenterRetestSetupObservation),
            typeof(CenterRetestSetupResult)
        ];
        Assert.All(contracts, static type => Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.Null(property.SetMethod)));

        CenterRetestSetupConfiguration configuration = new(
            new CenterRetestSetupVersion(
                CenterRetestSetupResult.CurrentAlgorithmVersion));
        Assert.Throws<ArgumentException>(() => new CenterRetestSetupResult(
            "center-retest-v2", configuration, priceContext, quality,
            result.Long, result.Short));
        Assert.Throws<ArgumentException>(() => new CenterRetestSetupResult(
            CenterRetestSetupResult.CurrentAlgorithmVersion, configuration,
            priceContext, quality, result.Short, result.Long));
        FeatureQualityEvaluation wrongCursor = ManualQuality(priceContext,
            cursor: new CausalEventCursor(Time(40), 100_000));
        Assert.Throws<ArgumentException>(() => new CenterRetestSetupResult(
            CenterRetestSetupResult.CurrentAlgorithmVersion, configuration,
            priceContext, wrongCursor, result.Long, result.Short));
    }

    private static void AssertUnknownQuality(PriceContextFeatureResult context,
        FeatureQualityEvaluation quality, string expectedReason)
    {
        CenterRetestSetupResult result = Evaluate(context, quality);
        Assert.Equal(CenterRetestSetupState.UnknownQuality, result.Long.State);
        Assert.False(result.Long.Conditions.DataQualityPassed);
        Assert.Contains(expectedReason, result.Long.Reasons);
    }

    private static CenterRetestSetupResult EvaluateManual(
        PriceContextFeatureResult context)
    {
        return Evaluate(context, ManualQuality(context));
    }

    private static CenterRetestSetupResult Evaluate(
        PriceContextFeatureResult context, FeatureQualityEvaluation quality,
        string version = "center-retest-v1")
    {
        return CenterRetestSetupEvaluator.Evaluate(context, quality,
            new CenterRetestSetupConfiguration(
                new CenterRetestSetupVersion(version)));
    }

    private static (PriceContextFeatureResult PriceContext, FeatureQualityEvaluation Quality)
        CalculateContext(IEnumerable<ScreenerBar> setup, IEnumerable<ScreenerBar> htf,
            CausalEventCursor cursor)
    {
        PriceContextFeatureInput input = new("feed-a", Instrument, "1m", Time(0), setup,
            "5m", Time(0), htf, Array.Empty<PriceVolumeObservation>());
        PriceContextFeatureResult priceContext = PriceContextFeatureCalculator.Calculate(
            input, cursor, ManualConfiguration());
        FeatureQualityEvaluation quality = FeatureQualityEvaluator.Evaluate(priceContext,
            new FeatureQualityPolicy(
                rollingTtl: null,
                setupTtl: TimeSpan.FromHours(1),
                htfTtl: TimeSpan.FromHours(1),
                priceVolumeTtl: null));
        return (priceContext, quality);
    }

    private static PriceContextFeatureConfiguration ManualConfiguration(
        string version = "price-context-v2")
    {
        return new PriceContextFeatureConfiguration(
            "1m", "5m", new PriceContextFeatureVersion(version),
            1, 1, 2, PriceContextAtrMode.Sma,
            PriceContextChannelCenterKind.Ema, 2, null,
            PriceContextChannelWidthKind.Atr, 2, 1m, 1m,
            2, 3, PriceContextMovingAverageKind.Ema, 3,
            null, null, null);
    }

    private static PriceContextFeatureResult ManualContext(
        decimal center = 100m, decimal priorCenter = 99m,
        decimal lower = 95m, decimal upper = 105m,
        decimal halfWidth = 5m, decimal width = 10m,
        PriceContextTrendDirection trend = PriceContextTrendDirection.Bull,
        PriceContextHtfRegime htfRegime = PriceContextHtfRegime.Bull,
        decimal open = 100m, decimal high = 102m, decimal low = 94m,
        decimal close = 101m,
        int availabilityOffsetMinutes = 0,
        ScreenerBar? channelProvenanceBar = null,
        string priceContextVersion = "price-context-v2")
    {
        ScreenerBar signalBar = ManualBar(open, high, low, close,
            availabilityOffsetMinutes);
        ScreenerBar htfBar = ManualHtfBar();
        CausalEventCursor cursor = new(Time(3), 10);
        PriceContextFeatureInput input = new("feed-a", Instrument, "1m", Time(0),
            new[] { signalBar }, "5m", Time(0), new[] { htfBar },
            Array.Empty<PriceVolumeObservation>());
        PriceContextObservationQuality quality = new(new[] { signalBar },
            new[] { htfBar }, Array.Empty<PriceVolumeObservation>(), Array.Empty<string>());
        PriceContextConfirmedPivots pivots = new(PriceContextFeatureState.UnknownWarmup,
            new ImmutableValueList<PriceContextPivotEvidence>(
                Array.Empty<PriceContextPivotEvidence>()), 1, 1, 3, 1,
            PriceContextFeatureCompleteness.Warmup,
            new ImmutableValueList<string>(WarmupReasons));
        PriceContextStructuralLevels levels = new(PriceContextFeatureState.UnknownWarmup,
            null, null, null, null, 3, 1, PriceContextFeatureCompleteness.Warmup,
            new ImmutableValueList<string>(WarmupReasons));
        PriceContextChannel channel = new(PriceContextFeatureState.Ready, center,
            priorCenter, upper, lower, halfWidth, width, width, null, null, 2, 2,
            PriceContextChannelCenterKind.Ema, PriceContextChannelWidthKind.Atr, null,
            1, 1, PriceContextFeatureCompleteness.Complete,
            new ImmutableValueList<ScreenerBar>(new[] { channelProvenanceBar ?? signalBar }),
            new ImmutableValueList<string>(Array.Empty<string>()));
        PriceContextLocalTrend localTrend = new(PriceContextFeatureState.Ready, trend,
            center, priorCenter, close, signalBar,
            new ImmutableValueList<string>(Array.Empty<string>()));
        PriceContextHtfContext htfContext = new(PriceContextFeatureState.Ready, null, 0,
            htfRegime, 101m, 100m, 100m, 95m, 105m, htfBar,
            new ImmutableValueList<ScreenerBar>(new[] { htfBar }),
            new ImmutableValueList<string>(Array.Empty<string>()));
        PriceContextFeatureValue vwap = new(PriceContextFeatureKind.Vwap,
            PriceContextFeatureState.UnknownConfiguration, null, null, 0, 0,
            PriceContextFeatureCompleteness.Unknown,
            new ImmutableValueList<PriceVolumeObservation>(
                Array.Empty<PriceVolumeObservation>()),
            new ImmutableValueList<string>(VwapNotConfiguredReasons));
        PriceContextFeatureValue poc = new(PriceContextFeatureKind.Poc,
            PriceContextFeatureState.UnknownConfiguration, null, null, 0, 0,
            PriceContextFeatureCompleteness.Unknown,
            new ImmutableValueList<PriceVolumeObservation>(
                Array.Empty<PriceVolumeObservation>()),
            new ImmutableValueList<string>(PocNotConfiguredReasons));
        return new PriceContextFeatureResult(ManualConfiguration(priceContextVersion),
            input, cursor, quality, pivots, levels, channel, localTrend, htfContext,
            vwap, poc);
    }

    private static FeatureQualityEvaluation ManualQuality(
        PriceContextFeatureResult context,
        bool includeChannel = true,
        bool includeLocalTrend = true,
        bool includeHtf = true,
        bool channelReady = true,
        bool localTrendReady = true,
        bool htfReady = true,
        bool duplicateChannel = false,
        string? channelFormula = null,
        string? channelOriginalState = null,
        FeatureQualityInputSeries channelSeries = FeatureQualityInputSeries.Setup,
        CausalEventCursor? cursor = null,
        bool includeUnreadyVwap = true)
    {
        string formula = channelFormula
            ?? context.Configuration.AlgorithmVersion?.Value
            ?? "unknown";
        string channelState = channelOriginalState ?? context.Channel.State.ToString();
        List<FeatureQualityEvidence> features = [];
        if (includeChannel)
        {
            features.Add(Evidence(FeatureQualityFeatureKind.PriceContextChannel,
                channelSeries, formula, channelState, channelReady));
            if (duplicateChannel)
            {
                features.Add(Evidence(FeatureQualityFeatureKind.PriceContextChannel,
                    channelSeries, formula, channelState, channelReady));
            }
        }

        if (includeLocalTrend)
        {
            features.Add(Evidence(FeatureQualityFeatureKind.PriceContextLocalTrend,
                FeatureQualityInputSeries.Setup, formula,
                context.LocalTrend.State.ToString(), localTrendReady));
        }

        if (includeHtf)
        {
            features.Add(Evidence(FeatureQualityFeatureKind.PriceContextHtfContext,
                FeatureQualityInputSeries.Htf, formula,
                context.HtfContext.State.ToString(), htfReady));
        }

        if (includeUnreadyVwap)
        {
            features.Add(Evidence(FeatureQualityFeatureKind.PriceContextVwap,
                FeatureQualityInputSeries.PriceVolume, formula,
                "UnknownConfiguration", isReady: false));
        }

        return new FeatureQualityEvaluation("feature-quality-v1",
            cursor ?? context.Cursor, new FeatureQualityPolicy(), null,
            context.Configuration.AlgorithmVersion?.Value,
            new ImmutableValueList<FeatureQualityEvidence>(features));
    }

    private static FeatureQualityEvidence Evidence(FeatureQualityFeatureKind kind,
        FeatureQualityInputSeries series, string formula, string originalState,
        bool isReady)
    {
        return new FeatureQualityEvidence(kind, series, formula, originalState,
            "Complete", FeatureQualityFreshness.Fresh, FeatureQualityFreshness.Fresh,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromHours(1),
            FeatureQualityCoverage.Complete, 1m, 4, 3, isReady,
            new ImmutableValueList<string>(Array.Empty<string>()));
    }

    private static ScreenerBar SetupBar(int index, decimal open, decimal high,
        decimal low, decimal close, decimal volume = 10m)
    {
        UtcTimestamp eventTime = Time(index);
        Bar bar = new($"setup-{index}-{open}-{high}-{low}-{close}", Instrument,
            eventTime, eventTime, Time(index + 1), new Price(open), new Price(high),
            new Price(low), new Price(close), new Quantity(volume));
        return new ScreenerBar("feed-a", bar, volume,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime, index);
    }

    private static ScreenerBar HtfBar(int index, decimal close, decimal high, decimal low)
    {
        UtcTimestamp eventTime = new(BaseTime.AddMinutes(index * 5));
        Bar bar = new($"htf-{index}-{close}-{high}-{low}", Instrument, eventTime,
            eventTime, new UtcTimestamp(eventTime.Value.AddMinutes(5)),
            new Price(Math.Max(low, close - 1m)), new Price(high), new Price(low),
            new Price(close), new Quantity(10m));
        return new ScreenerBar("feed-a", bar, 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)),
            new UtcTimestamp(eventTime.Value.AddMinutes(5)), index);
    }

    private static ScreenerBar ManualBar(decimal open, decimal high, decimal low,
        decimal close, int availabilityOffsetMinutes = 0)
    {
        UtcTimestamp eventTime = Time(1);
        Bar bar = new($"manual-{open}-{high}-{low}-{close}", Instrument, eventTime,
            eventTime, Time(2), new Price(open), new Price(high), new Price(low),
            new Price(close), new Quantity(10m));
        return new ScreenerBar("feed-a", bar, 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)),
            new UtcTimestamp(eventTime.Value.AddMinutes(availabilityOffsetMinutes)), 1);
    }

    private static ScreenerBar ManualHtfBar()
    {
        UtcTimestamp eventTime = Time(0);
        Bar bar = new("manual-htf", Instrument, eventTime, eventTime, Time(1),
            new Price(99m), new Price(101m), new Price(98m), new Price(100m),
            new Quantity(10m));
        return new ScreenerBar("feed-a", bar, 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)),
            new UtcTimestamp(eventTime.Value.AddMinutes(1)), 0);
    }

    private static UtcTimestamp Time(int offset)
    {
        return new UtcTimestamp(BaseTime.AddMinutes(offset));
    }
}
