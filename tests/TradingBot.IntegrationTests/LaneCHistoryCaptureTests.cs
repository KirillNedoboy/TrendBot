using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LaneCHistoryCaptureTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] IdentityDiagnosticCodes =
        ["binance-stream-Duplicate", "binance-stream-IdentityConflict"];

    [Fact]
    public async Task SnapshotDepthAndTradesReopenInCausalOrderWithSourceProvenance()
    {
        using TemporaryCapture fixture = new();
        CapturePersistenceOptions options = new() { StreamId = "lane-c" };
        DateTimeOffset time = BaseTime;
        BinanceOrderBookState snapshot = ReadyBook(7, 5, time);
        CaptureEnvelope[] expected;

        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options))
        {
            LaneCHistoryCapturePipeline pipeline = new(store, "lane-c");
            CaptureEnvelope snapshotRow = pipeline.CaptureRecoveredSnapshot(snapshot);
            BinanceDepthUpdateEvent depth = new("BTCUSDT", ToMillis(time.AddMilliseconds(2)),
                101, 102, 100, [new BinanceDepthLevel(99m, 3m)], [], "btcusdt@depth@100ms");
            CaptureEnvelope depthRow = Assert.IsType<CaptureEnvelope>(pipeline.CaptureStreamEvent(
                Stream("depth-session", 41, depth, time.AddMilliseconds(3),
                    time.AddMilliseconds(5)), 7));
            BinanceAggregateTradeEvent trade = new("BTCUSDT", ToMillis(time.AddMilliseconds(2)),
                90, 100m, 0.5m, 90, 90, ToMillis(time.AddMilliseconds(2)), false,
                "btcusdt@aggTrade");
            CaptureEnvelope tradeRow = Assert.IsType<CaptureEnvelope>(pipeline.CaptureStreamEvent(
                Stream("trade-session", 0, trade, time.AddMilliseconds(4),
                    time.AddMilliseconds(6)), 7));

            Assert.Equal(new long[] { 0, 1, 2 },
                new[] { snapshotRow.StableSequence, depthRow.StableSequence, tradeRow.StableSequence });
            Assert.Equal(41, depthRow.Provenance.SourceStableSequence);
            Assert.Equal(0, tradeRow.Provenance.SourceStableSequence);
            Assert.Equal(99, snapshotRow.Provenance.DepthMetadata?.FinalUpdateId);
            Assert.True(snapshotRow.Provenance.DepthMetadata?.IsSnapshot);
            Assert.Null(snapshotRow.Provenance.SourceStableSequence);
            Assert.Equal(time.AddMilliseconds(-1), snapshotRow.EventTime.Value);
            Assert.Equal(time.AddMilliseconds(-2), snapshotRow.ExchangeTime?.Value);
            Assert.Equal(101, depthRow.Provenance.DepthMetadata?.FirstUpdateId);
            Assert.Equal(102, depthRow.Provenance.DepthMetadata?.FinalUpdateId);
            Assert.Equal(100, depthRow.Provenance.DepthMetadata?.PreviousUpdateId);
            Assert.Equal(7, depthRow.Provenance.DepthMetadata?.RecoveryGeneration);
            Assert.Equal(time.AddMilliseconds(2), depthRow.EventTime.Value);
            Assert.Equal(time.AddMilliseconds(3), depthRow.ReceiveTime.Value);
            Assert.Equal(time.AddMilliseconds(4), tradeRow.ReceiveTime.Value);
            Assert.Equal(time.AddMilliseconds(5), depthRow.AvailabilityTime.Value);
            Assert.Equal(time.AddMilliseconds(1), depthRow.ExchangeTime?.Value);
            await store.FlushAsync(TestContext.Current.CancellationToken);
            expected = (await store.ReadAllAsync(TestContext.Current.CancellationToken)).ToArray();
        }

        await using ParquetCaptureStore reopened = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options);
        CaptureEnvelope[] actual = (await reopened.ReadAllAsync(
            TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(new long[] { 0, 1, 2 }, actual.Select(static row => row.CaptureSequence));
        Assert.Equal("trade-session", actual[2].Provenance.SessionId);
    }

    [Fact]
    public void DepthCaptureFailsClosedAfterRecoveryGapUntilNewSnapshot()
    {
        using TemporaryCapture fixture = new();
        CapturePersistenceOptions options = new() { StreamId = "lane-c-gap" };
        using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options);
        LaneCHistoryCapturePipeline pipeline = new(store, "lane-c-gap");
        DateTimeOffset time = BaseTime;
        pipeline.CaptureRecoveredSnapshot(ReadyBook(8, 6, time));
        CaptureEnvelope gap = pipeline.CaptureStreamDiagnostic(new BinanceStreamDiagnostic(
            BinanceStreamDiagnosticKind.Gap, "depth-session", "btcusdt@depth@100ms",
            "depth:BTCUSDT:102:103", "Sequence gap", 7,
            new UtcTimestamp(time.AddMilliseconds(2))));
        Assert.Equal(CaptureRecordKind.Diagnostic, gap.Kind);

        BinanceDepthUpdateEvent update = new("BTCUSDT", ToMillis(time.AddMilliseconds(1)),
            102, 103, 101, [], [], "btcusdt@depth@100ms");
        Assert.Throws<InvalidOperationException>(() => pipeline.CaptureStreamEvent(
            Stream("depth-session", 1, update, time.AddMilliseconds(2),
                time.AddMilliseconds(3)), 8));

        CaptureEnvelope recovered = pipeline.CaptureRecoveredSnapshot(ReadyBook(9, 7, time));
        CaptureEnvelope resumed = Assert.IsType<CaptureEnvelope>(pipeline.CaptureStreamEvent(
            Stream("depth-session-2", 0, update, time.AddMilliseconds(2),
                time.AddMilliseconds(3)), 9));
        Assert.Equal(9, recovered.Provenance.DepthMetadata?.RecoveryGeneration);
        Assert.Equal(9, resumed.Provenance.DepthMetadata?.RecoveryGeneration);
    }

    [Fact]
    public async Task DuplicateAndIdentityConflictDiagnosticsRemainInCaptureHistory()
    {
        using TemporaryCapture fixture = new();
        CapturePersistenceOptions options = new() { StreamId = "lane-c-diagnostics" };
        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options))
        {
            LaneCHistoryCapturePipeline pipeline = new(store, "lane-c-diagnostics");
            foreach (BinanceStreamDiagnosticKind kind in new[]
            {
                BinanceStreamDiagnosticKind.Duplicate,
                BinanceStreamDiagnosticKind.IdentityConflict
            })
            {
                pipeline.CaptureStreamDiagnostic(new BinanceStreamDiagnostic(kind,
                    "depth-session", "btcusdt@depth@100ms", "depth:BTCUSDT:1:2",
                    "Repeated source identity", 11,
                    new UtcTimestamp(BaseTime.AddMilliseconds(1))));
            }

            await store.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using ParquetCaptureStore reopened = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options);
        CaptureEnvelope[] rows = (await reopened.ReadAllAsync(
            TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal(IdentityDiagnosticCodes,
            rows.Select(static row => row.Diagnostic!.Code));
        Assert.All(rows, row => Assert.Equal(11, row.Provenance.SourceStableSequence));
    }

    private static BinanceOrderBookState ReadyBook(long generation, long stableSequence,
        DateTimeOffset time)
    {
        DateTimeOffset eventTime = time.AddMilliseconds(1);
        return new BinanceOrderBookState("BTCUSDT", BinanceOrderBookStateKind.Ready,
            100, 99, [new BinanceDepthLevel(99m, 2m)],
            [new BinanceDepthLevel(101m, 3m)], generation, stableSequence,
            ToMillis(eventTime), ToMillis(eventTime), new UtcTimestamp(time),
            new UtcTimestamp(time.AddMilliseconds(1)), new UtcTimestamp(time.AddMilliseconds(-1)),
            new UtcTimestamp(time), new UtcTimestamp(time.AddMilliseconds(1)),
            ToMillis(time.AddMilliseconds(-1)), ToMillis(time.AddMilliseconds(-2)));
    }

    private static BinanceStreamEnvelope Stream(string sessionId, long sourceSequence,
        BinanceWebSocketEvent payload, DateTimeOffset receiveTime, DateTimeOffset availabilityTime) =>
        new(sessionId, sourceSequence, payload, new UtcTimestamp(receiveTime),
            new UtcTimestamp(availabilityTime), new UtcTimestamp(BaseTime.AddMilliseconds(1)),
            "{\"synthetic\":true}");

    private static long ToMillis(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private sealed class TemporaryCapture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "tradingbot-lane-c-history-" + Guid.NewGuid().ToString("N"));

        public TemporaryCapture()
        {
            Directory.CreateDirectory(_root);
        }

        public string DatabasePath => Path.Combine(_root, "capture.db");

        public string SegmentDirectory => Path.Combine(_root, "segments");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
