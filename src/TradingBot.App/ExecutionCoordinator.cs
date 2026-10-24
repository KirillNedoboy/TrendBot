using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

public enum ExecutionFailurePoint
{
    BeforeDurableClaim,
    AfterDurableClaim,
    AfterExternalResult,
    AfterObservationCommit
}

public interface IExecutionFailureInjector
{
    void ThrowIf(ExecutionFailurePoint point);
}

public interface IPrivateOrderDispatcher
{
    ExecutionObservation Submit(OrderIntent intent, ExecutionDispatchAttempt attempt);
}

public interface IPrivateOrderReconciler
{
    ExecutionRecoveryEvidence Reconcile(OrderIntent intent, PortfolioId portfolioId,
        ExecutionStateSnapshot state, ReconciliationCheckpoint checkpoint);
}

public sealed class ExecutionCoordinator
{
    private readonly SqlitePortfolioOperationalStore _store;
    private readonly IPrivateOrderDispatcher _dispatcher;
    private readonly IExecutionFailureInjector? _failureInjector;

    public ExecutionCoordinator(SqlitePortfolioOperationalStore store,
        IPrivateOrderDispatcher dispatcher)
        : this(store, dispatcher, failureInjector: null)
    {
    }

    public ExecutionCoordinator(SqlitePortfolioOperationalStore store,
        IPrivateOrderDispatcher dispatcher, IExecutionFailureInjector? failureInjector)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _store = store;
        _dispatcher = dispatcher;
        _failureInjector = failureInjector;
    }

    /// <summary>
    /// Revalidates approved risk, durably saves the intent and reservation, and
    /// atomically claims the right to perform one adapter dispatch.
    /// </summary>
    public ExecutionDispatchClaimResult AuthorizeAndClaim(PortfolioId portfolioId,
        OrderIntent intent, PortfolioReservation reservation, RiskEvaluationResult riskEvaluation,
        long expectedReconciliationRevision, IRiskMathCalculator riskMathCalculator,
        UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(riskEvaluation);
        ArgumentNullException.ThrowIfNull(riskMathCalculator);
        ArgumentNullException.ThrowIfNull(claimedAt);
        if (!riskEvaluation.IsApproved)
        {
            throw new InvalidOperationException(
                "Only an explicitly approved risk evaluation can authorize dispatch.");
        }

        if (riskEvaluation.Input.PortfolioId != portfolioId)
        {
            throw new InvalidOperationException(
                "The risk evaluation portfolio does not match the dispatch portfolio.");
        }

        PortfolioReservationResult reservationResult = _store.TryReserve(intent, reservation,
            riskEvaluation, expectedReconciliationRevision, riskMathCalculator);
        if (reservationResult == PortfolioReservationResult.InsufficientCapacity)
        {
            throw new InvalidOperationException(
                "The approved order cannot acquire the required portfolio reservation.");
        }

        _failureInjector?.ThrowIf(ExecutionFailurePoint.BeforeDurableClaim);
        ExecutionDispatchClaimResult claim = _store.TryClaimDispatch(portfolioId, intent,
            expectedReconciliationRevision, claimedAt);
        _failureInjector?.ThrowIf(ExecutionFailurePoint.AfterDurableClaim);
        return claim;
    }

    public ExecutionTransitionResult Dispatch(PortfolioId portfolioId, OrderIntent intent,
        ExecutionDispatchClaimResult claim)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(claim);
        if (!StringComparer.Ordinal.Equals(claim.State.IntentId, intent.IntentId)
            || !StringComparer.Ordinal.Equals(claim.State.ClientOrderId, intent.ClientOrderId))
        {
            throw new InvalidOperationException(
                "The dispatch claim does not belong to the supplied order intent.");
        }

        if (claim.Status != ExecutionDispatchClaimStatus.Claimed)
        {
            ExecutionApplyDisposition disposition = claim.Status ==
                ExecutionDispatchClaimStatus.Conflict
                ? ExecutionApplyDisposition.Conflict
                : ExecutionApplyDisposition.Blocked;
            return new ExecutionTransitionResult(disposition, claim.State);
        }

        ExecutionDispatchAttempt attempt = claim.Attempt
            ?? throw new InvalidDataException("A claimed dispatch must include its attempt.");
        ExecutionObservation observation;
        try
        {
            observation = _dispatcher.Submit(intent, attempt)
                ?? throw new InvalidDataException("The adapter returned no observation.");
        }
        catch (TimeoutException)
        {
            observation = ExecutionObservation.Timeout($"timeout:{attempt.AttemptId}",
                intent.IntentId);
        }
        catch (Exception)
        {
            // A transport or adapter exception does not authorize a blind retry.
            observation = ExecutionObservation.Unknown($"unknown:{attempt.AttemptId}",
                intent.IntentId);
        }

        _failureInjector?.ThrowIf(ExecutionFailurePoint.AfterExternalResult);
        ExecutionTransitionResult result = _store.ApplyExecutionObservation(portfolioId,
            observation, claim.State.ExpectedReconciliationRevision);
        _failureInjector?.ThrowIf(ExecutionFailurePoint.AfterObservationCommit);
        return result;
    }

    /// <summary>
    /// Authorizes a protective dispatch from a known position and obtains one
    /// durable claim without running entry risk or acquiring entry capacity.
    /// </summary>
    public ExecutionDispatchClaimResult AuthorizeProtectiveAndClaim(
        ProtectiveExecutionRequest request, UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(claimedAt);
        ProtectiveExecutionGuard.Validate(request);
        return _store.TryClaimProtectiveDispatch(request, claimedAt);
    }

    /// <summary>Claims and dispatches one protected reduce-only intent.</summary>
    public ExecutionTransitionResult DispatchProtective(ProtectiveExecutionRequest request,
        UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionDispatchClaimResult claim = AuthorizeProtectiveAndClaim(request, claimedAt);
        return Dispatch(request.Position.PortfolioId, request.Intent, claim);
    }
}

