using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteEventJournalTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3));

    [Fact]
    public void ReopeningJournalRestoresAllMarketEventKindsAndCausalFields()
    {
        using TemporaryDatabase database = new();
        CausalMarketEvent[] expected = CreateFiveEvents();

        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            for (int index = 0; index < expected.Length; index++)
            {
                Assert.True(journal.Append("stream-a", expected[index]));
            }
        }

        using SqliteEventJournal reopened = SqliteEventJournal.Open(database.Path);
        IReadOnlyList<CausalMarketEvent> actual = reopened.ReadStream("stream-a");

        Assert.Equal(expected, actual);
        Assert.Collection(actual,
            item => Assert.IsType<Trade>(item.Payload),
            item => Assert.IsType<Bar>(item.Payload),
            item => Assert.IsType<BookDelta>(item.Payload),
            item => Assert.IsType<FundingUpdate>(item.Payload),
            item => Assert.IsType<OIUpdate>(item.Payload));
        Assert.Equal(12345678901234567890123456789m,
            ((Trade)actual[0].Payload).Price.Value);
        Assert.Equal(BaseTime.ToUniversalTime(), actual[0].ReceiveTime.Value);
        Assert.Equal(BaseTime.AddMinutes(1).ToUniversalTime(), actual[0].ExchangeTime!.Value);
        Assert.Null(actual[1].ExchangeTime);
    }

    [Fact]
    public void AppendIsIdempotentForAnEqualCausalEventAndRejectsSequenceConflicts()
    {
        using TemporaryDatabase database = new();
        CausalMarketEvent first = CreateTradeEvent("trade-1", 0, 100m);
        CausalMarketEvent sameSequenceDifferentPayload = CreateTradeEvent("trade-2", 0, 101m);
        CausalMarketEvent lowerSequence = CreateTradeEvent("trade-3", 1, 102m);
        CausalMarketEvent samePayloadNewSequence = CreateTradeEvent("trade-1", 3, 100m);

        using SqliteEventJournal journal = SqliteEventJournal.Open(database.Path);

        Assert.True(journal.Append("stream-a", first));
        Assert.False(journal.Append("stream-a", first));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = journal.Append("stream-a", sameSequenceDifferentPayload);
        });
        Assert.True(journal.Append("stream-a", CreateTradeEvent("trade-4", 2, 104m)));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = journal.Append("stream-a", lowerSequence);
        });
        Assert.True(journal.Append("stream-a", samePayloadNewSequence));

        Assert.Equal(3, journal.ReadStream("stream-a").Count);
    }

    [Fact]
    public void StreamsAreIndependentAndPreserveStableSequenceWhenEventTimeArrivesLate()
    {
        using TemporaryDatabase database = new();
        using SqliteEventJournal journal = SqliteEventJournal.Open(database.Path);

        Assert.True(journal.Append("BTCUSDT", CreateTradeEvent("late", 2,
            100m, BaseTime.AddMinutes(10))));
        Assert.True(journal.Append("BTCUSDT", CreateTradeEvent("earlier", 3,
            101m, BaseTime.AddMinutes(-10))));
        Assert.True(journal.Append("btcusdt", CreateTradeEvent("other-stream", 0, 102m)));

        Assert.Equal(["late", "earlier"], journal.ReadStream("BTCUSDT")
            .Select(static item => item.Payload.EventId));
        Assert.Equal(["other-stream"], journal.ReadStream("btcusdt")
            .Select(static item => item.Payload.EventId));
    }

    [Fact]
    public void ReadStreamReturnsAnImmutableSnapshot()
    {
        using TemporaryDatabase database = new();
        using SqliteEventJournal journal = SqliteEventJournal.Open(database.Path);
        CausalMarketEvent eventValue = CreateTradeEvent("trade-1", 0, 100m);
        journal.Append("stream-a", eventValue);

        IReadOnlyList<CausalMarketEvent> snapshot = journal.ReadStream("stream-a");
        IList<CausalMarketEvent> mutableView = Assert.IsAssignableFrom<IList<CausalMarketEvent>>(
            snapshot);

        Assert.Throws<NotSupportedException>(() => mutableView[0] = CreateTradeEvent("other", 0, 101m));
        Assert.Equal(eventValue, journal.ReadStream("stream-a")[0]);
    }

    [Fact]
    public void OpenMigratesAnEmptyDatabaseAndKeepsSchemaAndDurabilitySettings()
    {
        using TemporaryDatabase database = new();

        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.Empty(journal.ReadStream("stream-a"));
        }

        using SqliteConnection connection = OpenRaw(database.Path);
        Assert.Equal(1L, ExecuteScalar<long>(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", ExecuteScalar<string>(connection, "PRAGMA journal_mode;"));
        Assert.Equal(2L, ExecuteScalar<long>(connection, "PRAGMA synchronous;"));
        Assert.Equal(
            ["stream_id", "stable_sequence", "payload_json", "event_time_ticks",
                "receive_time_ticks", "availability_time_ticks", "exchange_time_ticks"],
            ReadColumnNames(connection));
    }

    [Fact]
    public void OpenRejectsUnknownAndNonEmptyUnversionedDatabasesWithoutReplacingThem()
    {
        using TemporaryDatabase unknownVersion = new();
        using (SqliteConnection connection = OpenRaw(unknownVersion.Path))
        {
            ExecuteNonQuery(connection, "PRAGMA user_version = 99;");
        }

        Assert.Throws<InvalidDataException>(() => SqliteEventJournal.Open(unknownVersion.Path));

        using TemporaryDatabase nonEmpty = new();
        using (SqliteConnection connection = OpenRaw(nonEmpty.Path))
        {
            ExecuteNonQuery(connection, "CREATE TABLE existing_marker (value TEXT NOT NULL);");
            ExecuteNonQuery(connection, "INSERT INTO existing_marker(value) VALUES ('preserve');");
        }

        Assert.Throws<InvalidDataException>(() => SqliteEventJournal.Open(nonEmpty.Path));
        using SqliteConnection verify = OpenRaw(nonEmpty.Path);
        Assert.Equal("preserve", ExecuteScalar<string>(verify,
            "SELECT value FROM existing_marker;"));
    }

    [Fact]
    public void ReadStreamRejectsCorruptedPayloadAndMismatchedCausalColumns()
    {
        using TemporaryDatabase corruptedPayload = new();
        using (SqliteEventJournal journal = SqliteEventJournal.Open(corruptedPayload.Path))
        {
            journal.Append("stream-a", CreateTradeEvent("trade-1", 0, 100m));
        }

        using (SqliteConnection connection = OpenRaw(corruptedPayload.Path))
        {
            UpdateColumn(connection, "payload_json", "not-json");
        }

        using (SqliteEventJournal journal = SqliteEventJournal.Open(corruptedPayload.Path))
        {
            Assert.Throws<InvalidDataException>(() =>
            {
                _ = journal.ReadStream("stream-a");
            });
        }

        using TemporaryDatabase mismatchedMetadata = new();
        using (SqliteEventJournal journal = SqliteEventJournal.Open(mismatchedMetadata.Path))
        {
            journal.Append("stream-a", CreateTradeEvent("trade-1", 0, 100m));
        }

        using (SqliteConnection connection = OpenRaw(mismatchedMetadata.Path))
        {
            UpdateColumn(connection, "event_time_ticks", long.MaxValue);
        }

        using SqliteEventJournal reopened = SqliteEventJournal.Open(mismatchedMetadata.Path);
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = reopened.ReadStream("stream-a");
        });
    }

    [Fact]
    public void BusyWriterFailsWithinConfiguredTimeoutAndDoesNotLeaveAnAppend()
    {
        using TemporaryDatabase database = new();
        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path, 1))
        using (SqliteConnection blocker = OpenRaw(database.Path))
        {
            using SqliteCommand begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();

            Assert.Throws<SqliteException>(() =>
            {
                _ = journal.Append("stream-a", CreateTradeEvent("blocked", 0, 100m));
            });

            using SqliteCommand rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        using SqliteEventJournal reopened = SqliteEventJournal.Open(database.Path, 1);
        Assert.Empty(reopened.ReadStream("stream-a"));
        Assert.True(reopened.Append("stream-a", CreateTradeEvent("after-release", 0, 100m)));
    }

    [Fact]
    public void OpenAndAppendRejectInvalidArguments()
    {
        using TemporaryDatabase database = new();

        Assert.Throws<ArgumentException>(() => SqliteEventJournal.Open(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqliteEventJournal.Open(database.Path, 0));

        using SqliteEventJournal journal = SqliteEventJournal.Open(database.Path);
        Assert.Throws<ArgumentException>(() =>
        {
            _ = journal.Append(" ", CreateTradeEvent("trade", 0, 100m));
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = journal.Append("stream-a", null!);
        });
        Assert.Throws<ArgumentException>(() => journal.ReadStream(" "));
    }

    private static CausalMarketEvent[] CreateFiveEvents() =>
    [
        CreateTradeEvent("trade-1", 0, 12345678901234567890123456789m, exchangeTime: BaseTime.AddMinutes(1)),
        new(new Bar("bar-1", Instrument, new UtcTimestamp(BaseTime.AddMinutes(1)),
                new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime.AddMinutes(1)),
                new Price(100m), new Price(105m), new Price(95m), new Price(101m),
                new Quantity(12.34567890123456789m)),
            new UtcTimestamp(BaseTime.AddMinutes(1).AddSeconds(1)),
            new UtcTimestamp(BaseTime.AddMinutes(1).AddSeconds(2)), 1),
        new(new BookDelta("book-1", Instrument, new UtcTimestamp(BaseTime.AddMinutes(2)),
                [new BookLevel(new Price(99m), new Quantity(1.23456789m))],
                [new BookLevel(new Price(101m), new Quantity(2.3456789m))]),
            new UtcTimestamp(BaseTime.AddMinutes(2).AddSeconds(1)),
            new UtcTimestamp(BaseTime.AddMinutes(2).AddSeconds(2)), 2),
        new(new FundingUpdate("funding-1", Instrument, new UtcTimestamp(BaseTime.AddMinutes(3)),
                0.00012345678901234567890123456789m,
                new UtcTimestamp(BaseTime.AddHours(8))),
            new UtcTimestamp(BaseTime.AddMinutes(3).AddSeconds(1)),
            new UtcTimestamp(BaseTime.AddMinutes(3).AddSeconds(2)), 3),
        new(new OIUpdate("oi-1", Instrument, new UtcTimestamp(BaseTime.AddMinutes(4)),
                123456.78901234567890123456789m),
            new UtcTimestamp(BaseTime.AddMinutes(4).AddSeconds(1)),
            new UtcTimestamp(BaseTime.AddMinutes(4).AddSeconds(2)), 4)
    ];

    private static CausalMarketEvent CreateTradeEvent(string eventId, long sequence,
        decimal price, DateTimeOffset? eventTime = null, DateTimeOffset? exchangeTime = null)
    {
        DateTimeOffset eventAt = eventTime ?? BaseTime.AddMinutes(sequence);
        return new CausalMarketEvent(
            new Trade(eventId, Instrument, new UtcTimestamp(eventAt), new Price(price),
                new Quantity(1.00000000000000000000000000001m), TradeSide.Buy),
            new UtcTimestamp(eventAt), new UtcTimestamp(eventAt.AddSeconds(1)), sequence,
            exchangeTime is null ? null : new UtcTimestamp(exchangeTime.Value));
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

    private static void UpdateColumn(SqliteConnection connection, string column, object value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"UPDATE event_journal SET {column} = $value;";
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string[] ReadColumnNames(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(event_journal);";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(1));
        }

        return names.ToArray();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-journal-{Guid.NewGuid():N}.sqlite");
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
