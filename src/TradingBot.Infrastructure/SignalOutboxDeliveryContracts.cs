using TradingBot.Core;

namespace TradingBot.Infrastructure;

public enum SignalOutboxDeliveryStatus
{
    Pending,
    Leased,
    Sending,
    Acknowledged,
    UnknownDelivery,
    PermanentFailure,
    RetryExhausted,
    Expired
}

public enum SignalOutboxFailureKind
{
    Retryable,
    RateLimited,
    Permanent
}

/// <summary>Immutable limits persisted with a delivery journal before dispatch.</summary>
public sealed record SignalOutboxDeliveryPolicy
{
    public static SignalOutboxDeliveryPolicy Default { get; } = new(
        maxAttempts: 5,
        maxAge: TimeSpan.FromHours(24),
        initialBackoff: TimeSpan.FromSeconds(5),
        maxBackoff: TimeSpan.FromMinutes(5),
        leaseDuration: TimeSpan.FromMinutes(1));

    public int MaxAttempts { get; }
    public TimeSpan MaxAge { get; }
    public TimeSpan InitialBackoff { get; }
    public TimeSpan MaxBackoff { get; }
    public TimeSpan LeaseDuration { get; }

    public SignalOutboxDeliveryPolicy(int maxAttempts, TimeSpan maxAge,
        TimeSpan initialBackoff, TimeSpan maxBackoff, TimeSpan leaseDuration)
    {
        if (maxAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts,
                "Delivery attempts must be between one and one hundred.");
        }
        if (maxAge <= TimeSpan.Zero || maxAge > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge,
                "Maximum delivery age must be positive and no greater than 365 days.");
        }
        if (initialBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialBackoff), initialBackoff,
                "Initial delivery backoff must be positive.");
        }
        if (maxBackoff < initialBackoff || maxBackoff > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(maxBackoff), maxBackoff,
                "Maximum delivery backoff must cover the initial backoff and be bounded.");
        }
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration,
                "Delivery lease must be positive and no greater than one day.");
        }

        MaxAttempts = maxAttempts;
        MaxAge = maxAge;
        InitialBackoff = initialBackoff;
        MaxBackoff = maxBackoff;
        LeaseDuration = leaseDuration;
    }
}

/// <summary>A lease for one immutable outbox item read from a committed signal journal.</summary>
public sealed record SignalOutboxLease
{
    public string StreamId { get; }
    public SignalJournalOutboxItem Item { get; }
    public SignalRecord Signal { get; }
    public SignalEvidenceRecord Evidence { get; }
    public string LeaseToken { get; }
    public DateTimeOffset LeaseExpiresAt { get; }
    public int NextAttemptNumber { get; }

    internal SignalOutboxLease(string streamId, SignalJournalOutboxItem item,
        SignalRecord signal, SignalEvidenceRecord evidence, string leaseToken, DateTimeOffset leaseExpiresAt,
        int nextAttemptNumber)
    {
        StreamId = streamId;
        Item = item;
        Signal = signal;
        Evidence = evidence;
        LeaseToken = leaseToken;
        LeaseExpiresAt = leaseExpiresAt;
        NextAttemptNumber = nextAttemptNumber;
    }
}

/// <summary>Durable delivery state and redacted receipts for one committed outbox item.</summary>
public sealed record SignalOutboxDeliverySnapshot
{
    public string StreamId { get; }
    public string OutboxId { get; }
    public string PayloadHash { get; }
    public string SignalId { get; }
    public string SignalRecordHash { get; }
    public SignalOutboxDeliveryStatus Status { get; }
    public int AttemptsStarted { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset NextAttemptAt { get; }
    public DateTimeOffset? LeaseExpiresAt { get; }
    public string? LastReasonCode { get; }
    public string? ExternalReceiptId { get; }
    public ImmutableValueList<SignalDeliveryReceipt> Receipts { get; }

    internal SignalOutboxDeliverySnapshot(string streamId, string outboxId,
        string payloadHash, string signalId, string signalRecordHash,
        SignalOutboxDeliveryStatus status, int attemptsStarted,
        DateTimeOffset createdAt, DateTimeOffset expiresAt,
        DateTimeOffset nextAttemptAt, DateTimeOffset? leaseExpiresAt,
        string? lastReasonCode, string? externalReceiptId,
        IEnumerable<SignalDeliveryReceipt> receipts)
    {
        StreamId = streamId;
        OutboxId = outboxId;
        PayloadHash = payloadHash;
        SignalId = signalId;
        SignalRecordHash = signalRecordHash;
        Status = status;
        AttemptsStarted = attemptsStarted;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        NextAttemptAt = nextAttemptAt;
        LeaseExpiresAt = leaseExpiresAt;
        LastReasonCode = lastReasonCode;
        ExternalReceiptId = externalReceiptId;
        Receipts = new ImmutableValueList<SignalDeliveryReceipt>(receipts);
    }
}
