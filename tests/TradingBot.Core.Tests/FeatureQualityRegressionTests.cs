using System.Globalization;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class FeatureQualityRegressionTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InvalidTtlIsPersistableAndRemainsUnknown()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m),
            Bar(1, 101m, 102m, 100m, 20m),
            Bar(2, 102m, 103m, 101m, 30m),
            Bar(3, 103m, 104m, 102m, 40m)
        ];
        RollingIndicatorResult result = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1), bars),
            new CausalEventCursor(Time(4), 4),
            new RollingIndicatorConfiguration("1m", new RollingIndicatorVersion("rolling-v1"),
                2, RollingAtrMode.Sma, 2, RollingMovingAverageKind.Sma, 2));

        FeatureQualitySnapshot snapshot = FeatureQualitySnapshot.Create(result,
            new FeatureQualityPolicy(TimeSpan.Zero, null, null, null));

        Assert.All(snapshot.Quality.Features, feature =>
        {
            Assert.Equal(FeatureQualityFreshness.Unknown, feature.Freshness);
            Assert.False(feature.IsReady);
            Assert.Contains("ttl-invalid:rolling", feature.Reasons);
        });
    }

    [Fact]
    public void SeriesSpecificPoliciesAgeEachDependencyIndependently()
    {
        ScreenerBar[] setup = Enumerable.Range(0, 6)
            .Select(index => ContextBar(index, index + 10m, index + 11m, index + 9m,
                htf: false))
            .ToArray();
        ScreenerBar[] htf = Enumerable.Range(0, 6)
            .Select(index => ContextBar(index, index + 100m, index + 102m, index + 98m,
                htf: true))
            .ToArray();
        PriceVolumeObservation[] trades = Enumerable.Range(0, 6)
            .Select(index => new PriceVolumeObservation("source", Instrument, Time(index),
                Time(index), Time(index), 200 + index, 100m + index, 1m))
            .ToArray();
        PriceContextFeatureResult result = PriceContextFeatureCalculator.Calculate(
            new PriceContextFeatureInput("source", Instrument, "1m", Time(0), setup,
                "5m", HtfTime(0), htf, trades), new CausalEventCursor(Time(30), 10_000),
            ContextConfiguration());

        FeatureQualityEvaluation evaluation = FeatureQualityEvaluator.Evaluate(result,
            new FeatureQualityPolicy(TimeSpan.FromMinutes(23), TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(24)));

        Assert.All(evaluation.Features.Where(feature =>
            feature.InputSeries == FeatureQualityInputSeries.Setup), feature =>
        {
            Assert.Equal(FeatureQualityFreshness.Stale, feature.Freshness);
            Assert.Equal(TimeSpan.FromMinutes(24), feature.MarketDataAge);
        });
        Assert.All(evaluation.Features.Where(feature =>
            feature.InputSeries == FeatureQualityInputSeries.Htf), feature =>
        {
            Assert.Equal(FeatureQualityFreshness.Fresh, feature.Freshness);
            Assert.Equal(TimeSpan.Zero, feature.MarketDataAge);
            Assert.Equal(TimeSpan.FromMinutes(1), feature.TimeToLive);
        });
        Assert.All(evaluation.Features.Where(feature =>
            feature.InputSeries == FeatureQualityInputSeries.PriceVolume), feature =>
        {
            Assert.Equal(FeatureQualityFreshness.Stale, feature.Freshness);
            Assert.Equal(TimeSpan.FromMinutes(25), feature.MarketDataAge);
        });
    }

    [Fact]
    public void TtlEpsilonBoundaryAndGreaterValueRemainFresh()
    {
        RollingIndicatorResult result = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1),
                [Bar(0, 100m, 101m, 99m, 10m), Bar(1, 101m, 102m, 100m, 20m),
                 Bar(2, 102m, 103m, 101m, 30m), Bar(3, 103m, 104m, 102m, 40m)]),
            new CausalEventCursor(Time(4), 4),
            new RollingIndicatorConfiguration("1m", new RollingIndicatorVersion("rolling-v1"),
                2, RollingAtrMode.Sma, 2, RollingMovingAverageKind.Sma, 2));
        TimeSpan exact = TimeSpan.FromMinutes(1);
        TimeSpan epsilon = TimeSpan.FromTicks(1);

        FeatureQualityEvidence below = Assert.Single(FeatureQualityEvaluator.Evaluate(result,
            new FeatureQualityPolicy(exact - epsilon, null, null, null)).Features,
            feature => feature.Kind == FeatureQualityFeatureKind.RollingMovingAverage);
        FeatureQualityEvidence equal = Assert.Single(FeatureQualityEvaluator.Evaluate(result,
            new FeatureQualityPolicy(exact, null, null, null)).Features,
            feature => feature.Kind == FeatureQualityFeatureKind.RollingMovingAverage);
        FeatureQualityEvidence above = Assert.Single(FeatureQualityEvaluator.Evaluate(result,
            new FeatureQualityPolicy(exact + epsilon, null, null, null)).Features,
            feature => feature.Kind == FeatureQualityFeatureKind.RollingMovingAverage);

        Assert.Equal(FeatureQualityFreshness.Stale, below.Freshness);
        Assert.Equal(FeatureQualityFreshness.Fresh, equal.Freshness);
        Assert.Equal(FeatureQualityFreshness.Fresh, above.Freshness);
    }

    [Fact]
    public void WarmupAndConflictDiagnosticsRemainBlockingEvidence()
    {
        RollingIndicatorConfiguration configuration = new("1m",
            new RollingIndicatorVersion("rolling-v1"), 3, RollingAtrMode.Sma, 3,
            RollingMovingAverageKind.Sma, 3);
        RollingIndicatorResult warmup = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1),
                [Bar(0, 100m, 101m, 99m, 10m)]), new CausalEventCursor(Time(1), 1),
            configuration);
        FeatureQualityEvaluation warmupQuality = FeatureQualityEvaluator.Evaluate(warmup,
            new FeatureQualityPolicy(TimeSpan.FromMinutes(5), null, null, null));
        Assert.All(warmupQuality.Features, feature =>
        {
            Assert.False(feature.IsReady);
            Assert.Contains("incomplete-input", feature.Reasons);
        });

        ScreenerBar conflict = BarWithId("bar-1", 1, 199m, 200m, 198m, 20m);
        RollingIndicatorResult conflicted = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1),
                [Bar(0, 100m, 101m, 99m, 10m),
                 Bar(1, 101m, 102m, 100m, 20m), conflict]),
            new CausalEventCursor(Time(2), 2), configuration);
        FeatureQualityEvaluation conflictQuality = FeatureQualityEvaluator.Evaluate(
            conflicted, new FeatureQualityPolicy(TimeSpan.FromMinutes(5), null, null, null));
        Assert.All(conflictQuality.Features, feature =>
        {
            Assert.False(feature.IsReady);
            Assert.Contains("conflict", feature.Reasons);
        });
    }

    [Fact]
    public void FutureObservationsCannotChangeEarlierQualityAgesOrValues()
    {
        ScreenerBar[] historic =
        [
            Bar(0, 100m, 101m, 99m, 10m),
            Bar(1, 101m, 102m, 100m, 20m),
            Bar(2, 102m, 103m, 101m, 30m),
            Bar(3, 103m, 104m, 102m, 40m)
        ];
        RollingIndicatorConfiguration configuration = new("1m",
            new RollingIndicatorVersion("rolling-v1"), 2, RollingAtrMode.Sma, 2,
            RollingMovingAverageKind.Sma, 2);
        CausalEventCursor cursor = new(Time(4), 4);
        RollingIndicatorResult baseline = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1), historic),
            cursor, configuration);
        RollingIndicatorResult withFuture = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1),
                historic.Append(Bar(8, 900m, 901m, 899m, 900m))), cursor, configuration);
        FeatureQualityEvaluation expected = FeatureQualityEvaluator.Evaluate(baseline,
            new FeatureQualityPolicy(TimeSpan.FromMinutes(5), null, null, null));
        FeatureQualityEvaluation actual = FeatureQualityEvaluator.Evaluate(withFuture,
            new FeatureQualityPolicy(TimeSpan.FromMinutes(5), null, null, null));

        Assert.Equal(baseline.Quality.EligibleBars, withFuture.Quality.EligibleBars);
        Assert.Contains("future", withFuture.Quality.Reasons);
        Assert.Equal(expected.Features.Count, actual.Features.Count);
        for (int index = 0; index < expected.Features.Count; index++)
        {
            FeatureQualityEvidence left = expected.Features[index];
            FeatureQualityEvidence right = actual.Features[index];
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.Freshness, right.Freshness);
            Assert.Equal(left.MarketDataAge, right.MarketDataAge);
            Assert.Equal(left.AvailabilityAge, right.AvailabilityAge);
            Assert.Equal(left.KnownObservationCount, right.KnownObservationCount);
        }
    }

    [Fact]
    public void PermutationCultureAndStrictJsonKeepSnapshotIdentityStable()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m),
            Bar(1, 101m, 102m, 100m, 20m),
            Bar(2, 102m, 103m, 101m, 30m),
            Bar(3, 103m, 104m, 102m, 40m)
        ];
        RollingIndicatorConfiguration configuration = new("1m",
            new RollingIndicatorVersion("rolling-v1"), 2, RollingAtrMode.Sma, 2,
            RollingMovingAverageKind.Sma, 2);
        FeatureQualityPolicy policy = new(TimeSpan.FromMinutes(5), null, null, null);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            RollingIndicatorResult first = RollingIndicatorCalculator.Calculate(
                new RollingIndicatorInput("source", Instrument, "1m", Time(-1), bars),
                new CausalEventCursor(Time(4), 4), configuration);
            RollingIndicatorResult permuted = RollingIndicatorCalculator.Calculate(
                new RollingIndicatorInput("source", Instrument, "1m", Time(-1),
                    bars.Reverse()), new CausalEventCursor(Time(4), 4), configuration);
            FeatureQualitySnapshot firstSnapshot = FeatureQualitySnapshot.Create(first, policy);
            FeatureQualitySnapshot permutedSnapshot = FeatureQualitySnapshot.Create(permuted,
                policy);
            string json = CoreJsonSerializer.Serialize(firstSnapshot);

            Assert.Equal(firstSnapshot.SnapshotId, permutedSnapshot.SnapshotId);
            Assert.Equal(json, CoreJsonSerializer.Serialize(permutedSnapshot));
            Assert.Equal(firstSnapshot, CoreJsonSerializer.Deserialize<FeatureQualitySnapshot>(
                json));
            Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<
                FeatureQualitySnapshot>(json[..^1] + ",\"unexpected\":true}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static ScreenerBar Bar(int offset, decimal close, decimal high, decimal low,
        decimal volume)
    {
        UtcTimestamp eventTime = Time(offset);
        Bar bar = new($"bar-{offset}", Instrument, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime,
            new Price(close - 1m), new Price(high), new Price(low), new Price(close),
            new Quantity(volume));
        return new ScreenerBar("source", bar, volume * 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime, offset);
    }

    private static ScreenerBar BarWithId(string eventId, int offset, decimal close,
        decimal high, decimal low, decimal volume)
    {
        UtcTimestamp eventTime = Time(offset);
        Bar bar = new(eventId, Instrument, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime,
            new Price(close - 1m), new Price(high), new Price(low), new Price(close),
            new Quantity(volume));
        return new ScreenerBar("source", bar, volume * 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime, offset);
    }

    private static ScreenerBar ContextBar(int index, decimal close, decimal high,
        decimal low, bool htf)
    {
        DateTimeOffset eventTime = htf ? HtfTime(index).Value : Time(index).Value;
        DateTimeOffset closeTime = htf ? eventTime.AddMinutes(5) : eventTime.AddMinutes(1);
        Bar bar = new($"{(htf ? "htf" : "setup")}-{index}", Instrument,
            new UtcTimestamp(eventTime), new UtcTimestamp(eventTime),
            new UtcTimestamp(closeTime), new Price(close - 1m), new Price(high),
            new Price(low), new Price(close), new Quantity(10m));
        return new ScreenerBar("source", bar, 100m,
            new UtcTimestamp(eventTime), new UtcTimestamp(closeTime),
            htf ? 100 + index : index);
    }

    private static PriceContextFeatureConfiguration ContextConfiguration() => new(
        "1m", "5m", new PriceContextFeatureVersion("price-context-v2"), 1, 1, 2,
        PriceContextAtrMode.Sma, PriceContextChannelCenterKind.Ema, 2, null,
        PriceContextChannelWidthKind.Atr, 2, 1m, 0.1m, 2, 3,
        PriceContextMovingAverageKind.Ema, 2, 3, 3, 1m);

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));

    private static UtcTimestamp HtfTime(int offset) =>
        new(BaseTime.AddMinutes(offset * 5));
}
