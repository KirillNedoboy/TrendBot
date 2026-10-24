using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum ExecutionRecoveryEvidenceStatus
{
    Fresh,
    Stale,
    Unknown,
    Conflicting,
    Manual
}

/// <summary>
/// Complete adapter evidence required before a durable execution can be
/// recovered or explicitly made retryable.
/// </summary>
public sealed record ExecutionRecoveryEvidence
{
    [JsonConstructor]
    public ExecutionRecoveryEvidence(string intentId, long reconciliationRevision,
        ExecutionRecoveryEvidenceStatus status, bool isComplete, bool orderHistoryComplete,
        bool orderKnown, bool positionsKnown, bool reservationsKnown,
        ImmutableValueList<ExecutionObservation> observations,
        ImmutableValueList<string>? fillIds = null,
        ImmutableValueList<string>? positionIds = null,
        ImmutableValueList<string>? reservationIds = null)
    {
        ContractGuard.RequireText(intentId, nameof(intentId));
        if (reconciliationRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationRevision),
                reconciliationRevision, "A reconciliation revision cannot be negative.");
        }

        ContractGuard.RequireDefined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(observations);
        fillIds ??= new ImmutableValueList<string>(Array.Empty<string>());
        positionIds ??= new ImmutableValueList<string>(Array.Empty<string>());
        reservationIds ??= new ImmutableValueList<string>(Array.Empty<string>());
        ValidateIdentities(fillIds, nameof(fillIds));
        ValidateIdentities(positionIds, nameof(positionIds));
        ValidateIdentities(reservationIds, nameof(reservationIds));
        foreach (ExecutionObservation observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);
            if (!StringComparer.Ordinal.Equals(observation.IntentId, intentId))
            {
                throw new ArgumentException(
                    "Recovery observations must belong to the recovered intent.",
                    nameof(observations));
            }
        }

        IntentId = intentId;
        ReconciliationRevision = reconciliationRevision;
        Status = status;
        IsComplete = isComplete;
        OrderHistoryComplete = orderHistoryComplete;
        OrderKnown = orderKnown;
        PositionsKnown = positionsKnown;
        ReservationsKnown = reservationsKnown;
        Observations = new ImmutableValueList<ExecutionObservation>(observations);
        FillIds = new ImmutableValueList<string>(fillIds);
        PositionIds = new ImmutableValueList<string>(positionIds);
        ReservationIds = new ImmutableValueList<string>(reservationIds);
    }

    public string IntentId { get; }
    public long ReconciliationRevision { get; }
    public ExecutionRecoveryEvidenceStatus Status { get; }
    public bool IsComplete { get; }
    public bool OrderHistoryComplete { get; }
    public bool OrderKnown { get; }
    public bool PositionsKnown { get; }
    public bool ReservationsKnown { get; }
    public ImmutableValueList<ExecutionObservation> Observations { get; }
    public ImmutableValueList<string> FillIds { get; }
    public ImmutableValueList<string> PositionIds { get; }
    public ImmutableValueList<string> ReservationIds { get; }

    public bool CanAuthorizeRetry => Status == ExecutionRecoveryEvidenceStatus.Fresh
        && IsComplete
        && OrderHistoryComplete
        && !OrderKnown
        && PositionsKnown
        && ReservationsKnown
        && Observations.Count == 0;

    public static ExecutionRecoveryEvidence FreshNoOrder(string intentId, long revision,
        bool historyComplete = true) => new(intentId, revision,
            ExecutionRecoveryEvidenceStatus.Fresh, true, historyComplete, false, true, true,
            new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()));

    public static ExecutionRecoveryEvidence FreshKnownOrder(string intentId, long revision,
        params ExecutionObservation[] observations) => new(intentId, revision,
            ExecutionRecoveryEvidenceStatus.Fresh, true, true, true, true, true,
            new ImmutableValueList<ExecutionObservation>(observations));

    private static void ValidateIdentities(ImmutableValueList<string> values,
        string parameterName)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            ContractGuard.RequireText(value, parameterName);
            if (!identities.Add(value))
            {
                throw new ArgumentException("Recovery identities cannot repeat.", parameterName);
            }
        }
    }
}

public enum ExecutionRecoveryDisposition
{
    Replayed,
    RetryAllowed,
    Blocked
}

public sealed record ExecutionRecoveryResult
{
    public ExecutionRecoveryResult(ExecutionRecoveryDisposition disposition,
        ExecutionStateSnapshot state, string reason)
    {
        ContractGuard.RequireDefined(disposition, nameof(disposition));
        ArgumentNullException.ThrowIfNull(state);
        ContractGuard.RequireText(reason, nameof(reason));
        Disposition = disposition;
        State = state;
        Reason = reason;
    }

    public ExecutionRecoveryDisposition Disposition { get; }
    public ExecutionStateSnapshot State { get; }
    public string Reason { get; }
}
