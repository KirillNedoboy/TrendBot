using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum ExecutionLifecycleState
{
    IntentPersisted,
    DispatchClaimed,
    Accepted,
    Rejected,
    PartiallyFilled,
    Filled,
    CancelRequested,
    Canceled,
    TimedOut,
    Unknown,
    Conflict
}

public enum ExecutionObservationType
{
    Accepted,
    Rejected,
    PartialFill,
    Filled,
    CancelRequested,
    Canceled,
    Timeout,
    Unknown
}

public enum ExecutionApplyDisposition
{
    Applied,
    AlreadyApplied,
    Conflict,
    Blocked
}

public enum ExecutionDispatchClaimStatus
{
    Claimed,
    AlreadyClaimed,
    Blocked,
    Conflict
}

public sealed record ExecutionDispatchAttempt
{
    [JsonConstructor]
    public ExecutionDispatchAttempt(string attemptId, string intentId, int attempt,
        long reconciliationRevision, UtcTimestamp claimedAt)
    {
        ContractGuard.RequireText(attemptId, nameof(attemptId));
        ContractGuard.RequireText(intentId, nameof(intentId));
        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt,
                "A dispatch attempt must be positive.");
        }

        if (reconciliationRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationRevision),
                reconciliationRevision, "A reconciliation revision cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(claimedAt);
        AttemptId = attemptId;
        IntentId = intentId;
        Attempt = attempt;
        ReconciliationRevision = reconciliationRevision;
        ClaimedAt = claimedAt;
    }

    public string AttemptId { get; }
    public string IntentId { get; }
    public int Attempt { get; }
    public long ReconciliationRevision { get; }
    public UtcTimestamp ClaimedAt { get; }
}

public sealed record ExecutionDispatchClaimResult
{
    [JsonConstructor]
    public ExecutionDispatchClaimResult(ExecutionDispatchClaimStatus status,
        ExecutionStateSnapshot state, ExecutionDispatchAttempt? attempt)
    {
        ContractGuard.RequireDefined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(state);
        Status = status;
        State = state;
        Attempt = attempt;
    }

    public ExecutionDispatchClaimStatus Status { get; }
    public ExecutionStateSnapshot State { get; }
    public ExecutionDispatchAttempt? Attempt { get; }
}

/// <summary>One immutable external observation retained as an idempotency receipt.</summary>
public sealed record ExecutionObservation
{
    [JsonConstructor]
    public ExecutionObservation(string receiptId, string intentId, ExecutionObservationType type,
        string? exchangeOrderId, decimal quantity, string? reason, bool isReconciliation = false)
    {
        ContractGuard.RequireText(receiptId, nameof(receiptId));
        ContractGuard.RequireText(intentId, nameof(intentId));
        ContractGuard.RequireDefined(type, nameof(type));
        if (quantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity,
                "An execution observation quantity cannot be negative.");
        }

        if (exchangeOrderId is not null)
        {
            ContractGuard.RequireText(exchangeOrderId, nameof(exchangeOrderId));
        }

        if (reason is not null)
        {
            ContractGuard.RequireText(reason, nameof(reason));
        }

