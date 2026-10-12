using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Captures one durable operational fill observation and its causal metadata.
/// </summary>
public sealed record Fill
{
    public string FillId { get; }

    public string IntentId { get; }

    public InstrumentId InstrumentId { get; }

    public Quantity Quantity { get; }

    public Price Price { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public Money? Fee { get; }

    public UtcTimestamp? ExchangeTime { get; }

    [JsonConstructor]
    public Fill(string fillId, string intentId, InstrumentId instrumentId, Quantity quantity,
        Price price, UtcTimestamp eventTime, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, long stableSequence, Money? fee = null,
        UtcTimestamp? exchangeTime = null)
    {
        ContractGuard.RequireText(fillId, nameof(fillId));
        ContractGuard.RequireText(intentId, nameof(intentId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(quantity);
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity.Value,
                "A fill quantity must be greater than zero.");
        }

        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException(
                "Availability time cannot precede receive time.", nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Stable sequence cannot be negative.");
        }

        FillId = fillId;
        IntentId = intentId;
        InstrumentId = instrumentId;
        Quantity = quantity;
        Price = price;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        Fee = fee;
        ExchangeTime = exchangeTime;
    }
}

/// <summary>Describes whether a portfolio position belongs to a known intent.</summary>
public enum PositionOrigin
{
    Known,
    Manual,
    Unknown
}

/// <summary>
/// Associates an immutable Core <see cref="Position"/> snapshot with a portfolio.
/// Manual and unknown snapshots intentionally have no intent identity.
/// </summary>
public sealed record PortfolioPositionSnapshot
{
    public PortfolioId PortfolioId { get; }

    public Position Position { get; }

    public string? IntentId { get; }

    public PositionOrigin Origin { get; }

    [JsonConstructor]
    public PortfolioPositionSnapshot(PortfolioId portfolioId, Position position, string? intentId,
        PositionOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(position);
        ContractGuard.RequireDefined(origin, nameof(origin));
        if (intentId is not null)
        {
            ContractGuard.RequireText(intentId, nameof(intentId));
        }

        if (origin == PositionOrigin.Known && intentId is null)
        {
            throw new ArgumentException(
                "A known position snapshot must identify its order intent.", nameof(intentId));
        }

        if (origin is PositionOrigin.Manual or PositionOrigin.Unknown && intentId is not null)
        {
            throw new ArgumentException(
                "Manual and unknown position snapshots cannot identify an order intent.",
                nameof(intentId));
        }

        PortfolioId = portfolioId;
        Position = position;
        IntentId = intentId;
        Origin = origin;
    }
}

/// <summary>Describes the durable reconciliation state of a portfolio.</summary>
public enum ReconciliationStatus
{
    Unreconciled,
    Acknowledged
}

/// <summary>
/// Captures one monotonic portfolio revision and the caller's known state at
/// that revision. New observations always persist it as unreconciled.
/// </summary>
public sealed record ReconciliationCheckpoint
{
    public PortfolioId PortfolioId { get; }

    public long Revision { get; }

    public ImmutableValueList<string> OpenIntentIds { get; }

    public ImmutableValueList<string> OpenPositionIds { get; }

    public ImmutableValueList<string> UnknownMarkers { get; }

    public ImmutableValueList<string> ManualPositionIds { get; }

    public ReconciliationStatus Status { get; }

    [JsonConstructor]
    public ReconciliationCheckpoint(PortfolioId portfolioId, long revision,
        ImmutableValueList<string> openIntentIds, ImmutableValueList<string> openPositionIds,
        ImmutableValueList<string> unknownMarkers, ImmutableValueList<string> manualPositionIds,
        ReconciliationStatus status = ReconciliationStatus.Unreconciled)
        : this(portfolioId, revision, (IEnumerable<string>)openIntentIds,
            (IEnumerable<string>)openPositionIds, (IEnumerable<string>)unknownMarkers,
            (IEnumerable<string>)manualPositionIds, status)
    {
    }

    public ReconciliationCheckpoint(PortfolioId portfolioId, long revision,
        IEnumerable<string> openIntentIds, IEnumerable<string> openPositionIds,
        IEnumerable<string> unknownMarkers, IEnumerable<string> manualPositionIds,
        ReconciliationStatus status = ReconciliationStatus.Unreconciled)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision,
                "A reconciliation revision cannot be negative.");
        }

        ContractGuard.RequireDefined(status, nameof(status));
        PortfolioId = portfolioId;
        Revision = revision;
        OpenIntentIds = SnapshotIdentifiers(openIntentIds, nameof(openIntentIds));
        OpenPositionIds = SnapshotIdentifiers(openPositionIds, nameof(openPositionIds));
        UnknownMarkers = SnapshotIdentifiers(unknownMarkers, nameof(unknownMarkers));
        ManualPositionIds = SnapshotIdentifiers(manualPositionIds, nameof(manualPositionIds));
        Status = status;
    }

    private static ImmutableValueList<string> SnapshotIdentifiers(IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        string[] snapshot = values.ToArray();
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (string value in snapshot)
        {
            ContractGuard.RequireText(value, parameterName);
            if (!identities.Add(value))
            {
                throw new ArgumentException("A reconciliation identifier cannot be repeated.",
                    parameterName);
            }
        }

        return new ImmutableValueList<string>(snapshot);
    }
}

/// <summary>Describes whether one operational observation changed durable state.</summary>
public enum PortfolioObservationResult
{
    Recorded,
    AlreadyRecorded
}
