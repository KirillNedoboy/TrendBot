namespace TradingBot.Core;

public enum TradeSide
{
    Buy,
    Sell
}

#pragma warning disable CA1720 // Long and Short are the domain's direction terms.
public enum Direction
{
    Long,
    Short
}
#pragma warning restore CA1720

public enum SetupType
{
    BoundaryRejection,
    CenterRetest
}

public enum SetupState
{
    Observing,
    ContextReady,
    SetupForming,
    SetupConfirmed,
    ConfirmationWindow,
    EntryArmed,
    OrderPending,
    PositionOpen,
    ExitPending,
    Cooldown,
    Terminal,
    Invalidated,
    Suspended
}

public enum DecisionOutcome
{
    Block,
    Allow,
    Shadow
}

public enum RiskDecisionOutcome
{
    Blocked,
    Approved
}

public enum OrderRole
{
    Entry,
    Stop,
    TakeProfit1,
    TakeProfit2,
    Emergency
}

/// <summary>A passive market event with source identity and event time.</summary>
public abstract record MarketEvent
{
    public string EventId { get; }

    public InstrumentId InstrumentId { get; }

    public UtcTimestamp EventTime { get; }

    protected MarketEvent(string eventId, InstrumentId instrumentId, UtcTimestamp eventTime)
    {
        ContractGuard.RequireText(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(eventTime);

        EventId = eventId;
        InstrumentId = instrumentId;
        EventTime = eventTime;
    }
}

public sealed record Trade : MarketEvent
{
    public Price Price { get; }

    public Quantity Quantity { get; }

    public TradeSide Side { get; }

    public Trade(string eventId, InstrumentId instrumentId, UtcTimestamp eventTime,
        Price price, Quantity quantity, TradeSide side)
        : base(eventId, instrumentId, eventTime)
    {
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(quantity);
        ContractGuard.RequireDefined(side, nameof(side));
        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "A trade quantity must be greater than zero.");
        }

        Price = price;
        Quantity = quantity;
        Side = side;
    }
}

public sealed record Bar : MarketEvent
{
    public UtcTimestamp OpenTime { get; }

    public UtcTimestamp CloseTime { get; }

    public Price Open { get; }

    public Price High { get; }

    public Price Low { get; }

    public Price Close { get; }

    public Quantity Volume { get; }

    public Bar(string eventId, InstrumentId instrumentId, UtcTimestamp eventTime,
        UtcTimestamp openTime, UtcTimestamp closeTime, Price open, Price high,
        Price low, Price close, Quantity volume)
        : base(eventId, instrumentId, eventTime)
    {
        ArgumentNullException.ThrowIfNull(openTime);
        ArgumentNullException.ThrowIfNull(closeTime);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(high);
        ArgumentNullException.ThrowIfNull(low);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(volume);
        if (openTime.Value >= closeTime.Value)
        {
            throw new ArgumentException("A bar open time must precede its close time.", nameof(closeTime));
        }

        if (high.Value < open.Value || high.Value < close.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(high), "A bar high must include its open and close prices.");
        }

        if (low.Value > open.Value || low.Value > close.Value || low.Value > high.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(low), "A bar low must include its open and close prices.");
        }

        OpenTime = openTime;
        CloseTime = closeTime;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
    }
}

public sealed record BookLevel
{
    public Price Price { get; }

    public Quantity Quantity { get; }

    public BookLevel(Price price, Quantity quantity)
    {
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(quantity);
        Price = price;
        Quantity = quantity;
    }
}

public sealed record BookDelta : MarketEvent
{
    public ImmutableValueList<BookLevel> Bids { get; }

    public ImmutableValueList<BookLevel> Asks { get; }

    public BookDelta(string eventId, InstrumentId instrumentId, UtcTimestamp eventTime,
        IEnumerable<BookLevel> bids, IEnumerable<BookLevel> asks)
        : base(eventId, instrumentId, eventTime)
    {
        Bids = SnapshotLevels(bids, nameof(bids));
        Asks = SnapshotLevels(asks, nameof(asks));
    }

    private static ImmutableValueList<BookLevel> SnapshotLevels(IEnumerable<BookLevel> levels,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(levels, parameterName);
        ImmutableValueList<BookLevel> snapshot = new(levels);
        if (snapshot.Any(static level => level is null))
        {
            throw new ArgumentException("Book levels cannot contain null values.", parameterName);
        }

        return snapshot;
    }
}

