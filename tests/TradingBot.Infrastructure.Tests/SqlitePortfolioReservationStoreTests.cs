using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqlitePortfolioReservationStoreTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void RegisterTryReserveAndRestartPersistLimitsIntentAndReservation()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(10m, "USDT"),
            new Money(100m, "USDT"), 2);
        OrderIntent intent = CreateIntent("intent-a", "client-a");
        PortfolioReservation reservation = new("reservation-a", portfolioId, intent.IntentId,
            new Money(3m, "USDT"), new Money(40m, "USDT"), 1);

        using (SqlitePortfolioReservationStore store =
            SqlitePortfolioReservationStore.Open(database.Path))
        {
            Assert.True(store.Register(limits));
            Assert.Equal(PortfolioReservationResult.Reserved,
                store.TryReserve(intent, reservation));
            Assert.Equal(limits, store.FindLimits(portfolioId));
            Assert.Equal(reservation, Assert.Single(store.ReadReservations(portfolioId)));
        }

        using SqlitePortfolioReservationStore reopened =
            SqlitePortfolioReservationStore.Open(database.Path);
        Assert.Equal(limits, reopened.FindLimits(portfolioId));
        Assert.Equal(reservation, Assert.Single(reopened.ReadReservations(portfolioId)));
        using SqliteOrderIntentJournal intents = SqliteOrderIntentJournal.Open(database.Path);
        Assert.Equal(intent, intents.Find(intent.IntentId));
    }

    [Fact]
    public void ExactRiskBoundaryIsAcceptedAndTheNextRiskReservationIsRejectedWithoutIntent()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(5m, "USDT"),
            new Money(100m, "USDT"), 3);
        using SqlitePortfolioReservationStore store = OpenRegistered(database.Path, limits);

        OrderIntent firstIntent = CreateIntent("intent-1", "client-1");
        PortfolioReservation firstReservation = CreateReservation("reservation-1", portfolioId,
            firstIntent, risk: 5m, notional: 10m, slots: 1);
        Assert.Equal(PortfolioReservationResult.Reserved,
            store.TryReserve(firstIntent, firstReservation));

        OrderIntent rejectedIntent = CreateIntent("intent-2", "client-2");
        PortfolioReservation rejectedReservation = CreateReservation("reservation-2", portfolioId,
            rejectedIntent, risk: 0.0000000000000000000000000001m, notional: 1m, slots: 1);
        Assert.Equal(PortfolioReservationResult.InsufficientCapacity,
            store.TryReserve(rejectedIntent, rejectedReservation));
        Assert.Null(store.FindReservation(rejectedReservation.ReservationId));
        using SqliteOrderIntentJournal intents = SqliteOrderIntentJournal.Open(database.Path);
        Assert.Null(intents.Find(rejectedIntent.IntentId));
    }

    [Fact]
    public void ExactNotionalBoundaryIsAcceptedAndTheNextExposureReservationIsRejected()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(50m, "USDT"), 3);
        using SqlitePortfolioReservationStore store = OpenRegistered(database.Path, limits);

        OrderIntent firstIntent = CreateIntent("intent-1", "client-1");
        PortfolioReservation firstReservation = CreateReservation("reservation-1", portfolioId,
            firstIntent, risk: 1m, notional: 50m, slots: 1);
        Assert.Equal(PortfolioReservationResult.Reserved,
            store.TryReserve(firstIntent, firstReservation));

        OrderIntent rejectedIntent = CreateIntent("intent-2", "client-2");
        PortfolioReservation rejectedReservation = CreateReservation("reservation-2", portfolioId,
            rejectedIntent, risk: 1m, notional: 0.00000000000000000001m, slots: 1);
        Assert.Equal(PortfolioReservationResult.InsufficientCapacity,
            store.TryReserve(rejectedIntent, rejectedReservation));
        Assert.Null(store.FindReservation(rejectedReservation.ReservationId));
    }

    [Fact]
    public void ExactPositionSlotBoundaryIsAcceptedAndTheNextSlotReservationIsRejected()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(100m, "USDT"), 1);
        using SqlitePortfolioReservationStore store = OpenRegistered(database.Path, limits);

        OrderIntent firstIntent = CreateIntent("intent-1", "client-1");
        PortfolioReservation firstReservation = CreateReservation("reservation-1", portfolioId,
            firstIntent, risk: 1m, notional: 1m, slots: 1);
        Assert.Equal(PortfolioReservationResult.Reserved,
            store.TryReserve(firstIntent, firstReservation));

        OrderIntent rejectedIntent = CreateIntent("intent-2", "client-2");
        PortfolioReservation rejectedReservation = CreateReservation("reservation-2", portfolioId,
            rejectedIntent, risk: 1m, notional: 1m, slots: 1);
        Assert.Equal(PortfolioReservationResult.InsufficientCapacity,
            store.TryReserve(rejectedIntent, rejectedReservation));
        Assert.Null(store.FindReservation(rejectedReservation.ReservationId));
    }

    [Fact]
    public void IdenticalRetryIsAlreadyReservedAndReservationOrIntentConflictsDoNotMutateState()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(100m, "USDT"), 3);
        using SqlitePortfolioReservationStore store = OpenRegistered(database.Path, limits);
        OrderIntent intent = CreateIntent("intent-1", "client-1");
        PortfolioReservation reservation = CreateReservation("reservation-1", portfolioId, intent,
            risk: 1m, notional: 2m, slots: 1);

        Assert.Equal(PortfolioReservationResult.Reserved, store.TryReserve(intent, reservation));
        Assert.Equal(PortfolioReservationResult.AlreadyReserved,
            store.TryReserve(intent, reservation));

        PortfolioReservation reservationConflict = CreateReservation("reservation-1", portfolioId,
            intent, risk: 2m, notional: 2m, slots: 1);
        Assert.Throws<InvalidOperationException>(() => store.TryReserve(intent, reservationConflict));

        OrderIntent intentConflict = CreateIntent("intent-2", "client-1");
        PortfolioReservation intentReservationConflict = CreateReservation("reservation-2",
            portfolioId, intentConflict, risk: 2m, notional: 2m, slots: 1);
        Assert.Throws<InvalidOperationException>(() =>
            store.TryReserve(intentConflict, intentReservationConflict));

        Assert.Equal(reservation, Assert.Single(store.ReadReservations(portfolioId)));
        using SqliteOrderIntentJournal intents = SqliteOrderIntentJournal.Open(database.Path);
        Assert.Equal(intent, intents.Find(intent.IntentId));
        Assert.Null(intents.Find("intent-2"));
    }

    [Fact]
    public void MissingPortfolioAndCurrencyConflictFailBeforeAnyMutation()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioId missingPortfolioId = new("portfolio-missing");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(100m, "USDT"), 1);
        using SqlitePortfolioReservationStore store = OpenRegistered(database.Path, limits);
        OrderIntent missingIntent = CreateIntent("missing-intent", "missing-client");
        PortfolioReservation missingReservation = CreateReservation("missing-reservation",
            missingPortfolioId, missingIntent, risk: 1m, notional: 1m, slots: 1);

        Assert.Throws<InvalidOperationException>(() =>
            store.TryReserve(missingIntent, missingReservation));

        OrderIntent currencyIntent = CreateIntent("currency-intent", "currency-client");
        PortfolioReservation currencyReservation = new("currency-reservation", portfolioId,
            currencyIntent.IntentId, new Money(1m, "USD"), new Money(1m, "USD"), 1);
        Assert.Throws<ArgumentException>(() =>
            store.TryReserve(currencyIntent, currencyReservation));

        Assert.Empty(store.ReadReservations(portfolioId));
        using SqliteOrderIntentJournal intents = SqliteOrderIntentJournal.Open(database.Path);
        Assert.Null(intents.Find(missingIntent.IntentId));
        Assert.Null(intents.Find(currencyIntent.IntentId));
    }

    [Fact]
    public async Task IndependentConnectionsSerializeRiskReservationsAtTheCapacityBoundary()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(3m, "USDT"),
            new Money(100m, "USDT"), 10);
        using SqlitePortfolioReservationStore first = OpenRegistered(database.Path, limits);
        using SqlitePortfolioReservationStore second =
            SqlitePortfolioReservationStore.Open(database.Path);
        using Barrier barrier = new(2);

        PortfolioReservation firstReservation = CreateReservation("reservation-1", portfolioId,
            CreateIntent("intent-1", "client-1"), risk: 2m, notional: 1m, slots: 1);
        PortfolioReservation secondReservation = CreateReservation("reservation-2", portfolioId,
            CreateIntent("intent-2", "client-2"), risk: 2m, notional: 1m, slots: 1);

        Task<PortfolioReservationResult> firstAttempt = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return first.TryReserve(CreateIntent("intent-1", "client-1"), firstReservation);
        });
        Task<PortfolioReservationResult> secondAttempt = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return second.TryReserve(CreateIntent("intent-2", "client-2"), secondReservation);
        });

        PortfolioReservationResult[] results = await Task.WhenAll(firstAttempt, secondAttempt);
        Assert.Equal(1, results.Count(result => result == PortfolioReservationResult.Reserved));
        Assert.Equal(1, results.Count(result => result == PortfolioReservationResult.InsufficientCapacity));
    }

    [Fact]
    public async Task IndependentConnectionsSerializeNotionalReservationsAtTheCapacityBoundary()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(3m, "USDT"), 10);
        using SqlitePortfolioReservationStore first = OpenRegistered(database.Path, limits);
        using SqlitePortfolioReservationStore second =
            SqlitePortfolioReservationStore.Open(database.Path);
        using Barrier barrier = new(2);

        OrderIntent firstIntent = CreateIntent("intent-1", "client-1");
        OrderIntent secondIntent = CreateIntent("intent-2", "client-2");
        PortfolioReservation firstReservation = CreateReservation("reservation-1", portfolioId,
            firstIntent, risk: 1m, notional: 2m, slots: 1);
        PortfolioReservation secondReservation = CreateReservation("reservation-2", portfolioId,
            secondIntent, risk: 1m, notional: 2m, slots: 1);

        Task<PortfolioReservationResult> firstAttempt = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return first.TryReserve(firstIntent, firstReservation);
        });
        Task<PortfolioReservationResult> secondAttempt = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return second.TryReserve(secondIntent, secondReservation);
        });

        PortfolioReservationResult[] results = await Task.WhenAll(firstAttempt, secondAttempt);
        Assert.Equal(1, results.Count(result => result == PortfolioReservationResult.Reserved));
        Assert.Equal(1, results.Count(result => result == PortfolioReservationResult.InsufficientCapacity));
    }

    [Fact]
    public async Task IndependentConnectionsSerializePositionSlotReservationsAtTheCapacityBoundary()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(100m, "USDT"),
            new Money(100m, "USDT"), 1);
        using SqlitePortfolioReservationStore first = OpenRegistered(database.Path, limits);
        using SqlitePortfolioReservationStore second =
            SqlitePortfolioReservationStore.Open(database.Path);
        using Barrier barrier = new(2);

        OrderIntent firstIntent = CreateIntent("intent-1", "client-1");
        OrderIntent secondIntent = CreateIntent("intent-2", "client-2");
        PortfolioReservation firstReservation = CreateReservation("reservation-1", portfolioId,
            firstIntent, risk: 1m, notional: 1m, slots: 1);
        PortfolioReservation secondReservation = CreateReservation("reservation-2", portfolioId,
            secondIntent, risk: 1m, notional: 1m, slots: 1);

        Task<PortfolioReservationResult> firstAttempt = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return first.TryReserve(firstIntent, firstReservation);
        });
        Task<PortfolioReservationResult> secondAttempt = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return second.TryReserve(secondIntent, secondReservation);
        });

        PortfolioReservationResult[] results = await Task.WhenAll(firstAttempt, secondAttempt);
        Assert.Equal(1, results.Count(result => result == PortfolioReservationResult.Reserved));
        Assert.Equal(1, results.Count(result => result == PortfolioReservationResult.InsufficientCapacity));
    }

    [Fact]
    public void BusyWriterLeavesIntentAndReservationUnchangedAfterRollback()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(10m, "USDT"),
            new Money(10m, "USDT"), 1);
        using SqlitePortfolioReservationStore store =
            OpenRegistered(database.Path, limits, commandTimeoutSeconds: 1);
        using SqliteConnection blocker = OpenRaw(database.Path);
        ExecuteNonQuery(blocker, "BEGIN IMMEDIATE;");

        OrderIntent intent = CreateIntent("blocked-intent", "blocked-client");
        PortfolioReservation reservation = CreateReservation("blocked-reservation", portfolioId,
            intent, risk: 1m, notional: 1m, slots: 1);
        Assert.Throws<SqliteException>(() => store.TryReserve(intent, reservation));

        ExecuteNonQuery(blocker, "ROLLBACK;");
        Assert.Null(store.FindReservation(reservation.ReservationId));
        using SqliteOrderIntentJournal intents = SqliteOrderIntentJournal.Open(database.Path, 1);
        Assert.Null(intents.Find(intent.IntentId));
    }

    [Fact]
    public void VersionThreeDatabaseMigratesToFourAndPreservesEventAndIntentRows()
    {
        using TemporaryDatabase database = new();
        CausalMarketEvent expectedEvent = new(
            new Trade("trade-1", Instrument, Time(0), new Price(100m),
                new Quantity(1m), TradeSide.Buy), Time(0), Time(1), 0);
        OrderIntent expectedIntent = CreateIntent("intent-1", "client-1");

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
                CREATE TABLE order_intents
                (
                    intent_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                    client_order_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                    intent_json TEXT NOT NULL
                ) WITHOUT ROWID;
                PRAGMA user_version = 3;
                """);

            using SqliteCommand insertEvent = connection.CreateCommand();
            insertEvent.CommandText = """
                INSERT INTO event_journal
                    (stream_id, stable_sequence, envelope_version, payload_json,
                     event_time_ticks, receive_time_ticks, availability_time_ticks,
                     exchange_time_ticks)
                VALUES ($stream_id, $stable_sequence, $envelope_version, $payload_json,
                        $event_time_ticks, $receive_time_ticks, $availability_time_ticks,
                        $exchange_time_ticks);
                """;
            insertEvent.Parameters.AddWithValue("$stream_id", "stream-a");
            insertEvent.Parameters.AddWithValue("$stable_sequence", expectedEvent.StableSequence);
            insertEvent.Parameters.AddWithValue("$envelope_version", ReplayEnvelope.CurrentFormatVersion);
            insertEvent.Parameters.AddWithValue("$payload_json",
                CoreJsonSerializer.Serialize(expectedEvent.Payload));
            insertEvent.Parameters.AddWithValue("$event_time_ticks", Ticks(expectedEvent.Payload.EventTime));
            insertEvent.Parameters.AddWithValue("$receive_time_ticks", Ticks(expectedEvent.ReceiveTime));
            insertEvent.Parameters.AddWithValue("$availability_time_ticks", Ticks(expectedEvent.AvailabilityTime));
            insertEvent.Parameters.AddWithValue("$exchange_time_ticks", DBNull.Value);
            insertEvent.ExecuteNonQuery();

            using SqliteCommand insertIntent = connection.CreateCommand();
            insertIntent.CommandText = """
                INSERT INTO order_intents (intent_id, client_order_id, intent_json)
                VALUES ($intent_id, $client_order_id, $intent_json);
                """;
            insertIntent.Parameters.AddWithValue("$intent_id", expectedIntent.IntentId);
            insertIntent.Parameters.AddWithValue("$client_order_id", expectedIntent.ClientOrderId);
            insertIntent.Parameters.AddWithValue("$intent_json", CoreJsonSerializer.Serialize(expectedIntent));
            insertIntent.ExecuteNonQuery();
        }

        using SqlitePortfolioReservationStore migrated = SqlitePortfolioReservationStore.Open(database.Path);
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(8L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(0L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM portfolios;"));
        Assert.Equal(0L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM reservations;"));

        using SqliteEventJournal events = SqliteEventJournal.Open(database.Path);
        Assert.Equal(expectedEvent, Assert.Single(events.ReadStream("stream-a")));
        using SqliteOrderIntentJournal intents = SqliteOrderIntentJournal.Open(database.Path);
        Assert.Equal(expectedIntent, intents.Find(expectedIntent.IntentId));
        Assert.Null(migrated.FindLimits(new PortfolioId("portfolio-a")));
    }

    private static SqlitePortfolioReservationStore OpenRegistered(string path,
        PortfolioLimits limits, int commandTimeoutSeconds = 30)
    {
        SqlitePortfolioReservationStore store =
            SqlitePortfolioReservationStore.Open(path, commandTimeoutSeconds);
        Assert.True(store.Register(limits));
        return store;
    }

    private static PortfolioReservation CreateReservation(string reservationId,
        PortfolioId portfolioId, OrderIntent intent, decimal risk, decimal notional, int slots) =>
        new(reservationId, portfolioId, intent.IntentId, new Money(risk, "USDT"),
            new Money(notional, "USDT"), slots);

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) =>
        new(intentId, "strategy-run-1", "setup-1", clientOrderId, Instrument, Direction.Long,
            OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddMinutes(minutes));

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
                $"tradingbot-reservation-{Guid.NewGuid():N}.sqlite");
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
