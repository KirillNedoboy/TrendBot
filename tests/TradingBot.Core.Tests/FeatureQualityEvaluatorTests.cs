using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class FeatureQualityEvaluatorTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluatorUsesCursorForBothAgesAndTreatsTtlBoundaryAsFresh()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m),
            Bar(1, 101m, 102m, 100m, 20m),
            Bar(2, 102m, 103m, 101m, 30m),
            Bar(3, 103m, 104m, 102m, 40m)
        ];
        RollingIndicatorInput input = new("source", Instrument, "1m", Time(-1), bars);
        CausalEventCursor cursor = new(Time(4), 4);
        RollingIndicatorConfiguration configuration = new(
            "1m", new RollingIndicatorVersion("rolling-v1"), 2,
            RollingAtrMode.Sma, 2, RollingMovingAverageKind.Sma, 2);
        RollingIndicatorResult calculation = RollingIndicatorCalculator.Calculate(
            input, cursor, configuration);

        FeatureQualityEvaluation boundary = FeatureQualityEvaluator.Evaluate(
            calculation, new FeatureQualityPolicy(
                rollingTtl: TimeSpan.FromMinutes(1),
                setupTtl: null, htfTtl: null, priceVolumeTtl: null));
        FeatureQualityEvidence movingAverage = Assert.Single(boundary.Features,
            feature => feature.Kind == FeatureQualityFeatureKind.RollingMovingAverage);
        Assert.Equal(FeatureQualityFreshness.Fresh, movingAverage.Freshness);
        Assert.Equal(TimeSpan.FromMinutes(1), movingAverage.MarketDataAge);
        Assert.Equal(TimeSpan.FromMinutes(1), movingAverage.AvailabilityAge);
        Assert.True(movingAverage.IsReady,
            $"state={movingAverage.OriginalState}; completeness={movingAverage.OriginalCompleteness}; "
            + $"freshness={movingAverage.Freshness}; coverage={movingAverage.Coverage}; "
            + $"known={movingAverage.KnownObservationCount}; required={movingAverage.RequiredObservationCount}; "
            + string.Join(",", movingAverage.Reasons));

        FeatureQualityEvaluation stale = FeatureQualityEvaluator.Evaluate(
            calculation, new FeatureQualityPolicy(
                rollingTtl: TimeSpan.FromMinutes(1).Subtract(TimeSpan.FromTicks(1)),
                setupTtl: null, htfTtl: null, priceVolumeTtl: null));
        Assert.All(stale.Features, feature =>
        {
            Assert.Equal(FeatureQualityFreshness.Stale, feature.Freshness);
            Assert.False(feature.IsReady);
        });

        FeatureQualityEvaluation unknown = FeatureQualityEvaluator.Evaluate(
            calculation, new FeatureQualityPolicy());
        Assert.All(unknown.Features, feature =>
        {
            Assert.Equal(FeatureQualityFreshness.Unknown, feature.Freshness);
            Assert.False(feature.IsReady);
            Assert.Contains(feature.Reasons,
                reason => reason.StartsWith("ttl-missing:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void DelayedOldMarketDataIsStaleEvenWhenItsAvailabilityIsFresh()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m, availabilityOffsetMinutes: 4),
            Bar(1, 101m, 102m, 100m, 20m, availabilityOffsetMinutes: 4),
            Bar(2, 102m, 103m, 101m, 30m, availabilityOffsetMinutes: 4)
        ];
        CausalEventCursor cursor = new(Time(4), 2);
        RollingIndicatorResult result = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1), bars), cursor,
            new RollingIndicatorConfiguration("1m", new RollingIndicatorVersion("rolling-v1"),
                2, RollingAtrMode.Sma, 2, RollingMovingAverageKind.Sma, 2));

        FeatureQualityEvidence evidence = Assert.Single(
            FeatureQualityEvaluator.Evaluate(result,
                new FeatureQualityPolicy(TimeSpan.FromMinutes(1), null, null, null)).Features,
            feature => feature.Kind == FeatureQualityFeatureKind.RollingMovingAverage);
        Assert.Equal(TimeSpan.FromMinutes(4), evidence.MarketDataAge);
        Assert.Equal(TimeSpan.Zero, evidence.AvailabilityAge);
        Assert.Equal(FeatureQualityFreshness.Stale, evidence.Freshness);
        Assert.False(evidence.IsReady);
    }

    [Fact]
    public void ExactDuplicatesKeepTheirDiagnosticButCountOnceAndGapsBlock()
    {
        ScreenerBar first = Bar(0, 100m, 101m, 99m, 10m);
        ScreenerBar second = Bar(1, 101m, 102m, 100m, 20m);
        RollingIndicatorConfiguration configuration = new("1m",
            new RollingIndicatorVersion("rolling-v1"), 2, RollingAtrMode.Sma, 2,
            RollingMovingAverageKind.Sma, 2);
        CausalEventCursor cursor = new(Time(2), 2);

        RollingIndicatorResult duplicate = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1),
                [first, first, second]), cursor, configuration);
        FeatureQualityEvidence duplicateEvidence = Assert.Single(
            FeatureQualityEvaluator.Evaluate(duplicate,
                new FeatureQualityPolicy(TimeSpan.FromMinutes(5), null, null, null)).Features,
            feature => feature.Kind == FeatureQualityFeatureKind.RollingMovingAverage);
        Assert.Equal(2, duplicateEvidence.KnownObservationCount);
        Assert.Contains("duplicate", duplicateEvidence.Reasons);
        Assert.False(duplicateEvidence.IsReady);
        Assert.Contains("incomplete-input", duplicateEvidence.Reasons);

        ScreenerBar third = Bar(2, 102m, 103m, 101m, 30m);
        RollingIndicatorResult gap = RollingIndicatorCalculator.Calculate(
            new RollingIndicatorInput("source", Instrument, "1m", Time(-1),
                [first, third]), cursor, configuration);
        FeatureQualityEvidence gapEvidence = Assert.Single(
            FeatureQualityEvaluator.Evaluate(gap,
                new FeatureQualityPolicy(TimeSpan.FromMinutes(5), null, null, null)).Features,
            feature => feature.Kind == FeatureQualityFeatureKind.RollingMovingAverage);
        Assert.Contains("gap", gapEvidence.Reasons);
        Assert.False(gapEvidence.IsReady);
    }

    private static ScreenerBar Bar(int offset, decimal close, decimal high, decimal low,
        decimal volume, int availabilityOffsetMinutes = 0)
    {
        UtcTimestamp eventTime = Time(offset);
        Bar bar = new($"bar-{offset}", Instrument, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime,
            new Price(close - 1m), new Price(high), new Price(low), new Price(close),
            new Quantity(volume));
        return new ScreenerBar("source", bar, volume * 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)),
            new UtcTimestamp(eventTime.Value.AddMinutes(availabilityOffsetMinutes)), offset);
    }

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));
}
