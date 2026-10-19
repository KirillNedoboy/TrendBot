using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteScreenerSnapshotStoreTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 2, 3, 4, 5,
        TimeSpan.Zero);

    [Fact]
    public void AppendFindRetryAndReopenPreserveCanonicalSnapshotAndPriorJournal()
    {
        using TemporaryDatabase database = new();
        ScreenerSnapshot expected = CreateSnapshot();
        CausalMarketEvent prior = new(
            new Trade("trade-before-snapshot", Instrument, Time(0), new Price(100m),
                new Quantity(1m), TradeSide.Buy), Time(0), Time(0), 0);

        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append("stream-a", prior));
        }

        using (SqliteScreenerSnapshotStore store = SqliteScreenerSnapshotStore.Open(
            database.Path))
        {
            Assert.True(store.Append(expected));
            Assert.False(store.Append(expected));
            Assert.Equal(expected, store.Find(expected.SnapshotId));
        }

        using SqliteScreenerSnapshotStore reopened = SqliteScreenerSnapshotStore.Open(
            database.Path);
        Assert.Equal(expected, reopened.Find(expected.Identity));
        using SqliteEventJournal events = SqliteEventJournal.Open(database.Path);
        Assert.Equal(prior, Assert.Single(events.ReadStream("stream-a")));
    }

    [Fact]
    public void MigrationFromPopulatedSchemaSixPreservesExistingRowsAndCreatesSnapshotTable()
    {
        using TemporaryDatabase database = new();
        ScreenerSnapshot expected = CreateSnapshot();
        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append("stream-a", new CausalMarketEvent(
                new Trade("trade-before-migration", Instrument, Time(0), new Price(100m),
                    new Quantity(1m), TradeSide.Buy), Time(0), Time(0), 0)));
        }

        using (SqliteConnection downgrade = OpenRaw(database.Path))
        {
            ExecuteNonQuery(downgrade, "DROP TABLE screener_snapshots;");
            ExecuteNonQuery(downgrade, "DROP TABLE universe_selection_snapshots;");
            ExecuteNonQuery(downgrade, "PRAGMA user_version = 6;");
        }

        using SqliteScreenerSnapshotStore store = SqliteScreenerSnapshotStore.Open(
            database.Path);
        Assert.True(store.Append(expected));
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(9L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM event_journal WHERE stream_id = 'stream-a';"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM screener_snapshots;"));
    }

    [Fact]
    public void SameIdentityWithDifferentPayloadIsRejectedAndCorruptRowsAreRejectedOnRead()
    {
        using TemporaryDatabase database = new();
        ScreenerSnapshot expected = CreateSnapshot();
        using SqliteScreenerSnapshotStore store = SqliteScreenerSnapshotStore.Open(
            database.Path);
        Assert.True(store.Append(expected));

        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            UpdateSnapshotJson(connection, expected.SnapshotId,
                CoreJsonSerializer.Serialize(CreateSnapshot(lastClose: 120m)));
        }

        Assert.Throws<InvalidDataException>(() => store.Find(expected.SnapshotId));
        Assert.Throws<InvalidDataException>(() => store.Append(expected));
    }

    [Fact]
    public void UnsupportedFormatAndFormulaVersionsAreRejectedOnRead()
    {
        using TemporaryDatabase database = new();
        ScreenerSnapshot expected = CreateSnapshot();
        using SqliteScreenerSnapshotStore store = SqliteScreenerSnapshotStore.Open(
            database.Path);
        Assert.True(store.Append(expected));

        string canonical = CoreJsonSerializer.Serialize(expected);
        string unsupportedFormat = canonical.Replace("\"formatVersion\":1",
            "\"formatVersion\":2", StringComparison.Ordinal);
        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            UpdateSnapshotJson(connection, expected.SnapshotId, unsupportedFormat);
        }

        Assert.Throws<InvalidDataException>(() => store.Find(expected.SnapshotId));

        using TemporaryDatabase formulaDatabase = new();
        using SqliteScreenerSnapshotStore formulaStore = SqliteScreenerSnapshotStore.Open(
            formulaDatabase.Path);
        Assert.True(formulaStore.Append(expected));
        string unsupportedFormula = canonical.Replace("screener-v1", "screener-v999",
            StringComparison.Ordinal);
        using (SqliteConnection connection = OpenRaw(formulaDatabase.Path))
        {
            UpdateSnapshotJson(connection, expected.SnapshotId, unsupportedFormula);
        }

        Assert.Throws<InvalidDataException>(() => formulaStore.Find(expected.SnapshotId));
    }

    [Fact]
    public void HeldWriterRollsBackAppendWithoutAPartialSnapshot()
    {
        using TemporaryDatabase database = new();
        ScreenerSnapshot expected = CreateSnapshot();
        using SqliteScreenerSnapshotStore store = SqliteScreenerSnapshotStore.Open(
            database.Path, commandTimeoutSeconds: 1);
        using SqliteConnection blocker = OpenRaw(database.Path);
        ExecuteNonQuery(blocker, "BEGIN IMMEDIATE;");

        Assert.Throws<SqliteException>(() => store.Append(expected));
        ExecuteNonQuery(blocker, "ROLLBACK;");
        Assert.Null(store.Find(expected.SnapshotId));
        Assert.True(store.Append(expected));
    }

    [Fact]
    public void PersistedInputCanBeReevaluatedToTheSameFeatureValues()
    {
        using TemporaryDatabase database = new();
        ScreenerSnapshot expected = CreateSnapshot();
        using SqliteScreenerSnapshotStore store = SqliteScreenerSnapshotStore.Open(
            database.Path);
        Assert.True(store.Append(expected));

        ScreenerSnapshot persisted = store.Find(expected.SnapshotId)!;
        ScreenerFeatureResult recalculated = ScreenerFeatureCalculator.Calculate(
            persisted.Input, persisted.Cursor, persisted.Configuration);
        Assert.Equal(persisted.Features, recalculated.Features);
    }

    private static ScreenerSnapshot CreateSnapshot(decimal lastClose = 119m)
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m, 100.1m),
            Bar(1, 105m, 108m, 103m, 20m, 200.2m),
            Bar(2, 111m, 114m, 109m, 30m, 300.3m),
            Bar(3, 116m, 120m, 112m, 40m, 400.4m),
            Bar(4, lastClose, lastClose + 4m, lastClose - 4m, 50m, 500.5m)
        ];
        ScreenerFeatureInput input = new("binance.usdm", Instrument, Time(4),
            At(4, 1), At(4, 2), 4, bars,
            [OpenInterest(0, 100m), OpenInterest(1, 110m), OpenInterest(2, 150m)],
            Quote(4, 99m, 101m));
        ScreenerFeatureConfiguration configuration = new(
            "1m", 2, 1, 2, 3, 2, new ScreenerFeatureVersion("screener-v1"),
            new ScreenerFreshnessPolicy(
                barTtl: TimeSpan.FromMinutes(5), quoteTtl: TimeSpan.FromMinutes(5),
                openInterestTtl: TimeSpan.FromMinutes(5)));
        CausalEventCursor cursor = new(Time(4), 4);
        return new ScreenerSnapshot(
            ScreenerFeatureCalculator.Calculate(input, cursor, configuration), configuration);
    }

    private static ScreenerBar Bar(int offset, decimal close, decimal high, decimal low,
        decimal volume, decimal quoteVolume)
    {
        Bar bar = new($"bar-{offset}", Instrument, Time(offset), Time(offset),
            Time(offset + 1), new Price(close - 1m), new Price(high), new Price(low),
            new Price(close), new Quantity(volume));
        return new ScreenerBar("binance.usdm", bar, quoteVolume, Time(offset),
            At(offset, 1), offset);
    }

    private static ScreenerOpenInterestObservation OpenInterest(int offset, decimal value) =>
        new("binance.usdm", Instrument, Time(offset), Time(offset),
            At(offset, 1), offset, value);

    private static ScreenerQuoteObservation Quote(int offset, decimal bid, decimal ask) =>
        new("binance.usdm", Instrument, Time(offset), Time(offset),
            At(offset, 1), offset, bid, ask, true);

    private static UtcTimestamp Time(int offset) => new(BaseTime.AddMinutes(offset));

    private static UtcTimestamp At(int offset, int milliseconds) =>
        new(BaseTime.AddMinutes(offset).AddMilliseconds(milliseconds));

    private static SqliteConnection OpenRaw(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void UpdateSnapshotJson(SqliteConnection connection, string snapshotId,
        string json)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE screener_snapshots SET snapshot_json = $json "
            + "WHERE snapshot_id = $id;";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$id", Convert.FromHexString(snapshotId));
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-screener-snapshot-{Guid.NewGuid():N}.sqlite");
        }

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }
}
