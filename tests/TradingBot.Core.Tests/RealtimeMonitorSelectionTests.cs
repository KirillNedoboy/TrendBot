using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class RealtimeMonitorSelectionTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RealtimeMonitorKeepsCandidateMembershipAndReferencesPreviousStage()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot[] universeSnapshots =
        [Snapshot("BTCUSDT", 100m, 1m), Snapshot("AUSDT", 80m, 1m),
            Snapshot("ETHUSDT", 60m, 1m)];
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            universeSnapshots.Select(static snapshot => Metadata(
                snapshot.Input.InstrumentId.Value)), universeSnapshots, cursor,
            UniverseConfiguration());
        ScreenerSnapshot[] candidateEvidence =
        [Snapshot("BTCUSDT", 300m, 1m), Snapshot("AUSDT", 200m, 1m),
            Snapshot("ETHUSDT", 100m, 1m)];
        StagedSelectionResult candidate = CandidatePoolSelectionStage.Evaluate(
            universe, candidateEvidence, CandidatePoolConfiguration());
        ScreenerSnapshot[] monitorEvidence =
        [Snapshot("AUSDT", 700m, 1m), Snapshot("BTCUSDT", 600m, 1m),
            Snapshot("ETHUSDT", 500m, 1m),
            Snapshot("NEWUSDT", 999_999m, 1m)];

        StagedSelectionResult result = RealtimeMonitorSelectionStage.Evaluate(
            candidate, monitorEvidence, RealtimeConfiguration());

        Assert.Equal(cursor, result.Cursor);
        Assert.Equal(StagedSelectionStage.RealtimeMonitor, result.Stage);
        Assert.Equal(["AUSDT", "BTCUSDT", "ETHUSDT"],
            result.Selected.Select(static item => item.InstrumentId.Value));
        Assert.Equal(StagedSelectionStage.CandidatePool, result.PreviousStage!.Stage);
        Assert.Equal(candidate.StageIdentity, result.PreviousStage.StageIdentity);
        Assert.Equal(cursor, result.PreviousStage.Cursor);
        Assert.Equal(candidate.Selected.Select(static item => item.InstrumentId.Value),
            result.PreviousStage.Membership.Select(static item => item.Value));
        StagedSelectionExclusion extra = Assert.Single(result.Exclusions,
            exclusion => exclusion.InstrumentId?.Value == "NEWUSDT");
        Assert.Equal(["not-in-candidate-pool"], extra.Reasons);
        Assert.Single(extra.ScreenerEvidence);
    }

    [Fact]
    public void RealtimeMonitorRequiresReadinessOnlyForConfiguredKeys()
    {
        ScreenerSnapshot universeSnapshot = Snapshot("BTCUSDT", 100m, 1m);
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            [Metadata("BTCUSDT")], [universeSnapshot], Cursor(),
            UniverseConfiguration());
        StagedSelectionResult candidate = CandidatePoolSelectionStage.Evaluate(
            universe, [universeSnapshot], CandidatePoolConfiguration());
        ScreenerSnapshot optionalMissing = Snapshot("BTCUSDT", 200m, 1m,
            includeQuote: false);

        StagedSelectionResult turnoverResult = RealtimeMonitorSelectionStage.Evaluate(
            candidate, [optionalMissing], RealtimeConfiguration(
                [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                    StagedSelectionDirection.Descending)]));
        Assert.Single(turnoverResult.Selected);
        Assert.Empty(turnoverResult.Exclusions);
        Assert.False(turnoverResult.Selected[0].RecalculatedFeatures.Spread.IsReady);

        StagedSelectionResult spreadResult = RealtimeMonitorSelectionStage.Evaluate(
            candidate, [optionalMissing], RealtimeConfiguration(
                [new StagedSelectionKey(ScreenerFeatureKind.Spread,
                    StagedSelectionDirection.Ascending)]));
        Assert.Empty(spreadResult.Selected);
        Assert.Equal(["feature-not-ready:Spread"],
            Assert.Single(spreadResult.Exclusions).Reasons);
    }

    [Fact]
    public void RealtimeMonitorRejectsConflictsAndFutureEvidenceAndSupportsAllSixKeys()
    {
        ScreenerSnapshot universeSnapshot = Snapshot("BTCUSDT", 100m, 1m);
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            [Metadata("BTCUSDT")], [universeSnapshot], Cursor(),
            UniverseConfiguration());
        StagedSelectionResult candidate = CandidatePoolSelectionStage.Evaluate(
            universe, [universeSnapshot], CandidatePoolConfiguration());
        ScreenerSnapshot conflict = Snapshot("BTCUSDT", 200m, 1m);

        StagedSelectionResult conflictResult = RealtimeMonitorSelectionStage.Evaluate(
            candidate, [universeSnapshot, conflict], RealtimeConfiguration());
        Assert.Empty(conflictResult.Selected);
        Assert.Equal(["screener-snapshot-conflict"],
            Assert.Single(conflictResult.Exclusions).Reasons);

        StagedSelectionResult futureResult = RealtimeMonitorSelectionStage.Evaluate(
            candidate, [SnapshotWithFutureBar("BTCUSDT")], RealtimeConfiguration());
        Assert.Empty(futureResult.Selected);
        Assert.Equal(["screener-observation-after-cursor"],
            Assert.Single(futureResult.Exclusions).Reasons);

        StagedSelectionConfiguration allKeys = RealtimeConfiguration(
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
        Assert.Equal(6, allKeys.RankingKeys.Count);
    }

    [Fact]
    public void RealtimeMonitorEnforcesTwentyItemCapacityWithProvenance()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot[] snapshots = Enumerable.Range(0, 21)
            .Select(index => Snapshot($"S{index:000}USDT", 100m, 1m))
            .ToArray();
        StagedSelectionResult universe = UniverseSelectionStage.Evaluate(
            snapshots.Select(static snapshot => Metadata(
                snapshot.Input.InstrumentId.Value)), snapshots, cursor,
            UniverseConfiguration());
        StagedSelectionResult candidate = CandidatePoolSelectionStage.Evaluate(
            universe, snapshots, CandidatePoolConfiguration());

        StagedSelectionResult result = RealtimeMonitorSelectionStage.Evaluate(
            candidate, snapshots, RealtimeConfiguration());

        Assert.Equal(20, result.Selected.Count);
        StagedSelectionExclusion exclusion = Assert.Single(result.Exclusions);
        Assert.Equal("S020USDT", exclusion.InstrumentId!.Value);
        Assert.Equal(["capacity-exceeded"], exclusion.Reasons);
        Assert.Equal(21, exclusion.Rank);
    }

    [Fact]
    public void ChainSnapshotRoundTripsAndReplaysWithTheSameIdentity()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot[] universeSnapshots =
        [Snapshot("BTCUSDT", 100m, 1m), Snapshot("AUSDT", 80m, 1m),
            Snapshot("ETHUSDT", 60m, 1m)];
        StagedSelectionInput input = new(cursor,
            universeSnapshots.Select(static snapshot => Metadata(
                snapshot.Input.InstrumentId.Value)), universeSnapshots);
        ScreenerSnapshot[] candidateEvidence =
        [Snapshot("BTCUSDT", 300m, 1m), Snapshot("AUSDT", 200m, 1m),
            Snapshot("ETHUSDT", 100m, 1m)];
        ScreenerSnapshot[] monitorEvidence =
        [Snapshot("BTCUSDT", 600m, 1m), Snapshot("AUSDT", 700m, 1m),
            Snapshot("ETHUSDT", 500m, 1m)];

        StagedSelectionChainSnapshot chain = StagedSelectionChainSnapshot.Create(
            input, UniverseConfiguration(), candidateEvidence,
            CandidatePoolConfiguration(), monitorEvidence, RealtimeConfiguration());

        string json = CoreJsonSerializer.Serialize(chain);
        StagedSelectionChainSnapshot restored =
            CoreJsonSerializer.Deserialize<StagedSelectionChainSnapshot>(
                json);
        StagedSelectionChainSnapshot replayed = chain.Replay();

        Assert.Contains("\"formatVersion\":1", json);
        Assert.Contains("\"algorithmVersion\":\"staged-selection-v1\"", json);
        Assert.DoesNotContain("snapshotIdentity", json);
        Assert.Equal(chain.SnapshotIdentity, restored.SnapshotIdentity);
        Assert.Equal(chain, restored);
        Assert.Equal(chain.SnapshotIdentity, replayed.SnapshotIdentity);
        Assert.Equal(chain.UniverseResult, replayed.UniverseResult);
        Assert.Equal(chain.CandidatePoolResult, replayed.CandidatePoolResult);
        Assert.Equal(chain.RealtimeMonitorResult, replayed.RealtimeMonitorResult);
        Assert.Equal(chain.UniverseResult.StageIdentity,
            chain.CandidatePoolResult.PreviousStage!.StageIdentity);
        Assert.Equal(chain.CandidatePoolResult.StageIdentity,
            chain.RealtimeMonitorResult.PreviousStage!.StageIdentity);
    }

    [Fact]
    public void ChainSnapshotRejectsAlteredLinksAndUnsupportedVersions()
    {
        StagedSelectionChainSnapshot chain = CreateChain();
        string json = CoreJsonSerializer.Serialize(chain);

        JsonObject alteredLink = JsonNode.Parse(json)!.AsObject();
        alteredLink["candidatePoolResult"]!["previousStage"]!["stageIdentity"] =
            "0000000000000000000000000000000000000000000000000000000000000000";
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<StagedSelectionChainSnapshot>(
                alteredLink.ToJsonString()));

        JsonObject skippedStage = JsonNode.Parse(json)!.AsObject();
        skippedStage["realtimeMonitorResult"]!["previousStage"]!["stage"] =
            "universe";
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<StagedSelectionChainSnapshot>(
                skippedStage.ToJsonString()));

        JsonObject unsupportedFormat = JsonNode.Parse(json)!.AsObject();
        unsupportedFormat["formatVersion"] = 99;
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<StagedSelectionChainSnapshot>(
                unsupportedFormat.ToJsonString()));

        JsonObject unsupportedAlgorithm = JsonNode.Parse(json)!.AsObject();
        unsupportedAlgorithm["algorithmVersion"] = "staged-selection-v999";
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<StagedSelectionChainSnapshot>(
                unsupportedAlgorithm.ToJsonString()));
    }

    private static StagedSelectionChainSnapshot CreateChain()
    {
        CausalEventCursor cursor = Cursor();
        ScreenerSnapshot[] snapshots =
        [Snapshot("BTCUSDT", 100m, 1m), Snapshot("AUSDT", 80m, 1m)];
        return StagedSelectionChainSnapshot.Create(
            new StagedSelectionInput(cursor,
                snapshots.Select(static snapshot => Metadata(
                    snapshot.Input.InstrumentId.Value)), snapshots),
            UniverseConfiguration(), snapshots, CandidatePoolConfiguration(), snapshots,
            RealtimeConfiguration());
    }

    private static StagedSelectionConfiguration UniverseConfiguration() => new(
        "universe-v1", StagedSelectionStage.Universe, 250,
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]);

    private static StagedSelectionConfiguration CandidatePoolConfiguration() => new(
        "candidate-v1", StagedSelectionStage.CandidatePool, 50,
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]);

    private static StagedSelectionConfiguration RealtimeConfiguration(
        IEnumerable<StagedSelectionKey>? keys = null) => new(
        "monitor-v1", StagedSelectionStage.RealtimeMonitor, 20,
        keys ??
        [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
            StagedSelectionDirection.Descending)]);

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
