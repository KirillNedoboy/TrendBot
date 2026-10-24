using System.Reflection;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ProtectiveExecutionTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void ProtectiveClaimRequiresKnownOppositeReduceOnlyPositionAndSkipsEntryReservation()
    {
        using TemporaryDatabase database = new();
        OrderIntent source = CreateIntent("protective-source", "protective-source-client",
            Direction.Long, OrderRole.Entry, 2m, reduceOnly: false);
        OrderIntent protective = CreateIntent("protective-stop", "protective-stop-client",
            Direction.Short, OrderRole.Stop, 1m, reduceOnly: true, limitPrice: 90m);
        PortfolioId portfolioId = new("protective-portfolio");
        PersistIntents(database.Path, source, protective);
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);

        UtcTimestamp observedAt = Time(1);
        Position position = new("position-protective", Instrument, Direction.Long,
            new Quantity(2m), observedAt, observedAt, new Price(100m));
        PortfolioPositionSnapshot snapshot = new(portfolioId, position, source.IntentId,
            PositionOrigin.Known);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null, [snapshot], new ReconciliationCheckpoint(portfolioId, 1,
                [source.IntentId], [position.PositionId], [], [],
                ReconciliationStatus.Unreconciled)));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 1).Status);

        object request = CreateRequest(protective, snapshot, source.IntentId, 1);
        RecordingDispatcher dispatcher = new();
        ExecutionCoordinator coordinator = new(store, dispatcher);
        ExecutionDispatchClaimResult claim = InvokeProtectiveClaim(coordinator, request, Time(2));

        Assert.Equal(ExecutionDispatchClaimStatus.Claimed, claim.Status);
        Assert.Empty(store.ReadReservations(portfolioId));

        ExecutionTransitionResult dispatched = coordinator.Dispatch(portfolioId, protective, claim);
        Assert.Equal(ExecutionApplyDisposition.Applied, dispatched.Disposition);
        Assert.Equal(1, dispatcher.Count);
    }

    [Fact]
    public void ProtectiveClaimRejectsQuantityBeyondKnownPosition()
    {
        using TemporaryDatabase database = new();
        OrderIntent source = CreateIntent("protective-capacity-source", "protective-capacity-source-client",
            Direction.Long, OrderRole.Entry, 2m, reduceOnly: false);
        OrderIntent protective = CreateIntent("protective-capacity-stop", "protective-capacity-stop-client",
            Direction.Short, OrderRole.Stop, 3m, reduceOnly: true, limitPrice: 90m);
        PortfolioId portfolioId = new("protective-capacity-portfolio");
        PersistIntents(database.Path, source, protective);
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        UtcTimestamp observedAt = Time(1);
        Position position = new("position-capacity", Instrument, Direction.Long,
            new Quantity(2m), observedAt, observedAt, new Price(100m));
        PortfolioPositionSnapshot snapshot = new(portfolioId, position, source.IntentId,
            PositionOrigin.Known);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null, [snapshot], new ReconciliationCheckpoint(portfolioId, 1,
                [source.IntentId], [position.PositionId], [], [],
                ReconciliationStatus.Unreconciled)));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 1).Status);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            CreateRequest(protective, snapshot, source.IntentId, 1));

        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void ProtectiveClaimRejectsManualPositionAndNonReduceOnlyIntent()
    {
        using TemporaryDatabase database = new();
        OrderIntent source = CreateIntent("protective-manual-source", "protective-manual-source-client",
            Direction.Long, OrderRole.Entry, 1m, reduceOnly: false);
        OrderIntent protective = CreateIntent("protective-manual-stop", "protective-manual-stop-client",
            Direction.Short, OrderRole.Stop, 1m, reduceOnly: true, limitPrice: 90m);
        PortfolioId portfolioId = new("protective-manual-portfolio");
        PersistIntents(database.Path, source, protective);
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        UtcTimestamp observedAt = Time(1);
        Position position = new("position-manual", Instrument, Direction.Long,
            new Quantity(1m), observedAt, observedAt, new Price(100m));
        PortfolioPositionSnapshot manual = new(portfolioId, position, null, PositionOrigin.Manual);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            CreateRequest(protective, manual, source.IntentId, 0));
        Assert.IsType<ArgumentException>(exception.InnerException);

        OrderIntent entry = CreateIntent("protective-entry", "protective-entry-client",
            Direction.Short, OrderRole.Entry, 1m, reduceOnly: false);
        TargetInvocationException nonReduceOnly = Assert.Throws<TargetInvocationException>(() =>
            CreateRequest(entry, new PortfolioPositionSnapshot(portfolioId, position,
                source.IntentId, PositionOrigin.Known), source.IntentId, 0));
        Assert.IsType<ArgumentException>(nonReduceOnly.InnerException);
    }

    [Fact]
    public void ProtectiveClaimBlocksStaleReconciliationRevisionBeforeDispatcher()
    {
        using TemporaryDatabase database = new();
        OrderIntent source = CreateIntent("protective-stale-source", "protective-stale-source-client",
            Direction.Long, OrderRole.Entry, 1m, reduceOnly: false);
        OrderIntent protective = CreateIntent("protective-stale-stop", "protective-stale-stop-client",
            Direction.Short, OrderRole.Stop, 1m, reduceOnly: true, limitPrice: 90m);
        PortfolioId portfolioId = new("protective-stale-portfolio");
        PersistIntents(database.Path, source, protective);
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        UtcTimestamp observedAt = Time(1);
        Position position = new("position-stale", Instrument, Direction.Long,
            new Quantity(1m), observedAt, observedAt, new Price(100m));
        PortfolioPositionSnapshot snapshot = new(portfolioId, position, source.IntentId,
            PositionOrigin.Known);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(null, [snapshot], new ReconciliationCheckpoint(portfolioId, 1,
                [source.IntentId], [position.PositionId], [], [],
                ReconciliationStatus.Unreconciled)));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 1).Status);

        object request = CreateRequest(protective, snapshot, source.IntentId, 0);
        RecordingDispatcher dispatcher = new();
        ExecutionCoordinator coordinator = new(store, dispatcher);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeProtectiveClaim(coordinator, request, Time(2)));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(0, dispatcher.Count);
    }

    [Fact]
    public void ProtectiveClaimIsIdempotentAcrossRestart()
    {
        using TemporaryDatabase database = new();
        OrderIntent source = CreateIntent("protective-restart-source", "protective-restart-source-client",
            Direction.Long, OrderRole.Entry, 1m, reduceOnly: false);
        OrderIntent protective = CreateIntent("protective-restart-stop", "protective-restart-stop-client",
            Direction.Short, OrderRole.TakeProfit1, 1m, reduceOnly: true, limitPrice: 110m);
        PortfolioId portfolioId = new("protective-restart-portfolio");
        PersistIntents(database.Path, source, protective);
        UtcTimestamp observedAt = Time(1);
        object request;
        using (SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
                   database.Path))
        {
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 0).Status);
            Position position = new("position-restart", Instrument, Direction.Long,
                new Quantity(1m), observedAt, observedAt, new Price(100m));
            PortfolioPositionSnapshot snapshot = new(portfolioId, position, source.IntentId,
                PositionOrigin.Known);
            Assert.Equal(PortfolioObservationResult.Recorded,
                store.RecordObservation(null, [snapshot], new ReconciliationCheckpoint(portfolioId, 1,
                    [source.IntentId], [position.PositionId], [], [],
                    ReconciliationStatus.Unreconciled)));
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 1).Status);
            request = CreateRequest(protective, snapshot, source.IntentId, 1);
            Assert.Equal(ExecutionDispatchClaimStatus.Claimed,
                InvokeProtectiveClaim(new ExecutionCoordinator(store, new RecordingDispatcher()),
                    request, Time(2)).Status);
        }

        using SqlitePortfolioOperationalStore reopened = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(14L, reopened.ReadSchemaVersionForTests());
        RecordingDispatcher dispatcher = new();
        ExecutionDispatchClaimResult duplicate = InvokeProtectiveClaim(
            new ExecutionCoordinator(reopened, dispatcher), request, Time(3));

        Assert.Equal(ExecutionDispatchClaimStatus.AlreadyClaimed, duplicate.Status);
        Assert.Equal(0, dispatcher.Count);
        Assert.Single(ReadProtectiveClaims(reopened, protective.IntentId));
    }

    [Fact]
    public void SchemaThirteenRecreatesProtectiveClaimTableBeforeUse()
    {
        using TemporaryDatabase database = new();
        using (SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
                   database.Path))
        {
            Assert.Equal(14L, store.ReadSchemaVersionForTests());
        }

        using (Microsoft.Data.Sqlite.SqliteConnection connection = new(
                   $"Data Source={database.Path};Mode=ReadWrite"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "DROP TABLE protective_dispatch_claims; PRAGMA user_version = 13;";
            command.ExecuteNonQuery();
        }

        using SqlitePortfolioOperationalStore migrated = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(14L, migrated.ReadSchemaVersionForTests());
        Assert.Empty(ReadProtectiveClaims(migrated, "missing-protective-intent"));
    }

    private static void PersistIntents(string path, params OrderIntent[] intents)
    {
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(path);
        foreach (OrderIntent intent in intents)
        {
            Assert.True(journal.Append(intent));
        }
    }

    private static object CreateRequest(OrderIntent intent, PortfolioPositionSnapshot position,
        string sourceIntentId, long revision)
    {
        Type requestType = typeof(OrderIntent).Assembly.GetType(
            "TradingBot.Core.ProtectiveExecutionRequest")
            ?? throw new InvalidOperationException(
                "The protective execution request contract is not implemented.");
        ConstructorInfo constructor = requestType.GetConstructor(
            [typeof(OrderIntent), typeof(PortfolioPositionSnapshot), typeof(string), typeof(long)])
            ?? throw new InvalidOperationException(
                "The protective execution request constructor is not implemented.");
        return constructor.Invoke([intent, position, sourceIntentId, revision]);
    }

    private static ExecutionDispatchClaimResult InvokeProtectiveClaim(
        ExecutionCoordinator coordinator, object request, UtcTimestamp claimedAt)
    {
        MethodInfo method = coordinator.GetType().GetMethod("AuthorizeProtectiveAndClaim",
            [request.GetType(), typeof(UtcTimestamp)])
            ?? throw new InvalidOperationException(
                "The protective execution claim API is not implemented.");
        return (ExecutionDispatchClaimResult)(method.Invoke(coordinator, [request, claimedAt])
            ?? throw new InvalidOperationException("The protective claim returned no result."));
    }

    private static IEnumerable<object> ReadProtectiveClaims(
        SqlitePortfolioOperationalStore store, string intentId)
    {
        MethodInfo method = typeof(SqlitePortfolioOperationalStore).GetMethod(
            "ReadProtectiveDispatchClaims", [typeof(string)])
            ?? throw new InvalidOperationException(
                "The durable protective claim read API is not implemented.");
        return ((System.Collections.IEnumerable)(method.Invoke(store, [intentId])
            ?? throw new InvalidOperationException("The protective claims returned no result.")))
            .Cast<object>();
    }

    private static OrderIntent CreateIntent(string intentId, string clientOrderId,
        Direction direction, OrderRole role, decimal quantity, bool reduceOnly,
        decimal? limitPrice = null) =>
        new(intentId, "run-protective", "setup-protective", clientOrderId, Instrument,
            direction, role, 1, new Quantity(quantity),
            limitPrice is decimal value ? new Price(value) : null, null, reduceOnly);

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private sealed class RecordingDispatcher : IPrivateOrderDispatcher
    {
        public int Count { get; private set; }

        public ExecutionObservation Submit(OrderIntent intent, ExecutionDispatchAttempt attempt)
        {
            Count++;
            return ExecutionObservation.Accepted($"protective-receipt-{Count}", intent.IntentId,
                $"protective-order-{Count}");
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-protective-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
