using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteUniverseSelectionSnapshotStoreTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AppendFindRetryReopenAndBinaryIdentityPreserveCanonicalChain()
    {
        using TemporaryDatabase database = new();
        StagedSelectionChainSnapshot expected = CreateChain();

        using (SqliteUniverseSelectionSnapshotStore store =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path))
        {
            Assert.True(store.Append(expected));
            Assert.False(store.Append(expected));
            Assert.Equal(expected, store.Find(expected.SnapshotIdentity));
            Assert.Equal(expected, store.Find(Convert.FromHexString(expected.Identity)));
        }

        using SqliteUniverseSelectionSnapshotStore reopened =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path);
        Assert.Equal(expected, reopened.Find(expected.Identity));

        using SqliteConnection connection = OpenRaw(database.Path);
        Assert.Equal("blob", ExecuteScalar<string>(connection,
            "SELECT typeof(snapshot_id) FROM universe_selection_snapshots;"));
        Assert.Equal(CoreJsonSerializer.Serialize(expected), ExecuteScalar<string>(connection,
            "SELECT snapshot_json FROM universe_selection_snapshots;"));
        Assert.Equal(StagedSelectionChainSnapshot.CurrentFormatVersion,
            ExecuteScalar<long>(connection,
                "SELECT format_version FROM universe_selection_snapshots;"));
        Assert.Equal(StagedSelectionChainSnapshot.CurrentAlgorithmVersion,
            ExecuteScalar<string>(connection,
                "SELECT algorithm_version FROM universe_selection_snapshots;"));
        Assert.Equal(
            ["snapshot_id", "format_version", "algorithm_version", "snapshot_json"],
            ReadColumnNames(connection));
    }

    [Fact]
    public void CorruptUnsupportedNonCanonicalAndIdentityMismatchedRowsFailClosed()
    {
        StagedSelectionChainSnapshot expected = CreateChain();

        using (TemporaryDatabase database = new())
        {
            using SqliteUniverseSelectionSnapshotStore store =
                SqliteUniverseSelectionSnapshotStore.Open(database.Path);
            Assert.True(store.Append(expected));
            string pretty = CoreJsonSerializer.Serialize(expected).Replace(
                "{\"formatVersion\"", "{\n  \"formatVersion\"",
                StringComparison.Ordinal);
            UpdateRow(database.Path, Convert.FromHexString(expected.Identity), pretty, null, null);
            Assert.Throws<InvalidDataException>(() => store.Find(expected.Identity));
        }

        using (TemporaryDatabase database = new())
        {
            using SqliteUniverseSelectionSnapshotStore store =
                SqliteUniverseSelectionSnapshotStore.Open(database.Path);
            Assert.True(store.Append(expected));
            UpdateRow(database.Path, Convert.FromHexString(expected.Identity),
                ReplaceProperty(expected, "formatVersion", JsonValue.Create(99)!), null, null);
            Assert.Throws<InvalidDataException>(() => store.Find(expected.Identity));
        }

        using (TemporaryDatabase database = new())
        {
            using SqliteUniverseSelectionSnapshotStore store =
                SqliteUniverseSelectionSnapshotStore.Open(database.Path);
            Assert.True(store.Append(expected));
            UpdateRow(database.Path, Convert.FromHexString(expected.Identity),
                ReplaceProperty(expected, "algorithmVersion",
                    JsonValue.Create("staged-selection-v999")!), null, null);
            Assert.Throws<InvalidDataException>(() => store.Find(expected.Identity));
        }

        using (TemporaryDatabase database = new())
        {
            using SqliteUniverseSelectionSnapshotStore store =
                SqliteUniverseSelectionSnapshotStore.Open(database.Path);
            Assert.True(store.Append(expected));
            UpdateRow(database.Path, Convert.FromHexString(expected.Identity), null, 99, null);
            Assert.Throws<InvalidDataException>(() => store.Find(expected.Identity));
        }

        using (TemporaryDatabase database = new())
        {
            using SqliteUniverseSelectionSnapshotStore store =
                SqliteUniverseSelectionSnapshotStore.Open(database.Path);
            Assert.True(store.Append(expected));
            byte[] alteredIdentity = new byte[32];
            UpdateRow(database.Path, Convert.FromHexString(expected.Identity), null, null,
                alteredIdentity);
            Assert.Throws<InvalidDataException>(() => store.Find(alteredIdentity));
        }
    }

    [Fact]
    public void AlteredChainThatFailsReplayIsRejectedAndAppendDoesNotHideCorruption()
    {
        using TemporaryDatabase database = new();
        StagedSelectionChainSnapshot expected = CreateChain();
        using SqliteUniverseSelectionSnapshotStore store =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path);
        Assert.True(store.Append(expected));

        string altered = ReplaceUniverseSelection(expected);
        UpdateRow(database.Path, Convert.FromHexString(expected.Identity), altered, null, null);

        Assert.Throws<InvalidDataException>(() => store.Find(expected.Identity));
        Assert.Throws<InvalidDataException>(() => store.Append(expected));
    }

    [Fact]
    public void HeldWriterRollsBackAppendWithoutASelectionRow()
    {
        using TemporaryDatabase database = new();
        StagedSelectionChainSnapshot expected = CreateChain();
        using SqliteUniverseSelectionSnapshotStore store =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path, 1);
        using SqliteConnection blocker = OpenRaw(database.Path);
        ExecuteNonQuery(blocker, "BEGIN IMMEDIATE;");

        Assert.Throws<SqliteException>(() => store.Append(expected));
        ExecuteNonQuery(blocker, "ROLLBACK;");
        Assert.Null(store.Find(expected.Identity));
        Assert.True(store.Append(expected));
    }

    [Fact]
    public void PopulatedSchemaSevenMigratesToEightAndPreservesPriorRows()
    {
        using TemporaryDatabase database = new();
        StagedSelectionChainSnapshot expected = CreateChain();
        CausalMarketEvent prior = new(
            new Trade("trade-before-universe-snapshot", new InstrumentId("BTCUSDT"),
                Time(0), new Price(100m), new Quantity(1m), TradeSide.Buy),
            Time(0), Time(0), 0);

        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append("stream-a", prior));
        }

        using (SqliteScreenerSnapshotStore screener =
            SqliteScreenerSnapshotStore.Open(database.Path))
        {
            Assert.True(screener.Append(expected.UniverseInput.ScreenerSnapshots[0]));
        }

        using (SqliteUniverseSelectionSnapshotStore store =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path))
        {
            Assert.True(store.Append(expected));
        }

        using (SqliteConnection downgrade = OpenRaw(database.Path))
        {
            ExecuteNonQuery(downgrade,
                "DROP TABLE universe_selection_snapshots; PRAGMA user_version = 7;");
        }

        using SqliteUniverseSelectionSnapshotStore migrated =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path);
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(11L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.True(migrated.Append(expected));
        Assert.Equal(prior, Assert.Single(
            SqliteEventJournal.Open(database.Path).ReadStream("stream-a")));
        Assert.Equal(expected.UniverseInput.ScreenerSnapshots[0],
            SqliteScreenerSnapshotStore.Open(database.Path).Find(
                expected.UniverseInput.ScreenerSnapshots[0].Identity));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM universe_selection_snapshots;"));
    }

    [Fact]
    public void SchemaSevenMigrationRollsBackUnderAHeldWriter()
    {
        using TemporaryDatabase database = new();
        using (SqliteUniverseSelectionSnapshotStore store =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path))
        {
        }

        using (SqliteConnection downgrade = OpenRaw(database.Path))
        {
            ExecuteNonQuery(downgrade, "DROP TABLE universe_selection_snapshots;");
            ExecuteNonQuery(downgrade, "PRAGMA user_version = 7;");
        }

        using SqliteConnection blocker = OpenRaw(database.Path);
        ExecuteNonQuery(blocker, "BEGIN IMMEDIATE;");
        Assert.Throws<SqliteException>(() =>
            SqliteUniverseSelectionSnapshotStore.Open(database.Path, 1));
        ExecuteNonQuery(blocker, "ROLLBACK;");

        using SqliteConnection beforeRetry = OpenRaw(database.Path);
        Assert.Equal(7L, ExecuteScalar<long>(beforeRetry, "PRAGMA user_version;"));
        Assert.Equal(0L, ExecuteScalar<long>(beforeRetry,
            "SELECT COUNT(*) FROM sqlite_master "
            + "WHERE type = 'table' AND name = 'universe_selection_snapshots';"));

        using SqliteUniverseSelectionSnapshotStore migrated =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path);
        using SqliteConnection afterRetry = OpenRaw(database.Path);
        Assert.Equal(11L, ExecuteScalar<long>(afterRetry, "PRAGMA user_version;"));
    }

    [Fact]
    public void WrongSelectionTableDefinitionIsRejectedByExactSchemaValidation()
    {
        using TemporaryDatabase database = new();
        using (SqliteUniverseSelectionSnapshotStore store =
            SqliteUniverseSelectionSnapshotStore.Open(database.Path))
        {
        }

        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            ExecuteNonQuery(connection, "DROP TABLE universe_selection_snapshots;");
            ExecuteNonQuery(connection, """
                CREATE TABLE universe_selection_snapshots
                (
                    snapshot_id TEXT NOT NULL PRIMARY KEY,
                    format_version INTEGER NOT NULL,
                    algorithm_version TEXT NOT NULL,
                    snapshot_json TEXT NOT NULL
                );
                """);
        }

        Assert.Throws<InvalidDataException>(() =>
            SqliteUniverseSelectionSnapshotStore.Open(database.Path));
    }

    private static StagedSelectionChainSnapshot CreateChain()
    {
        CausalEventCursor cursor = new(Time(10), 10_000);
        ScreenerSnapshot snapshot = CreateScreenerSnapshot();
        StagedSelectionInput input = new(cursor,
            [CreateMetadata()], [snapshot]);
        StagedSelectionConfiguration universe = new(
            "universe-v1", StagedSelectionStage.Universe, 250,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]);
        StagedSelectionConfiguration candidate = new(
            "candidate-v1", StagedSelectionStage.CandidatePool, 50,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]);
        StagedSelectionConfiguration monitor = new(
            "monitor-v1", StagedSelectionStage.RealtimeMonitor, 20,
            [new StagedSelectionKey(ScreenerFeatureKind.Turnover,
                StagedSelectionDirection.Descending)]);
        return StagedSelectionChainSnapshot.Create(input, universe, [snapshot],
            candidate, [snapshot], monitor);
    }

    private static ExchangeEligibilityMetadata CreateMetadata() => new(
        new InstrumentId("BTCUSDT"), "BTCUSDT", "PERPETUAL", "TRADING", "BTC",
        "USDT", "USDT", new UtcTimestamp(DateTimeOffset.UnixEpoch),
        new UtcTimestamp(DateTimeOffset.UnixEpoch.AddDays(1)), 8, 8, 8, 8,
        2.5m, 5m,
        [
            new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 0.01m,
                maxPrice: 1_000_000m, tickSize: 0.01m),
            new ExchangeMetadataFilter("LOT_SIZE", minQuantity: 0.001m,
                maxQuantity: 100_000m, stepSize: 0.001m),
            new ExchangeMetadataFilter("MIN_NOTIONAL", notional: 5m)
        ], [], Time(0), Time(1));

    private static ScreenerSnapshot CreateScreenerSnapshot()
    {
        InstrumentId instrument = new("BTCUSDT");
        ScreenerFeatureConfiguration configuration = new(
            "1m", 1, 1, 1, 1, 1, new ScreenerFeatureVersion("screener-v1"),
            new ScreenerFreshnessPolicy(
                defaultTtl: TimeSpan.FromMinutes(30),
                barTtl: TimeSpan.FromMinutes(30),
                quoteTtl: TimeSpan.FromMinutes(30),
                openInterestTtl: TimeSpan.FromMinutes(30), sourceTtls: []));
        ScreenerBar[] bars = Enumerable.Range(0, 5)
            .Select(offset =>
            {
                UtcTimestamp time = Time(offset);
                Bar bar = new($"bar-{offset}", instrument, time, time,
                    Time(offset + 1), new Price(100m + offset),
                    new Price(102m + offset), new Price(99m + offset),
                    new Price(101m + offset), new Quantity(10m));
                return new ScreenerBar("binance.usdm", bar, 100m + offset,
                    time, At(offset, 1), offset);
            }).ToArray();
        ScreenerFeatureInput input = new("binance.usdm", instrument, Time(4),
            At(4, 1), At(4, 2), 4, bars,
            [
                new ScreenerOpenInterestObservation("binance.usdm", instrument,
                    Time(0), Time(0), At(0, 1), 0, 100m),
                new ScreenerOpenInterestObservation("binance.usdm", instrument,
                    Time(1), Time(1), At(1, 1), 1, 110m),
                new ScreenerOpenInterestObservation("binance.usdm", instrument,
                    Time(2), Time(2), At(2, 1), 2, 120m)
            ],
            [new ScreenerQuoteObservation("binance.usdm", instrument, Time(4),
                Time(4), At(4, 1), 4, 100m, 101m, true)]);
        return new ScreenerSnapshot(
            ScreenerFeatureCalculator.Calculate(input,
                new CausalEventCursor(Time(10), 10_000), configuration), configuration);
    }

    private static string ReplaceProperty(StagedSelectionChainSnapshot snapshot,
        string property, JsonNode value)
    {
        JsonObject root = JsonNode.Parse(CoreJsonSerializer.Serialize(snapshot))!.AsObject();
        root[property] = value;
        return root.ToJsonString();
    }

    private static string ReplaceUniverseSelection(
        StagedSelectionChainSnapshot snapshot)
    {
        JsonObject root = JsonNode.Parse(CoreJsonSerializer.Serialize(snapshot))!.AsObject();
        root["universeResult"]!.AsObject()["selected"] = new JsonArray();
        return root.ToJsonString();
    }

    private static void UpdateRow(string path, byte[] identity, string? json,
        int? formatVersion, byte[]? replacementIdentity)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        List<string> assignments = [];
        if (json is not null)
        {
            assignments.Add("snapshot_json = $json");
            command.Parameters.AddWithValue("$json", json);
        }

        if (formatVersion is not null)
        {
            assignments.Add("format_version = $format_version");
            command.Parameters.AddWithValue("$format_version", formatVersion.Value);
        }

        if (replacementIdentity is not null)
        {
            assignments.Add("snapshot_id = $replacement_id");
            command.Parameters.AddWithValue("$replacement_id", replacementIdentity);
        }

        command.Parameters.AddWithValue("$identity", identity);
        command.CommandText = $"UPDATE universe_selection_snapshots SET "
            + $"{string.Join(", ", assignments)} WHERE snapshot_id = $identity;";
        command.ExecuteNonQuery();
    }

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

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static string[] ReadColumnNames(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(universe_selection_snapshots);";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names.ToArray();
    }

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));

    private static UtcTimestamp At(int offset, int milliseconds) =>
        new(BaseTime.AddMinutes(offset).AddMilliseconds(milliseconds));

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-universe-snapshot-{Guid.NewGuid():N}.sqlite");
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
