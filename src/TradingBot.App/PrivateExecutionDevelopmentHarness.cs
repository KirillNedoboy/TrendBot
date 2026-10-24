using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>
/// Programmatic development-only execution surface. The harness owns sealed
/// scripted dispatcher and reconciler seams; it accepts no transport, endpoint,
/// signer, or credential and therefore cannot reach private activation.
/// </summary>
public sealed class PrivateExecutionDevelopmentHarness
{
    private readonly SqlitePortfolioOperationalStore _store;
    private readonly PortfolioId _portfolioId;
    private readonly ScriptedPrivateOrderDispatcher _dispatcher;
    private readonly ExecutionCoordinator _coordinator;
    private readonly ExecutionRecoveryCoordinator _recoveryCoordinator;

    public PrivateExecutionDevelopmentHarness(SqlitePortfolioOperationalStore store,
        PortfolioId portfolioId, IReadOnlyList<ExecutionObservation> observations,
        IReadOnlyList<ExecutionRecoveryEvidence> reconciliationEvidence)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(reconciliationEvidence);
        _store = store;
        _portfolioId = portfolioId;
        _dispatcher = new ScriptedPrivateOrderDispatcher(observations);
        _coordinator = new ExecutionCoordinator(store, _dispatcher);
        _recoveryCoordinator = new ExecutionRecoveryCoordinator(store,
            new ScriptedPrivateOrderReconciler(reconciliationEvidence));
    }

    /// <summary>Gets the number of scripted external calls made by this harness.</summary>
    public int DispatchCount => _dispatcher.DispatchCount;

    /// <summary>
    /// Claims a durable lifecycle attempt, obtains the durable single-use
    /// development permission, and only then invokes the sealed scripted adapter.
    /// </summary>
    public ExecutionTransitionResult Dispatch(OrderIntent intent,
        long expectedReconciliationRevision, UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(claimedAt);
        ExecutionDispatchClaimResult claim = _store.TryClaimDispatch(_portfolioId, intent,
            expectedReconciliationRevision, claimedAt);
        if (claim.Status != ExecutionDispatchClaimStatus.Claimed)
        {
            return _coordinator.Dispatch(_portfolioId, intent, claim);
        }

        ExecutionDispatchAttempt attempt = claim.Attempt
            ?? throw new InvalidDataException("A claimed development dispatch has no attempt.");
        if (!_store.TryClaimDevelopmentDispatch(attempt))
        {
            return new ExecutionTransitionResult(ExecutionApplyDisposition.Blocked,
                claim.State);
        }

        return _coordinator.Dispatch(_portfolioId, intent, claim);
    }

    /// <summary>
    /// Dispatches only when the caller also supplies an active reservation that
    /// is already durable and bound to this portfolio and intent.
    /// </summary>
    public ExecutionTransitionResult Dispatch(OrderIntent intent, PortfolioReservation reservation,
        long expectedReconciliationRevision, UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(reservation);
        if (!StringComparer.Ordinal.Equals(reservation.IntentId, intent.IntentId)
            || reservation.PortfolioId != _portfolioId
            || !_store.ReadReservations(_portfolioId).Any(saved => saved.Equals(reservation)))
        {
            throw new InvalidOperationException(
                "A development dispatch requires an active durable reservation for the intent.");
        }

        return Dispatch(intent, expectedReconciliationRevision, claimedAt);
    }

    /// <summary>Reconciles only against the constructor-supplied synthetic evidence.</summary>
    public ExecutionRecoveryResult Reconcile(string intentId,
        long expectedReconciliationRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        return _recoveryCoordinator.Reconcile(_portfolioId, intentId,
            expectedReconciliationRevision);
    }

    private sealed class ScriptedPrivateOrderDispatcher : IPrivateOrderDispatcher
    {
        private readonly Queue<ExecutionObservation> _observations;

        public ScriptedPrivateOrderDispatcher(IReadOnlyList<ExecutionObservation> observations)
        {
            _observations = new Queue<ExecutionObservation>(observations);
        }

        public int DispatchCount { get; private set; }

        public ExecutionObservation Submit(OrderIntent intent, ExecutionDispatchAttempt attempt)
        {
            ArgumentNullException.ThrowIfNull(intent);
            ArgumentNullException.ThrowIfNull(attempt);
            if (!_observations.TryDequeue(out ExecutionObservation? observation))
            {
                throw new InvalidOperationException(
                    "The development harness has no scripted private observation.");
            }

            if (!StringComparer.Ordinal.Equals(observation.IntentId, intent.IntentId))
            {
                throw new InvalidOperationException(
                    "The scripted private observation targets a different intent.");
            }

            DispatchCount++;
            return observation;
        }
    }

    private sealed class ScriptedPrivateOrderReconciler : IPrivateOrderReconciler
    {
        private readonly Queue<ExecutionRecoveryEvidence> _evidence;

        public ScriptedPrivateOrderReconciler(
            IReadOnlyList<ExecutionRecoveryEvidence> reconciliationEvidence)
        {
            _evidence = new Queue<ExecutionRecoveryEvidence>(reconciliationEvidence);
        }

        public ExecutionRecoveryEvidence Reconcile(OrderIntent intent, PortfolioId portfolioId,
            ExecutionStateSnapshot state, ReconciliationCheckpoint checkpoint)
        {
            ArgumentNullException.ThrowIfNull(intent);
            ArgumentNullException.ThrowIfNull(portfolioId);
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(checkpoint);
            if (!_evidence.TryDequeue(out ExecutionRecoveryEvidence? evidence))
            {
                return new ExecutionRecoveryEvidence(intent.IntentId, checkpoint.Revision,
                    ExecutionRecoveryEvidenceStatus.Unknown, false, false, false, false, false,
                    new ImmutableValueList<ExecutionObservation>(
                        Array.Empty<ExecutionObservation>()));
            }

            if (!StringComparer.Ordinal.Equals(evidence.IntentId, intent.IntentId)
                || evidence.ReconciliationRevision != checkpoint.Revision)
            {
                throw new InvalidOperationException(
                    "The scripted reconciliation evidence targets a different intent or revision.");
            }

            return evidence;
        }
    }
}
