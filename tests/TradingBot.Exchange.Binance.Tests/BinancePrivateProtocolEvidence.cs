namespace TradingBot.Exchange.Binance.Tests;

public enum BinancePrivateProtocolEvidenceReason
{
    IncompleteEvidence,
    StaleEvidence,
    UnknownStatus,
    UserDataDisconnected,
    ListenKeyExpired,
    ClockSkew,
    RateLimited,
    AuthenticationFailure,
    AmbiguousRequestOutcome,
    ReconciliationRequired,
    OrderResponseMissing,
    Http503UnknownOutcome,
    Http503ServiceUnavailable,
    Http503InternalError,
    Http503SystemThrottled
}

public enum BinancePrivateHttp503Variant
{
    None,
    UnknownOutcome,
    ServiceUnavailable,
    InternalError,
    SystemThrottled
}

internal sealed record BinancePrivateProtocolEvidenceSnapshot(
    bool EvidenceComplete,
    DateTimeOffset? LastEvidenceAt,
    bool UserDataConnected,
    bool ListenKeyActive,
    long? ClockSkewMilliseconds,
    bool UnknownStatus,
    bool RateLimitObserved,
    bool AuthenticationFailureObserved,
    bool RequestOutcomeKnown,
    bool OrderResponseExpected,
    bool OrderResponsePresent,
    BinancePrivateHttp503Variant Http503Variant);

internal sealed record BinancePrivateProtocolEvidenceDiagnostic(
    BinancePrivateProtocolEvidenceReason Reason,
    string Message);

internal static class BinancePrivateProtocolEvidenceEvaluator
{
    public static IReadOnlyList<BinancePrivateProtocolEvidenceDiagnostic> Diagnose(
        BinancePrivateProtocolEvidenceSnapshot evidence,
        DateTimeOffset now,
        TimeSpan syntheticFreshnessWindow,
        TimeSpan syntheticClockSkewTolerance)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            syntheticFreshnessWindow, TimeSpan.Zero, nameof(syntheticFreshnessWindow));
        ArgumentOutOfRangeException.ThrowIfLessThan(
            syntheticClockSkewTolerance, TimeSpan.Zero, nameof(syntheticClockSkewTolerance));

        List<BinancePrivateProtocolEvidenceDiagnostic> diagnostics = [];

        if (!evidence.EvidenceComplete)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.IncompleteEvidence,
                "Private protocol evidence is incomplete.");
        }

        if (evidence.LastEvidenceAt is { } lastEvidenceAt
            && now - lastEvidenceAt > syntheticFreshnessWindow)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.StaleEvidence,
                "Evidence exceeded the caller-supplied synthetic freshness interval.");
        }

        if (evidence.UnknownStatus)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.UnknownStatus,
                "The observed status is not confirmed by the official protocol evidence.");
        }

        if (!evidence.UserDataConnected)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.UserDataDisconnected,
                "The user-data stream is disconnected.");
        }

        if (!evidence.ListenKeyActive)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.ListenKeyExpired,
                "The listenKey is expired or otherwise inactive.");
        }

        if (evidence.ClockSkewMilliseconds is { } clockSkew
            && Math.Abs(clockSkew) > syntheticClockSkewTolerance.TotalMilliseconds)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.ClockSkew,
                "Clock skew exceeded the caller-supplied synthetic tolerance.");
        }

        if (evidence.RateLimitObserved)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.RateLimited,
                "The evidence contains a rate-limit response or throttle signal.");
        }

        if (evidence.AuthenticationFailureObserved)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.AuthenticationFailure,
                "The evidence contains an authentication failure.");
        }

        if (!evidence.RequestOutcomeKnown)
        {
            AddAmbiguousOutcome(diagnostics);
        }

        if (evidence.OrderResponseExpected && !evidence.OrderResponsePresent)
        {
            Add(diagnostics, BinancePrivateProtocolEvidenceReason.OrderResponseMissing,
                "An absent order in the response does not prove non-execution.");
        }

        switch (evidence.Http503Variant)
        {
            case BinancePrivateHttp503Variant.UnknownOutcome:
                Add(diagnostics, BinancePrivateProtocolEvidenceReason.Http503UnknownOutcome,
                    "HTTP 503 reported an unknown execution result; reconciliation is required.");
                AddAmbiguousOutcome(diagnostics);
                break;
            case BinancePrivateHttp503Variant.ServiceUnavailable:
                Add(diagnostics, BinancePrivateProtocolEvidenceReason.Http503ServiceUnavailable,
                    "HTTP 503 reported service unavailable.");
                break;
            case BinancePrivateHttp503Variant.InternalError:
                Add(diagnostics, BinancePrivateProtocolEvidenceReason.Http503InternalError,
                    "HTTP 503 reported an internal error.");
                break;
            case BinancePrivateHttp503Variant.SystemThrottled:
                Add(diagnostics, BinancePrivateProtocolEvidenceReason.Http503SystemThrottled,
                    "HTTP 503 reported system throttling.");
                break;
            case BinancePrivateHttp503Variant.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evidence));
        }

        return diagnostics.ToArray();
    }

    private static void AddAmbiguousOutcome(
        ICollection<BinancePrivateProtocolEvidenceDiagnostic> diagnostics)
    {
        Add(diagnostics, BinancePrivateProtocolEvidenceReason.AmbiguousRequestOutcome,
            "The request result is ambiguous and cannot be treated as success or failure.");
        Add(diagnostics, BinancePrivateProtocolEvidenceReason.ReconciliationRequired,
            "Reconciliation is required before interpreting the request outcome.");
    }

    private static void Add(
        ICollection<BinancePrivateProtocolEvidenceDiagnostic> diagnostics,
        BinancePrivateProtocolEvidenceReason reason,
        string message)
    {
        diagnostics.Add(new BinancePrivateProtocolEvidenceDiagnostic(reason, message));
    }
}
