using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Runtime-only asynchronous transport seam for the exchange-test lane.</summary>
public interface IExchangeTestTransport
{
    ValueTask<ExchangeTestTransportResult> SubmitAsync(OrderIntent intent,
        ExecutionDispatchAttempt attempt, CancellationToken cancellationToken);

    ValueTask<ExecutionRecoveryEvidence> ReconcileAsync(OrderIntent intent,
        PortfolioId portfolioId, ExecutionStateSnapshot state,
        ReconciliationCheckpoint checkpoint, CancellationToken cancellationToken);
}

/// <summary>
/// Normalized result returned by a runtime exchange-test transport. The
/// transport must prove reduce-only protection for a partial fill; this type
/// deliberately carries no raw exchange frame.
/// </summary>
public sealed record ExchangeTestTransportResult
{
    public ExchangeTestTransportResult(ExecutionObservation observation,
        IReadOnlyList<ExchangeTestEventReceipt> privateEvents,
        bool reduceOnlyProtectionConfirmed, Fill? fill = null,
        IReadOnlyList<PortfolioPositionSnapshot>? positionSnapshots = null,
        ReconciliationCheckpoint? checkpoint = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(privateEvents);
        if (observation.Type == ExecutionObservationType.PartialFill
            && !reduceOnlyProtectionConfirmed)
        {
            // The executor converts this into a durable unknown state. The
            // constructor remains usable for the fixture that proves that
            // fail-closed transition.
        }

        Observation = observation;
        PrivateEvents = privateEvents.ToArray();
        ReduceOnlyProtectionConfirmed = reduceOnlyProtectionConfirmed;
        Fill = fill;
        PositionSnapshots = (positionSnapshots ?? Array.Empty<PortfolioPositionSnapshot>()).ToArray();
        Checkpoint = checkpoint;
    }

    public ExecutionObservation Observation { get; }
    public IReadOnlyList<ExchangeTestEventReceipt> PrivateEvents { get; }
    public bool ReduceOnlyProtectionConfirmed { get; }
    public Fill? Fill { get; }
    public IReadOnlyList<PortfolioPositionSnapshot> PositionSnapshots { get; }
    public ReconciliationCheckpoint? Checkpoint { get; }
}

/// <summary>Explicit entry safety state owned by the private-stream monitor.</summary>
public sealed record ExchangeTestSafetyState
{
    public ExchangeTestSafetyState(bool testEnvironmentConfirmed,
        bool privateStreamConnected, bool privateStreamFresh,
        bool hasUnknownPositions, bool hasManualPositions,
        bool reconciliationRequired = false)
    {
        TestEnvironmentConfirmed = testEnvironmentConfirmed;
        PrivateStreamConnected = privateStreamConnected;
        PrivateStreamFresh = privateStreamFresh;
        HasUnknownPositions = hasUnknownPositions;
        HasManualPositions = hasManualPositions;
        ReconciliationRequired = reconciliationRequired;
    }

    public bool TestEnvironmentConfirmed { get; }
    public bool PrivateStreamConnected { get; }
    public bool PrivateStreamFresh { get; }
    public bool HasUnknownPositions { get; }
    public bool HasManualPositions { get; }
    public bool ReconciliationRequired { get; }

    public bool EntryAllowed => TestEnvironmentConfirmed
        && PrivateStreamConnected
        && PrivateStreamFresh
        && !HasUnknownPositions
        && !HasManualPositions
        && !ReconciliationRequired;

    public static ExchangeTestSafetyState Ready { get; } = new(true, true, true,
        false, false);

    public static ExchangeTestSafetyState StalePrivateStream { get; } = new(true, true,
        false, false, false, true);

    public static ExchangeTestSafetyState UnknownOrManualPosition { get; } = new(true, true,
        true, true, false, true);
}

public sealed record ExchangeTestExecutionResult
{
    public ExchangeTestExecutionResult(ExecutionTransitionResult transition,
        bool protectionBlocked = false)
    {
        ArgumentNullException.ThrowIfNull(transition);
        Transition = transition;
        ProtectionBlocked = protectionBlocked;
    }

    public ExecutionTransitionResult Transition { get; }
    public bool ProtectionBlocked { get; }
}

