using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ExecutionRecoveryTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void RestartAfterClaimUsesFreshCompleteNoOrderEvidenceBeforeRetry()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-retry", "client-retry");
        PortfolioId portfolioId = new("portfolio-retry");
        PersistIntent(database.Path, intent);
        using SqlitePortfolioOperationalStore claimed = OpenClaimed(database.Path, portfolioId,
            intent);
        Assert.Equal(ExecutionApplyDisposition.Applied,
            claimed.ApplyExecutionObservation(ExecutionObservation.Unknown(
                "receipt-unknown", intent.IntentId), 0).Disposition);

        using SqlitePortfolioOperationalStore reopened = SqlitePortfolioOperationalStore.Open(
            database.Path);
        FakeReconciler reconciler = new(ExecutionRecoveryEvidence.FreshNoOrder(
            intent.IntentId, 0, historyComplete: true));
        ExecutionRecoveryCoordinator coordinator = new(reopened, reconciler);

        ExecutionRecoveryResult stale = coordinator.Reconcile(portfolioId, intent.IntentId, 1);
        Assert.Equal(ExecutionRecoveryDisposition.Blocked, stale.Disposition);
        ExecutionRecoveryResult recovered = coordinator.Reconcile(portfolioId, intent.IntentId, 0);
        Assert.True(recovered.Disposition == ExecutionRecoveryDisposition.RetryAllowed,
            recovered.Reason);
        ExecutionDispatchClaimResult retry = reopened.TryClaimDispatch(portfolioId, intent, 0,
            Time(2));
        Assert.Equal(ExecutionDispatchClaimStatus.Claimed, retry.Status);
        Assert.Equal(2, retry.State.DispatchAttempt);
    }

    [Fact]
    public void LimitedHistoryAbsenceBlocksRetryAndPreservesUnknownState()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-limited", "client-limited");
        PortfolioId portfolioId = new("portfolio-limited");
        PersistIntent(database.Path, intent);
        using SqlitePortfolioOperationalStore store = OpenClaimed(database.Path, portfolioId,
                   intent);
        Assert.Equal(ExecutionApplyDisposition.Applied,
            store.ApplyExecutionObservation(ExecutionObservation.Unknown(
                "receipt-unknown", intent.IntentId), 0).Disposition);

        FakeReconciler reconciler = new(ExecutionRecoveryEvidence.FreshNoOrder(
            intent.IntentId, 0, historyComplete: false));
        ExecutionRecoveryResult result = new ExecutionRecoveryCoordinator(store, reconciler)
            .Reconcile(portfolioId, intent.IntentId, 0);

        Assert.Equal(ExecutionRecoveryDisposition.Blocked, result.Disposition);
        Assert.Equal(ExecutionLifecycleState.Unknown, store.ReadExecutionState(intent.IntentId)!.State);
        Assert.Equal(ExecutionDispatchClaimStatus.Blocked,
            store.TryClaimDispatch(portfolioId, intent, 0, Time(2)).Status);
    }

    [Fact]
    public void RepeatedAcceptedObservationAfterCommitIsIdempotentAcrossRestart()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-after-commit", "client-after-commit");
        PortfolioId portfolioId = new("portfolio-after-commit");
        PersistIntent(database.Path, intent);
        using (SqlitePortfolioOperationalStore store = OpenClaimed(database.Path, portfolioId,
                   intent))
        {
            ExecutionObservation accepted = ExecutionObservation.Accepted(
                "receipt-accepted", intent.IntentId, "exchange-1");
            Assert.Equal(ExecutionApplyDisposition.Applied,
                store.ApplyExecutionObservation(accepted, 0).Disposition);
        }

        using SqlitePortfolioOperationalStore reopened = SqlitePortfolioOperationalStore.Open(
            database.Path);
        ExecutionRecoveryCoordinator coordinator = new(reopened, new FakeReconciler(
            ExecutionRecoveryEvidence.FreshKnownOrder(intent.IntentId, 0,
                ExecutionObservation.Accepted("receipt-accepted", intent.IntentId,
                    "exchange-1", isReconciliation: true))));

        ExecutionRecoveryResult result = coordinator.Reconcile(portfolioId, intent.IntentId, 0);

        Assert.True(result.Disposition == ExecutionRecoveryDisposition.Replayed, result.Reason);
        Assert.Equal(ExecutionLifecycleState.Accepted,
            reopened.ReadExecutionState(intent.IntentId)!.State);
        Assert.Single(reopened.ReadExecutionReceipts(intent.IntentId));
    }

    [Fact]
    public void PartialFillAndCancelRaceReplaysInOrderWithoutRetry()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-partial-cancel", "client-partial-cancel");
        PortfolioId portfolioId = new("portfolio-partial-cancel");
        PersistIntent(database.Path, intent);
        using SqlitePortfolioOperationalStore store = OpenClaimed(database.Path, portfolioId,
                   intent);
        Assert.Equal(ExecutionApplyDisposition.Applied,
            store.ApplyExecutionObservation(ExecutionObservation.Accepted(
                "receipt-accepted", intent.IntentId, "exchange-1"), 0).Disposition);
        Assert.Equal(ExecutionApplyDisposition.Applied,
            store.ApplyExecutionObservation(ExecutionObservation.PartialFill(
                "receipt-partial", intent.IntentId, "exchange-1", 0.4m), 0).Disposition);

        ExecutionRecoveryEvidence evidence = ExecutionRecoveryEvidence.FreshKnownOrder(
            intent.IntentId, 0,
            ExecutionObservation.Accepted("receipt-accepted", intent.IntentId, "exchange-1",
                isReconciliation: true),
            ExecutionObservation.PartialFill("receipt-partial", intent.IntentId, "exchange-1",
                0.4m, isReconciliation: true),
            ExecutionObservation.Canceled("receipt-canceled", intent.IntentId, "exchange-1",
                isReconciliation: true));
        ExecutionRecoveryResult result = new ExecutionRecoveryCoordinator(store,
            new FakeReconciler(evidence)).Reconcile(portfolioId, intent.IntentId, 0);

        Assert.True(result.Disposition == ExecutionRecoveryDisposition.Replayed, result.Reason);
        Assert.Equal(ExecutionLifecycleState.Canceled,
            store.ReadExecutionState(intent.IntentId)!.State);
        Assert.Equal(3, store.ReadExecutionReceipts(intent.IntentId).Count);
        Assert.Equal(ExecutionDispatchClaimStatus.Blocked,
            store.TryClaimDispatch(portfolioId, intent, 0, Time(2)).Status);
    }

    [Fact]
    public void FailureInjectionAfterExternalResultLeavesClaimForReconciliation()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-failure", "client-failure");
        PortfolioId portfolioId = new("portfolio-failure");
        PersistIntent(database.Path, intent);
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        ThrowOnce injector = new(ExecutionFailurePoint.AfterExternalResult);
        ExecutionCoordinator dispatcher = new(store,
            new AcceptedDispatcher(), injector);

        ExecutionDispatchClaimResult claim = store.TryClaimDispatch(portfolioId, intent, 0,
            Time(1));
        Assert.Throws<InjectedFailure>(() => dispatcher.Dispatch(portfolioId, intent,
            claim));
        Assert.Equal(ExecutionLifecycleState.DispatchClaimed,
            store.ReadExecutionState(intent.IntentId)!.State);

        ExecutionRecoveryResult recovered = new ExecutionRecoveryCoordinator(store,
            new FakeReconciler(ExecutionRecoveryEvidence.FreshKnownOrder(intent.IntentId, 0,
                ExecutionObservation.Accepted("receipt-accepted", intent.IntentId, "exchange-1",
                    isReconciliation: true)))).Reconcile(portfolioId, intent.IntentId, 0);
        Assert.True(recovered.Disposition == ExecutionRecoveryDisposition.Replayed,
            recovered.Reason);
        Assert.Equal(ExecutionLifecycleState.Accepted,
            store.ReadExecutionState(intent.IntentId)!.State);
    }

    [Fact]
    public void ManualAndConflictingEvidenceRemainBlocked()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-manual", "client-manual");
        PortfolioId portfolioId = new("portfolio-manual");
        PersistIntent(database.Path, intent);
        using SqlitePortfolioOperationalStore store = OpenClaimed(database.Path, portfolioId,
            intent);
        Assert.Equal(ExecutionApplyDisposition.Applied,
            store.ApplyExecutionObservation(ExecutionObservation.Unknown(
                "receipt-unknown", intent.IntentId), 0).Disposition);

        FakeReconciler reconciler = new(new ExecutionRecoveryEvidence(intent.IntentId, 0,
            ExecutionRecoveryEvidenceStatus.Manual, true, true, false, true, true,
            new ImmutableValueList<ExecutionObservation>([])));
        ExecutionRecoveryCoordinator coordinator = new(store, reconciler);
        Assert.Equal(ExecutionRecoveryDisposition.Blocked,
            coordinator.Reconcile(portfolioId, intent.IntentId, 0).Disposition);

        reconciler.Evidence = new ExecutionRecoveryEvidence(intent.IntentId, 0,
            ExecutionRecoveryEvidenceStatus.Conflicting, true, true, false, true, true,
            new ImmutableValueList<ExecutionObservation>([]));
        Assert.Equal(ExecutionRecoveryDisposition.Blocked,
            coordinator.Reconcile(portfolioId, intent.IntentId, 0).Disposition);
    }

    [Fact]
    public void RestartRecoveryMatchesDurableFillAndPositionFacts()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-facts", "client-facts");
        PortfolioId portfolioId = new("portfolio-facts");
        PersistIntent(database.Path, intent);
        using SqlitePortfolioOperationalStore store = OpenClaimed(database.Path, portfolioId,
            intent);
        Assert.Equal(ExecutionApplyDisposition.Applied,
            store.ApplyExecutionObservation(ExecutionObservation.Unknown(
                "receipt-unknown", intent.IntentId), 0).Disposition);

        UtcTimestamp observedAt = Time(3);
        Fill fill = new("fill-facts", intent.IntentId, Instrument, new Quantity(0.4m),
            new Price(100m), observedAt, observedAt, observedAt, 1);
        Position position = new("position-facts", Instrument, Direction.Long,
            new Quantity(0.4m), observedAt, observedAt, new Price(100m));
        PortfolioPositionSnapshot snapshot = new(portfolioId, position, intent.IntentId,
            PositionOrigin.Known);
        ReconciliationCheckpoint next = new(portfolioId, 1, [intent.IntentId],
            [position.PositionId], [], [], ReconciliationStatus.Unreconciled);
        Assert.Equal(PortfolioObservationResult.Recorded,
            store.RecordObservation(fill, [snapshot], next));
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 1).Status);

        ExecutionRecoveryEvidence evidence = new(intent.IntentId, 1,
            ExecutionRecoveryEvidenceStatus.Fresh, true, true, true, true, true,
            new ImmutableValueList<ExecutionObservation>([
                ExecutionObservation.Accepted("receipt-accepted", intent.IntentId, "exchange-1",
                    isReconciliation: true),
                ExecutionObservation.PartialFill("receipt-partial", intent.IntentId, "exchange-1",
                    0.4m, isReconciliation: true)
            ]),
            new ImmutableValueList<string>([fill.FillId]),
            new ImmutableValueList<string>([position.PositionId]),
            new ImmutableValueList<string>([]));
        ExecutionRecoveryResult recovered = new ExecutionRecoveryCoordinator(store,
            new FakeReconciler(evidence)).Reconcile(portfolioId, intent.IntentId, 1);

        Assert.True(recovered.Disposition == ExecutionRecoveryDisposition.Replayed,
            recovered.Reason);
        Assert.Equal(ExecutionLifecycleState.PartiallyFilled,
            store.ReadExecutionState(intent.IntentId)!.State);
        Assert.Equal(fill, store.FindFill(fill.FillId));
        Assert.Single(store.ReadPositionSnapshots(portfolioId));
        Assert.Empty(store.ReadReservations(portfolioId));
    }

    private static void PersistIntent(string path, OrderIntent intent)
    {
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(path);
        Assert.True(journal.Append(intent));
    }

    private static SqlitePortfolioOperationalStore OpenClaimed(string path,
        PortfolioId portfolioId, OrderIntent intent)
    {
        SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        Assert.Equal(ExecutionDispatchClaimStatus.Claimed,
            store.TryClaimDispatch(portfolioId, intent, 0, Time(1)).Status);
        return store;
    }

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) =>
        new(intentId, "run-1", "setup-1", clientOrderId, Instrument, Direction.Long,
            OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private sealed class FakeReconciler : IPrivateOrderReconciler
    {
        public ExecutionRecoveryEvidence Evidence { get; set; }

        public FakeReconciler(ExecutionRecoveryEvidence evidence) => Evidence = evidence;

        public ExecutionRecoveryEvidence Reconcile(OrderIntent intent,
            PortfolioId portfolioId, ExecutionStateSnapshot state,
            ReconciliationCheckpoint checkpoint) => Evidence;
    }

    private sealed class AcceptedDispatcher : IPrivateOrderDispatcher
    {
        public ExecutionObservation Submit(OrderIntent intent, ExecutionDispatchAttempt attempt) =>
            ExecutionObservation.Accepted("receipt-accepted", intent.IntentId, "exchange-1");
    }

    private sealed class ThrowOnce : IExecutionFailureInjector
    {
        private readonly ExecutionFailurePoint _point;
        private bool _thrown;

        public ThrowOnce(ExecutionFailurePoint point) => _point = point;

        public void ThrowIf(ExecutionFailurePoint point)
        {
            if (!_thrown && point == _point)
            {
                _thrown = true;
                throw new InjectedFailure();
            }
        }
    }

    private sealed class InjectedFailure : Exception
    {
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-recovery-{Guid.NewGuid():N}.sqlite");

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
