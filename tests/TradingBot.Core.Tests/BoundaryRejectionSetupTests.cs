using System.Reflection;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class BoundaryRejectionSetupTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] WarmupReasons = ["warmup"];
    private static readonly string[] VwapNotConfiguredReasons = ["vwap-not-configured"];
    private static readonly string[] PocNotConfiguredReasons = ["poc-not-configured"];

    [Fact]
    public void LongBoundaryRejectionBecomesReadyOnlyWhenAllSpecifiedConditionsPass()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m),
            SetupBar(1, 12m, 13m, 11m),
            SetupBar(2, 14m, 15m, 13m),
            SetupBar(3, 16m, 17m, 10m)
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
        Assert.Equal(10m, priceContext.Channel.Lower);
        Assert.Equal(20m, priceContext.Channel.Upper);
        Assert.Equal(10m, priceContext.Channel.Width);
        Assert.Equal(PriceContextTrendDirection.Bull, priceContext.LocalTrend.Direction);
        Assert.Equal(PriceContextHtfRegime.Bull, priceContext.HtfContext.ConfirmedRegime);
        Assert.False(quality.IsReady);

        BoundaryRejectionSetupResult result = Evaluate(priceContext, quality, 10m);

        Assert.Equal(BoundaryRejectionSetupResult.CurrentAlgorithmVersion, result.AlgorithmVersion);
        Assert.Equal(BoundaryRejectionSetupState.Ready, result.Long.State);
        Assert.Equal(Direction.Long, result.Long.Direction);
        Assert.True(result.Long.IsReady);
        Assert.True(result.Long.Conditions.AllConditionsPassed);
        Assert.True(result.Long.Conditions.HtfContextMatches);
        Assert.True(result.Long.Conditions.LocalTrendMatches);
        Assert.True(result.Long.Conditions.BoundaryTouched);
        Assert.True(result.Long.Conditions.CloseReclaimed);
        Assert.False(result.Long.Conditions.ClosedThroughBoundary);
        Assert.True(result.Long.Conditions.BarConfirmed);
        Assert.True(result.Long.Conditions.MinimumChannelWidthPassed);
        Assert.True(result.Long.Conditions.DataQualityPassed);
        Assert.Equal(10m, result.Long.LowerBand);
        Assert.Equal(10m, result.Long.ChannelWidth);
        Assert.Equal(10m, result.Long.MinimumChannelWidth);
        Assert.Equal(PriceContextTrendDirection.Bull, result.Long.LocalTrendDirection);
        Assert.Equal(PriceContextHtfRegime.Bull, result.Long.ConfirmedHtfRegime);
        Assert.Equal(priceContext.LocalTrend.CurrentBar, result.Long.SignalBar);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup, result.Short.State);
        Assert.Contains("htf-context-not-bear", result.Short.Reasons);
        Assert.True(result.IsReady);
    }

    [Fact]
    public void ShortBoundaryRejectionUsesMirroredRules()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 20m, 21m, 19m),
            SetupBar(1, 18m, 19m, 17m),
            SetupBar(2, 16m, 17m, 15m),
            SetupBar(3, 14m, 20m, 13m)
        ];
        ScreenerBar[] htf =
        [
            HtfBar(0, 20m, 21m, 19m), HtfBar(1, 22m, 23m, 21m),
            HtfBar(2, 18m, 19m, 17m), HtfBar(3, 16m, 17m, 15m),
            HtfBar(4, 16m, 17m, 15m), HtfBar(5, 16m, 17m, 15m)
        ];

        (PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality) =
            CalculateContext(setup, htf, new CausalEventCursor(Time(30), 10_000));

        Assert.Equal(10m, priceContext.Channel.Lower);
        Assert.Equal(20m, priceContext.Channel.Upper);
        Assert.Equal(10m, priceContext.Channel.Width);
        Assert.Equal(PriceContextTrendDirection.Bear, priceContext.LocalTrend.Direction);
        Assert.Equal(PriceContextHtfRegime.Bear, priceContext.HtfContext.ConfirmedRegime);

        BoundaryRejectionSetupResult result = Evaluate(priceContext, quality, 10m);

        Assert.Equal(BoundaryRejectionSetupState.Ready, result.Short.State);
        Assert.Equal(Direction.Short, result.Short.Direction);
        Assert.True(result.Short.Conditions.AllConditionsPassed);
        Assert.True(result.Short.Conditions.BoundaryTouched);
        Assert.True(result.Short.Conditions.CloseReclaimed);
        Assert.Equal(20m, result.Short.UpperBand);
        Assert.Equal(PriceContextTrendDirection.Bear, result.Short.LocalTrendDirection);
        Assert.Equal(PriceContextHtfRegime.Bear, result.Short.ConfirmedHtfRegime);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup, result.Long.State);
        Assert.Contains("htf-context-not-bull", result.Long.Reasons);
        Assert.True(result.IsReady);
    }

    [Fact]
    public void ConditionFailuresReturnNoSetupWithStableReasons()
    {
        PriceContextFeatureResult htfMismatch = ManualContext(
            htfRegime: PriceContextHtfRegime.Bear);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup,
            EvaluateManual(htfMismatch).Long.State);
        Assert.Contains("htf-context-not-bull",
            EvaluateManual(htfMismatch).Long.Reasons);

        PriceContextFeatureResult trendMismatch = ManualContext(
            trend: PriceContextTrendDirection.Neutral);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup,
            EvaluateManual(trendMismatch).Long.State);
        Assert.Contains("local-trend-not-bull",
            EvaluateManual(trendMismatch).Long.Reasons);

        PriceContextFeatureResult noTouch = ManualContext(
            open: 96m, high: 98m, low: 96m, close: 97m);
        BoundaryRejectionSetupResult noTouchResult = EvaluateManual(noTouch);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup, noTouchResult.Long.State);
        Assert.False(noTouchResult.Long.Conditions.BoundaryTouched);
        Assert.Contains("lower-band-not-touched", noTouchResult.Long.Reasons);

        PriceContextFeatureResult equality = ManualContext(
            open: 95m, high: 97m, low: 94m, close: 95m);
        BoundaryRejectionSetupResult equalityResult = EvaluateManual(equality);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup, equalityResult.Long.State);
        Assert.True(equalityResult.Long.Conditions.BoundaryTouched);
        Assert.False(equalityResult.Long.Conditions.CloseReclaimed);
        Assert.False(equalityResult.Long.Conditions.ClosedThroughBoundary);
        Assert.Contains("close-not-above-lower-band", equalityResult.Long.Reasons);

        PriceContextFeatureResult shortEquality = ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 103m, high: 104m, low: 102m, close: 104m);
        BoundaryRejectionSetupResult shortEqualityResult = EvaluateManual(shortEquality);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup, shortEqualityResult.Short.State);
        Assert.True(shortEqualityResult.Short.Conditions.BoundaryTouched);
        Assert.False(shortEqualityResult.Short.Conditions.CloseReclaimed);
        Assert.Contains("close-not-below-upper-band", shortEqualityResult.Short.Reasons);
    }

    [Fact]
    public void CloseThroughBoundaryIsInvalidatedWhileEqualityIsNot()
    {
        PriceContextFeatureResult longBreak = ManualContext(
            open: 94m, high: 96m, low: 94m, close: 94m);
        BoundaryRejectionSetupResult longResult = EvaluateManual(longBreak);

        Assert.Equal(BoundaryRejectionSetupState.Invalidated, longResult.Long.State);
        Assert.True(longResult.Long.IsInvalidated);
        Assert.False(longResult.Long.Conditions.CloseReclaimed);
        Assert.True(longResult.Long.Conditions.ClosedThroughBoundary);
        Assert.Contains("closed-through-lower-band", longResult.Long.Reasons);
        Assert.DoesNotContain("close-not-above-lower-band", longResult.Long.Reasons);

        PriceContextFeatureResult shortBreak = ManualContext(
            center: 99m, priorCenter: 100m, lower: 94m, upper: 104m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: 104m, high: 106m, low: 104m, close: 105m);
        BoundaryRejectionSetupResult shortResult = EvaluateManual(shortBreak);

        Assert.Equal(BoundaryRejectionSetupState.Invalidated, shortResult.Short.State);
        Assert.True(shortResult.Short.Conditions.ClosedThroughBoundary);
        Assert.Contains("closed-through-upper-band", shortResult.Short.Reasons);
    }

    [Fact]
    public void MinimumChannelWidthUsesFullSpanInclusively()
    {
        PriceContextFeatureResult context = ManualContext();
        FeatureQualityEvaluation quality = ManualQuality(context);

        Assert.Equal(BoundaryRejectionSetupState.Ready,
            Evaluate(context, quality, 10m).Long.State);
        Assert.Equal(BoundaryRejectionSetupState.NoSetup,
            Evaluate(context, quality, 10.0001m).Long.State);
        Assert.Contains("channel-width-below-minimum",
            Evaluate(context, quality, 10.0001m).Long.Reasons);

        // HalfWidth is 5 and the full span is 10, so a 9 threshold only passes
        // when the full channel span is used.
        BoundaryRejectionSetupResult fullSpan = Evaluate(context, quality, 9m);
        Assert.Equal(BoundaryRejectionSetupState.Ready, fullSpan.Long.State);
        Assert.Equal(5m, fullSpan.Long.ChannelHalfWidth);
        Assert.Equal(10m, fullSpan.Long.ChannelWidth);
    }

    [Fact]
    public void MissingOrUnsupportedConfigurationFailsClosed()
    {
        PriceContextFeatureResult context = ManualContext();
        FeatureQualityEvaluation quality = ManualQuality(context);

        BoundaryRejectionSetupResult missing = BoundaryRejectionSetupEvaluator.Evaluate(
            context, quality, new BoundaryRejectionSetupConfiguration());
        Assert.Equal(BoundaryRejectionSetupState.UnknownConfiguration, missing.Long.State);
        Assert.Equal(BoundaryRejectionSetupState.UnknownConfiguration, missing.Short.State);
        Assert.Contains("configuration-algorithm-version-missing", missing.Long.Reasons);
        Assert.Contains("configuration-minimum-channel-width-missing", missing.Long.Reasons);

        BoundaryRejectionSetupResult unsupportedVersion = BoundaryRejectionSetupEvaluator.Evaluate(
            context, quality, new BoundaryRejectionSetupConfiguration(
                new BoundaryRejectionSetupVersion("boundary-rejection-v2"), 10m));
        Assert.Equal(BoundaryRejectionSetupState.UnknownConfiguration,
            unsupportedVersion.Long.State);
        Assert.Contains("configuration-algorithm-version-unsupported",
            unsupportedVersion.Long.Reasons);

        BoundaryRejectionSetupResult zeroWidth = BoundaryRejectionSetupEvaluator.Evaluate(
            context, quality, new BoundaryRejectionSetupConfiguration(
                new BoundaryRejectionSetupVersion(
                    BoundaryRejectionSetupResult.CurrentAlgorithmVersion), 0m));
        Assert.Equal(BoundaryRejectionSetupState.UnknownConfiguration, zeroWidth.Long.State);
        Assert.Contains("configuration-minimum-channel-width-non-positive",
            zeroWidth.Long.Reasons);

        BoundaryRejectionSetupResult negativeWidth = BoundaryRejectionSetupEvaluator.Evaluate(
            context, quality, new BoundaryRejectionSetupConfiguration(
                new BoundaryRejectionSetupVersion(
                    BoundaryRejectionSetupResult.CurrentAlgorithmVersion), -1m));
        Assert.Equal(BoundaryRejectionSetupState.UnknownConfiguration,
            negativeWidth.Long.State);

        PriceContextFeatureResult wrongContextVersion = ManualContext(
            priceContextVersion: "price-context-v3");
        BoundaryRejectionSetupResult wrongVersion = EvaluateManual(wrongContextVersion);
        Assert.Equal(BoundaryRejectionSetupState.UnknownConfiguration,
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

        BoundaryRejectionSetupResult result = Evaluate(context, quality, 10m);

        Assert.Equal(BoundaryRejectionSetupState.Ready, result.Long.State);
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
        Assert.Throws<ArgumentException>(() => Evaluate(context, wrongCursor, 10m));
    }

    [Fact]
    public void SignalBarMustBeConfirmedAndEligibleAtTheCausalCursor()
    {
        PriceContextFeatureResult ineligible = ManualContext(availabilityOffsetMinutes: 5);
        BoundaryRejectionSetupResult ineligibleResult = EvaluateManual(ineligible);

        Assert.Equal(BoundaryRejectionSetupState.UnknownInput, ineligibleResult.Long.State);
        Assert.False(ineligibleResult.Long.Conditions.BarConfirmed);
        Assert.Contains("signal-bar-not-eligible", ineligibleResult.Long.Reasons);

        ScreenerBar otherBar = ManualBar(96m, 99m, 95m, 97m);
        PriceContextFeatureResult mismatchedProvenance = ManualContext(
            channelProvenanceBar: otherBar);
        BoundaryRejectionSetupResult mismatchResult = EvaluateManual(mismatchedProvenance);

        Assert.Equal(BoundaryRejectionSetupState.UnknownQuality, mismatchResult.Long.State);
        Assert.False(mismatchResult.Long.Conditions.BarConfirmed);
        Assert.Contains("signal-bar-not-latest-channel-bar",
            mismatchResult.Long.Reasons);
    }

    [Fact]
    public void FutureDataCannotMakeAnEarlierCursorReady()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m),
            SetupBar(1, 12m, 13m, 11m),
            SetupBar(2, 14m, 15m, 13m),
            SetupBar(3, 16m, 17m, 10m)
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
        Assert.Equal(earlyTruncated.Channel.Lower, earlyWithFuture.Channel.Lower);
        Assert.Equal(earlyTruncated.Channel.Upper, earlyWithFuture.Channel.Upper);
        Assert.Equal(earlyTruncated.LocalTrend.Direction,
            earlyWithFuture.LocalTrend.Direction);

        BoundaryRejectionSetupResult earlyResult = Evaluate(earlyWithFuture, earlyQuality, 10m);
        Assert.Equal(BoundaryRejectionSetupState.UnknownWarmup, earlyResult.Long.State);
        Assert.NotEqual(BoundaryRejectionSetupState.Ready, earlyResult.Short.State);

        BoundaryRejectionSetupResult lateResult = Evaluate(late, lateQuality, 10m);
        Assert.Equal(BoundaryRejectionSetupState.Ready, lateResult.Long.State);
    }

    [Fact]
    public void StrictJsonRoundTripAndImmutableEvidenceArePreserved()
    {
        ScreenerBar[] setup =
        [
            SetupBar(0, 10m, 11m, 9m),
            SetupBar(1, 12m, 13m, 11m),
            SetupBar(2, 14m, 15m, 13m),
            SetupBar(3, 16m, 17m, 10m)
        ];
        ScreenerBar[] htf =
        [
            HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
            HtfBar(2, 12m, 13m, 11m), HtfBar(3, 14m, 15m, 13m),
            HtfBar(4, 14m, 15m, 13m), HtfBar(5, 14m, 15m, 13m)
        ];
        (PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality) =
            CalculateContext(setup, htf, new CausalEventCursor(Time(30), 10_000));
        BoundaryRejectionSetupResult result = Evaluate(priceContext, quality, 10m);

        string json = CoreJsonSerializer.Serialize(result);
        BoundaryRejectionSetupResult? restored =
            CoreJsonSerializer.Deserialize<BoundaryRejectionSetupResult>(json);
        Assert.Equal(result, restored);
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<BoundaryRejectionSetupResult>(
                json[..^1] + ",\"unexpected\":true}"));

        Type[] contracts =
        [
            typeof(BoundaryRejectionSetupVersion),
            typeof(BoundaryRejectionSetupConfiguration),
            typeof(BoundaryRejectionConditionEvidence),
            typeof(BoundaryRejectionSetupObservation),
            typeof(BoundaryRejectionSetupResult)
        ];
        Assert.All(contracts, static type => Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.Null(property.SetMethod)));

        BoundaryRejectionSetupConfiguration configuration = new(
            new BoundaryRejectionSetupVersion(
                BoundaryRejectionSetupResult.CurrentAlgorithmVersion), 10m);
        Assert.Throws<ArgumentException>(() => new BoundaryRejectionSetupResult(
            "boundary-rejection-v2", configuration, priceContext, quality,
            result.Long, result.Short));
        Assert.Throws<ArgumentException>(() => new BoundaryRejectionSetupResult(
            BoundaryRejectionSetupResult.CurrentAlgorithmVersion, configuration,
            priceContext, quality, result.Short, result.Long));
        FeatureQualityEvaluation wrongCursor = ManualQuality(priceContext,
            cursor: new CausalEventCursor(Time(40), 100_000));
        Assert.Throws<ArgumentException>(() => new BoundaryRejectionSetupResult(
            BoundaryRejectionSetupResult.CurrentAlgorithmVersion, configuration,
            priceContext, wrongCursor, result.Long, result.Short));
    }

    private static void AssertUnknownQuality(PriceContextFeatureResult context,
        FeatureQualityEvaluation quality, string expectedReason)
    {
        BoundaryRejectionSetupResult result = Evaluate(context, quality, 10m);
        Assert.Equal(BoundaryRejectionSetupState.UnknownQuality, result.Long.State);
        Assert.False(result.Long.Conditions.DataQualityPassed);
        Assert.Contains(expectedReason, result.Long.Reasons);
    }

    private static BoundaryRejectionSetupResult EvaluateManual(
        PriceContextFeatureResult context, decimal minimumChannelWidth = 10m)
    {
        return Evaluate(context, ManualQuality(context), minimumChannelWidth);
    }

    private static BoundaryRejectionSetupResult Evaluate(
        PriceContextFeatureResult context, FeatureQualityEvaluation quality,
        decimal minimumChannelWidth)
    {
        return BoundaryRejectionSetupEvaluator.Evaluate(context, quality,
            new BoundaryRejectionSetupConfiguration(
                new BoundaryRejectionSetupVersion(
                    BoundaryRejectionSetupResult.CurrentAlgorithmVersion),
                minimumChannelWidth));
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
        decimal open = 95m, decimal high = 98m, decimal low = 94m, decimal close = 96m,
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

    private static ScreenerBar SetupBar(int index, decimal close, decimal high,
        decimal low, decimal volume = 10m)
    {
        UtcTimestamp eventTime = Time(index);
        Bar bar = new($"setup-{index}-{close}-{high}-{low}", Instrument, eventTime,
            eventTime, Time(index + 1), new Price(Math.Max(low, close - 1m)),
            new Price(high), new Price(low), new Price(close), new Quantity(volume));
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
