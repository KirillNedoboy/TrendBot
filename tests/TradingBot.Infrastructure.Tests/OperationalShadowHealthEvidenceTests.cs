using System.Reflection;
using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class OperationalShadowHealthEvidenceTests
{
    [Fact]
    public void HealthEvidenceAppendReadIsIdempotentAndSeparatedByRun()
    {
        using TemporaryDatabase database = new();
        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        Type evidenceType = RequireEvidenceType();
        object first = CreateEvidence(evidenceType, "run-a", 0,
            "2026-01-01T00:00:00Z", "{\"formatVersion\":1,\"status\":\"unknown\"}",
            null);
        object second = CreateEvidence(evidenceType, "run-a", 1,
            "2026-01-01T00:00:01Z", "{\"formatVersion\":1,\"status\":\"degraded\"}",
            "shadow/0/event-0");
        object otherRun = CreateEvidence(evidenceType, "run-b", 0,
            "2026-01-01T00:00:02Z", "{\"formatVersion\":1,\"status\":\"unknown\"}",
            null);
        MethodInfo append = typeof(OperationalShadowStore).GetMethod("AppendHealthEvidence",
            [evidenceType]) ?? throw new MissingMethodException(
                typeof(OperationalShadowStore).FullName, "AppendHealthEvidence");

        Assert.True(ReadBoolean(append.Invoke(store, [first])!, "Appended"));
        Assert.True(ReadBoolean(append.Invoke(store, [second])!, "Appended"));
        Assert.True(ReadBoolean(append.Invoke(store, [otherRun])!, "Appended"));
        Assert.False(ReadBoolean(append.Invoke(store, [first])!, "Appended"));
        Assert.True(ReadBoolean(append.Invoke(store, [first])!, "Duplicate"));

        MethodInfo read = typeof(OperationalShadowStore).GetMethod("ReadHealthEvidence",
            [typeof(string)]) ?? throw new MissingMethodException(
                typeof(OperationalShadowStore).FullName, "ReadHealthEvidence");
        object rows = read.Invoke(store, ["run-a"])
            ?? throw new InvalidOperationException("Health evidence read returned null.");
        Assert.Equal(2, ((System.Collections.IEnumerable)rows).Cast<object>().Count());
        object firstRow = ((System.Collections.IEnumerable)rows).Cast<object>().First();
        Assert.Equal("run-a", ReadString(firstRow, "RunId"));
        Assert.Equal(0L, ReadLong(firstRow, "HealthSequence"));
        Assert.Null(ReadNullableString(firstRow, "CausalCheckpointLink"));
    }

    [Fact]
    public void VersionOneMigrationPreservesShadowJournalAndAddsHealthEvidenceSchema()
    {
        using TemporaryDatabase database = new();
        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            ExecuteNonQuery(connection, """
                CREATE TABLE shadow_entries
                (
                    stream_id TEXT COLLATE BINARY NOT NULL,
                    stable_sequence INTEGER NOT NULL,
                    event_id TEXT COLLATE BINARY NOT NULL,
                    event_time_ticks INTEGER NOT NULL,
                    receive_time_ticks INTEGER NOT NULL,
                    availability_time_ticks INTEGER NOT NULL,
                    exchange_time_ticks INTEGER,
                    causal_event_json TEXT NOT NULL,
                    derived_state_json TEXT NOT NULL,
                    outcome_json TEXT NOT NULL,
                    checkpoint_json TEXT NOT NULL,
                    PRIMARY KEY (stream_id, stable_sequence),
                    UNIQUE (stream_id, event_id)
                ) WITHOUT ROWID;
                CREATE TABLE shadow_checkpoints
                (
                    stream_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                    stable_sequence INTEGER NOT NULL,
                    event_id TEXT COLLATE BINARY NOT NULL,
                    derived_state_json TEXT NOT NULL,
                    outcome_json TEXT NOT NULL,
                    checkpoint_json TEXT NOT NULL
                ) WITHOUT ROWID;
                INSERT INTO shadow_entries
                    (stream_id, stable_sequence, event_id, event_time_ticks,
                     receive_time_ticks, availability_time_ticks, exchange_time_ticks,
                     causal_event_json, derived_state_json, outcome_json, checkpoint_json)
                VALUES ('shadow', 4, 'event-4', 639028224000000000,
                        639028224000000000, 639028224000000000, NULL,
                        '{"event":4}', '{"state":4}', '{"outcome":"blocked"}',
                        '{"checkpoint":4}');
                PRAGMA user_version = 1;
                """);
        }

        using OperationalShadowStore store = OperationalShadowStore.Open(database.Path);
        Assert.Equal(2, OperationalShadowStore.SchemaVersion);
        Assert.Single(store.ReadAll("shadow"));
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' " +
            "AND name = 'shadow_health_evidence';"));
    }

    private static Type RequireEvidenceType() =>
        typeof(OperationalShadowStore).Assembly.GetType(
            "TradingBot.Infrastructure.OperationalShadowHealthEvidence")
        ?? throw new MissingMemberException("OperationalShadowHealthEvidence");

    private static object CreateEvidence(Type evidenceType, string runId, long sequence,
        string checkedAt, string json, string? causalCheckpointLink)
    {
        ConstructorInfo constructor = evidenceType.GetConstructor([
                typeof(string), typeof(long), typeof(UtcTimestamp), typeof(string),
                typeof(string), typeof(int)])
            ?? throw new MissingMethodException(evidenceType.FullName, ".ctor");
        UtcTimestamp timestamp = new(DateTimeOffset.Parse(checkedAt,
            System.Globalization.CultureInfo.InvariantCulture));
        return constructor.Invoke([runId, sequence, timestamp, json, causalCheckpointLink, 1]);
    }

    private static bool ReadBoolean(object value, string propertyName) =>
        (bool)(value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, propertyName));

    private static long ReadLong(object value, string propertyName) =>
        (long)(value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, propertyName));

    private static string ReadString(object value, string propertyName) =>
        (string)(value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, propertyName));

    private static string? ReadNullableString(object value, string propertyName) =>
        (string?)value.GetType().GetProperty(propertyName)?.GetValue(value);

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

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-shadow-health-{Guid.NewGuid():N}.sqlite");
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