        ReceiptId = receiptId;
        IntentId = intentId;
        Type = type;
        ExchangeOrderId = exchangeOrderId;
        Quantity = quantity;
        Reason = reason;
        IsReconciliation = isReconciliation;
    }

    public string ReceiptId { get; }
    public string IntentId { get; }
    public ExecutionObservationType Type { get; }
    public string? ExchangeOrderId { get; }
    public decimal Quantity { get; }
    public string? Reason { get; }
    public bool IsReconciliation { get; }

    public static ExecutionObservation Accepted(string receiptId, string intentId,
        string exchangeOrderId, bool isReconciliation = false) =>
        new(receiptId, intentId, ExecutionObservationType.Accepted, exchangeOrderId, 0m, null,
            isReconciliation);

    public static ExecutionObservation Rejected(string receiptId, string intentId,
        string exchangeOrderId, string reason, bool isReconciliation = false) =>
        new(receiptId, intentId, ExecutionObservationType.Rejected, exchangeOrderId, 0m, reason,
            isReconciliation);

    public static ExecutionObservation PartialFill(string receiptId, string intentId,
        string exchangeOrderId, decimal quantity, bool isReconciliation = false)
    {
        RequirePositiveFillQuantity(quantity);
        return new ExecutionObservation(receiptId, intentId, ExecutionObservationType.PartialFill,
            exchangeOrderId, quantity, null, isReconciliation);
    }

    public static ExecutionObservation Filled(string receiptId, string intentId,
        string exchangeOrderId, decimal quantity, bool isReconciliation = false)
    {
        RequirePositiveFillQuantity(quantity);
        return new ExecutionObservation(receiptId, intentId, ExecutionObservationType.Filled,
            exchangeOrderId, quantity, null, isReconciliation);
    }

    public static ExecutionObservation CancelRequested(string receiptId, string intentId,
        string exchangeOrderId, bool isReconciliation = false) =>
        new(receiptId, intentId, ExecutionObservationType.CancelRequested, exchangeOrderId, 0m,
            null, isReconciliation);

    public static ExecutionObservation Canceled(string receiptId, string intentId,
        string exchangeOrderId, bool isReconciliation = false) =>
        new(receiptId, intentId, ExecutionObservationType.Canceled, exchangeOrderId, 0m, null,
            isReconciliation);

    public static ExecutionObservation Timeout(string receiptId, string intentId,
        bool isReconciliation = false) =>
        new(receiptId, intentId, ExecutionObservationType.Timeout, null, 0m, null,
            isReconciliation);

    public static ExecutionObservation Unknown(string receiptId, string intentId,
        bool isReconciliation = false) =>
        new(receiptId, intentId, ExecutionObservationType.Unknown, null, 0m, null,
            isReconciliation);

    private static void RequirePositiveFillQuantity(decimal quantity)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity,
                "A fill observation quantity must be positive.");
        }
    }
}

/// <summary>Durable deterministic lifecycle state for one persisted order intent.</summary>
public sealed record ExecutionStateSnapshot
{
    public const int CurrentFormatVersion = 1;

    [JsonConstructor]
    public ExecutionStateSnapshot(int formatVersion, string intentId, string clientOrderId,
        decimal intentQuantity, decimal filledQuantity, int dispatchAttempt,
        ExecutionLifecycleState state, string? exchangeOrderId,
        ImmutableValueList<ExecutionObservation> receipts, bool requiresReconciliation,
        long expectedReconciliationRevision)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The execution state format is unsupported.");
        }

        ContractGuard.RequireText(intentId, nameof(intentId));
        ContractGuard.RequireText(clientOrderId, nameof(clientOrderId));
        if (intentQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(intentQuantity), intentQuantity,
                "An execution intent quantity must be positive.");
        }

        if (filledQuantity < 0m || filledQuantity > intentQuantity)
        {
            throw new ArgumentOutOfRangeException(nameof(filledQuantity), filledQuantity,
                "Filled quantity must stay within the intent quantity.");
        }

        if (dispatchAttempt < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatchAttempt), dispatchAttempt,
                "A dispatch attempt cannot be negative.");
        }

        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(receipts);
        if (expectedReconciliationRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedReconciliationRevision),
                expectedReconciliationRevision, "A reconciliation revision cannot be negative.");
        }

        HashSet<string> receiptIds = new(StringComparer.Ordinal);
        foreach (ExecutionObservation receipt in receipts)
        {
            ArgumentNullException.ThrowIfNull(receipt);
            if (!receiptIds.Add(receipt.ReceiptId))
            {
                throw new ArgumentException("Execution receipts cannot repeat an identity.",
                    nameof(receipts));
            }

            if (!StringComparer.Ordinal.Equals(receipt.IntentId, intentId))
            {
                throw new ArgumentException("Execution receipts must belong to the state intent.",
                    nameof(receipts));
            }
        }

        if (exchangeOrderId is not null)
        {
            ContractGuard.RequireText(exchangeOrderId, nameof(exchangeOrderId));
        }

        FormatVersion = formatVersion;
        IntentId = intentId;
        ClientOrderId = clientOrderId;
        IntentQuantity = intentQuantity;
        FilledQuantity = filledQuantity;
        DispatchAttempt = dispatchAttempt;
        State = state;
        ExchangeOrderId = exchangeOrderId;
        Receipts = new ImmutableValueList<ExecutionObservation>(receipts);
        RequiresReconciliation = requiresReconciliation;
        ExpectedReconciliationRevision = expectedReconciliationRevision;
    }

    public int FormatVersion { get; }
    public string IntentId { get; }
    public string ClientOrderId { get; }
    public decimal IntentQuantity { get; }
    public decimal FilledQuantity { get; }
    public int DispatchAttempt { get; }
    public ExecutionLifecycleState State { get; }
    public string? ExchangeOrderId { get; }
    public ImmutableValueList<ExecutionObservation> Receipts { get; }
    public bool RequiresReconciliation { get; }
    public long ExpectedReconciliationRevision { get; }
}