public sealed class ExecutionRecoveryCoordinator
{
    private readonly SqlitePortfolioOperationalStore _store;
    private readonly IPrivateOrderReconciler _reconciler;
    private readonly IExecutionFailureInjector? _failureInjector;

    public ExecutionRecoveryCoordinator(SqlitePortfolioOperationalStore store,
        IPrivateOrderReconciler reconciler, IExecutionFailureInjector? failureInjector = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(reconciler);
        _store = store;
        _reconciler = reconciler;
        _failureInjector = failureInjector;
    }

    public ExecutionRecoveryResult Reconcile(PortfolioId portfolioId, string intentId,
        long expectedReconciliationRevision)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        if (expectedReconciliationRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedReconciliationRevision),
                expectedReconciliationRevision, "A reconciliation revision cannot be negative.");
        }

        OrderIntent intent = _store.FindIntent(intentId)
            ?? throw new InvalidOperationException("The recovery intent is not durable.");
        ExecutionStateSnapshot state = _store.ReadExecutionState(intentId)
            ?? throw new InvalidOperationException("The recovery execution state is not durable.");
        ReconciliationCheckpoint checkpoint = _store.ReadCheckpoint(portfolioId);
        if (checkpoint.Revision != expectedReconciliationRevision
            || checkpoint.Status != ReconciliationStatus.Acknowledged)
        {
            return Blocked(state, "The requested reconciliation revision is stale or unacknowledged.");
        }

        ExecutionRecoveryEvidence evidence = _reconciler.Reconcile(intent, portfolioId, state,
            checkpoint);
        if (!StringComparer.Ordinal.Equals(evidence.IntentId, intentId)
            || evidence.ReconciliationRevision != expectedReconciliationRevision)
        {
            return Blocked(state, "The adapter evidence identity or revision is conflicting.");
        }

        if (evidence.Status != ExecutionRecoveryEvidenceStatus.Fresh
            || !evidence.IsComplete
            || !evidence.OrderHistoryComplete
            || !evidence.PositionsKnown
            || !evidence.ReservationsKnown)
        {
            return Blocked(state,
                "Complete fresh order, position, and reservation evidence is required.");
        }

        if (!MatchesLocalOperationalFacts(portfolioId, intentId, evidence))
        {
            return Blocked(state,
                "Recovery evidence does not match durable fills, positions, or reservations.");
        }

        foreach (ExecutionObservation observation in evidence.Observations)
        {
            if (!observation.IsReconciliation)
            {
                return Blocked(state,
                    "Recovery observations must be explicitly marked as reconciliation.");
            }
        }

        if (evidence.CanAuthorizeRetry)
        {
            try
            {
                ExecutionStateSnapshot recovered = _store.RecoverExecutionForRetry(portfolioId,
                    evidence);
                _failureInjector?.ThrowIf(ExecutionFailurePoint.AfterObservationCommit);
                return new ExecutionRecoveryResult(ExecutionRecoveryDisposition.RetryAllowed,
                    recovered, "Fresh complete no-order evidence explicitly authorized retry.");
            }
            catch (InvalidOperationException exception)
            {
                return Blocked(state, exception.Message);
            }
        }

        if (evidence.Observations.Count == 0)
        {
            return Blocked(state, "Fresh evidence did not prove a retryable no-order state.");
        }

        try
        {
            _ = _store.BeginExecutionReconciliation(portfolioId, intentId,
                expectedReconciliationRevision);
            ExecutionStateSnapshot latest = state;
            foreach (ExecutionObservation observation in evidence.Observations)
            {
                ExecutionTransitionResult transition = _store.ApplyExecutionObservation(
                    portfolioId, observation, expectedReconciliationRevision);
                latest = transition.State;
                if (transition.Disposition is ExecutionApplyDisposition.Conflict
                    or ExecutionApplyDisposition.Blocked)
                {
                    return Blocked(latest,
                        "Reconciliation evidence conflicts with the durable execution state.");
                }
            }

            _failureInjector?.ThrowIf(ExecutionFailurePoint.AfterObservationCommit);
            return new ExecutionRecoveryResult(ExecutionRecoveryDisposition.Replayed, latest,
                "Fresh reconciliation observations were applied idempotently.");
        }
        catch (InvalidOperationException exception)
        {
            return Blocked(state, exception.Message);
        }
    }

    private static ExecutionRecoveryResult Blocked(ExecutionStateSnapshot state, string reason) =>
        new(ExecutionRecoveryDisposition.Blocked, state, reason);

    private bool MatchesLocalOperationalFacts(PortfolioId portfolioId, string intentId,
        ExecutionRecoveryEvidence evidence)
    {
        HashSet<string> actualFillIds = _store.ReadFills(intentId)
            .Select(static fill => fill.FillId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> actualPositionIds = _store.ReadPositionSnapshots(portfolioId)
            .Select(static snapshot => snapshot.Position.PositionId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> actualReservationIds = _store.ReadReservations(portfolioId)
            .Where(reservation => StringComparer.Ordinal.Equals(reservation.IntentId, intentId))
            .Select(static reservation => reservation.ReservationId)
            .ToHashSet(StringComparer.Ordinal);
        return actualFillIds.SetEquals(evidence.FillIds)
            && actualPositionIds.SetEquals(evidence.PositionIds)
            && actualReservationIds.SetEquals(evidence.ReservationIds);
    }
}
