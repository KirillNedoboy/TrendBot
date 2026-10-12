using Microsoft.Data.Sqlite;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class DeterministicPersistenceRecoveryTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void RestartReproducesEnvelopeOrderCanonicalJsonAndProjection()
    {
        using TemporaryDatabase database = new();
        ReplayEnvelope[] expected =
        [
            CreateEnvelope("first", 0, 2, 2),
            CreateEnvelope("late-event", 2, 0, 0),
            CreateEnvelope("unavailable", 5, 1, 3),
            CreateEnvelope("boundary", 7, 2, 2)
        ];
        CausalEventCursor cursor = new(Time(2), 7);

        string[] beforeJson;
        string[] beforeStoredIds;
        string[] beforeSnapshotIds;
        string beforeProjection;
        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            foreach (ReplayEnvelope envelope in expected)
            {
                Assert.True(journal.Append(envelope));
            }

            IReadOnlyList<ReplayEnvelope> before = journal.ReadEnvelopes("stream-a");
            beforeJson = before.Select(CoreJsonSerializer.Serialize).ToArray();
            beforeStoredIds = before.Select(static item => item.CausalEvent.Payload.EventId).ToArray();
            ReplayStreamSnapshot snapshot = ReplaySnapshotBuilder.Build("stream-a", before, cursor);
            beforeSnapshotIds = snapshot.Events
                .Select(static item => item.CausalEvent.Payload.EventId).ToArray();
            beforeProjection = Project(snapshot);
        }

        using (SqliteEventJournal reopened = SqliteEventJournal.Open(database.Path))
        {
            IReadOnlyList<ReplayEnvelope> after = reopened.ReadEnvelopes("stream-a");
            string[] afterJson = after.Select(CoreJsonSerializer.Serialize).ToArray();
            string[] afterStoredIds = after
                .Select(static item => item.CausalEvent.Payload.EventId).ToArray();
            ReplayStreamSnapshot snapshot = ReplaySnapshotBuilder.Build("stream-a", after, cursor);
            string[] afterSnapshotIds = snapshot.Events
                .Select(static item => item.CausalEvent.Payload.EventId).ToArray();

            Assert.Equal(expected, after);
            Assert.Equal(beforeJson, afterJson);
            Assert.Equal(beforeStoredIds, afterStoredIds);
            Assert.Equal(beforeSnapshotIds, afterSnapshotIds);
            Assert.Equal(beforeProjection, Project(snapshot));
        }

        Assert.Equal(["first", "late-event", "unavailable", "boundary"], beforeStoredIds);
        Assert.Equal(["late-event", "first", "boundary"], beforeSnapshotIds);
    }

    [Fact]
    public void DuplicateConflictAndSequenceGapBehaviorsLeaveDurableRowsDeterministic()
    {
        using TemporaryDatabase database = new();
        ReplayEnvelope first = CreateEnvelope("first", 0, 0, 0);
        ReplayEnvelope gap = CreateEnvelope("gap", 2, 2, 2);
        ReplayEnvelope conflictingSequence = CreateEnvelope("conflict", 2, 3, 3);
        ReplayEnvelope lowerSequence = CreateEnvelope("lower", 1, 1, 1);

        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append(first));
            Assert.False(journal.Append(first));
            Assert.True(journal.Append(gap));
            Assert.Throws<InvalidOperationException>(() => journal.Append(conflictingSequence));
            Assert.Throws<InvalidOperationException>(() => journal.Append(lowerSequence));
            Assert.Equal(["first", "gap"], journal.ReadEnvelopes("stream-a")
                .Select(static item => item.CausalEvent.Payload.EventId));
        }

        using SqliteEventJournal reopened = SqliteEventJournal.Open(database.Path);
        Assert.Equal(["first", "gap"], reopened.ReadEnvelopes("stream-a")
            .Select(static item => item.CausalEvent.Payload.EventId));
    }

    [Fact]
    public void CorruptRowsFailClosedWithoutChangingOtherDurableRows()
    {
        using TemporaryDatabase database = new();
        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append(CreateEnvelope("first", 0, 0, 0)));
            Assert.True(journal.Append(CreateEnvelope("second", 1, 1, 1)));
        }

        using (SqliteConnection connection = OpenRaw(database.Path))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE event_journal
                SET payload_json = 'not-json'
                WHERE stable_sequence = 1;
                """;
            command.ExecuteNonQuery();
        }

        using SqliteEventJournal reopened = SqliteEventJournal.Open(database.Path);
        Assert.Throws<InvalidDataException>(() => reopened.ReadEnvelopes("stream-a"));

        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(2L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM event_journal WHERE stream_id = 'stream-a';"));
    }

    [Fact]
    public void BusyWriterDoesNotLeaveAPartialAppendAfterFailure()
    {
        using TemporaryDatabase database = new();
        using SqliteEventJournal journal = SqliteEventJournal.Open(database.Path, 1);
        using SqliteConnection blocker = OpenRaw(database.Path);
        using (SqliteCommand begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => journal.Append(
            CreateEnvelope("blocked", 0, 0, 0)));

        using (SqliteCommand rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        using SqliteEventJournal reopened = SqliteEventJournal.Open(database.Path, 1);
        Assert.Empty(reopened.ReadEnvelopes("stream-a"));
        Assert.True(reopened.Append(CreateEnvelope("after-release", 0, 0, 0)));
        Assert.Single(reopened.ReadEnvelopes("stream-a"));
    }

    private static string Project(ReplayStreamSnapshot snapshot) =>
        string.Join("|", snapshot.Events.Select(static envelope =>
            $"{envelope.CausalEvent.Payload.EventId}:{envelope.CausalEvent.StableSequence}:"
            + $"{envelope.CausalEvent.Payload.EventTime.Value.UtcDateTime.Ticks}"));

    private static ReplayEnvelope CreateEnvelope(string id, long sequence,
        int eventMinutes, int availabilityMinutes) => new("stream-a",
        new CausalMarketEvent(
            new Trade(id, Instrument, Time(eventMinutes), new Price(100m + sequence),
                new Quantity(1.00000000000000000000000000001m), TradeSide.Buy),
            Time(eventMinutes), Time(availabilityMinutes), sequence,
            Time(eventMinutes + 1)));

    private static UtcTimestamp Time(int minutes) =>
        new(BaseTime.AddMinutes(minutes).ToUniversalTime());

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

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-recovery-{Guid.NewGuid():N}.sqlite");
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
