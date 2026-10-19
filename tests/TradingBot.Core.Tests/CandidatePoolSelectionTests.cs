using System.Globalization;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CandidatePoolSelectionTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CandidatePoolKeepsOnlyUniverseMembershipAndReferencesPreviousStage()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot[] universeSnapshots =
        [Snapshot("BTCUSDT", 100m, 1m), Snapshot("AUSDT", 80m, 1m),
            Snapshot("ETHUSDT", 60m, 1m)];
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            universeSnapshots.Select(static snapshot => Metadata(
                snapshot.Input.InstrumentId.Value)), universeSnapshots, cursor,
            UniverseConfiguration());
        StagedSelectionConfiguration configuration = CandidateConfiguration(2,
        [
            new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending),
            new StagedSelectionKey(ScreenerFeatureKind.RelativeVolume,
                StagedSelectionDirection.Descending)
        ]);
        ScreenerSnapshot[] stageSnapshots =
        [
            Snapshot("ETHUSDT", 500m, 1m),
            Snapshot("BTCUSDT", 200m, 1m),
            Snapshot("NEWUSDT", 999_999m, 1m)
        ];

        StagedSelectionResult result = CandidatePoolSelectionStage.Evaluate(
            universe, stageSnapshots, configuration);

        Assert.Equal(cursor, result.Cursor);
        Assert.Equal(StagedSelectionStage.CandidatePool, result.Stage);
        Assert.Equal(["ETHUSDT", "BTCUSDT"],
            result.Selected.Select(static candidate => candidate.InstrumentId.Value));
        Assert.All(result.Selected, candidate =>
            Assert.Contains(candidate.InstrumentId, universe.Selected
                .Select(static item => item.InstrumentId)));
        Assert.Equal(universe.StageIdentity, result.PreviousStage!.StageIdentity);
        Assert.Equal(StagedSelectionStage.Universe, result.PreviousStage.Stage);
        Assert.Equal(cursor, result.PreviousStage.Cursor);
        Assert.Equal(
            universe.Selected.Select(static item => item.InstrumentId.Value),
            result.PreviousStage.Membership.Select(static item => item.Value));

        StagedSelectionExclusion extra = Assert.Single(result.Exclusions,
            exclusion => exclusion.InstrumentId?.Value == "NEWUSDT");
        Assert.Equal(["not-in-universe"], extra.Reasons);
        Assert.Single(extra.ScreenerEvidence);
        Assert.Equal(1, result.Selected[0].Rank);
        Assert.Equal(2, result.Selected[1].Rank);
    }

    [Fact]
    public void CandidatePoolRequiresReadinessOnlyForConfiguredKeys()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot universeSnapshot = Snapshot("BTCUSDT", 100m, 1m);
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            [Metadata("BTCUSDT")], [universeSnapshot], cursor, UniverseConfiguration());
        ScreenerSnapshot optionalMissing = Snapshot("BTCUSDT", 200m, 1m,
            includeQuote: false);

        StagedSelectionResult turnoverResult = CandidatePoolSelectionStage.Evaluate(
            universe, [optionalMissing], CandidateConfiguration(50,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]));

        Assert.Single(turnoverResult.Selected);
        Assert.Empty(turnoverResult.Exclusions);
        Assert.False(turnoverResult.Selected[0].RecalculatedFeatures.Spread.IsReady);

        StagedSelectionResult spreadResult = CandidatePoolSelectionStage.Evaluate(
            universe, [optionalMissing], CandidateConfiguration(50,
            [new StagedSelectionKey(ScreenerFeatureKind.Spread,
                StagedSelectionDirection.Ascending)]));

        Assert.Empty(spreadResult.Selected);
        Assert.Equal(["feature-not-ready:Spread"],
            Assert.Single(spreadResult.Exclusions,
                exclusion => exclusion.InstrumentId?.Value == "BTCUSDT").Reasons);
    }

    [Fact]
    public void CandidatePoolRejectsCursorMismatchFutureDataAndConflictingSnapshots()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot universeSnapshot = Snapshot("BTCUSDT", 100m, 1m);
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            [Metadata("BTCUSDT")], [universeSnapshot], cursor, UniverseConfiguration());
        ScreenerSnapshot conflict = Snapshot("BTCUSDT", 200m, 1m);
        ScreenerSnapshot future = SnapshotWithFutureBar("BTCUSDT");

        StagedSelectionResult conflictResult = CandidatePoolSelectionStage.Evaluate(
            universe, [universeSnapshot, conflict], CandidateConfiguration(50,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]));
        Assert.Empty(conflictResult.Selected);
        Assert.Equal(["screener-snapshot-conflict"],
            Assert.Single(conflictResult.Exclusions).Reasons);

        ScreenerSnapshot mismatchedCursor = SnapshotWithCursor("BTCUSDT");
        StagedSelectionResult mismatchResult = CandidatePoolSelectionStage.Evaluate(
            universe, [mismatchedCursor], CandidateConfiguration(50,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]));
        Assert.Empty(mismatchResult.Selected);
        Assert.Equal(["screener-cursor-mismatch"],
            Assert.Single(mismatchResult.Exclusions).Reasons);

        StagedSelectionResult futureResult = CandidatePoolSelectionStage.Evaluate(
            universe, [future], CandidateConfiguration(50,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]));
        Assert.Empty(futureResult.Selected);
        Assert.Equal(["screener-observation-after-cursor"],
            Assert.Single(futureResult.Exclusions).Reasons);
    }

    [Fact]
    public void CandidatePoolSupportsEveryMinimumScreenerFeatureWithExplicitDirections()
    {
        StagedSelectionConfiguration configuration = CandidateConfiguration(50,
        [
            new StagedSelectionKey(ScreenerFeatureKind.RelativeVolume,
                StagedSelectionDirection.Descending),
            new StagedSelectionKey(ScreenerFeatureKind.PriceAcceleration,
                StagedSelectionDirection.Ascending),
            new StagedSelectionKey(ScreenerFeatureKind.AtrExpansion,
                StagedSelectionDirection.Descending),
            new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending),
            new StagedSelectionKey(ScreenerFeatureKind.Spread,
                StagedSelectionDirection.Ascending),
            new StagedSelectionKey(ScreenerFeatureKind.OpenInterestAnomaly,
                StagedSelectionDirection.Descending)
        ]);

        Assert.Equal(6, configuration.RankingKeys.Count);
    }

    [Fact]
    public void CandidatePoolIsPermutationIndependentAndRoundTripsItsStageReference()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot[] universeSnapshots =
        [Snapshot("BTCUSDT", 100m, 1m), Snapshot("AUSDT", 100m, 1m),
            Snapshot("BUSDT", 50m, 1m)];
        StagedSelectionConfiguration universeConfiguration = UniverseConfiguration();
        StagedSelectionConfiguration configuration = CandidateConfiguration(50,
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]);
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            StagedSelectionResult firstUniverse = UniverseSelectionStage.Evaluate(
                universeSnapshots.Select(static snapshot =>
                    Metadata(snapshot.Input.InstrumentId.Value)), universeSnapshots, cursor,
                universeConfiguration);
            StagedSelectionResult first = CandidatePoolSelectionStage.Evaluate(
                firstUniverse, universeSnapshots, configuration);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            StagedSelectionResult secondUniverse = UniverseSelectionStage.Evaluate(
                universeSnapshots.Reverse().Select(static snapshot =>
                    Metadata(snapshot.Input.InstrumentId.Value)),
                universeSnapshots.Reverse(), cursor, universeConfiguration);
            StagedSelectionResult second = CandidatePoolSelectionStage.Evaluate(
                secondUniverse, universeSnapshots.Reverse(), configuration);

            Assert.Equal(first, second);
            Assert.Equal(first.StageIdentity, second.StageIdentity);
            Assert.Equal(first, CoreJsonSerializer.Deserialize<StagedSelectionResult>(
                CoreJsonSerializer.Serialize(first)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CandidatePoolTruncatesAtFiftyWithRankedCapacityProvenance()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot[] snapshots = Enumerable.Range(0, 51)
            .Select(index => Snapshot($"S{index:000}USDT", 100m, 1m))
            .ToArray();
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            snapshots.Select(static snapshot => Metadata(
                snapshot.Input.InstrumentId.Value)), snapshots, cursor,
            UniverseConfiguration());

        StagedSelectionResult result = CandidatePoolSelectionStage.Evaluate(
            universe, snapshots, CandidateConfiguration(50,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]));

        Assert.Equal(50, result.Selected.Count);
        StagedSelectionExclusion exclusion = Assert.Single(result.Exclusions);
        Assert.Equal("S050USDT", exclusion.InstrumentId!.Value);
        Assert.Equal(["capacity-exceeded"], exclusion.Reasons);
        Assert.Equal(51, exclusion.Rank);
    }

    private static StagedSelectionConfiguration UniverseConfiguration() => new(
        "universe-v1", StagedSelectionStage.Universe, 250,
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]);

    private static StagedSelectionConfiguration CandidateConfiguration(int capacity,
        IEnumerable<StagedSelectionKey> keys) => new(
        "candidate-v1", StagedSelectionStage.CandidatePool, capacity, keys);

    private static CausalEventCursor Cursor() => new(Time(10), 10_000);

    private static ExchangeEligibilityMetadata Metadata(string symbol) =>
        new(new InstrumentId(symbol), symbol, "PERPETUAL", "TRADING", symbol[..^4],
            "USDT", "USDT", new UtcTimestamp(DateTimeOffset.UnixEpoch),
            new UtcTimestamp(DateTimeOffset.UnixEpoch.AddDays(1)), 8, 8, 8, 8,
            2.5m, 5m,
            [
                new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 0.01m,
                    maxPrice: 1_000_000m, tickSize: 0.01m),
                new ExchangeMetadataFilter("LOT_SIZE", minQuantity: 0.001m,
                    maxQuantity: 100_000m, stepSize: 0.001m),
                new ExchangeMetadataFilter("MIN_NOTIONAL", notional: 5m)
            ], [], At(0), At(1));

    private static ScreenerSnapshot Snapshot(string symbol, decimal quoteVolume,
        decimal askOffset, bool includeQuote = true)
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
                At(5), At(5, 1), 1, 100m, 100m + askOffset, true)]
            : [];
        ScreenerFeatureInput input = new("binance.usdm", instrument, Time(5),
            At(5), At(5, 1), 1, bars, openInterest, quotes);
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

    private static ScreenerSnapshot SnapshotWithCursor(string symbol)
    {
        ScreenerSnapshot baseline = Snapshot(symbol, 100m, 1m);
        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            baseline.Input, new CausalEventCursor(Time(9), 10_000),
            baseline.Configuration);
        return new ScreenerSnapshot(result, baseline.Configuration);
    }

    private static ScreenerFeatureConfiguration FeatureConfiguration() => new(
        "1m", 1, 1, 1, 1, 1, new ScreenerFeatureVersion("screener-v1"),
        new ScreenerFreshnessPolicy(
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
