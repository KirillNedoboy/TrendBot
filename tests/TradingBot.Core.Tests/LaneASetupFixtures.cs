using TradingBot.Core;

namespace TradingBot.Core.Tests;

internal sealed record LaneAFixtureCase(
    string Name,
    PriceContextFeatureResult PriceContext,
    FeatureQualityEvaluation Quality,
    decimal BoundaryMinimumWidth,
    BoundaryRejectionSetupState ExpectedBoundaryLong,
    BoundaryRejectionSetupState ExpectedBoundaryShort,
    CenterRetestSetupState ExpectedCenterLong,
    CenterRetestSetupState ExpectedCenterShort);

internal static class LaneASetupFixtures
{
    internal const decimal BoundaryMinimumWidth = 10m;

    internal static readonly InstrumentId Instrument = new("BTCUSDT");
    internal static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] WarmupReasons = ["warmup"];
    private static readonly string[] VwapNotConfiguredReasons = ["vwap-not-configured"];
    private static readonly string[] PocNotConfiguredReasons = ["poc-not-configured"];

    internal static IReadOnlyList<LaneAFixtureCase> Cases { get; } = CreateCases();

    internal static BoundaryRejectionSetupResult EvaluateBoundary(
        LaneAFixtureCase fixture) =>
        EvaluateBoundary(fixture.PriceContext, fixture.Quality,
            fixture.BoundaryMinimumWidth);

    internal static BoundaryRejectionSetupResult EvaluateBoundary(
        PriceContextFeatureResult context, FeatureQualityEvaluation quality,
        decimal minimumChannelWidth) =>
        BoundaryRejectionSetupEvaluator.Evaluate(context, quality,
            new BoundaryRejectionSetupConfiguration(
                new BoundaryRejectionSetupVersion(
                    BoundaryRejectionSetupResult.CurrentAlgorithmVersion),
                minimumChannelWidth));

    internal static CenterRetestSetupResult EvaluateCenter(LaneAFixtureCase fixture) =>
        EvaluateCenter(fixture.PriceContext, fixture.Quality);

    internal static CenterRetestSetupResult EvaluateCenter(
        PriceContextFeatureResult context, FeatureQualityEvaluation quality) =>
        CenterRetestSetupEvaluator.Evaluate(context, quality,
            new CenterRetestSetupConfiguration(
                new CenterRetestSetupVersion(
                    CenterRetestSetupResult.CurrentAlgorithmVersion)));

    internal static (ScreenerBar[] Setup, ScreenerBar[] Htf) CreateLongHistory() =>
        (
            [
                SetupBar(0, 9m, 11m, 9m, 10m),
                SetupBar(1, 11m, 13m, 11m, 12m),
                SetupBar(2, 13m, 15m, 13m, 14m),
                SetupBar(3, 15m, 17m, 10m, 16m)
            ],
            [
                HtfBar(0, 10m, 11m, 9m), HtfBar(1, 8m, 9m, 7m),
                HtfBar(2, 12m, 13m, 11m), HtfBar(3, 14m, 15m, 13m),
                HtfBar(4, 14m, 15m, 13m), HtfBar(5, 14m, 15m, 13m)
            ]);

    internal static IReadOnlyList<CausalEventCursor> ReplayCursors() =>
        [
            new(Time(1), 10_000), new(Time(2), 10_000), new(Time(3), 10_000),
            new(Time(4), 10_000), new(Time(5), 10_000), new(Time(10), 10_000),
            new(Time(15), 10_000), new(Time(20), 10_000), new(Time(25), 10_000),
            new(Time(30), 10_000)
        ];

    internal static (ScreenerBar[] Setup, ScreenerBar[] Htf) Prefix(
        IEnumerable<ScreenerBar> setup, IEnumerable<ScreenerBar> htf,
        UtcTimestamp asOf) =>
        (
            setup.Where(bar => bar.CloseTime.Value <= asOf.Value).ToArray(),
            htf.Where(bar => bar.CloseTime.Value <= asOf.Value).ToArray());

    internal static (PriceContextFeatureResult PriceContext, FeatureQualityEvaluation Quality)
        Calculate(IEnumerable<ScreenerBar> setup, IEnumerable<ScreenerBar> htf,
            CausalEventCursor cursor)
    {
        PriceContextFeatureInput input = new("feed-a", Instrument, "1m", Time(0),
            setup, "5m", Time(0), htf, Array.Empty<PriceVolumeObservation>());
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

    private static IReadOnlyList<LaneAFixtureCase> CreateCases() =>
        [
            MakeLongCase("both-long-ready", 100m, 102m, 94m, 101m,
                BoundaryRejectionSetupState.Ready, CenterRetestSetupState.Ready),
            MakeLongCase("center-long-only", 100m, 102m, 96m, 101m,
                BoundaryRejectionSetupState.NoSetup, CenterRetestSetupState.Ready),
            MakeLongCase("boundary-long-only", 94m, 98m, 94m, 96m,
                BoundaryRejectionSetupState.Ready,
                CenterRetestSetupState.Invalidated),
            MakeLongCase("both-long-invalidated", 100m, 102m, 94m, 94m,
                BoundaryRejectionSetupState.Invalidated,
                CenterRetestSetupState.Invalidated),
            MakeLongCase("center-long-invalidated-only", 99m, 101m, 94m, 99m,
                BoundaryRejectionSetupState.Ready, CenterRetestSetupState.Invalidated),
            MakeLongCase("center-long-no-width", 100m, 102m, 94m, 101m,
                BoundaryRejectionSetupState.NoSetup, CenterRetestSetupState.Ready,
                lower: 100m, upper: 100m, halfWidth: 0m, width: 0m),
            MakeLongCase("both-long-unknown-quality", 100m, 102m, 94m, 101m,
                BoundaryRejectionSetupState.UnknownQuality,
                CenterRetestSetupState.UnknownQuality,
                includeChannelQuality: false,
                boundaryShort: BoundaryRejectionSetupState.UnknownQuality,
                centerShort: CenterRetestSetupState.UnknownQuality),
            MakeShortCase("both-short-ready", 99m, 106m, 98m, 98m,
                BoundaryRejectionSetupState.Ready, CenterRetestSetupState.Ready),
            MakeShortCase("center-short-only", 99m, 99m, 98m, 98m,
                BoundaryRejectionSetupState.NoSetup, CenterRetestSetupState.Ready),
            MakeShortCase("boundary-short-only", 104m, 106m, 103m, 103m,
                BoundaryRejectionSetupState.Ready,
                CenterRetestSetupState.Invalidated),
            MakeShortCase("both-short-invalidated", 104m, 106m, 104m, 105m,
                BoundaryRejectionSetupState.Invalidated,
                CenterRetestSetupState.Invalidated),
            MakeShortCase("center-short-invalidated-only", 100m, 101m, 98m, 100m,
                BoundaryRejectionSetupState.NoSetup,
                CenterRetestSetupState.Invalidated),
            MakeShortCase("center-short-no-width", 99m, 106m, 98m, 98m,
                BoundaryRejectionSetupState.NoSetup, CenterRetestSetupState.Ready,
                lower: 99m, upper: 99m, halfWidth: 0m, width: 0m),
            MakeShortCase("both-short-unknown-quality", 99m, 106m, 98m, 98m,
                BoundaryRejectionSetupState.UnknownQuality,
                CenterRetestSetupState.UnknownQuality,
                includeChannelQuality: false,
                boundaryLong: BoundaryRejectionSetupState.UnknownQuality,
                centerLong: CenterRetestSetupState.UnknownQuality)
        ];

    private static LaneAFixtureCase MakeLongCase(string name, decimal open,
        decimal high, decimal low, decimal close,
        BoundaryRejectionSetupState boundaryLong,
        CenterRetestSetupState centerLong,
        decimal? center = null, decimal? priorCenter = null,
        decimal? lower = null, decimal? upper = null,
        decimal? halfWidth = null, decimal? width = null,
        bool includeChannelQuality = true,
        BoundaryRejectionSetupState? boundaryShort = null,
        CenterRetestSetupState? centerShort = null)
    {
        PriceContextFeatureResult context = CreateSnapshot(
            center: center ?? 100m,
            priorCenter: priorCenter ?? 99m,
            lower: lower ?? 95m,
            upper: upper ?? 105m,
            halfWidth: halfWidth ?? 5m,
            width: width ?? 10m,
            trend: PriceContextTrendDirection.Bull,
            htfRegime: PriceContextHtfRegime.Bull,
            open: open,
            high: high,
            low: low,
            close: close);
        return new LaneAFixtureCase(name, context,
            CreateQuality(context, includeChannelQuality), BoundaryMinimumWidth,
            boundaryLong,
            boundaryShort ?? BoundaryRejectionSetupState.NoSetup,
            centerLong,
            centerShort ?? CenterRetestSetupState.NoSetup);
    }

    private static LaneAFixtureCase MakeShortCase(string name, decimal open,
        decimal high, decimal low, decimal close,
        BoundaryRejectionSetupState boundaryShort,
        CenterRetestSetupState centerShort,
        decimal? center = null, decimal? priorCenter = null,
        decimal? lower = null, decimal? upper = null,
        decimal? halfWidth = null, decimal? width = null,
        bool includeChannelQuality = true,
        BoundaryRejectionSetupState? boundaryLong = null,
        CenterRetestSetupState? centerLong = null)
    {
        PriceContextFeatureResult context = CreateSnapshot(
            center: center ?? 99m,
            priorCenter: priorCenter ?? 100m,
            lower: lower ?? 94m,
            upper: upper ?? 104m,
            halfWidth: halfWidth ?? 5m,
            width: width ?? 10m,
            trend: PriceContextTrendDirection.Bear,
            htfRegime: PriceContextHtfRegime.Bear,
            open: open,
            high: high,
            low: low,
            close: close);
        return new LaneAFixtureCase(name, context,
            CreateQuality(context, includeChannelQuality), BoundaryMinimumWidth,
            boundaryLong ?? BoundaryRejectionSetupState.NoSetup,
            boundaryShort,
            centerLong ?? CenterRetestSetupState.NoSetup,
            centerShort);
    }

    private static PriceContextFeatureResult CreateSnapshot(decimal center,
        decimal priorCenter, decimal lower, decimal upper, decimal halfWidth,
        decimal width, PriceContextTrendDirection trend,
        PriceContextHtfRegime htfRegime, decimal open, decimal high,
        decimal low, decimal close)
    {
        ScreenerBar signalBar = ManualBar(open, high, low, close);
        ScreenerBar htfBar = ManualHtfBar();
        CausalEventCursor cursor = new(Time(3), 10);
        PriceContextFeatureInput input = new("feed-a", Instrument, "1m", Time(0),
            new[] { signalBar }, "5m", Time(0), new[] { htfBar },
            Array.Empty<PriceVolumeObservation>());
        PriceContextObservationQuality quality = new(new[] { signalBar },
            new[] { htfBar }, Array.Empty<PriceVolumeObservation>(),
            Array.Empty<string>());
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
            new ImmutableValueList<ScreenerBar>(new[] { signalBar }),
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
        return new PriceContextFeatureResult(ManualConfiguration(), input, cursor,
            quality, pivots, levels, channel, localTrend, htfContext, vwap, poc);
    }

    private static FeatureQualityEvaluation CreateQuality(
        PriceContextFeatureResult context, bool includeChannelQuality)
    {
        string formula = context.Configuration.AlgorithmVersion?.Value ?? "unknown";
        List<FeatureQualityEvidence> features = [];
        if (includeChannelQuality)
        {
            features.Add(Evidence(FeatureQualityFeatureKind.PriceContextChannel,
                FeatureQualityInputSeries.Setup, formula,
                context.Channel.State.ToString(), isReady: true));
        }

        features.Add(Evidence(FeatureQualityFeatureKind.PriceContextLocalTrend,
            FeatureQualityInputSeries.Setup, formula,
            context.LocalTrend.State.ToString(), isReady: true));
        features.Add(Evidence(FeatureQualityFeatureKind.PriceContextHtfContext,
            FeatureQualityInputSeries.Htf, formula,
            context.HtfContext.State.ToString(), isReady: true));
        features.Add(Evidence(FeatureQualityFeatureKind.PriceContextVwap,
            FeatureQualityInputSeries.PriceVolume, formula,
            "UnknownConfiguration", isReady: false));

        return new FeatureQualityEvaluation("feature-quality-v1", context.Cursor,
            new FeatureQualityPolicy(), null,
            context.Configuration.AlgorithmVersion?.Value,
            new ImmutableValueList<FeatureQualityEvidence>(features));
    }

    private static FeatureQualityEvidence Evidence(FeatureQualityFeatureKind kind,
        FeatureQualityInputSeries series, string formula, string originalState,
        bool isReady) => new(kind, series, formula, originalState, "Complete",
            FeatureQualityFreshness.Fresh, FeatureQualityFreshness.Fresh,
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromHours(1),
            FeatureQualityCoverage.Complete, 1m, 4, 3, isReady,
            new ImmutableValueList<string>(Array.Empty<string>()));

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
        decimal close)
    {
        UtcTimestamp eventTime = Time(1);
        Bar bar = new($"manual-{open}-{high}-{low}-{close}", Instrument, eventTime,
            eventTime, Time(2), new Price(open), new Price(high), new Price(low),
            new Price(close), new Quantity(10m));
        return new ScreenerBar("feed-a", bar, 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)),
            eventTime, 1);
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

    private static PriceContextFeatureConfiguration ManualConfiguration() => new(
        "1m", "5m", new PriceContextFeatureVersion(
            PriceContextFeatureCalculator.CurrentAlgorithmVersion),
        1, 1, 2, PriceContextAtrMode.Sma,
        PriceContextChannelCenterKind.Ema, 2, null,
        PriceContextChannelWidthKind.Atr, 2, 1m, 1m,
        2, 3, PriceContextMovingAverageKind.Ema, 3,
        null, null, null);

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));
}
