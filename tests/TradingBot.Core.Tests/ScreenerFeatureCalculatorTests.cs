using System.Globalization;
using System.Reflection;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ScreenerFeatureCalculatorTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 2, 3, 4, 5,
        TimeSpan.Zero);

    [Fact]
    public void CalculateReturnsTheSixConfiguredFeaturesUsingExactDecimalFormulas()
    {
        ScreenerBar[] bars =
        [
            Bar(0, close: 100m, high: 101m, low: 99m, volume: 10m, quoteVolume: 100.1m),
            Bar(1, close: 105m, high: 108m, low: 103m, volume: 20m, quoteVolume: 200.2m),
            Bar(2, close: 111m, high: 114m, low: 109m, volume: 30m, quoteVolume: 300.3m),
            Bar(3, close: 116m, high: 120m, low: 112m, volume: 40m, quoteVolume: 400.4m),
            Bar(4, close: 119m, high: 123m, low: 115m, volume: 50m, quoteVolume: 500.5m)
        ];
        ScreenerOpenInterestObservation[] openInterest =
        [
            OpenInterest(0, 100m),
            OpenInterest(1, 110m),
            OpenInterest(2, 150m)
        ];
        ScreenerQuoteObservation quote = Quote(4, bid: 99m, ask: 101m);
        ScreenerFeatureInput input = Input(bars, openInterest, quote);
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 2,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 2,
            turnoverWindow: 3,
            openInterestAnomalyWindow: 2);

        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            input, new CausalEventCursor(Time(4), 4), configuration);

        Assert.Equal(configuration.Version, result.Version);
        Assert.Equal(50m / 35m, result.RelativeVolume.Value);
        Assert.Equal(119m / 116m - 116m / 111m, result.PriceAcceleration.Value);
        Assert.Equal(0m, result.AtrExpansion.Value);
        Assert.Equal(1201.2m, result.Turnover.Value);
        Assert.Equal(0.02m, result.Spread.Value);
        Assert.Equal(150m / 105m - 1m, result.OpenInterestAnomaly.Value);
        Assert.All(result.Features, static feature =>
        {
            Assert.Equal(ScreenerFeatureState.Ready, feature.State);
            Assert.True(feature.IsKnown);
            Assert.Null(feature.Reason);
        });
    }

    [Fact]
    public void AtrExpansionUsesTrueRangeWindowsAndQuoteTurnoverNeverReconstructsFromCloseAndVolume()
    {
        ScreenerBar[] bars =
        [
            Bar(0, close: 100m, high: 101m, low: 99m, volume: 100m, quoteVolume: 1.25m),
            Bar(1, close: 102m, high: 103m, low: 101m, volume: 100m, quoteVolume: 2.5m),
            Bar(2, close: 104m, high: 105m, low: 103m, volume: 100m, quoteVolume: 3.75m),
            Bar(3, close: 108m, high: 110m, low: 104m, volume: 100m, quoteVolume: 5.125m),
            Bar(4, close: 112m, high: 116m, low: 107m, volume: 100m, quoteVolume: 7.875m)
        ];
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 2,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 2,
            turnoverWindow: 2,
            openInterestAnomalyWindow: 1);

        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            Input(bars, [OpenInterest(0, 1m), OpenInterest(1, 2m)], Quote(4, 99m, 101m)),
            new CausalEventCursor(Time(4), 4), configuration);

        // TR values are 3, 3, 6, 9. Means are 3 and 7.5; expansion is 7.5 / 3 - 1.
        Assert.Equal(1.5m, result.AtrExpansion.Value);
        Assert.Equal(13m, result.Turnover.Value);
    }

    [Fact]
    public void CalculateUsesOnlyCausallyEligibleSamplesAndOrdersByEventTimeThenStableSequence()
    {
        ScreenerBar[] ordered =
        [
            Bar(0, close: 100m, high: 101m, low: 99m, volume: 1m, quoteVolume: 10m),
            Bar(1, close: 101m, high: 102m, low: 100m, volume: 2m, quoteVolume: 20m),
            Bar(2, close: 102m, high: 103m, low: 101m, volume: 3m, quoteVolume: 30m),
            Bar(3, close: 103m, high: 104m, low: 102m, volume: 4m, quoteVolume: 40m)
        ];
        ScreenerBar future = Bar(4, close: 1000m, high: 1001m, low: 999m,
            volume: 1000m, quoteVolume: 1000m);
        ScreenerBar[] shuffledWithFuture = [future, ordered[2], ordered[0], ordered[3], ordered[1]];
        CausalEventCursor cursor = new(Time(3), 3);
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 2,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 3,
            openInterestAnomalyWindow: 1);
        ScreenerOpenInterestObservation[] oi =
        [OpenInterest(0, 10m), OpenInterest(1, 10m), OpenInterest(2, 11m)];

        ScreenerFeatureResult baseline = ScreenerFeatureCalculator.Calculate(
            Input(ordered, oi, Quote(3, 99m, 101m)), cursor, configuration);
        ScreenerFeatureResult withFuture = ScreenerFeatureCalculator.Calculate(
            Input(shuffledWithFuture, oi, Quote(3, 99m, 101m)), cursor, configuration);

        Assert.Equal(baseline.Features, withFuture.Features);
        Assert.Equal(90m, baseline.Turnover.Value);
        Assert.Equal(103m / 102m - 102m / 101m, baseline.PriceAcceleration.Value);
    }

    [Fact]
    public void EachFeatureReportsWarmupAtItsExactBoundary()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 1m, 10m),
            Bar(1, 101m, 102m, 100m, 2m, 20m),
            Bar(2, 102m, 103m, 101m, 3m, 30m),
            Bar(3, 103m, 104m, 102m, 4m, 40m)
        ];
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 4,
            priceAccelerationWindow: 2,
            atrExpansionWindow: 2,
            turnoverWindow: 5,
            openInterestAnomalyWindow: 2);

        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            Input(bars, [OpenInterest(0, 1m), OpenInterest(1, 2m)], null),
            new CausalEventCursor(Time(3), 3), configuration);

        Assert.Equal(ScreenerFeatureState.UnknownWarmup, result.RelativeVolume.State);
        Assert.Equal(ScreenerFeatureState.UnknownWarmup, result.PriceAcceleration.State);
        Assert.Equal(ScreenerFeatureState.UnknownWarmup, result.AtrExpansion.State);
        Assert.Equal(ScreenerFeatureState.UnknownWarmup, result.Turnover.State);
        Assert.Equal(ScreenerFeatureState.UnknownWarmup, result.OpenInterestAnomaly.State);
        Assert.Equal(ScreenerFeatureState.UnknownInput, result.Spread.State);
        Assert.All(result.Features, static feature => Assert.NotNull(feature.Reason));
    }

    [Fact]
    public void MissingConfigurationProducesUnknownConfigurationWithoutNumericDefaults()
    {
        ScreenerFeatureConfiguration configuration = new(
            timeframe: null,
            relativeVolumeWindow: null,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            version: null);

        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            Input([Bar(0, 100m, 101m, 99m, 1m, 10m)], [OpenInterest(0, 1m)], null),
            new CausalEventCursor(Time(0), 0), configuration);

        Assert.All(result.Features, static feature =>
        {
            Assert.Equal(ScreenerFeatureState.UnknownConfiguration, feature.State);
            Assert.Null(feature.Value);
            Assert.Contains("configuration", feature.Reason!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MissingIndividualWindowLeavesOnlyThatFeatureUnconfigured()
    {
        ScreenerFeatureConfiguration configuration = new(
            timeframe: "1m",
            relativeVolumeWindow: null,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            version: new ScreenerFeatureVersion("screener-v1"));

        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            Input(
                [
                    Bar(0, 100m, 101m, 99m, 1m, 10m),
                    Bar(1, 101m, 102m, 100m, 2m, 20m),
                    Bar(2, 102m, 103m, 101m, 3m, 30m)
                ],
                [OpenInterest(0, 1m), OpenInterest(1, 2m)], Quote(2, 99m, 101m)),
            new CausalEventCursor(Time(2), 2), configuration);

        Assert.Equal(ScreenerFeatureState.UnknownConfiguration, result.RelativeVolume.State);
        Assert.Equal(ScreenerFeatureState.Ready, result.Turnover.State);
        Assert.Equal(ScreenerFeatureState.Ready, result.Spread.State);
    }

    [Fact]
    public void ZeroDenominatorsAndDecimalOverflowRemainUnknownWithReasons()
    {
        ScreenerBar[] zeroVolumeBars =
        [
            Bar(0, 100m, 100m, 100m, 0m, 1m),
            Bar(1, 100m, 100m, 100m, 0m, 2m),
            Bar(2, 100m, 100m, 100m, 1m, 3m)
        ];
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 2,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 2);

        ScreenerFeatureResult zeroResult = ScreenerFeatureCalculator.Calculate(
            Input(zeroVolumeBars, [OpenInterest(0, 0m), OpenInterest(1, 0m), OpenInterest(2, 1m)],
                Quote(2, 99m, 101m)), new CausalEventCursor(Time(2), 2), configuration);

        Assert.Equal(ScreenerFeatureState.UnknownZeroDenominator, zeroResult.RelativeVolume.State);
        Assert.Equal(ScreenerFeatureState.UnknownZeroDenominator, zeroResult.AtrExpansion.State);
        Assert.Equal(ScreenerFeatureState.UnknownZeroDenominator, zeroResult.OpenInterestAnomaly.State);
        Assert.NotEmpty(zeroResult.RelativeVolume.Reason!);

        ScreenerBar[] overflowBars =
        [
            Bar(0, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, 1m, decimal.MaxValue),
            Bar(1, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, 1m, decimal.MaxValue),
            Bar(2, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue, 1m, decimal.MaxValue)
        ];
        ScreenerFeatureResult overflowResult = ScreenerFeatureCalculator.Calculate(
            Input(overflowBars, [OpenInterest(0, 1m), OpenInterest(1, 2m)],
                Quote(2, 99m, 101m)), new CausalEventCursor(Time(2), 2),
            Configuration(2, 1, 1, 3, 1));

        Assert.Equal(ScreenerFeatureState.UnknownOverflow, overflowResult.Turnover.State);
        Assert.NotEmpty(overflowResult.Turnover.Reason!);
    }

    [Fact]
    public void CausalInputSnapshotsCollectionsAndContractsExposeNoPublicSetters()
    {
        ScreenerBar[] bars = [Bar(0, 100m, 101m, 99m, 1m, 10m)];
        ScreenerOpenInterestObservation[] openInterest = [OpenInterest(0, 1m)];
        ScreenerFeatureInput input = Input(bars, openInterest, Quote(0, 99m, 101m));
        bars[0] = Bar(0, 999m, 1000m, 998m, 1m, 999m);
        openInterest[0] = OpenInterest(0, 999m);

        Assert.Equal(100m, input.ClosedBars[0].Bar.Close.Value);
        Assert.Equal(1m, input.OpenInterest[0].Value);
        Assert.All(
            [typeof(ScreenerFeatureInput), typeof(ScreenerFeatureConfiguration),
                typeof(ScreenerFeatureResult), typeof(ScreenerFeatureValue), typeof(ScreenerBar),
                typeof(ScreenerOpenInterestObservation), typeof(ScreenerQuoteObservation)],
            static type => Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => Assert.Null(property.SetMethod)));
    }

    [Fact]
    public void ExplicitFreshnessUsesAvailabilityAndTreatsExactTtlAsFresh()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 1m, 10m),
            Bar(1, 101m, 102m, 100m, 2m, 20m),
            Bar(2, 102m, 103m, 101m, 3m, 30m)
        ];
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 1,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            freshnessPolicy: new ScreenerFreshnessPolicy(
                defaultTtl: TimeSpan.FromMinutes(1)));
        CausalEventCursor cursor = new(
            new UtcTimestamp(Time(2).Value.AddMinutes(1)), 2);

        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            Input(bars, [OpenInterest(1, 1m), OpenInterest(2, 2m)],
                Quote(2, 99m, 101m)), cursor, configuration);

        Assert.Equal(ScreenerFeatureFreshness.Fresh, result.RelativeVolume.Freshness);
        Assert.Equal(ScreenerFeatureCompleteness.Complete, result.RelativeVolume.Completeness);
        Assert.True(result.RelativeVolume.IsReady);

        ScreenerFeatureConfiguration missingTtl = Configuration(
            relativeVolumeWindow: 1,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            freshnessPolicy: new ScreenerFreshnessPolicy());
        ScreenerFeatureResult unknown = ScreenerFeatureCalculator.Calculate(
            Input(bars, [OpenInterest(1, 1m), OpenInterest(2, 2m)],
                Quote(2, 99m, 101m)), cursor, missingTtl);

        Assert.Equal(ScreenerFeatureFreshness.Unknown, unknown.RelativeVolume.Freshness);
        Assert.False(unknown.RelativeVolume.IsReady);
        Assert.Contains("freshness-ttl-missing", unknown.RelativeVolume.Reasons);
    }

    [Fact]
    public void StaleFreshnessUsesLatestRequiredAvailabilityAndLateAvailabilityIsExplicit()
    {
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 1,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            freshnessPolicy: new ScreenerFreshnessPolicy(
                defaultTtl: TimeSpan.FromMinutes(1)));
        CausalEventCursor cursor = new(Time(2), 2);
        ScreenerBar staleBar = Bar(1, 101m, 102m, 100m, 2m, 20m,
            availabilityOffsetMinutes: -2);
        ScreenerFeatureResult stale = ScreenerFeatureCalculator.Calculate(
            Input([Bar(0, 100m, 101m, 99m, 1m, 10m), staleBar],
                [OpenInterest(1, 1m), OpenInterest(2, 2m)], Quote(2, 99m, 101m)),
            cursor, configuration);

        Assert.Equal(ScreenerFeatureFreshness.Stale, stale.RelativeVolume.Freshness);
        Assert.False(stale.RelativeVolume.IsReady);

        ScreenerBar late = Bar(1, 101m, 102m, 100m, 2m, 20m,
            availabilityOffsetMinutes: 2);
        ScreenerFeatureResult lateResult = ScreenerFeatureCalculator.Calculate(
            Input([Bar(0, 100m, 101m, 99m, 1m, 10m), late],
                [OpenInterest(1, 1m), OpenInterest(2, 2m)], Quote(2, 99m, 101m)),
            cursor, configuration);

        Assert.Equal(ScreenerFeatureCompleteness.Warmup, lateResult.RelativeVolume.Completeness);
        Assert.Contains("late-availability", lateResult.RelativeVolume.Reasons);
    }

    [Fact]
    public void CausalBarSelectionRequiresCloseAtOrBeforeCursor()
    {
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 1,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            freshnessPolicy: new ScreenerFreshnessPolicy(TimeSpan.FromMinutes(5)));
        ScreenerBar closingAfterCursor = new(
            "feed-a",
            new Bar("bar-future-close", Instrument, Time(1), Time(1), Time(3),
                new Price(101m), new Price(102m), new Price(100m), new Price(101m),
                new Quantity(2m)),
            20m, Time(1), Time(1), 1);

        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            Input([Bar(0, 100m, 101m, 99m, 1m, 10m), closingAfterCursor],
                [OpenInterest(0, 1m), OpenInterest(1, 2m)], Quote(1, 99m, 101m)),
            new CausalEventCursor(Time(2), 1), configuration);

        Assert.Equal(ScreenerFeatureCompleteness.Warmup, result.RelativeVolume.Completeness);
        Assert.Contains("bar-close-after-cursor", result.RelativeVolume.Reasons);
    }

    [Fact]
    public void ExactDuplicatesCountOnceAndConflictingKeysAndGapsRemainUnknown()
    {
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 1,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            freshnessPolicy: new ScreenerFreshnessPolicy(TimeSpan.FromMinutes(5)));
        ScreenerBar first = Bar(0, 100m, 101m, 99m, 1m, 10m);
        ScreenerBar second = Bar(1, 102m, 103m, 101m, 2m, 20m);
        ScreenerFeatureResult duplicate = ScreenerFeatureCalculator.Calculate(
            Input([first, first, second],
                [OpenInterest(0, 1m), OpenInterest(1, 2m), OpenInterest(1, 2m)],
                Quote(1, 99m, 101m)), new CausalEventCursor(Time(1), 1), configuration);

        Assert.Equal(2m, duplicate.RelativeVolume.Value);
        Assert.Equal(ScreenerFeatureCompleteness.Complete, duplicate.RelativeVolume.Completeness);

        ScreenerBar conflicting = Bar(1, 103m, 104m, 102m, 2m, 20m);
        ScreenerFeatureResult conflict = ScreenerFeatureCalculator.Calculate(
            Input([first, second, conflicting],
                [OpenInterest(0, 1m), OpenInterest(1, 2m)], Quote(1, 99m, 101m)),
            new CausalEventCursor(Time(1), 1), configuration);

        Assert.Equal(ScreenerFeatureState.UnknownQuality, conflict.RelativeVolume.State);
        Assert.Contains("conflict", conflict.RelativeVolume.Reasons);

        ScreenerFeatureResult gap = ScreenerFeatureCalculator.Calculate(
            Input([first, Bar(2, 102m, 103m, 101m, 2m, 20m)],
                [OpenInterest(0, 1m), OpenInterest(2, 2m)], Quote(2, 99m, 101m)),
            new CausalEventCursor(Time(2), 2), configuration);

        Assert.Equal(ScreenerFeatureState.UnknownQuality, gap.RelativeVolume.State);
        Assert.Contains("bar-gap", gap.RelativeVolume.Reasons);

        ScreenerFeatureResult quoteDuplicate = ScreenerFeatureCalculator.Calculate(
            new ScreenerFeatureInput("feed-a", Instrument, Time(1), Time(1), Time(1), 1,
                [first, second], [OpenInterest(0, 1m), OpenInterest(1, 2m)],
                [Quote(1, 99m, 101m), Quote(1, 99m, 101m)]),
            new CausalEventCursor(Time(1), 1), configuration);
        Assert.Equal(ScreenerFeatureState.Ready, quoteDuplicate.Spread.State);

        ScreenerFeatureResult quoteConflict = ScreenerFeatureCalculator.Calculate(
            new ScreenerFeatureInput("feed-a", Instrument, Time(1), Time(1), Time(1), 1,
                [first, second], [OpenInterest(0, 1m), OpenInterest(1, 2m)],
                [Quote(1, 99m, 101m), Quote(1, 98m, 101m)]),
            new CausalEventCursor(Time(1), 1), configuration);
        Assert.Equal(ScreenerFeatureState.UnknownQuality, quoteConflict.Spread.State);
        Assert.Contains("conflict", quoteConflict.Spread.Reasons);

        ScreenerFeatureResult openInterestConflict = ScreenerFeatureCalculator.Calculate(
            Input([first, second], [OpenInterest(0, 1m), OpenInterest(1, 2m),
                OpenInterest(1, 3m)], Quote(1, 99m, 101m)),
            new CausalEventCursor(Time(1), 1), configuration);
        Assert.Equal(ScreenerFeatureState.UnknownQuality,
            openInterestConflict.OpenInterestAnomaly.State);
        Assert.Contains("conflict", openInterestConflict.OpenInterestAnomaly.Reasons);
    }

    [Fact]
    public void QualityAndFreshnessArePermutationAndCultureIndependent()
    {
        ScreenerFeatureConfiguration configuration = Configuration(
            relativeVolumeWindow: 1,
            priceAccelerationWindow: 1,
            atrExpansionWindow: 1,
            turnoverWindow: 1,
            openInterestAnomalyWindow: 1,
            freshnessPolicy: new ScreenerFreshnessPolicy(TimeSpan.FromMinutes(5)));
        ScreenerBar[] ordered =
        [
            Bar(0, 100m, 101m, 99m, 1m, 10m),
            Bar(1, 102m, 103m, 101m, 2m, 20m)
        ];
        ScreenerOpenInterestObservation[] oi = [OpenInterest(0, 1m), OpenInterest(1, 2m)];
        ScreenerQuoteObservation quote = Quote(1, 99m, 101m);
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            ScreenerFeatureResult first = ScreenerFeatureCalculator.Calculate(
                Input(ordered, oi, quote), new CausalEventCursor(Time(1), 1), configuration);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            ScreenerFeatureResult second = ScreenerFeatureCalculator.Calculate(
                Input(ordered.Reverse(), oi.Reverse(), quote),
                new CausalEventCursor(Time(1), 1), configuration);

            Assert.Equal(first.Features, second.Features);
            Assert.Equal(first.RelativeVolume.Reasons, second.RelativeVolume.Reasons);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static ScreenerFeatureConfiguration Configuration(int relativeVolumeWindow,
        int priceAccelerationWindow, int atrExpansionWindow, int turnoverWindow,
        int openInterestAnomalyWindow, ScreenerFreshnessPolicy? freshnessPolicy = null)
    {
        return new ScreenerFeatureConfiguration("1m", relativeVolumeWindow,
            priceAccelerationWindow, atrExpansionWindow, turnoverWindow,
            openInterestAnomalyWindow, new ScreenerFeatureVersion("screener-v1"),
            freshnessPolicy);
    }

    private static ScreenerFeatureInput Input(IEnumerable<ScreenerBar> bars,
        IEnumerable<ScreenerOpenInterestObservation> openInterest,
        ScreenerQuoteObservation? quote)
    {
        return new ScreenerFeatureInput("feed-a", Instrument, Time(4), Time(4), Time(4), 4,
            bars, openInterest, quote is null ? [] : [quote]);
    }

    private static ScreenerBar Bar(int index, decimal close, decimal high, decimal low,
        decimal volume, decimal quoteVolume, int availabilityOffsetMinutes = 0)
    {
        UtcTimestamp eventTime = Time(index);
        return new ScreenerBar("feed-a",
            new Bar($"bar-{index}", Instrument, eventTime,
                new UtcTimestamp(Time(index).Value.AddSeconds(-30)), eventTime,
                new Price(close),
                new Price(high), new Price(low),
                new Price(close), new Quantity(volume)), quoteVolume,
            new UtcTimestamp(Time(index).Value.AddMinutes(Math.Min(0, availabilityOffsetMinutes))),
            new UtcTimestamp(Time(index).Value.AddMinutes(availabilityOffsetMinutes)), index);
    }

    private static ScreenerOpenInterestObservation OpenInterest(int index, decimal value,
        int availabilityOffsetMinutes = 0)
    {
        return new ScreenerOpenInterestObservation("feed-a", Instrument, Time(index),
            new UtcTimestamp(Time(index).Value.AddMinutes(Math.Min(0, availabilityOffsetMinutes))),
            new UtcTimestamp(Time(index).Value.AddMinutes(availabilityOffsetMinutes)), index, value);
    }

    private static ScreenerQuoteObservation Quote(int index, decimal bid, decimal ask,
        int availabilityOffsetMinutes = 0)
    {
        return new ScreenerQuoteObservation("feed-a", Instrument, Time(index),
            new UtcTimestamp(Time(index).Value.AddMinutes(Math.Min(0, availabilityOffsetMinutes))),
            new UtcTimestamp(Time(index).Value.AddMinutes(availabilityOffsetMinutes)), index,
            new Price(bid), new Price(ask), isReady: true);
    }

    private static UtcTimestamp Time(int index) =>
        new(BaseTime.AddMinutes(index));
}