/// <summary>
/// Asynchronous exchange-test execution boundary. Durable reservation and
/// dispatch claims are acquired before the injected transport is called; a
/// timeout, unknown response, stale stream, manual position, or protection
/// failure never authorizes an implicit retry or a production fallback.
/// </summary>
public sealed class ExchangeTestExecutor
{
    private readonly ExchangeTestConfiguration _configuration;
    private readonly SqlitePortfolioOperationalStore _portfolioStore;
    private readonly SqliteExchangeTestStore _exchangeStore;
    private readonly IExchangeTestTransport _transport;
    private readonly TimeProvider _timeProvider;
    private ExchangeTestSafetyState _safetyState;

    public ExchangeTestExecutor(ExchangeTestConfiguration configuration,
        SqlitePortfolioOperationalStore portfolioStore,
        SqliteExchangeTestStore exchangeStore, IExchangeTestTransport transport,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(portfolioStore);
        ArgumentNullException.ThrowIfNull(exchangeStore);
        ArgumentNullException.ThrowIfNull(transport);
        if (configuration.Policies.AllowProductionEndpoints
            || configuration.Policies.AllowProductionCredentials
            || configuration.Policies.RealMoneyActivationAvailable
            || configuration.Policies.NetworkCallsAllowedInFixtures
            || !StringComparer.Ordinal.Equals(configuration.EnvironmentId,
                ExchangeTestConfiguration.TestEnvironmentId))
        {
            throw new InvalidOperationException(
                "The exchange-test executor requires fail-closed test-environment policy.");
        }

        ExchangeTestRunMetadata metadata = exchangeStore.ReadRunMetadata();
        if (!StringComparer.Ordinal.Equals(metadata.ConfigurationId,
                configuration.ConfigurationId)
            || !StringComparer.Ordinal.Equals(metadata.EnvironmentId,
                configuration.EnvironmentId))
        {
            throw new InvalidOperationException(
                "The exchange-test run metadata does not match its configuration.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(portfolioStore.DatabasePath,
                exchangeStore.DatabasePath))
        {
            throw new InvalidOperationException(
                "The exchange-test executor requires one SQLite database for lifecycle and receipts.");
        }

        _configuration = configuration;
        _portfolioStore = portfolioStore;
        _exchangeStore = exchangeStore;
        _transport = transport;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _safetyState = new ExchangeTestSafetyState(
            testEnvironmentConfirmed: true,
            privateStreamConnected: false,
            privateStreamFresh: false,
            hasUnknownPositions: false,
            hasManualPositions: false,
            reconciliationRequired: true);
    }

    public ExchangeTestSafetyState SafetyState => _safetyState;

    public void SetSafetyState(ExchangeTestSafetyState safetyState)
    {
        ArgumentNullException.ThrowIfNull(safetyState);
        if (!safetyState.TestEnvironmentConfirmed)
        {
            throw new InvalidOperationException(
                "The exchange-test executor cannot run without test-environment confirmation.");
        }

        _safetyState = safetyState;
    }