public sealed record FundingUpdate : MarketEvent
{
    public decimal FundingRate { get; }

    public UtcTimestamp? NextFundingTime { get; }

    public FundingUpdate(string eventId, InstrumentId instrumentId, UtcTimestamp eventTime,
        decimal fundingRate, UtcTimestamp? nextFundingTime = null)
        : base(eventId, instrumentId, eventTime)
    {
        FundingRate = fundingRate;
        NextFundingTime = nextFundingTime;
    }
}

public sealed record OIUpdate : MarketEvent
{
    public decimal OpenInterest { get; }

    public OIUpdate(string eventId, InstrumentId instrumentId, UtcTimestamp eventTime,
        decimal openInterest)
        : base(eventId, instrumentId, eventTime)
    {
        if (openInterest < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openInterest), "Open interest cannot be negative.");
        }

        OpenInterest = openInterest;
    }
}

public sealed record Setup
{
    public string SetupId { get; }

    public InstrumentId InstrumentId { get; }

    public SetupType SetupType { get; }

    public Direction Direction { get; }

    public SetupState State { get; }

    public string StructuralAnchorId { get; }

    public UtcTimestamp AnchorTime { get; }

    public Price AnchorPrice { get; }

    public Price AnchorLevel { get; }

    public Price HighSinceAnchor { get; }

    public Price LowSinceAnchor { get; }

    public string ContextVersion { get; }

    public Setup(string setupId, InstrumentId instrumentId, SetupType setupType,
        Direction direction, SetupState state, string structuralAnchorId,
        UtcTimestamp anchorTime, Price anchorPrice, Price anchorLevel,
        Price highSinceAnchor, Price lowSinceAnchor, string contextVersion)
    {
        ContractGuard.RequireText(setupId, nameof(setupId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(setupType, nameof(setupType));
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireText(structuralAnchorId, nameof(structuralAnchorId));
        ArgumentNullException.ThrowIfNull(anchorTime);
        ArgumentNullException.ThrowIfNull(anchorPrice);
        ArgumentNullException.ThrowIfNull(anchorLevel);
        ArgumentNullException.ThrowIfNull(highSinceAnchor);
        ArgumentNullException.ThrowIfNull(lowSinceAnchor);
        ContractGuard.RequireText(contextVersion, nameof(contextVersion));
        if (highSinceAnchor.Value < anchorPrice.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(highSinceAnchor), "The high since anchor must include the anchor price.");
        }

        if (lowSinceAnchor.Value > anchorPrice.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(lowSinceAnchor), "The low since anchor must include the anchor price.");
        }

        SetupId = setupId;
        InstrumentId = instrumentId;
        SetupType = setupType;
        Direction = direction;
        State = state;
        StructuralAnchorId = structuralAnchorId;
        AnchorTime = anchorTime;
        AnchorPrice = anchorPrice;
        AnchorLevel = anchorLevel;
        HighSinceAnchor = highSinceAnchor;
        LowSinceAnchor = lowSinceAnchor;
        ContextVersion = contextVersion;
    }
}

public sealed record Decision
{
    public string DecisionId { get; }

    public string SetupId { get; }

    public DecisionOutcome Outcome { get; }

    public ImmutableValueList<string> Reasons { get; }

    public Decision(string decisionId, string setupId, DecisionOutcome outcome,
        IEnumerable<string> reasons)
    {
        ContractGuard.RequireText(decisionId, nameof(decisionId));
        ContractGuard.RequireText(setupId, nameof(setupId));
        ContractGuard.RequireDefined(outcome, nameof(outcome));
        DecisionId = decisionId;
        SetupId = setupId;
        Outcome = outcome;
        Reasons = ContractGuard.RequireReasons(reasons, nameof(reasons));
    }
}

public sealed record RiskDecision
{
    public string RiskDecisionId { get; }

    public string DecisionId { get; }

    public RiskDecisionOutcome Outcome { get; }

    public ImmutableValueList<string> Reasons { get; }

    public RiskDecision(string riskDecisionId, string decisionId, RiskDecisionOutcome outcome,
        IEnumerable<string> reasons)
    {
        ContractGuard.RequireText(riskDecisionId, nameof(riskDecisionId));
        ContractGuard.RequireText(decisionId, nameof(decisionId));
        ContractGuard.RequireDefined(outcome, nameof(outcome));
        RiskDecisionId = riskDecisionId;
        DecisionId = decisionId;
        Outcome = outcome;
        Reasons = ContractGuard.RequireReasons(reasons, nameof(reasons));
    }
}