public sealed record ExecutionTransitionResult
{
    public ExecutionTransitionResult(ExecutionApplyDisposition disposition,
        ExecutionStateSnapshot state)
    {
        ContractGuard.RequireDefined(disposition, nameof(disposition));
        ArgumentNullException.ThrowIfNull(state);
        Disposition = disposition;
        State = state;
    }

    public ExecutionApplyDisposition Disposition { get; }
    public ExecutionStateSnapshot State { get; }
}

/// <summary>
/// Pure fail-closed transitions for the durable execution lifecycle. It never
/// performs I/O or authorizes a side effect.
/// </summary>
public static class ExecutionStateMachine
{
    public static ExecutionStateSnapshot Start(OrderIntent intent,
        long expectedReconciliationRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (expectedReconciliationRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedReconciliationRevision),
                expectedReconciliationRevision, "A reconciliation revision cannot be negative.");
        }

        return new ExecutionStateSnapshot(ExecutionStateSnapshot.CurrentFormatVersion,
            intent.IntentId, intent.ClientOrderId, intent.Quantity.Value, 0m, 0,
            ExecutionLifecycleState.IntentPersisted, null,
            new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()), false,
            expectedReconciliationRevision);
    }

    public static ExecutionStateSnapshot Claim(ExecutionStateSnapshot state, int attempt)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt,
                "A dispatch attempt must be positive.");
        }

        if (state.RequiresReconciliation)
        {
            throw new InvalidOperationException(
                "An execution requiring reconciliation cannot be dispatched.");
        }

        if (state.State == ExecutionLifecycleState.DispatchClaimed)
        {
            if (state.DispatchAttempt == attempt)
            {
                return state;
            }

            throw new InvalidOperationException("An execution dispatch claim already exists.");
        }

        if (state.State != ExecutionLifecycleState.IntentPersisted)
        {
            throw new InvalidOperationException(
                "Only a persisted intent can acquire a dispatch claim.");
        }

        return Rebuild(state, ExecutionLifecycleState.DispatchClaimed, state.FilledQuantity,
            state.ExchangeOrderId, state.Receipts, false, attempt);
    }

    /// <summary>
    /// Explicitly clears an interrupted, no-order execution for a fresh retry.
    /// The caller must first obtain complete current evidence; limited history,
    /// known orders, fills, or positions cannot authorize this transition.
    /// </summary>
    public static ExecutionStateSnapshot RecoverForRetry(ExecutionStateSnapshot state,
        ExecutionRecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!StringComparer.Ordinal.Equals(state.IntentId, evidence.IntentId))
        {
            throw new InvalidOperationException(
                "Recovery evidence must target the persisted execution intent.");
        }

        if (!evidence.CanAuthorizeRetry)
        {
            throw new InvalidOperationException(
                "Only fresh complete no-order evidence can authorize a retry.");
        }

        if (state.FilledQuantity != 0m || state.ExchangeOrderId is not null)
        {
            throw new InvalidOperationException(
                "An execution with fills or a known exchange order cannot be retried.");
        }

        if (state.State is not (ExecutionLifecycleState.DispatchClaimed
            or ExecutionLifecycleState.TimedOut
            or ExecutionLifecycleState.Unknown
            or ExecutionLifecycleState.Conflict))
        {
            throw new InvalidOperationException(
                "Only an interrupted or reconciliation-required execution can be recovered.");
        }

        return new ExecutionStateSnapshot(ExecutionStateSnapshot.CurrentFormatVersion,
            state.IntentId, state.ClientOrderId, state.IntentQuantity, state.FilledQuantity,
            state.DispatchAttempt, ExecutionLifecycleState.IntentPersisted, null, state.Receipts,
            false, evidence.ReconciliationRevision);
    }

    /// <summary>Rebinds reconciliation-required state to a checked current revision.</summary>
    public static ExecutionStateSnapshot BindReconciliationRevision(
        ExecutionStateSnapshot state, long reconciliationRevision)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (reconciliationRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationRevision),
                reconciliationRevision, "A reconciliation revision cannot be negative.");
        }

        if (state.ExpectedReconciliationRevision == reconciliationRevision)
        {
            return state;
        }

        if (!state.RequiresReconciliation)
        {
            throw new InvalidOperationException(
                "Only reconciliation-required state can change its expected revision.");
        }

        return new ExecutionStateSnapshot(ExecutionStateSnapshot.CurrentFormatVersion,
            state.IntentId, state.ClientOrderId, state.IntentQuantity, state.FilledQuantity,
            state.DispatchAttempt, state.State, state.ExchangeOrderId, state.Receipts, true,
            reconciliationRevision);
    }

    public static ExecutionTransitionResult Apply(ExecutionStateSnapshot state,
        ExecutionObservation observation)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        if (!StringComparer.Ordinal.Equals(state.IntentId, observation.IntentId))
        {
            throw new InvalidOperationException(
                "An execution observation must target the persisted intent.");
        }

        ExecutionObservation? prior = state.Receipts.FirstOrDefault(receipt =>
            StringComparer.Ordinal.Equals(receipt.ReceiptId, observation.ReceiptId));
        if (prior is not null)
        {
            if (SameReceiptEvidence(prior, observation))
            {
                return new ExecutionTransitionResult(ExecutionApplyDisposition.AlreadyApplied,
                    state);
            }

            return Conflict(state, observation);
        }

        if (state.RequiresReconciliation && !observation.IsReconciliation)
        {
            return new ExecutionTransitionResult(ExecutionApplyDisposition.Blocked, state);
        }

        try
        {
            ExecutionStateSnapshot next = ApplyNew(state, observation);
            return new ExecutionTransitionResult(ExecutionApplyDisposition.Applied, next);
        }
        catch (InvalidOperationException)
        {
            return Conflict(state, observation);
        }
        catch (ArgumentException)
        {
            return Conflict(state, observation);
        }
    }

    private static ExecutionStateSnapshot ApplyNew(ExecutionStateSnapshot state,
        ExecutionObservation observation)
    {
        if (state.State is ExecutionLifecycleState.Conflict
            && !observation.IsReconciliation)
        {
            throw new InvalidOperationException("A conflicting execution requires recovery.");
        }

        if (IsTerminal(state.State) && !observation.IsReconciliation)
        {
            throw new InvalidOperationException("A terminal execution cannot change state.");
        }

        string? exchangeOrderId = state.ExchangeOrderId ?? observation.ExchangeOrderId;
        if (observation.ExchangeOrderId is not null
            && state.ExchangeOrderId is not null
            && !StringComparer.Ordinal.Equals(state.ExchangeOrderId,
                observation.ExchangeOrderId))
        {
            throw new InvalidOperationException(
                "An execution observation conflicts with the exchange order identity.");
        }

        ExecutionLifecycleState nextState;
        decimal filledQuantity = state.FilledQuantity;
        bool requiresReconciliation = state.RequiresReconciliation;
        switch (observation.Type)
        {
            case ExecutionObservationType.Accepted:
                RequireExchangeOrderId(observation);
                RequireState(state.State, ExecutionLifecycleState.IntentPersisted,
                    ExecutionLifecycleState.DispatchClaimed, ExecutionLifecycleState.Unknown,
                    ExecutionLifecycleState.TimedOut, ExecutionLifecycleState.Accepted,
                    ExecutionLifecycleState.CancelRequested);
                nextState = ExecutionLifecycleState.Accepted;
                requiresReconciliation = false;
                break;
            case ExecutionObservationType.Rejected:
                RequireExchangeOrderId(observation);
                RequireText(observation.Reason, "A rejection requires a reason.");
                RequireState(state.State, ExecutionLifecycleState.IntentPersisted,
                    ExecutionLifecycleState.DispatchClaimed, ExecutionLifecycleState.Accepted,
                    ExecutionLifecycleState.Unknown, ExecutionLifecycleState.TimedOut);
                nextState = ExecutionLifecycleState.Rejected;
                requiresReconciliation = false;
                break;
            case ExecutionObservationType.PartialFill:
                RequireExchangeOrderId(observation);
                RequirePositiveQuantity(observation);
                RequireState(state.State, ExecutionLifecycleState.Accepted,
                    ExecutionLifecycleState.PartiallyFilled, ExecutionLifecycleState.CancelRequested,
                    ExecutionLifecycleState.Unknown, ExecutionLifecycleState.TimedOut);
                filledQuantity = checked(state.FilledQuantity + observation.Quantity);
                if (filledQuantity > state.IntentQuantity)
                {
                    throw new InvalidOperationException("An execution fill exceeds the intent quantity.");
                }

                nextState = filledQuantity == state.IntentQuantity
                    ? ExecutionLifecycleState.Filled
                    : ExecutionLifecycleState.PartiallyFilled;
                requiresReconciliation = false;
                break;
            case ExecutionObservationType.Filled:
                RequireExchangeOrderId(observation);
                RequirePositiveQuantity(observation);
                RequireState(state.State, ExecutionLifecycleState.Accepted,
                    ExecutionLifecycleState.PartiallyFilled, ExecutionLifecycleState.CancelRequested,
                    ExecutionLifecycleState.Unknown, ExecutionLifecycleState.TimedOut);
                filledQuantity = checked(state.FilledQuantity + observation.Quantity);
                if (filledQuantity != state.IntentQuantity)
                {
                    throw new InvalidOperationException(
                        "A filled observation must account for the complete intent quantity.");
                }

                nextState = ExecutionLifecycleState.Filled;
                requiresReconciliation = false;
                break;
            case ExecutionObservationType.CancelRequested:
                RequireExchangeOrderId(observation);
                RequireState(state.State, ExecutionLifecycleState.Accepted,
                    ExecutionLifecycleState.PartiallyFilled, ExecutionLifecycleState.CancelRequested,
                    ExecutionLifecycleState.Unknown, ExecutionLifecycleState.TimedOut);
                nextState = ExecutionLifecycleState.CancelRequested;
                requiresReconciliation = false;
                break;
            case ExecutionObservationType.Canceled:
                RequireExchangeOrderId(observation);
                RequireState(state.State, ExecutionLifecycleState.Accepted,
                    ExecutionLifecycleState.PartiallyFilled, ExecutionLifecycleState.CancelRequested,
                    ExecutionLifecycleState.Unknown, ExecutionLifecycleState.TimedOut);
                nextState = ExecutionLifecycleState.Canceled;
                requiresReconciliation = false;
                break;
            case ExecutionObservationType.Timeout:
                RequireState(state.State, ExecutionLifecycleState.DispatchClaimed,
                    ExecutionLifecycleState.Accepted, ExecutionLifecycleState.CancelRequested);
                nextState = ExecutionLifecycleState.TimedOut;
                requiresReconciliation = true;
                break;
            case ExecutionObservationType.Unknown:
                RequireState(state.State, ExecutionLifecycleState.DispatchClaimed,
                    ExecutionLifecycleState.Accepted, ExecutionLifecycleState.CancelRequested,
                    ExecutionLifecycleState.TimedOut, ExecutionLifecycleState.Unknown);
                nextState = ExecutionLifecycleState.Unknown;
                requiresReconciliation = true;
                break;
            default:
                throw new InvalidOperationException("Unknown execution observation type.");
        }

        return Rebuild(state, nextState, filledQuantity, exchangeOrderId,
            state.Receipts.Append(observation), requiresReconciliation, state.DispatchAttempt);
    }

    private static ExecutionTransitionResult Conflict(ExecutionStateSnapshot state,
        ExecutionObservation observation)
    {
        ImmutableValueList<ExecutionObservation> receipts = state.Receipts.Any(receipt =>
            StringComparer.Ordinal.Equals(receipt.ReceiptId, observation.ReceiptId))
            ? state.Receipts
            : new ImmutableValueList<ExecutionObservation>(state.Receipts.Append(observation));
        ExecutionStateSnapshot conflict = Rebuild(state, ExecutionLifecycleState.Conflict,
            state.FilledQuantity, state.ExchangeOrderId, receipts, true, state.DispatchAttempt);
        return new ExecutionTransitionResult(ExecutionApplyDisposition.Conflict, conflict);
    }

    private static ExecutionStateSnapshot Rebuild(ExecutionStateSnapshot state,
        ExecutionLifecycleState lifecycle, decimal filledQuantity, string? exchangeOrderId,
        IEnumerable<ExecutionObservation> receipts, bool requiresReconciliation, int attempt)
    {
        return new ExecutionStateSnapshot(ExecutionStateSnapshot.CurrentFormatVersion,
            state.IntentId, state.ClientOrderId, state.IntentQuantity, filledQuantity, attempt,
            lifecycle, exchangeOrderId, new ImmutableValueList<ExecutionObservation>(receipts),
            requiresReconciliation, state.ExpectedReconciliationRevision);
    }

    private static bool SameReceiptEvidence(ExecutionObservation left,
        ExecutionObservation right) =>
        StringComparer.Ordinal.Equals(left.ReceiptId, right.ReceiptId)
        && StringComparer.Ordinal.Equals(left.IntentId, right.IntentId)
        && left.Type == right.Type
        && StringComparer.Ordinal.Equals(left.ExchangeOrderId, right.ExchangeOrderId)
        && left.Quantity == right.Quantity
        && StringComparer.Ordinal.Equals(left.Reason, right.Reason);

    private static bool IsTerminal(ExecutionLifecycleState state) => state is
        ExecutionLifecycleState.Rejected or ExecutionLifecycleState.Filled
            or ExecutionLifecycleState.Canceled;

    private static void RequireExchangeOrderId(ExecutionObservation observation)
    {
        RequireText(observation.ExchangeOrderId,
            "An execution observation requires an exchange order identity.");
    }

    private static void RequirePositiveQuantity(ExecutionObservation observation)
    {
        if (observation.Quantity <= 0m)
        {
            throw new InvalidOperationException("A fill observation quantity must be positive.");
        }
    }

    private static void RequireText(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void RequireState(ExecutionLifecycleState actual,
        params ExecutionLifecycleState[] allowed)
    {
        if (!allowed.Contains(actual))
        {
            throw new InvalidOperationException("The execution observation is not valid for state.");
        }
    }
}