    /// <summary>
    /// Revalidates approved risk and atomically persists the reservation and
    /// one dispatch claim before invoking the test transport.
    /// </summary>
    public async ValueTask<ExchangeTestExecutionResult> SubmitAsync(PortfolioId portfolioId,
        OrderIntent intent, PortfolioReservation reservation,
        RiskEvaluationResult riskEvaluation, long expectedReconciliationRevision,
        IRiskMathCalculator riskMathCalculator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(riskEvaluation);
        ArgumentNullException.ThrowIfNull(riskMathCalculator);
        EnsureEntryAllowed(intent);
        if (!riskEvaluation.IsApproved)
        {
            throw new InvalidOperationException(
                "Only an approved risk evaluation can enter the exchange-test executor.");
        }

        PortfolioReservationResult reservationResult = _portfolioStore.TryReserve(intent,
            reservation, riskEvaluation, expectedReconciliationRevision, riskMathCalculator);
        if (reservationResult == PortfolioReservationResult.InsufficientCapacity)
        {
            throw new InvalidOperationException(
                "The exchange-test order could not acquire a durable reservation.");
        }

        ExecutionDispatchClaimResult claim = _portfolioStore.TryClaimDispatch(portfolioId,
            intent, expectedReconciliationRevision, Now());
        return await DispatchAsync(portfolioId, intent, claim, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Dispatches one already durable claim through the test transport.</summary>
    public async ValueTask<ExchangeTestExecutionResult> DispatchAsync(PortfolioId portfolioId,
        OrderIntent intent, ExecutionDispatchClaimResult claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(claim);
        if (!StringComparer.Ordinal.Equals(claim.State.IntentId, intent.IntentId)
            || !StringComparer.Ordinal.Equals(claim.State.ClientOrderId, intent.ClientOrderId))
        {
            throw new InvalidOperationException(
                "The exchange-test claim does not belong to the supplied intent.");
        }

        if (claim.Status != ExecutionDispatchClaimStatus.Claimed)
        {
            ExecutionApplyDisposition disposition = claim.Status ==
                ExecutionDispatchClaimStatus.Conflict
                ? ExecutionApplyDisposition.Conflict
                : ExecutionApplyDisposition.Blocked;
            return new ExchangeTestExecutionResult(new ExecutionTransitionResult(disposition,
                claim.State));
        }

        ExecutionStateSnapshot? durableState = _portfolioStore.ReadExecutionState(
            intent.IntentId);
        if (durableState is null || durableState.State != ExecutionLifecycleState.DispatchClaimed
            || durableState.DispatchAttempt != claim.State.DispatchAttempt)
        {
            return new ExchangeTestExecutionResult(new ExecutionTransitionResult(
                ExecutionApplyDisposition.Blocked, durableState ?? claim.State));
        }

        if (intent.Role == OrderRole.Entry && !_safetyState.EntryAllowed)
        {
            return new ExchangeTestExecutionResult(new ExecutionTransitionResult(
                ExecutionApplyDisposition.Blocked, claim.State));
        }

        ExecutionDispatchAttempt attempt = claim.Attempt
            ?? throw new InvalidDataException("A claimed exchange-test dispatch has no attempt.");
        ExecutionObservation observation;
        IReadOnlyList<ExchangeTestEventReceipt> privateEvents = [];
        ExchangeTestTransportResult? transportResult = null;
        bool protectionBlocked = false;
        try
        {
            transportResult = await _transport.SubmitAsync(intent, attempt,
                cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(transportResult.Observation.IntentId,
                    intent.IntentId))
            {
                throw new InvalidDataException(
                    "The exchange-test transport returned an observation for another intent.");
            }

            observation = transportResult.Observation;
            privateEvents = transportResult.PrivateEvents;
            if (observation.Type == ExecutionObservationType.PartialFill
                && !transportResult.ReduceOnlyProtectionConfirmed)
            {
                protectionBlocked = true;
                observation = ExecutionObservation.Unknown(
                    $"unknown:partial-protection:{attempt.AttemptId}", intent.IntentId);
            }
        }
        catch (TimeoutException)
        {
            observation = ExecutionObservation.Timeout($"timeout:{attempt.AttemptId}",
                intent.IntentId);
        }
        catch (OperationCanceledException)
        {
            observation = ExecutionObservation.Unknown($"unknown:{attempt.AttemptId}",
                intent.IntentId);
        }
        catch (Exception)
        {
            // Rate/auth/503 and transport failures never prove that no order
            // exists. Unknown is reconciled by durable client identity.
            observation = ExecutionObservation.Unknown($"unknown:{attempt.AttemptId}",
                intent.IntentId);
        }

        ExecutionTransitionResult transition;
        try
        {
            foreach (ExchangeTestEventReceipt receipt in privateEvents)
            {
                if (!StringComparer.Ordinal.Equals(receipt.IntentId, intent.IntentId))
                {
                    throw new InvalidDataException(
                        "A private event receipt targets a different durable intent.");
                }
            }

            transition = _portfolioStore.ApplyExchangeTestObservation(_exchangeStore,
                portfolioId, observation, claim.State.ExpectedReconciliationRevision,
                privateEvents, protectionBlocked ? null : transportResult?.Fill,
                protectionBlocked ? Array.Empty<PortfolioPositionSnapshot>()
                    : transportResult?.PositionSnapshots,
                protectionBlocked ? null : transportResult?.Checkpoint);
        }
        catch (Exception)
        {
            // A receipt conflict is itself unknown external state; do not
            // permit a caller to repeat the external request.
            protectionBlocked = true;
            observation = ExecutionObservation.Unknown($"unknown:receipt:{attempt.AttemptId}",
                intent.IntentId);
            transition = _portfolioStore.ApplyExchangeTestObservation(_exchangeStore,
                portfolioId, observation, claim.State.ExpectedReconciliationRevision,
                Array.Empty<ExchangeTestEventReceipt>());
        }

        ApplyPrivateEventSafety(privateEvents);
        return new ExchangeTestExecutionResult(transition, protectionBlocked);
    }

    /// <summary>Reconciles one durable unknown/timeout state using fresh evidence only.</summary>
    public async ValueTask<ExecutionRecoveryResult> ReconcileAsync(PortfolioId portfolioId,
        string intentId, long expectedReconciliationRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ExecutionStateSnapshot state = _portfolioStore.ReadExecutionState(intentId)
            ?? throw new InvalidOperationException("The recovery intent is not durable.");
        if (!_safetyState.TestEnvironmentConfirmed || _safetyState.HasUnknownPositions
            || _safetyState.HasManualPositions)
        {
            return new ExecutionRecoveryResult(ExecutionRecoveryDisposition.Blocked, state,
                "Manual or unknown position state blocks exchange-test recovery.");
        }

        OrderIntent intent = _portfolioStore.FindIntent(intentId)
            ?? throw new InvalidOperationException("The recovery intent is not durable.");
        ReconciliationCheckpoint checkpoint = _portfolioStore.ReadCheckpoint(portfolioId);
        ExecutionRecoveryEvidence evidence;
        try
        {
            evidence = await _transport.ReconcileAsync(intent, portfolioId, state, checkpoint,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new ExecutionRecoveryResult(ExecutionRecoveryDisposition.Blocked, state,
                "Fresh complete reconciliation evidence was unavailable.");
        }

        ExecutionRecoveryCoordinator coordinator = new(_portfolioStore,
            new OneShotReconciler(evidence));
        return coordinator.Reconcile(portfolioId, intentId, expectedReconciliationRevision);
    }

    /// <summary>Persists a normalized private event and applies its safety gate.</summary>
    public ExchangeTestReceiptWriteDisposition RecordPrivateEvent(
        ExchangeTestEventReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ExchangeTestReceiptWriteDisposition disposition = _exchangeStore.AppendPrivateEvent(
            receipt);
        ApplyPrivateEventSafety([receipt]);
        return disposition;
    }

    public ValueTask<ExchangeTestReceiptWriteDisposition> RecordPrivateEventAsync(
        ExchangeTestEventReceipt receipt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(RecordPrivateEvent(receipt));
    }

    private void EnsureEntryAllowed(OrderIntent intent)
    {
        if (intent.Role == OrderRole.Entry && !_safetyState.EntryAllowed)
        {
            throw new InvalidOperationException(
                "Private stream freshness, position certainty, and test-environment confirmation are required before an entry.");
        }
    }

    private UtcTimestamp Now() => new(_timeProvider.GetUtcNow());

    private void ApplyPrivateEventSafety(IEnumerable<ExchangeTestEventReceipt> receipts)
    {
        if (!receipts.Any(receipt => receipt.RequiresReconciliation
            || StringComparer.Ordinal.Equals(receipt.EventType, "ALGO_UPDATE")
            || StringComparer.Ordinal.Equals(receipt.EventType, "listenKeyExpired")))
        {
            return;
        }

        _safetyState = new ExchangeTestSafetyState(
            _safetyState.TestEnvironmentConfirmed,
            _safetyState.PrivateStreamConnected,
            _safetyState.PrivateStreamFresh,
            _safetyState.HasUnknownPositions,
            _safetyState.HasManualPositions,
            reconciliationRequired: true);
    }

    private sealed class OneShotReconciler : IPrivateOrderReconciler
    {
        private readonly ExecutionRecoveryEvidence _evidence;

        public OneShotReconciler(ExecutionRecoveryEvidence evidence)
        {
            _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        }

        public ExecutionRecoveryEvidence Reconcile(OrderIntent intent, PortfolioId portfolioId,
            ExecutionStateSnapshot state, ReconciliationCheckpoint checkpoint) => _evidence;
    }
}
