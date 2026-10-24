using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteExecutionStateTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void DispatchClaimIsAtomicIdempotentAndDurableAcrossRestart()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-claim", "client-claim");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        PortfolioId portfolioId = new("portfolio-claim");
        using (SqlitePortfolioOperationalStore store = OpenStore(database.Path))
        {
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 0).Status);

            ExecutionDispatchClaimResult first = store.TryClaimDispatch(intent, 0, Time(1));
            ExecutionDispatchClaimResult duplicate = store.TryClaimDispatch(intent, 0, Time(2));
            Assert.Equal(ExecutionDispatchClaimStatus.Claimed, first.Status);
            Assert.Equal(ExecutionDispatchClaimStatus.AlreadyClaimed, duplicate.Status);
            Assert.Equal(first.State, duplicate.State);
            Assert.Equal(1, first.State.DispatchAttempt);
            Assert.Single(store.ReadExecutionAttempts(intent.IntentId));
        }

        using SqlitePortfolioOperationalStore reopened = OpenStore(database.Path);
        Assert.Equal(ExecutionLifecycleState.DispatchClaimed,
            reopened.ReadExecutionState(intent.IntentId)!.State);
        Assert.Single(reopened.ReadExecutionAttempts(intent.IntentId));
    }

    [Fact]
    public void ApplyingObservationChecksRevisionPersistsReceiptAndIsIdempotent()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = PrepareClaim(database.Path, "intent-observe", "client-observe");
        using SqlitePortfolioOperationalStore store = OpenStore(database.Path);
        ExecutionObservation accepted = ExecutionObservation.Accepted("receipt-accepted",
            intent.IntentId, "exchange-1");

        ExecutionTransitionResult applied = store.ApplyExecutionObservation(accepted, 0);
        ExecutionTransitionResult duplicate = store.ApplyExecutionObservation(accepted, 0);
        Assert.Equal(ExecutionApplyDisposition.Applied, applied.Disposition);
        Assert.Equal(ExecutionApplyDisposition.AlreadyApplied, duplicate.Disposition);
        Assert.Equal(ExecutionLifecycleState.Accepted,
            store.ReadExecutionState(intent.IntentId)!.State);
        Assert.Single(store.ReadExecutionReceipts(intent.IntentId));

        Assert.Throws<InvalidOperationException>(() =>
            store.ApplyExecutionObservation(
                ExecutionObservation.PartialFill("receipt-stale", intent.IntentId, "exchange-1", 0.2m),
                1));
    }

    [Fact]
    public async Task ConcurrentClaimsProduceOneDispatchRight()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-concurrent", "client-concurrent");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        PortfolioId portfolioId = new("portfolio-concurrent");
        using SqlitePortfolioOperationalStore setup = OpenStore(database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            setup.AcknowledgeReconciliation(portfolioId, 0).Status);

        ExecutionDispatchClaimResult[] results = await Task.WhenAll(
            Task.Run(() =>
            {
                using SqlitePortfolioOperationalStore store = OpenStore(database.Path);
                return store.TryClaimDispatch(portfolioId, intent, 0, Time(1));
            }),
            Task.Run(() =>
            {
                using SqlitePortfolioOperationalStore store = OpenStore(database.Path);
                return store.TryClaimDispatch(portfolioId, intent, 0, Time(2));
            }));

        Assert.Equal(1, results.Count(result =>
            result.Status == ExecutionDispatchClaimStatus.Claimed));
        Assert.Equal(1, results.Count(result =>
            result.Status == ExecutionDispatchClaimStatus.AlreadyClaimed));
        using SqlitePortfolioOperationalStore reopened = OpenStore(database.Path);
        Assert.Single(reopened.ReadExecutionAttempts(intent.IntentId));
    }

    [Fact]
    public void UnknownOutcomeIsDurableAndBlocksNonReconciliationObservation()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = PrepareClaim(database.Path, "intent-unknown", "client-unknown");
        using SqlitePortfolioOperationalStore store = OpenStore(database.Path);

        ExecutionTransitionResult unknown = store.ApplyExecutionObservation(
            ExecutionObservation.Unknown("receipt-unknown", intent.IntentId), 0);
        Assert.Equal(ExecutionApplyDisposition.Applied, unknown.Disposition);
        Assert.True(unknown.State.RequiresReconciliation);

        ExecutionTransitionResult blocked = store.ApplyExecutionObservation(
            ExecutionObservation.Accepted("receipt-late", intent.IntentId, "exchange-2"), 0);
        Assert.Equal(ExecutionApplyDisposition.Blocked, blocked.Disposition);
        Assert.Equal(ExecutionLifecycleState.Unknown,
            store.ReadExecutionState(intent.IntentId)!.State);
    }

    [Fact]
    public void ConflictingReceiptIsPersistedAsFailClosedState()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = PrepareClaim(database.Path, "intent-conflict", "client-conflict");
        using SqlitePortfolioOperationalStore store = OpenStore(database.Path);
        Assert.Equal(ExecutionApplyDisposition.Applied,
            store.ApplyExecutionObservation(
                ExecutionObservation.Accepted("receipt-same", intent.IntentId, "exchange-1"), 0)
                .Disposition);

        ExecutionTransitionResult conflict = store.ApplyExecutionObservation(
            ExecutionObservation.Rejected("receipt-same", intent.IntentId, "exchange-1", "different"),
            0);
        Assert.Equal(ExecutionApplyDisposition.Conflict, conflict.Disposition);
        Assert.Equal(ExecutionLifecycleState.Conflict,
            store.ReadExecutionState(intent.IntentId)!.State);
        Assert.True(store.ReadExecutionState(intent.IntentId)!.RequiresReconciliation);
    }

    [Fact]
    public void MigrationFromSchemaElevenCreatesExecutionTablesWithoutChangingIntent()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-migration", "client-migration");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        using SqlitePortfolioOperationalStore store = OpenStore(database.Path);
        Assert.Equal(intent, SqliteOrderIntentJournal.Open(database.Path).Find(intent.IntentId));
        Assert.Equal(14L, store.ReadSchemaVersionForTests());
    }

    private static SqlitePortfolioOperationalStore OpenStore(string path) =>
        SqlitePortfolioOperationalStore.Open(path);

    private static OrderIntent PrepareClaim(string path, string intentId, string clientOrderId)
    {
        OrderIntent intent = CreateIntent(intentId, clientOrderId);
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(path);
        Assert.True(journal.Append(intent));
        using SqlitePortfolioOperationalStore store = OpenStore(path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(new PortfolioId($"portfolio-{intentId}"), 0).Status);
        Assert.Equal(ExecutionDispatchClaimStatus.Claimed,
            store.TryClaimDispatch(intent, 0, Time(1)).Status);
        return intent;
    }

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) =>
        new(intentId, "run-1", "setup-1", clientOrderId, Instrument, Direction.Long,
            OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-execution-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
                File.Delete(Path + "-wal");
                File.Delete(Path + "-shm");
            }
            catch (IOException)
            {
            }
        }
    }
}
