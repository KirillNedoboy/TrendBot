using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteOperationalStateTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void ObservationPersistsFillSnapshotAndUnreconciledCheckpointAcrossRestart()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path);
        Fill fill = new("fill-a", intent.IntentId, intent.InstrumentId, new Quantity(0.4m),
            new Price(101.25m), Time(0), Time(1), Time(2), 17,
            new Money(0.012345m, "USDT"), Time(3));
        Position position = CreatePosition("position-a", 0.4m, 2);
        PortfolioPositionSnapshot snapshot = new(portfolioId, position, intent.IntentId,
            PositionOrigin.Known);
        ReconciliationCheckpoint checkpoint = OpenCheckpoint(portfolioId, 1,
            [intent.IntentId], [position.PositionId], [], []);

        using (SqlitePortfolioReservationStore store = OpenStore(database.Path))
        {
            Assert.Equal(PortfolioObservationResult.Recorded,
                store.RecordObservation(fill, [snapshot], checkpoint));
            Assert.Equal(ReconciliationStatus.Unreconciled,
                store.ReadCheckpoint(portfolioId).Status);
        }

        using SqlitePortfolioReservationStore reopened = OpenStore(database.Path);
        Assert.Equal(fill, reopened.FindFill(fill.FillId));
        Fill persistedFill = reopened.FindFill(fill.FillId)!;
        Assert.Equal(0.4m, persistedFill.Quantity.Value);
        Assert.Equal(101.25m, persistedFill.Price.Value);
        Assert.Equal(new Money(0.012345m, "USDT"), persistedFill.Fee);
        Assert.Equal(Time(0), persistedFill.EventTime);
        Assert.Equal(Time(1), persistedFill.ReceiveTime);
        Assert.Equal(Time(2), persistedFill.AvailabilityTime);
        Assert.Equal(Time(3), persistedFill.ExchangeTime);
        Assert.Equal(17, persistedFill.StableSequence);
        Assert.Equal(snapshot, Assert.Single(reopened.ReadPositionSnapshots(portfolioId)));
        Assert.Equal(Unreconciled(checkpoint), reopened.ReadCheckpoint(portfolioId));
        Assert.False(reopened.CanRetry(portfolioId));
    }

    [Fact]
    public void DuplicateObservationIsIdempotentAndNewObservationInvalidatesAcknowledgement()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path,
            reservationQuantity: 2m);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        Fill fill = CreateFill("fill-a", intent, 1m);
        ReconciliationCheckpoint first = OpenCheckpoint(portfolioId, 1,
            [intent.IntentId], [], [], []);

        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(fill, [], first));
        Assert.Equal(PortfolioObservationResult.AlreadyRecorded,
            store.RecordObservation(fill, [], first));
        Assert.Equal(Unreconciled(first), store.ReadCheckpoint(portfolioId));

        ReconciliationCheckpoint acknowledged = store.AcknowledgeReconciliation(portfolioId, 1);
        Assert.Equal(ReconciliationStatus.Acknowledged, acknowledged.Status);
        Assert.True(store.CanRetry(portfolioId));

        Fill secondFill = CreateFill("fill-b", intent, 0.1m);
        ReconciliationCheckpoint second = OpenCheckpoint(portfolioId, 2,
            [intent.IntentId], [], [], []);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(secondFill, [], second));
        Assert.Equal(ReconciliationStatus.Unreconciled, store.ReadCheckpoint(portfolioId).Status);
        Assert.False(store.CanRetry(portfolioId));
    }

    [Fact]
    public void FillIdentityConflictsAggregateOverflowAndStaleRevisionDoNotMutate()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        Fill first = CreateFill("fill-a", intent, 0.6m);
        ReconciliationCheckpoint firstCheckpoint = OpenCheckpoint(portfolioId, 1,
            [intent.IntentId], [], [], []);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(first, [], firstCheckpoint));

        Fill conflicting = CreateFill("fill-a", intent, 0.5m);
        ReconciliationCheckpoint conflictCheckpoint = OpenCheckpoint(portfolioId, 2,
            [intent.IntentId], [], [], []);
        Assert.Throws<InvalidOperationException>(() =>
            store.RecordObservation(conflicting, [], conflictCheckpoint));

        Fill overflow = CreateFill("fill-b", intent, 0.5m);
        Assert.Throws<InvalidOperationException>(() =>
            store.RecordObservation(overflow, [], conflictCheckpoint));

        ReconciliationCheckpoint stale = OpenCheckpoint(portfolioId, 1,
            [intent.IntentId], [], [], []);
        Fill other = CreateFill("fill-c", intent, 0.1m);
        Assert.Throws<InvalidOperationException>(() =>
            store.RecordObservation(other, [], stale));

        Assert.Null(store.FindFill("fill-b"));
        Assert.Null(store.FindFill("fill-c"));
        Assert.Equal(1, store.ReadCheckpoint(portfolioId).Revision);
        Assert.Equal(first, store.FindFill(first.FillId));
    }

    [Fact]
    public void UnknownAndManualStateBlocksAcknowledgementAndRetry()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        Position manualPosition = CreatePosition("manual-position", 1m, 1);
        PortfolioPositionSnapshot snapshot = new(portfolioId, manualPosition, null,
            PositionOrigin.Manual);
        ReconciliationCheckpoint checkpoint = OpenCheckpoint(portfolioId, 1,
            [intent.IntentId], [manualPosition.PositionId], ["unknown-order"],
            [manualPosition.PositionId]);

        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null, [snapshot], checkpoint));
        Assert.False(store.CanRetry(portfolioId));
        Assert.Throws<InvalidOperationException>(() =>
            store.AcknowledgeReconciliation(portfolioId, 1));
        Assert.False(store.CanRetry(portfolioId));
    }

    [Theory]
    [InlineData(PositionOrigin.Manual)]
    [InlineData(PositionOrigin.Unknown)]
    public void LatestActualUnresolvedOriginBlocksAcknowledgementWithoutMarkers(
        PositionOrigin origin)
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, _) = CreateReservedState(database.Path);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        PortfolioPositionSnapshot snapshot = new(portfolioId,
            CreatePosition($"unresolved-{origin}", 1m, 1), null, origin);

        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null, [snapshot],
                OpenCheckpoint(portfolioId, 1, [], [], [], [])));
        Assert.Throws<InvalidOperationException>(() =>
            store.AcknowledgeReconciliation(portfolioId, 1));
        Assert.False(store.CanRetry(portfolioId));
    }

    [Fact]
    public void ExplicitClearingAndReconciliationAllowsRecoveryAfterRestart()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path);
        using (SqlitePortfolioReservationStore store = OpenStore(database.Path))
        {
            PortfolioPositionSnapshot manual = new(portfolioId,
                CreatePosition("manual-position", 1m, 1), null, PositionOrigin.Manual);
            Assert.Equal(PortfolioObservationResult.Recorded,
                store.RecordObservation(null, [manual],
                    OpenCheckpoint(portfolioId, 1, [], [], [], [])));
            Assert.Throws<InvalidOperationException>(() =>
                store.AcknowledgeReconciliation(portfolioId, 1));

            PortfolioPositionSnapshot reconciled = new(portfolioId,
                CreatePosition("manual-position", 0m, 2), intent.IntentId,
                PositionOrigin.Known);
            Assert.Equal(PortfolioObservationResult.Recorded,
                store.RecordObservation(null, [reconciled],
                    OpenCheckpoint(portfolioId, 2, [], [], [], [])));
            Assert.Throws<InvalidOperationException>(() =>
                store.AcknowledgeReconciliation(portfolioId, 1));
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 2).Status);
            Assert.True(store.CanRetry(portfolioId));
        }

        using SqlitePortfolioReservationStore reopened = OpenStore(database.Path);
        Assert.True(reopened.CanRetry(portfolioId));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            reopened.ReadCheckpoint(portfolioId).Status);
    }

    [Fact]
    public void EmptyPortfolioRequiresExplicitRevisionZeroAcknowledgement()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("empty-portfolio");
        using (SqlitePortfolioReservationStore store = OpenStore(database.Path))
        {
            Assert.Equal(ReconciliationStatus.Unreconciled,
                store.ReadCheckpoint(portfolioId).Status);
            Assert.False(store.CanRetry(portfolioId));
            Assert.Throws<InvalidOperationException>(() =>
                store.ReleaseAfterReconciliation(portfolioId, "missing-intent", 0));
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 0).Status);
            Assert.True(store.CanRetry(portfolioId));
        }

        using SqlitePortfolioReservationStore reopened = OpenStore(database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            reopened.ReadCheckpoint(portfolioId).Status);
    }

    [Fact]
    public void InconsistentKnownSnapshotLinkRollsBackObservation()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, _) = CreateReservedState(database.Path);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        PortfolioPositionSnapshot inconsistent = new(portfolioId,
            CreatePosition("inconsistent-position", 1m, 1), "missing-intent",
            PositionOrigin.Known);

        Assert.Throws<InvalidOperationException>(() =>
            store.RecordObservation(null, [inconsistent],
                OpenCheckpoint(portfolioId, 1, [], [], [], [])));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.ReadCheckpoint(portfolioId).Status);
        Assert.Empty(store.ReadPositionSnapshots(portfolioId));
    }

    [Fact]
    public void PartialFillRetainsFullReservationUntilAcknowledgedClosedCheckpointThenReleaseIsIdempotent()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path,
            reservationQuantity: 1m);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        Fill fill = CreateFill("fill-a", intent, 0.5m);
        Position openPosition = CreatePosition("position-a", 0.5m, 1);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(fill,
                [new PortfolioPositionSnapshot(portfolioId, openPosition, intent.IntentId,
                    PositionOrigin.Known)],
                OpenCheckpoint(portfolioId, 1, [intent.IntentId], [openPosition.PositionId], [], [])));
        Assert.False(store.CanRetry(portfolioId));
        OrderIntent nextIntent = CreateIntent("intent-next", "client-next", 1m);
        PortfolioReservation nextReservation = new("reservation-next", portfolioId,
            nextIntent.IntentId, new Money(8m, "USDT"), new Money(60m, "USDT"), 1);
        Assert.Equal(PortfolioReservationResult.InsufficientCapacity,
            RiskReservationTestSupport.Reserve(store, nextIntent, nextReservation));
        Assert.Throws<InvalidOperationException>(() =>
            store.ReleaseAfterReconciliation(portfolioId, intent.IntentId, 1));

        Fill secondFill = CreateFill("fill-b", intent, 0.25m);
        Position secondOpenPosition = CreatePosition("position-a", 0.75m, 2);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(secondFill,
                [new PortfolioPositionSnapshot(portfolioId, secondOpenPosition,
                    intent.IntentId, PositionOrigin.Known)],
                OpenCheckpoint(portfolioId, 2, [intent.IntentId],
                    [secondOpenPosition.PositionId], [], [])));
        Assert.Equal(PortfolioReservationResult.InsufficientCapacity,
            RiskReservationTestSupport.Reserve(store, nextIntent, nextReservation));
        Assert.Throws<InvalidOperationException>(() =>
            store.ReleaseAfterReconciliation(portfolioId, intent.IntentId, 2));

        Position closedPosition = CreatePosition("position-a", 0m, 2);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null,
                [new PortfolioPositionSnapshot(portfolioId, closedPosition, intent.IntentId,
                    PositionOrigin.Known)],
                OpenCheckpoint(portfolioId, 3, [], [], [], [])));
        Assert.Throws<InvalidOperationException>(() =>
            store.ReleaseAfterReconciliation(portfolioId, intent.IntentId, 3));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 3).Status);
        Assert.True(store.CanRetry(portfolioId));
        Assert.True(store.ReleaseAfterReconciliation(portfolioId, intent.IntentId, 3));
        Assert.False(store.ReleaseAfterReconciliation(portfolioId, intent.IntentId, 3));
        Assert.NotNull(store.FindRiskEvaluation("evaluation-intent-a"));

        Assert.Equal(PortfolioReservationResult.Reserved,
            RiskReservationTestSupport.Reserve(store, nextIntent, nextReservation));
    }

    [Fact]
    public void ReleaseRequiresAnAssociatedPositionSnapshot()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);

        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null, [],
                OpenCheckpoint(portfolioId, 1, [], [], [], [])));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 1).Status);

        Assert.Throws<InvalidOperationException>(() =>
            store.ReleaseAfterReconciliation(portfolioId, intent.IntentId, 1));
        Assert.Equal(intent.IntentId, Assert.Single(store.ReadReservations(portfolioId)).IntentId);
    }

    [Fact]
    public void ReleaseRequiresClosedIntentEvenWhenPositionSnapshotIsClosed()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        Position closedPosition = CreatePosition("position-a", 0m, 1);

        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null,
                [new PortfolioPositionSnapshot(portfolioId, closedPosition, intent.IntentId,
                    PositionOrigin.Known)],
                OpenCheckpoint(portfolioId, 1, [intent.IntentId], [], [], [])));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 1).Status);

        Assert.Throws<InvalidOperationException>(() =>
            store.ReleaseAfterReconciliation(portfolioId, intent.IntentId, 1));
        Assert.Equal(intent.IntentId, Assert.Single(store.ReadReservations(portfolioId)).IntentId);
    }

    [Fact]
    public void CheckedFillQuantityOverflowRollsBackObservation()
    {
        using TemporaryDatabase database = new();
        (PortfolioId portfolioId, OrderIntent intent) = CreateReservedState(database.Path,
            reservationQuantity: decimal.MaxValue);
        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        Fill first = CreateFill("fill-max", intent, decimal.MaxValue);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(first, [],
                OpenCheckpoint(portfolioId, 1, [intent.IntentId], [], [], [])));

        Fill overflowing = CreateFill("fill-overflow", intent, 1m);
        Assert.Throws<OverflowException>(() =>
            store.RecordObservation(overflowing, [],
                OpenCheckpoint(portfolioId, 2, [intent.IntentId], [], [], [])));
        Assert.Null(store.FindFill(overflowing.FillId));
        Assert.Equal(1, store.ReadCheckpoint(portfolioId).Revision);
    }

    [Fact]
    public void VersionFourMigrationPreservesPriorStateAndCreatesOperationalTables()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("portfolio-a");
        OrderIntent intent = CreateIntent("intent-a", "client-a", 1m);
        PortfolioLimits limits = new(portfolioId, new Money(10m, "USDT"),
            new Money(100m, "USDT"), 1);
        PortfolioReservation reservation = new("reservation-a", portfolioId, intent.IntentId,
            new Money(3m, "USDT"), new Money(40m, "USDT"), 1);
        CausalMarketEvent marketEvent = new(
            new Trade("trade-a", Instrument, Time(0), new Price(100m), new Quantity(1m),
                TradeSide.Buy), Time(1), Time(2), 0);

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
                CREATE TABLE portfolios
                (
                    portfolio_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                    limits_json TEXT NOT NULL
                ) WITHOUT ROWID;
                CREATE TABLE reservations
                (
                    reservation_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                    portfolio_id TEXT COLLATE BINARY NOT NULL,
                    intent_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                    reservation_json TEXT NOT NULL
                ) WITHOUT ROWID;
                PRAGMA user_version = 4;
                """);
            Insert(connection, """
                INSERT INTO event_journal
                (stream_id, stable_sequence, payload_json, event_time_ticks,
                 receive_time_ticks, availability_time_ticks, exchange_time_ticks,
                 envelope_version)
                VALUES ($stream, $sequence, $payload, $event, $receive, $available,
                        NULL, 1);
                """, ("$stream", "stream-a"), ("$sequence", 0L),
                ("$payload", CoreJsonSerializer.Serialize(marketEvent.Payload)),
                ("$event", marketEvent.Payload.EventTime.Value.UtcDateTime.Ticks),
                ("$receive", marketEvent.ReceiveTime.Value.UtcDateTime.Ticks),
                ("$available", marketEvent.AvailabilityTime.Value.UtcDateTime.Ticks));
            Insert(connection, """
                INSERT INTO order_intents (intent_id, client_order_id, intent_json)
                VALUES ($intent, $client, $json);
                """, ("$intent", intent.IntentId),
                ("$client", intent.ClientOrderId), ("$json", CoreJsonSerializer.Serialize(intent)));
            Insert(connection, """
                INSERT INTO portfolios (portfolio_id, limits_json)
                VALUES ($portfolio, $json);
                """, ("$portfolio", portfolioId.Value),
                ("$json", CoreJsonSerializer.Serialize(limits)));
            Insert(connection, """
                INSERT INTO reservations
                (reservation_id, portfolio_id, intent_id, reservation_json)
                VALUES ($reservation, $portfolio, $intent, $json);
                """, ("$reservation", reservation.ReservationId),
                ("$portfolio", portfolioId.Value), ("$intent", intent.IntentId),
                ("$json", CoreJsonSerializer.Serialize(reservation)));
        }

        using SqlitePortfolioReservationStore store = OpenStore(database.Path);
        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(14L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(marketEvent, Assert.Single(SqliteEventJournal.Open(database.Path).ReadStream("stream-a")));
        Assert.Equal(intent, SqliteOrderIntentJournal.Open(database.Path).Find(intent.IntentId));
        Assert.Equal(limits, store.FindLimits(portfolioId));
        Assert.Equal(reservation, Assert.Single(store.ReadReservations(portfolioId)));
        Assert.Equal(0L, ExecuteScalar<long>(verify, "SELECT COUNT(*) FROM fills;"));
        Assert.Equal(0L, ExecuteScalar<long>(verify, "SELECT COUNT(*) FROM position_snapshots;"));
        Assert.Equal(0L, ExecuteScalar<long>(verify, "SELECT COUNT(*) FROM reconciliation_checkpoints;"));
    }

    private static (PortfolioId PortfolioId, OrderIntent Intent) CreateReservedState(string path,
        decimal reservationQuantity = 1m)
    {
        PortfolioId portfolioId = new("portfolio-a");
        PortfolioLimits limits = new(portfolioId, new Money(10m, "USDT"),
            new Money(100m, "USDT"), 2);
        OrderIntent intent = CreateIntent("intent-a", "client-a", reservationQuantity);
        PortfolioReservation reservation = new("reservation-a", portfolioId, intent.IntentId,
            new Money(3m, "USDT"), new Money(40m, "USDT"), 1);
        using SqlitePortfolioReservationStore store = OpenStore(path);
        Assert.True(store.Register(limits));
        Assert.Equal(PortfolioReservationResult.Reserved,
            RiskReservationTestSupport.Reserve(store, intent, reservation));
        return (portfolioId, intent);
    }

    private static SqlitePortfolioReservationStore OpenStore(string path) =>
        SqlitePortfolioReservationStore.Open(path);

    private static OrderIntent CreateIntent(string intentId, string clientOrderId,
        decimal quantity) => new(intentId, "strategy-run-1",
        DecisionProjectionFixtures.CreateProjection(DecisionEngineProfile.Allow)
            .Result.Decision.SetupId,
        clientOrderId, Instrument, Direction.Long, OrderRole.Entry, 1, new Quantity(quantity),
        new Price(100m));

    private static Fill CreateFill(string fillId, OrderIntent intent, decimal quantity) =>
        new(fillId, intent.IntentId, intent.InstrumentId, new Quantity(quantity),
            new Price(100m), Time(0), Time(1), Time(1), fillId[^1] - '0');

    private static Position CreatePosition(string positionId, decimal quantity, int minute) =>
        new(positionId, Instrument, Direction.Long, new Quantity(quantity), Time(0), Time(minute),
            new Price(100m));

    private static ReconciliationCheckpoint OpenCheckpoint(PortfolioId portfolioId, long revision,
        IEnumerable<string> openIntentIds, IEnumerable<string> openPositionIds,
        IEnumerable<string> unknownMarkers, IEnumerable<string> manualPositionIds) =>
        new(portfolioId, revision, openIntentIds, openPositionIds, unknownMarkers, manualPositionIds);

    private static ReconciliationCheckpoint Unreconciled(ReconciliationCheckpoint checkpoint) =>
        new(checkpoint.PortfolioId, checkpoint.Revision, checkpoint.OpenIntentIds,
            checkpoint.OpenPositionIds, checkpoint.UnknownMarkers, checkpoint.ManualPositionIds,
            ReconciliationStatus.Unreconciled);

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddMinutes(minutes));

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

    private static void Insert(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
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
                $"tradingbot-operational-{Guid.NewGuid():N}.sqlite");
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
