using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class ReplayEnvelopeJournalTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3));

    [Fact]
    public void EnvelopeAppendAndReadPreserveVersionAndStreamIdentity()
    {
        using TemporaryDatabase database = new();
        ReplayEnvelope expected = new("stream-a", CreateTrade("trade-1", 0, 100m));

        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append(expected));
            Assert.False(journal.Append(expected));
        }

        using SqliteEventJournal reopened = SqliteEventJournal.Open(database.Path);
        ReplayEnvelope actual = Assert.Single(reopened.ReadEnvelopes("stream-a"));
        Assert.Equal(expected, actual);
        Assert.Equal(ReplayEnvelope.CurrentFormatVersion, actual.FormatVersion);
    }

    [Fact]
    public void VersionOneDatabaseMigratesTransactionallyAndAssignsVersionOne()
    {
        using TemporaryDatabase database = new();
        CausalMarketEvent expected = CreateTrade("trade-1", 7, 100m);

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
                    PRIMARY KEY (stream_id, stable_sequence)
                ) WITHOUT ROWID;
                """);
            ExecuteNonQuery(connection, "PRAGMA user_version = 1;");
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO event_journal
                    (stream_id, stable_sequence, payload_json, event_time_ticks,
                     receive_time_ticks, availability_time_ticks, exchange_time_ticks)
                VALUES ($stream_id, $stable_sequence, $payload_json, $event_time_ticks,
                        $receive_time_ticks, $availability_time_ticks, $exchange_time_ticks);
                """;
            command.Parameters.AddWithValue("$stream_id", "stream-a");
            command.Parameters.AddWithValue("$stable_sequence", expected.StableSequence);
            command.Parameters.AddWithValue("$payload_json",
                CoreJsonSerializer.Serialize(expected.Payload));
            command.Parameters.AddWithValue("$event_time_ticks", Ticks(expected.Payload.EventTime));
            command.Parameters.AddWithValue("$receive_time_ticks", Ticks(expected.ReceiveTime));
            command.Parameters.AddWithValue("$availability_time_ticks", Ticks(expected.AvailabilityTime));
            command.Parameters.AddWithValue("$exchange_time_ticks", DBNull.Value);
            command.ExecuteNonQuery();
        }

        using SqliteEventJournal migrated = SqliteEventJournal.Open(database.Path);
        ReplayEnvelope actual = Assert.Single(migrated.ReadEnvelopes("stream-a"));

        Assert.Equal(new ReplayEnvelope("stream-a", expected), actual);
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(5L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT envelope_version FROM event_journal WHERE stream_id = 'stream-a';"));
    }

    private static CausalMarketEvent CreateTrade(string eventId, long sequence, decimal price) => new(
        new Trade(eventId, Instrument, Time(sequence), new Price(price), new Quantity(1m),
            TradeSide.Buy), Time(sequence),
        new UtcTimestamp(Time(sequence).Value.AddSeconds(1)), sequence);

    private static UtcTimestamp Time(long minutes) =>
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
                $"tradingbot-envelope-{Guid.NewGuid():N}.sqlite");
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