public sealed record OrderIntent
{
    public string IntentId { get; }

    public string StrategyRunId { get; }

    public string SetupId { get; }

    public string ClientOrderId { get; }

    public InstrumentId InstrumentId { get; }

    public Direction Direction { get; }

    public OrderRole Role { get; }

    public int Attempt { get; }

    public Quantity Quantity { get; }

    public Price? LimitPrice { get; }

    public UtcTimestamp? ExpiresAt { get; }

    public bool ReduceOnly { get; }

    public OrderIntent(string intentId, string strategyRunId, string setupId,
        string clientOrderId, InstrumentId instrumentId, Direction direction,
        OrderRole role, int attempt, Quantity quantity, Price? limitPrice = null,
        UtcTimestamp? expiresAt = null, bool reduceOnly = false)
    {
        ContractGuard.RequireText(intentId, nameof(intentId));
        ContractGuard.RequireText(strategyRunId, nameof(strategyRunId));
        ContractGuard.RequireText(setupId, nameof(setupId));
        ContractGuard.RequireText(clientOrderId, nameof(clientOrderId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireDefined(role, nameof(role));
        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "An attempt must be positive.");
        }

        ArgumentNullException.ThrowIfNull(quantity);
        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "An order quantity must be greater than zero.");
        }

        IntentId = intentId;
        StrategyRunId = strategyRunId;
        SetupId = setupId;
        ClientOrderId = clientOrderId;
        InstrumentId = instrumentId;
        Direction = direction;
        Role = role;
        Attempt = attempt;
        Quantity = quantity;
        LimitPrice = limitPrice;
        ExpiresAt = expiresAt;
        ReduceOnly = reduceOnly;
    }
}

public sealed record Position
{
    public string PositionId { get; }

    public InstrumentId InstrumentId { get; }

    public Direction Direction { get; }

    public Quantity Quantity { get; }

    public Price? AverageEntryPrice { get; }

    public UtcTimestamp OpenedAt { get; }

    public UtcTimestamp UpdatedAt { get; }

    public Money? UnrealizedPnl { get; }

    public Position(string positionId, InstrumentId instrumentId, Direction direction,
        Quantity quantity, UtcTimestamp openedAt, UtcTimestamp updatedAt,
        Price? averageEntryPrice = null, Money? unrealizedPnl = null)
    {
        ContractGuard.RequireText(positionId, nameof(positionId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(direction, nameof(direction));
        ArgumentNullException.ThrowIfNull(quantity);
        ArgumentNullException.ThrowIfNull(openedAt);
        ArgumentNullException.ThrowIfNull(updatedAt);
        if (updatedAt.Value < openedAt.Value)
        {
            throw new ArgumentException("A position update cannot precede its opening time.", nameof(updatedAt));
        }

        PositionId = positionId;
        InstrumentId = instrumentId;
        Direction = direction;
        Quantity = quantity;
        AverageEntryPrice = averageEntryPrice;
        OpenedAt = openedAt;
        UpdatedAt = updatedAt;
        UnrealizedPnl = unrealizedPnl;
    }
}

/// <summary>Describes the diagnostic relationship between two market events.</summary>
public enum MarketEventRelation
{
    Duplicate,
    IdentityConflict,
    EarlierEventTime,
    LaterEventTime,
    SameEventTime,
    DifferentInstrument
}

/// <summary>Compares event identity and event time without assigning processing order.</summary>
public static class MarketEventComparison
{
    /// <summary>Compares a previous event with a candidate event.</summary>
    public static MarketEventRelation Compare(MarketEvent previous, MarketEvent candidate)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);

        if (previous.InstrumentId != candidate.InstrumentId)
        {
            return MarketEventRelation.DifferentInstrument;
        }

        if (previous.GetType() == candidate.GetType()
            && StringComparer.Ordinal.Equals(previous.EventId, candidate.EventId))
        {
            return previous.Equals(candidate)
                ? MarketEventRelation.Duplicate
                : MarketEventRelation.IdentityConflict;
        }

        int timeComparison = candidate.EventTime.Value.CompareTo(previous.EventTime.Value);
        if (timeComparison < 0)
        {
            return MarketEventRelation.EarlierEventTime;
        }

        if (timeComparison > 0)
        {
            return MarketEventRelation.LaterEventTime;
        }

        return MarketEventRelation.SameEventTime;
    }
}
