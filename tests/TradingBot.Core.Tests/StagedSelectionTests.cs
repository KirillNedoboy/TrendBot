using System.Globalization;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class StagedSelectionTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UniverseStageRanksReadyFeaturesLexicographicallyAndPreservesEvidence()
    {
        CausalEventCursor cursor = Cursor();
        StagedSelectionConfiguration configuration = UniverseConfiguration(2,
        [
            new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending),
            new StagedSelectionKey(ScreenerFeatureKind.Spread,
                StagedSelectionDirection.Ascending)
        ]);
        ExchangeEligibilityMetadata[] metadata =
        [
            Metadata("BTCUSDT"),
            Metadata("AUSDT"),
            Metadata("CUSDT")
        ];
        ScreenerSnapshot[] snapshots =
        [
            Snapshot("BTCUSDT", 200m, 2m),
            Snapshot("AUSDT", 200m, 1m),
            Snapshot("CUSDT", 100m, 1m)
        ];

        StagedSelectionResult result = UniverseSelectionStage.Evaluate(
            metadata, snapshots, cursor, configuration);

        Assert.Equal(["AUSDT", "BTCUSDT"],
            result.Selected.Select(static candidate => candidate.InstrumentId.Value));
        Assert.Equal([1, 2], result.Selected.Select(static candidate => candidate.Rank));
        Assert.Equal(200m, result.Selected[0].Comparisons[0].Value);
        Assert.Equal(1m / 100.5m, result.Selected[0].Comparisons[1].Value);
        Assert.Equal(snapshots[1], result.Selected[0].ScreenerEvidence);
        Assert.Equal(configuration, result.Configuration);
        Assert.Equal(cursor, result.Cursor);
        Assert.DoesNotContain(result.Selected[0].RecalculatedFeatures.Features,
            static feature => !feature.IsReady);

        StagedSelectionExclusion capacity = result.Exclusions.Single(
            exclusion => exclusion.Metadata!.InstrumentId.Value == "CUSDT");
        Assert.Equal(["capacity-exceeded"], capacity.Reasons);
        Assert.Equal(3, capacity.Rank);
        Assert.Equal(100m, capacity.Comparisons[0].Value);
        Assert.Equal(1m / 100.5m, capacity.Comparisons[1].Value);
    }

    [Fact]
    public void UniverseStageRequiresAvailableMetadataAndReadyFreshConfiguredFeatures()
    {
        CausalEventCursor cursor = Cursor();
        StagedSelectionConfiguration configuration = UniverseConfiguration(250,
        [
            new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending),
            new StagedSelectionKey(ScreenerFeatureKind.Spread,
                StagedSelectionDirection.Ascending)
        ]);
        ExchangeEligibilityMetadata[] metadata =
        [
            Metadata("BTCUSDT"),
            Metadata("FUTUREUSDT", availabilityOffsetMinutes: 11),
            Metadata("MISSINGUSDT")
        ];
        ScreenerSnapshot[] snapshots =
        [
            Snapshot("BTCUSDT", 100m, 1m, includeQuote: false),
            Snapshot("FUTUREUSDT", 100m, 1m),
        ];

        StagedSelectionResult result = UniverseSelectionStage.Evaluate(
            metadata, snapshots, cursor, configuration);

        Assert.Empty(result.Selected);
        Assert.Equal(
            ["feature-not-ready:Spread"],
            result.Exclusions.Single(
                exclusion => exclusion.Metadata!.InstrumentId.Value == "BTCUSDT").Reasons);
        Assert.Equal(["metadata-not-available-at-cursor"],
            result.Exclusions.Single(
                exclusion => exclusion.Metadata!.InstrumentId.Value == "FUTUREUSDT").Reasons);
        Assert.Equal(["screener-snapshot-missing"],
            result.Exclusions.Single(
                exclusion => exclusion.Metadata!.InstrumentId.Value == "MISSINGUSDT").Reasons);
    }

    [Fact]
    public void UniverseStageRejectsStaleRequiredFeaturesWithoutSubstitutingAValue()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot baseline = Snapshot("STALEUSDT", 100m, 1m);
        ScreenerFeatureConfiguration staleConfiguration = new(
            timeframe: "1m", relativeVolumeWindow: 1, priceAccelerationWindow: 1,
            atrExpansionWindow: 1, turnoverWindow: 1, openInterestAnomalyWindow: 1,
            version: new ScreenerFeatureVersion("screener-v1"),
            freshnessPolicy: new ScreenerFreshnessPolicy(
                defaultTtl: TimeSpan.FromMinutes(30), barTtl: TimeSpan.FromMinutes(30),
                quoteTtl: TimeSpan.FromMinutes(1),
                openInterestTtl: TimeSpan.FromMinutes(30), sourceTtls: []));
        ScreenerFeatureResult staleResult = ScreenerFeatureCalculator.Calculate(
            baseline.Input, cursor, staleConfiguration);
        ScreenerSnapshot staleSnapshot = new(staleResult, staleConfiguration);

        StagedSelectionResult result = UniverseSelectionStage.Evaluate(
            [Metadata("STALEUSDT")], [staleSnapshot], cursor,
            UniverseConfiguration(250,
            [new StagedSelectionKey(ScreenerFeatureKind.Spread,
                StagedSelectionDirection.Ascending)]));

        Assert.Empty(result.Selected);
        StagedSelectionExclusion exclusion = result.Exclusions.Single();
        Assert.Equal(["feature-not-ready:Spread"], exclusion.Reasons);
        Assert.Empty(exclusion.Comparisons);
    }

    [Fact]
    public void UniverseStageRejectsFutureInputsAndConflictingSnapshotsWithoutUsingTheirValues()
    {
        CausalEventCursor cursor = Cursor();
        StagedSelectionConfiguration configuration = UniverseConfiguration(250,
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]);
        ExchangeEligibilityMetadata[] metadata =
        [Metadata("FUTUREUSDT"), Metadata("CONFLICTUSDT"), Metadata("DUPUSDT")];
        ScreenerSnapshot duplicate = Snapshot("DUPUSDT", 100m, 1m);
        ScreenerSnapshot conflict = Snapshot("CONFLICTUSDT", 100m, 1m);
        ScreenerSnapshot[] snapshots =
        [
            SnapshotWithFutureBar("FUTUREUSDT"),
            conflict,
            Snapshot("CONFLICTUSDT", 999_999m, 1m),
            duplicate,
            duplicate
        ];

        StagedSelectionResult result = UniverseSelectionStage.Evaluate(
            metadata, snapshots, cursor, configuration);

        Assert.Equal(["DUPUSDT"],
            result.Selected.Select(static candidate => candidate.InstrumentId.Value));
        Assert.Equal(["screener-observation-after-cursor"],
            result.Exclusions.Single(
                exclusion => exclusion.Metadata!.InstrumentId.Value == "FUTUREUSDT").Reasons);
        Assert.Equal(["screener-snapshot-conflict"],
            result.Exclusions.Single(
                exclusion => exclusion.Metadata!.InstrumentId.Value == "CONFLICTUSDT").Reasons);
        Assert.DoesNotContain(result.Exclusions,
            exclusion => exclusion.Metadata?.InstrumentId.Value == "DUPUSDT");
    }

    [Fact]
    public void UniverseStageIsPermutationAndCultureIndependentAndRoundTrips()
    {
        CausalEventCursor cursor = Cursor();
        StagedSelectionConfiguration configuration = UniverseConfiguration(250,
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]);
        ExchangeEligibilityMetadata[] metadata =
        [Metadata("aUSDT"), Metadata("AUSDT"), Metadata("BUSDT")];
        ScreenerSnapshot[] snapshots =
        [Snapshot("aUSDT", 100m, 1m), Snapshot("AUSDT", 100m, 1m),
            Snapshot("BUSDT", 50m, 1m)];
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            StagedSelectionResult first = UniverseSelectionStage.Evaluate(
                metadata, snapshots, cursor, configuration);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            StagedSelectionResult second = UniverseSelectionStage.Evaluate(
                metadata.Reverse(), snapshots.Reverse(), cursor, configuration);

            Assert.Equal(first, second);
            Assert.Equal(["AUSDT", "aUSDT", "BUSDT"],
                first.Selected.Select(static candidate => candidate.InstrumentId.Value));
            string json = CoreJsonSerializer.Serialize(first);
            Assert.Equal(first, CoreJsonSerializer.Deserialize<StagedSelectionResult>(json));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void UniverseConfigurationRejectsInvalidKeysAndCapacity()
    {
        Assert.Throws<ArgumentException>(() => UniverseConfiguration(250, []));
        Assert.Throws<ArgumentException>(() => UniverseConfiguration(250,
        [
            new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending),
            new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Ascending)
        ]));
        Assert.Throws<ArgumentException>(() => UniverseConfiguration(250,
        [new StagedSelectionKey(ScreenerFeatureKind.RelativeVolume,
            StagedSelectionDirection.Descending)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => UniverseConfiguration(251,
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]));
    }

    private static StagedSelectionConfiguration UniverseConfiguration(int capacity,
        IEnumerable<StagedSelectionKey> keys) => new(
            algorithmVersion: "universe-v1", stage: StagedSelectionStage.Universe,
            maximumCandidates: capacity, rankingKeys: keys);

    private static CausalEventCursor Cursor() =>
        new(Time(10), 10_000);

    private static ExchangeEligibilityMetadata Metadata(string symbol,
        int availabilityOffsetMinutes = 1)
    {
        return new ExchangeEligibilityMetadata(new InstrumentId(symbol), symbol,
            "PERPETUAL", "TRADING", symbol[..^4], "USDT", "USDT",
            new UtcTimestamp(DateTimeOffset.UnixEpoch),
            new UtcTimestamp(DateTimeOffset.UnixEpoch.AddDays(1)), 8, 8, 8, 8,
            2.5m, 5m,
            [
                new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 0.01m,
                    maxPrice: 1_000_000m, tickSize: 0.01m),
                new ExchangeMetadataFilter("LOT_SIZE", minQuantity: 0.001m,
                    maxQuantity: 100_000m, stepSize: 0.001m),
                new ExchangeMetadataFilter("MIN_NOTIONAL", notional: 5m)
            ], [], At(availabilityOffsetMinutes - 1),
            At(availabilityOffsetMinutes));
    }

    private static ScreenerSnapshot Snapshot(string symbol, decimal quoteVolume,
        decimal askOffset, int stableSequence = 1, bool includeQuote = true)
    {
        InstrumentId instrument = new(symbol);
        ScreenerFeatureConfiguration configuration = FeatureConfiguration();
        ScreenerBar[] bars = Enumerable.Range(0, 5)
            .Select(offset => Bar(instrument, offset, offset == 4 ? quoteVolume : 10m))
            .ToArray();
        ScreenerOpenInterestObservation[] openInterest =
        [
            OpenInterest(instrument, 0, 100m), OpenInterest(instrument, 1, 110m),
            OpenInterest(instrument, 2, 120m)
        ];
        ScreenerQuoteObservation[] quotes = includeQuote
            ? [new ScreenerQuoteObservation("binance.usdm", instrument, Time(5),
                At(5), At(5, 1), stableSequence, 100m, 100m + askOffset, true)]
            : [];
        ScreenerFeatureInput input = new("binance.usdm", instrument, Time(5),
            At(5), At(5, 1), stableSequence, bars, openInterest, quotes);
        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            input, Cursor(), configuration);
        return new ScreenerSnapshot(result, configuration);
    }

    private static ScreenerSnapshot SnapshotWithFutureBar(string symbol)
    {
        ScreenerSnapshot baseline = Snapshot(symbol, 100m, 1m);
        InstrumentId instrument = new(symbol);
        ScreenerBar future = Bar(instrument, 11, 1m);
        ScreenerFeatureInput input = new(baseline.Input.SourceId, instrument,
            baseline.Input.EventTime, baseline.Input.ReceiveTime,
            baseline.Input.AvailabilityTime, baseline.Input.StableSequence,
            baseline.Input.ClosedBars.Concat([future]), baseline.Input.OpenInterest,
            baseline.Input.Quotes);
        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            input, Cursor(), baseline.Configuration);
        return new ScreenerSnapshot(result, baseline.Configuration);
    }

    private static ScreenerFeatureConfiguration FeatureConfiguration() => new(
        timeframe: "1m", relativeVolumeWindow: 1, priceAccelerationWindow: 1,
        atrExpansionWindow: 1, turnoverWindow: 1, openInterestAnomalyWindow: 1,
        version: new ScreenerFeatureVersion("screener-v1"),
        freshnessPolicy: new ScreenerFreshnessPolicy(
            defaultTtl: TimeSpan.FromMinutes(30), barTtl: TimeSpan.FromMinutes(30),
            quoteTtl: TimeSpan.FromMinutes(30),
            openInterestTtl: TimeSpan.FromMinutes(30), sourceTtls: []));

    private static ScreenerBar Bar(InstrumentId instrument, int offset,
        decimal quoteVolume)
    {
        UtcTimestamp open = Time(offset);
        Bar bar = new($"{instrument.Value}-bar-{offset}", instrument, Time(offset),
            open, Time(offset + 1), new Price(100m + offset),
            new Price(102m + offset), new Price(99m + offset),
            new Price(101m + offset), new Quantity(10m));
        return new ScreenerBar("binance.usdm", bar, quoteVolume, At(offset),
            At(offset, 1), offset);
    }

    private static ScreenerOpenInterestObservation OpenInterest(InstrumentId instrument,
        int offset, decimal value) => new("binance.usdm", instrument, Time(offset),
            At(offset), At(offset, 1), offset, value);

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));

    private static UtcTimestamp At(int offset, int milliseconds = 0) =>
        new(BaseTime.AddMinutes(offset).AddMilliseconds(milliseconds));
}
