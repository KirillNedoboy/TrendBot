using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneCFeatureTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly LaneCFeatureConfiguration Configuration = new(
        "lane-c-formulas-v1", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(5), 1, 100, 2m, 1m, 0.5m);
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void EvaluatesSpreadTopDepthImbalanceFlowChangesAndAbsorptionEvidence()
    {
        LaneCFeatureResult result = Evaluate(CreateHistory(), Configuration, 3,
            BaseTime.AddSeconds(3));

        Assert.Equal(LaneCFeatureState.Ready, result.State);
        Assert.Equal(99m, result.BestBid);
        Assert.Equal(100m, result.BestAsk);
        Assert.Equal(1m, result.Spread);
        Assert.Equal(1m, result.TopBidDepth);
        Assert.Equal(5m, result.TopAskDepth);
        Assert.Equal(-2m / 3m, result.BookImbalance);
        Assert.Equal(3m, result.AggressiveBuyVolume);
        Assert.Equal(1m, result.AggressiveSellVolume);
        Assert.Equal(0.5m, result.AggressorImbalance);
        Assert.Equal(1m, result.RemovedBidLiquidity);
        Assert.Equal(5m, result.AddedAskLiquidity);
        Assert.Equal(-0.5m, result.MidPriceMove);
        Assert.Equal(0.125m, result.PriceResponsePerAggressorVolume);
        Assert.True(result.AbsorptionCandidate);
        Assert.Equal(1m, Assert.Single(result.Levels, level =>
            level.Side == LaneCLevelSide.Bid && level.Price.Value == 99m).CurrentQuantity);
        Assert.True(Assert.Single(result.Levels, level =>
            level.Side == LaneCLevelSide.Ask && level.Price.Value == 100m).IsWallCandidate);
    }

    [Fact]
    public void RejectsDepthGapAndStaleBookWithoutProducingFeatures()
    {
        CaptureEnvelope[] gapHistory = [Snapshot(0), GapDiagnostic(1)];
        LaneCFeatureResult gap = Evaluate(gapHistory, Configuration, 1,
            BaseTime.AddSeconds(3));
        LaneCFeatureResult stale = Evaluate(CreateHistory(), Configuration, 3,
            BaseTime.AddSeconds(10));

        Assert.Equal(LaneCFeatureState.SequenceGap, gap.State);
        Assert.Equal("depth-sequence-gap", Assert.Single(gap.Reasons));
        Assert.Equal(LaneCFeatureState.Stale, stale.State);
        Assert.Equal("book-stale", Assert.Single(stale.Reasons));
        Assert.Null(stale.Spread);
    }

    [Fact]
    public void TracksWallPersistenceReplenishmentWithdrawalAndSizeMigrationCandidates()
    {
        CaptureEnvelope[] history =
        [
            Snapshot(0),
            DepthRecord(1, new BookDelta("book-update-1", Instrument, Utc(1),
                [new BookLevel(new Price(99m), new Quantity(1m))],
                [new BookLevel(new Price(101m), new Quantity(0m)),
                 new BookLevel(new Price(100m), new Quantity(3m))]),
                new CaptureDepthMetadata(101, 101, 100, 1, false)),
            DepthRecord(2, new BookDelta("book-update-2", Instrument, Utc(2),
                [new BookLevel(new Price(99m), new Quantity(3m))],
                [new BookLevel(new Price(100m), new Quantity(0m)),
                 new BookLevel(new Price(103m), new Quantity(3m))]),
                new CaptureDepthMetadata(102, 102, 101, 1, false))
        ];

        LaneCFeatureResult result = Evaluate(history, Configuration, 2,
            BaseTime.AddSeconds(3));
        LaneCLevelFeature replenishedBid = Assert.Single(result.Levels, level =>
            level.Side == LaneCLevelSide.Bid && level.Price.Value == 99m);
        LaneCLevelFeature withdrawnAsk = Assert.Single(result.Levels, level =>
            level.Side == LaneCLevelSide.Ask && level.Price.Value == 101m);
        LaneCLevelFeature movedAsk = Assert.Single(result.Levels, level =>
            level.Side == LaneCLevelSide.Ask && level.Price.Value == 103m);
        LaneCLevelFeature persistentBid = Assert.Single(result.Levels, level =>
            level.Side == LaneCLevelSide.Bid && level.Price.Value == 98m);

        Assert.Equal(LaneCFeatureState.WarmingUp, result.State);
        Assert.Equal(1, replenishedBid.ReplenishmentCandidateCount);
        Assert.Equal(1, withdrawnAsk.WithdrawalCandidateCount);
        Assert.True(movedAsk.IsWallCandidate);
        Assert.Equal(3, persistentBid.ObservedBookCount);
        Assert.True(persistentBid.IsWallCandidate);
        Assert.Equal(1, result.WallMigrationCandidateCount);
    }

    [Fact]
    public void AppliesCursorBeforeWindowMetricsAndPreservesZeroDenominators()
    {
        CaptureEnvelope[] records = [.. CreateHistory(), FutureTrade(4)];
        LaneCFeatureResult result = Evaluate(records, Configuration, 3,
            BaseTime.AddSeconds(3));
        LaneCFeatureResult emptyBook = Evaluate([EmptySnapshot(0)], Configuration, 0,
            BaseTime.AddSeconds(3));

        Assert.Equal(3m, result.AggressiveBuyVolume);
        Assert.DoesNotContain(result.Reasons, static reason => reason.Contains("future",
            StringComparison.OrdinalIgnoreCase));
        Assert.Null(emptyBook.BestBid);
        Assert.Null(emptyBook.BestAsk);
        Assert.Null(emptyBook.BookImbalance);
        Assert.Contains("book-imbalance-zero-denominator", emptyBook.Reasons);
    }

    [Fact]
    public void IncludesWindowBoundaryAndExcludesTheRecordImmediatelyBeforeIt()
    {
        LaneCFeatureConfiguration oneSecondWindow = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            1, 100, 2m, 1m, 0.5m);
        CaptureEnvelope[] records = [.. CreateHistory(), TradeRecord(4, "before-window",
            TradeSide.Buy, 100m, 99m, BaseTime.AddMilliseconds(1999))];

        LaneCFeatureResult result = Evaluate(records, oneSecondWindow, 4,
            BaseTime.AddSeconds(3));

        Assert.Equal(3m, result.AggressiveBuyVolume);
        Assert.Equal(1m, result.AggressiveSellVolume);
        Assert.Equal(LaneCFeatureState.WarmingUp, result.State);
    }

    [Fact]
    public void MarksTradeEvidenceStaleWhenItsExplicitAvailabilityAgeExpires()
    {
        LaneCFeatureConfiguration shortTradeFreshness = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500),
            1, 100, 2m, 1m, 0.5m);

        LaneCFeatureResult result = Evaluate(CreateHistory(), shortTradeFreshness, 3,
            BaseTime.AddSeconds(3));

        Assert.Equal(LaneCFeatureState.Stale, result.State);
        Assert.Equal("trade-stale", Assert.Single(result.Reasons));
    }

    [Fact]
    public void ExplicitUnknownThresholdsRemainUnknownAndReplayIdentityIsStable()
    {
        LaneCFeatureConfiguration unknown = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            1, 100);
        LaneCFeatureInput input = Input(CreateHistory(), 3, BaseTime.AddSeconds(3));
        LaneCFeatureResult first = LaneCFeatureEvaluator.Evaluate(input, unknown);
        LaneCFeatureResult second = LaneCFeatureEvaluator.Evaluate(input, unknown);
        string json = CoreJsonSerializer.Serialize(first);
        LaneCFeatureResult restored = CoreJsonSerializer.Deserialize<LaneCFeatureResult>(json);

        Assert.Equal(LaneCFeatureState.Ready, first.State);
        Assert.Null(first.AbsorptionCandidate);
        Assert.All(first.Levels, static level => Assert.Null(level.IsWallCandidate));
        Assert.Contains("absorption-criteria-unknown", first.Reasons);
        Assert.Contains("wall-quantity-floor-unknown", first.Reasons);
        Assert.Equal(first, second);
        Assert.Equal(first, restored);
        Assert.Equal(first.InputIdentity, restored.InputIdentity);
    }

    [Fact]
    public void RejectsInputBeyondExplicitBound()
    {
        LaneCFeatureConfiguration bounded = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            1, 1);
        LaneCFeatureInput input = Input(CreateHistory(), 3, BaseTime.AddSeconds(3));

        Assert.Throws<ArgumentException>(() => LaneCFeatureEvaluator.Evaluate(input, bounded));
    }

    private static LaneCFeatureResult Evaluate(IEnumerable<CaptureEnvelope> records,
        LaneCFeatureConfiguration configuration, long maxStableSequence,
        DateTimeOffset asOf) => LaneCFeatureEvaluator.Evaluate(
        Input(records, maxStableSequence, asOf), configuration);

    private static LaneCFeatureInput Input(IEnumerable<CaptureEnvelope> records,
        long maxStableSequence, DateTimeOffset asOf) => new(Instrument, records,
        new CausalEventCursor(new UtcTimestamp(asOf), maxStableSequence), "synthetic");

    private static CaptureEnvelope[] CreateHistory() =>
    [
        Snapshot(0),
        DepthUpdate(1),
        TradeRecord(2, "buy", TradeSide.Buy, 3m, 99m, BaseTime.AddSeconds(2)),
        TradeRecord(3, "sell", TradeSide.Sell, 1m, 100m, BaseTime.AddSeconds(2))
    ];

    private static CaptureEnvelope Snapshot(long sequence) => DepthRecord(sequence,
        new BookDelta("book-snapshot", Instrument, Utc(0),
            [new BookLevel(new Price(99m), new Quantity(2m)),
             new BookLevel(new Price(98m), new Quantity(3m))],
            [new BookLevel(new Price(101m), new Quantity(4m)),
             new BookLevel(new Price(102m), new Quantity(1m))]),
        new CaptureDepthMetadata(100, 100, null, 1, true));

    private static CaptureEnvelope EmptySnapshot(long sequence) => DepthRecord(sequence,
        new BookDelta("book-empty", Instrument, Utc(0), [], []),
        new CaptureDepthMetadata(100, 100, null, 1, true));

    private static CaptureEnvelope DepthUpdate(long sequence) => DepthRecord(sequence,
        new BookDelta("book-update", Instrument, Utc(1),
            [new BookLevel(new Price(99m), new Quantity(1m))],
            [new BookLevel(new Price(100m), new Quantity(5m))]),
        new CaptureDepthMetadata(101, 101, 100, 1, false));

    private static CaptureEnvelope GapDiagnostic(long sequence) =>
        CaptureEnvelope.FromDiagnostic("lane-c", sequence, Utc(1), Utc(1), sequence,
            new CaptureDiagnostic("orderbook-Gap", "Sequence gap", ["BTCUSDT"]),
            Provenance(sequence));

    private static CaptureEnvelope FutureTrade(long sequence) => TradeRecord(sequence,
        "future", TradeSide.Buy, 100m, 100m, BaseTime.AddSeconds(9));

    private static CaptureEnvelope TradeRecord(long sequence, string eventId,
        TradeSide side, decimal quantity, decimal price, DateTimeOffset eventTime) =>
        CaptureEnvelope.FromMarketEvent("lane-c", sequence,
            new Trade(eventId, Instrument, new UtcTimestamp(eventTime),
                new Price(price), new Quantity(quantity), side),
            new UtcTimestamp(eventTime), new UtcTimestamp(eventTime), sequence,
            Provenance(sequence));

    private static CaptureEnvelope DepthRecord(long sequence, BookDelta delta,
        CaptureDepthMetadata metadata) => CaptureEnvelope.FromMarketEvent("lane-c", sequence,
        delta, new UtcTimestamp(delta.EventTime.Value),
        new UtcTimestamp(delta.EventTime.Value), sequence, Provenance(sequence, metadata));

    private static CaptureProvenance Provenance(long sequence,
        CaptureDepthMetadata? depth = null) => new("synthetic", "fixture", "session",
        null, null, new ImmutableValueList<string>([]), depth, sequence);

    private static UtcTimestamp Utc(int seconds) =>
        new(BaseTime.AddSeconds(seconds));
}
