using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteOrderIntentJournalTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void AppendFindAndRestartPreserveEveryOrderIntentFieldAndCanonicalJson()
    {
        using TemporaryDatabase database = new();
        OrderIntent expected = CreateIntent("intent-1", "client-1", Direction.Short,
            OrderRole.Entry, 3, reduceOnly: true);

        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(expected));
            Assert.Equal(expected, journal.Find(expected.IntentId));
        }

        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            Assert.Equal(7L, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
            Assert.Equal(CoreJsonSerializer.Serialize(expected), ExecuteScalar<string>(
                connection, "SELECT intent_json FROM order_intents WHERE intent_id = 'intent-1';"));
        }

        using SqliteOrderIntentJournal reopened = SqliteOrderIntentJournal.Open(database.Path);
        Assert.Equal(expected, reopened.Find(expected.IntentId));
        Assert.Null(reopened.Find("missing"));
    }

    [Fact]
    public void IdenticalRetryIsFalseAndIntentIdConflictRollsBackWithoutMutation()
    {
        using TemporaryDatabase database = new();
        OrderIntent first = CreateIntent("intent-1", "client-1", Direction.Long,
            OrderRole.Entry, 1, reduceOnly: false);
        OrderIntent sameIntentId = CreateIntent("intent-1", "client-2", Direction.Long,
            OrderRole.Entry, 2, reduceOnly: false);

        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path);
        Assert.True(journal.Append(first));
        Assert.False(journal.Append(first));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = journal.Append(sameIntentId);
        });

        Assert.Equal(first, journal.Find("intent-1"));
        Assert.Null(journal.Find("intent-2"));
        using SqliteConnection connection = OpenRaw(database.Path);
        Assert.Equal(1L, ExecuteScalar<long>(connection,
            "SELECT COUNT(*) FROM order_intents;"));
    }

    [Fact]
    public void ClientOrderIdConflictIsRejectedWithoutChangingEitherIdentity()
    {
        using TemporaryDatabase database = new();
        OrderIntent first = CreateIntent("intent-1", "client-1", Direction.Long,
            OrderRole.Entry, 1, reduceOnly: false);
        OrderIntent sameClientId = CreateIntent("intent-2", "client-1", Direction.Short,
            OrderRole.Stop, 1, reduceOnly: true);

        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path);
        Assert.True(journal.Append(first));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = journal.Append(sameClientId);
        });

        Assert.Equal(first, journal.Find("intent-1"));
        Assert.Null(journal.Find("intent-2"));
        using SqliteConnection connection = OpenRaw(database.Path);
        Assert.Equal(1L, ExecuteScalar<long>(connection,
            "SELECT COUNT(*) FROM order_intents WHERE client_order_id = 'client-1';"));
    }

    [Fact]
    public void EventAndIntentJournalsShareVersionThreeDatabaseAndPreserveBothRecords()
    {
        using TemporaryDatabase database = new();
        CausalMarketEvent marketEvent = new(
            new Trade("trade-1", Instrument, Time(0), new Price(100m),
                new Quantity(1m), TradeSide.Buy),
            Time(0), Time(1), 0);
        OrderIntent intent = CreateIntent("intent-1", "client-1", Direction.Long,
            OrderRole.Entry, 1, reduceOnly: false);

        using (SqliteEventJournal events = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(events.Append("stream-a", marketEvent));
        }

        using (SqliteOrderIntentJournal intents = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(intents.Append(intent));
        }

        using SqliteEventJournal reopenedEvents = SqliteEventJournal.Open(database.Path);
        using SqliteOrderIntentJournal reopenedIntents = SqliteOrderIntentJournal.Open(database.Path);
        Assert.Equal(marketEvent, Assert.Single(reopenedEvents.ReadStream("stream-a")));
        Assert.Equal(intent, reopenedIntents.Find(intent.IntentId));
    }

    [Fact]
    public void VersionTwoDatabaseMigratesToThreeAndPreservesTheEventJournal()
    {
        using TemporaryDatabase database = new();
        CausalMarketEvent expectedEvent = new(
            new Trade("trade-1", Instrument, Time(0), new Price(100m),
                new Quantity(1m), TradeSide.Buy),
            Time(0), Time(1), 0);

        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            ExecuteNonQuery(connection, """
                CREATE TABLE event_journal
                (
                    stream_id TEXT COLLATE BINARY NOT NULL,
                    stable_sequence INTEGER NOT NULL,
                    payload_json TEXT NOT NULL,
                    event_time_ticks INTEGER NOT NULL,
                    receive_time_ticks INTEGER NOT NULL,
                    availability_time_ticks INTEGER NOT NULL,
                    exchange_time_ticks INTEGER NULL,
                    envelope_version INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (stream_id, stable_sequence)
                ) WITHOUT ROWID;
                """);
            ExecuteNonQuery(connection, "PRAGMA user_version = 2;");
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO event_journal
                    (stream_id, stable_sequence, envelope_version, payload_json,
                     event_time_ticks, receive_time_ticks, availability_time_ticks,
                     exchange_time_ticks)
                VALUES ($stream_id, $stable_sequence, $envelope_version, $payload_json,
                        $event_time_ticks, $receive_time_ticks, $availability_time_ticks,
                        $exchange_time_ticks);
                """;
            insert.Parameters.AddWithValue("$stream_id", "stream-a");
            insert.Parameters.AddWithValue("$stable_sequence", expectedEvent.StableSequence);
            insert.Parameters.AddWithValue("$envelope_version", ReplayEnvelope.CurrentFormatVersion);
            insert.Parameters.AddWithValue("$payload_json",
                CoreJsonSerializer.Serialize(expectedEvent.Payload));
            insert.Parameters.AddWithValue("$event_time_ticks", Ticks(expectedEvent.Payload.EventTime));
            insert.Parameters.AddWithValue("$receive_time_ticks", Ticks(expectedEvent.ReceiveTime));
            insert.Parameters.AddWithValue("$availability_time_ticks", Ticks(expectedEvent.AvailabilityTime));
            insert.Parameters.AddWithValue("$exchange_time_ticks", DBNull.Value);
            insert.ExecuteNonQuery();
        }

        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path);
        Assert.True(journal.Append(CreateIntent("intent-1", "client-1", Direction.Long,
            OrderRole.Entry, 1, reduceOnly: false)));

        using SqliteEventJournal events = SqliteEventJournal.Open(database.Path);
        Assert.Equal(expectedEvent, Assert.Single(events.ReadStream("stream-a")));
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(7L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM order_intents;"));
    }

    [Fact]
    public void BusyWriterFailsBeforeCommitAndLeavesNoPartialIntent()
    {
        using TemporaryDatabase database = new();
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path, 1);
        using SqliteConnection blocker = OpenRaw(database.Path);
        using (SqliteCommand begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() =>
        {
            _ = journal.Append(CreateIntent("blocked", "blocked-client", Direction.Long,
                OrderRole.Entry, 1, reduceOnly: false));
        });

        using (SqliteCommand rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        using SqliteOrderIntentJournal reopened = SqliteOrderIntentJournal.Open(database.Path, 1);
        Assert.Null(reopened.Find("blocked"));
        Assert.True(reopened.Append(CreateIntent("after-release", "after-client",
            Direction.Long, OrderRole.Entry, 1, reduceOnly: false)));
    }

    private static OrderIntent CreateIntent(string intentId, string clientOrderId,
        Direction direction, OrderRole role, int attempt, bool reduceOnly) =>
        new(intentId, "strategy-run-1", "setup-1", clientOrderId, Instrument, direction,
            role, attempt, new Quantity(0.12345678901234567890123456789m),
            new Price(99.8765432109876543210987654321m), new UtcTimestamp(BaseTime.AddMinutes(5)),
            reduceOnly);

    private static UtcTimestamp Time(int minutes) =>
        new(BaseTime.AddMinutes(minutes).ToUniversalTime());

    private static long Ticks(UtcTimestamp timestamp) => timestamp.Value.UtcDateTime.Ticks;

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

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-intent-{Guid.NewGuid():N}.sqlite");
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
