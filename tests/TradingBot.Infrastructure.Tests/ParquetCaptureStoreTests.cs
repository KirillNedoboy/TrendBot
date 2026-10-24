using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class ParquetCaptureStoreTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FlushWritesBoundedParquetAndRestoresCausalRowsAndCheckpoint()
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope[] expected = CreateRows();
        CapturePersistenceOptions options = new()
        {
            StreamId = "depth-stream",
            MaxRowsPerSegment = 2,
            MaxBufferedRows = 4
        };

        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options))
        {
            foreach (CaptureEnvelope row in expected)
            {
                store.Append(row);
            }

            IReadOnlyList<CaptureSegmentMetadata> segments = await store.FlushAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(2, segments.Count);
            Assert.Equal(2, segments[0].RowCount);
            Assert.Equal(1, segments[1].RowCount);
            Assert.All(segments, segment =>
            {
                Assert.True(File.Exists(segment.Path));
                Assert.Equal(1, segment.FormatVersion);
            });
            Assert.Equal(3, store.Checkpoint.NextCaptureSequence);
            Assert.Equal(2, store.Checkpoint.LastDurableSequence);
        }

        await using ParquetCaptureStore reopened = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options);
        Assert.Equal(3, reopened.Checkpoint.NextCaptureSequence);
        IReadOnlyList<CaptureEnvelope> actual = await reopened.ReadAllAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task RestartPreservesSequenceAndSchemaSixKeepsPriorJournalRows()
    {
        using TemporaryCaptureStore fixture = new();
        Trade priorEvent = new("trade-before-capture", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(BaseTime), new Price(100m), new Quantity(1m), TradeSide.Buy);
        using (SqliteEventJournal journal = SqliteEventJournal.Open(fixture.DatabasePath))
        {
            Assert.True(journal.Append("trade-stream", new CausalMarketEvent(priorEvent,
                new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime), 4,
                new UtcTimestamp(BaseTime.AddMilliseconds(-1)))));
        }

        using (SqliteConnection downgrade = new(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath
        }.ConnectionString))
        {
            downgrade.Open();
            ExecuteNonQuery(downgrade, "DROP TABLE capture_segments;");
            ExecuteNonQuery(downgrade, "DROP TABLE capture_checkpoints;");
            ExecuteNonQuery(downgrade, "DROP TABLE screener_snapshots;");
            ExecuteNonQuery(downgrade, "DROP TABLE universe_selection_snapshots;");
            ExecuteNonQuery(downgrade, "PRAGMA user_version = 5;");
        }

        CaptureEnvelope first = CreateRows()[0];
        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory,
            new CapturePersistenceOptions { StreamId = first.StreamId }))
        {
            await store.AppendAsync(first, TestContext.Current.CancellationToken);
            await store.FlushAsync(TestContext.Current.CancellationToken);
        }

        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ConnectionString))
        {
            connection.Open();
            Assert.Equal(14L, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
            Assert.Equal(1L, ExecuteScalar<long>(connection,
                "SELECT COUNT(*) FROM event_journal WHERE stream_id = 'trade-stream';"));
            Assert.Equal(1L, ExecuteScalar<long>(connection,
                "SELECT COUNT(*) FROM capture_segments;"));
            Assert.Equal(1L, ExecuteScalar<long>(connection,
                "SELECT COUNT(*) FROM capture_checkpoints;"));
        }
    }

    [Fact]
    public async Task AppendRejectsSequenceGapsAndConfiguredBufferOverflow()
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope[] rows = CreateRows();
        CapturePersistenceOptions options = new()
        {
            StreamId = rows[0].StreamId,
            MaxRowsPerSegment = 1,
            MaxBufferedRows = 2
        };
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options);

        store.Append(rows[0]);
        Assert.Throws<ArgumentException>(() => store.Append(rows[2]));
        store.Append(rows[1]);
        Assert.Throws<InvalidOperationException>(() => store.Append(rows[2]));
    }

    [Fact]
    public async Task FlushRejectsOversizeSegmentWithoutPublishingFileOrCheckpoint()
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope row = CreateRows()[0];
        CapturePersistenceOptions options = new()
        {
            StreamId = row.StreamId,
            MaxSegmentBytes = 1,
            FlushOnDispose = false
        };

        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options))
        {
            store.Append(row);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.FlushAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(1, store.BufferedRowCount);
            Assert.Empty(store.Segments);
            Assert.Empty(Directory.GetFiles(fixture.SegmentDirectory));
            Assert.Equal(0, store.Checkpoint.NextCaptureSequence);
            Assert.Equal(-1, store.Checkpoint.LastDurableSequence);
        }

        await using ParquetCaptureStore reopened = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, options);
        Assert.Empty(reopened.Segments);
        Assert.Equal(0, reopened.Checkpoint.NextCaptureSequence);
    }

    private static CaptureEnvelope[] CreateRows()
    {
        CaptureProvenance depthProvenance = new("binance.usdm", "btcusdt@depth",
            "session-1", "depth:BTCUSDT:1:2", "{\"depth\":true}", ["public"]);
        BookDelta depth = new("depth-1", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(BaseTime), [new BookLevel(new Price(100m), new Quantity(2m))], []);
        CaptureEnvelope first = CaptureEnvelope.FromMarketEvent("depth-stream", 0, depth,
            new UtcTimestamp(BaseTime.AddMilliseconds(1)),
            new UtcTimestamp(BaseTime.AddMilliseconds(2)), 10, depthProvenance,
            new UtcTimestamp(BaseTime.AddMilliseconds(-1)));

        CaptureProvenance tradeProvenance = new("binance.usdm", "btcusdt@aggTrade",
            "session-1", "aggTrade:BTCUSDT:2", "{\"trade\":true}", []);
        Trade trade = new("trade-1", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(BaseTime.AddSeconds(1)), new Price(101m), new Quantity(1m),
            TradeSide.Sell);
        CaptureEnvelope second = CaptureEnvelope.FromMarketEvent("depth-stream", 1, trade,
            new UtcTimestamp(BaseTime.AddSeconds(1)),
            new UtcTimestamp(BaseTime.AddSeconds(1).AddMilliseconds(1)), 11,
            tradeProvenance);

        CaptureDiagnostic diagnostic = new("gap", "depth gap", ["u=3"]);
        CaptureEnvelope third = CaptureEnvelope.FromDiagnostic("depth-stream", 2,
            new UtcTimestamp(BaseTime.AddSeconds(2)),
            new UtcTimestamp(BaseTime.AddSeconds(2)), 12, diagnostic,
            new CaptureProvenance("binance.usdm", "btcusdt@depth", "session-1", null,
                null, ["gap"]));
        return [first, second, third];
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryCaptureStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "tradingbot-capture-" + Guid.NewGuid().ToString("N"));

        public TemporaryCaptureStore()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "capture.db");
            SegmentDirectory = Path.Combine(_root, "segments");
        }

        public string DatabasePath { get; }

        public string SegmentDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
