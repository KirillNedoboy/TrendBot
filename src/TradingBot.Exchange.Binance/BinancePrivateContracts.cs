using System.Text.Json;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

public enum BinanceOrderSide
{
    Buy,
    Sell
}

public enum BinanceOrderType
{
    Market,
    Limit,
    Stop,
    TakeProfit,
    StopMarket,
    TakeProfitMarket,
    TrailingStopMarket
}

public enum BinanceAlgoOrderType
{
    Stop,
    TakeProfit,
    StopMarket,
    TakeProfitMarket,
    TrailingStopMarket
}

public enum BinanceTimeInForce
{
    GoodTillCancel,
    ImmediateOrCancel,
    FillOrKill,
    GoodTillCrossing,
    GoodTillDate
}

#pragma warning disable CA1720 // LONG/SHORT are the Binance wire values.
public enum BinancePositionSide
{
    Both,
    Long,
    Short
}
#pragma warning restore CA1720

public enum BinancePositionMode
{
    OneWay,
    Hedge,
    Unknown
}

public enum BinanceOrderStatus
{
    New,
    PartiallyFilled,
    Filled,
    Canceled,
    Expired,
    ExpiredInMatch,
    Unknown
}

public enum BinanceOrderExecutionType
{
    New,
    Canceled,
    Calculated,
    Expired,
    Trade,
    Amendment,
    Unknown
}

public interface IBinancePrivateRequestSigner
{
    string Sign(string canonicalQuery);
}

/// <summary>
/// Isolated USD-M private adapter surface. Implementations own wire paths and
/// parsing; callers provide signing, transport, and time seams for fixtures.
/// </summary>
public interface IBinancePrivateRestClient
{
    Task<BinancePrivateOrder> PlaceOrderAsync(BinancePrivateOrderRequest request,
        CancellationToken cancellationToken = default);

    Task<BinancePrivateAlgoOrder> PlaceAlgoOrderAsync(BinancePrivateAlgoOrderRequest request,
        CancellationToken cancellationToken = default);

    Task<BinancePrivateOrder> QueryOrderAsync(string symbol, long? orderId,
        string? clientOrderId, CancellationToken cancellationToken = default);

    Task CancelOrderAsync(string symbol, long? orderId, string? clientOrderId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinancePrivateOrder>> GetOpenOrdersAsync(string? symbol,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinancePrivateOrder>> GetAllOrdersAsync(string symbol,
        long? orderId = null, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null,
        int? limit = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinancePrivateTrade>> GetUserTradesAsync(string symbol,
        CancellationToken cancellationToken = default);

    Task<BinancePrivateAlgoOrder> QueryAlgoOrderAsync(string symbol, long? algoId,
        string? clientAlgoId, CancellationToken cancellationToken = default);

    Task CancelAlgoOrderAsync(string symbol, long? algoId, string? clientAlgoId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinancePrivateAlgoOrder>> GetOpenAlgoOrdersAsync(string symbol,
        CancellationToken cancellationToken = default);

    Task<BinancePositionMode> GetPositionModeAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinancePrivatePosition>> GetPositionsAsync(string? symbol,
        CancellationToken cancellationToken = default);

    Task<string> StartUserDataStreamAsync(CancellationToken cancellationToken = default);

    Task KeepAliveUserDataStreamAsync(string listenKey,
        CancellationToken cancellationToken = default);

    Task CloseUserDataStreamAsync(string listenKey,
        CancellationToken cancellationToken = default);
}

public sealed class BinancePrivateProtocolException : InvalidOperationException
{
    public BinancePrivateProtocolException(string message)
        : base(message)
    {
    }
}

public sealed record BinancePrivateRestOptions
{
    public Uri BaseAddress { get; }

    public string ApiKey { get; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public long MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    public long? ReceiveWindowMilliseconds { get; init; }

    public BinancePrivateRestOptions(Uri baseAddress, string apiKey, string? apiSecret = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The private REST base address must be absolute.",
                nameof(baseAddress));
        }

        BaseAddress = baseAddress;
        ApiKey = apiKey;
        _ = apiSecret;
    }
}

public sealed record BinancePrivateOrderRequest
{
    public BinancePrivateOrderRequest(string symbol, BinanceOrderSide side,
        BinanceOrderType type, Quantity quantity, string clientOrderId,
        Price? price = null, BinanceTimeInForce? timeInForce = null,
        BinancePositionSide positionSide = BinancePositionSide.Both,
        bool reduceOnly = false)
    {
        Symbol = RequireSymbol(symbol);
        Side = side;
        Type = type;
        Quantity = quantity ?? throw new ArgumentNullException(nameof(quantity));
        ClientOrderId = RequireText(clientOrderId, nameof(clientOrderId));
        Price = price;
        TimeInForce = timeInForce;
        PositionSide = positionSide;
        ReduceOnly = reduceOnly;
    }

    public string Symbol { get; }
    public BinanceOrderSide Side { get; }
    public BinanceOrderType Type { get; }
    public Quantity Quantity { get; }
    public string ClientOrderId { get; }
    public Price? Price { get; }
    public BinanceTimeInForce? TimeInForce { get; }
    public BinancePositionSide PositionSide { get; }
    public bool ReduceOnly { get; }

    private static string RequireSymbol(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.ToUpperInvariant();
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

public sealed record BinancePrivateAlgoOrderRequest
{
    public BinancePrivateAlgoOrderRequest(string symbol, BinanceOrderSide side,
        BinanceAlgoOrderType type, Quantity quantity, string clientAlgoId,
        Price triggerPrice, BinancePositionSide positionSide = BinancePositionSide.Both,
        bool reduceOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientAlgoId);
        ArgumentNullException.ThrowIfNull(quantity);
        ArgumentNullException.ThrowIfNull(triggerPrice);
        Symbol = symbol.ToUpperInvariant();
        Side = side;
        Type = type;
        Quantity = quantity;
        ClientAlgoId = clientAlgoId;
        TriggerPrice = triggerPrice;
        PositionSide = positionSide;
        ReduceOnly = reduceOnly;
    }

    public string Symbol { get; }
    public BinanceOrderSide Side { get; }
    public BinanceAlgoOrderType Type { get; }
    public Quantity Quantity { get; }
    public string ClientAlgoId { get; }
    public Price TriggerPrice { get; }
    public BinancePositionSide PositionSide { get; }
    public bool ReduceOnly { get; }
}

public sealed record BinancePrivateOrder
{
    public long OrderId { get; init; }
    public string ClientOrderId { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public BinanceOrderSide Side { get; init; }
    public BinancePositionSide PositionSide { get; init; }
    public BinanceOrderType Type { get; init; }
    public BinanceOrderStatus Status { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public BinanceTimeInForce? TimeInForce { get; init; }
    public decimal OriginalQuantity { get; init; }
    public decimal ExecutedQuantity { get; init; }
    public decimal Price { get; init; }
    public decimal AveragePrice { get; init; }
    public decimal? StopPrice { get; init; }
    public bool ReduceOnly { get; init; }
    public bool ClosePosition { get; init; }
    public long OrderTimeMilliseconds { get; init; }
    public long UpdateTimeMilliseconds { get; init; }
    public JsonElement Raw { get; init; }

    public bool RequiresReconciliation => Status == BinanceOrderStatus.Unknown;
}

public sealed record BinancePrivateAlgoOrder
{
    public long AlgoId { get; init; }
    public string ClientAlgoId { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public BinanceOrderSide Side { get; init; }
    public BinancePositionSide PositionSide { get; init; }
    public string OrderTypeText { get; init; } = string.Empty;
    public BinanceAlgoOrderType? OrderType { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal? TriggerPrice { get; init; }
    public bool ReduceOnly { get; init; }
    public bool RequiresReconciliation { get; init; }
    public IReadOnlyDictionary<string, JsonElement> UnknownFields { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public JsonElement Raw { get; init; }
}

public sealed record BinancePrivateTrade
{
    public long TradeId { get; init; }
    public long OrderId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public BinanceOrderSide Side { get; init; }
    public BinancePositionSide PositionSide { get; init; }
    public decimal Price { get; init; }
    public decimal Quantity { get; init; }
    public decimal QuoteQuantity { get; init; }
    public decimal Commission { get; init; }
    public string CommissionAsset { get; init; } = string.Empty;
    public decimal RealizedPnl { get; init; }
    public bool Buyer { get; init; }
    public bool Maker { get; init; }
    public long TradeTimeMilliseconds { get; init; }
}

public sealed record BinancePrivatePosition
{
    public string Symbol { get; init; } = string.Empty;
    public decimal PositionAmount { get; init; }
    public BinancePositionSide PositionSide { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal? BreakEvenPrice { get; init; }
    public decimal? MarkPrice { get; init; }
    public decimal? UnrealizedProfit { get; init; }
    public string? MarginType { get; init; }
    public long UpdateTimeMilliseconds { get; init; }

    public PortfolioPositionSnapshot ToCoreSnapshot(PortfolioId portfolioId,
        string? intentId, UtcTimestamp openedAt)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(openedAt);
        decimal absoluteQuantity = Math.Abs(PositionAmount);
        UtcTimestamp updatedAt = UpdateTimeMilliseconds > 0
            ? new UtcTimestamp(BinanceTimestamp.ToUtc(UpdateTimeMilliseconds))
            : openedAt;
        if (updatedAt.Value < openedAt.Value)
        {
            updatedAt = openedAt;
        }

        Position position = new($"binance:{Symbol}:{PositionSide}", new InstrumentId(Symbol),
            PositionAmount < 0m ? Direction.Short : Direction.Long,
            new Quantity(absoluteQuantity), openedAt, updatedAt,
            EntryPrice > 0m ? new Price(EntryPrice) : null);
        PositionOrigin origin = intentId is null ? PositionOrigin.Unknown : PositionOrigin.Known;
        return new PortfolioPositionSnapshot(portfolioId, position, intentId, origin);
    }
}

public sealed record BinancePrivatePositionUpdate
{
    public string Symbol { get; init; } = string.Empty;
    public decimal PositionAmount { get; init; }
    public BinancePositionSide PositionSide { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal? BreakEvenPrice { get; init; }
    public decimal? UnrealizedProfit { get; init; }
    public string? MarginType { get; init; }
    public Direction Direction => PositionAmount < 0m ? Direction.Short : Direction.Long;
}

public sealed record BinancePrivateFill
{
    public long TradeId { get; init; }
    public string Symbol { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal Price { get; init; }
    public decimal? Commission { get; init; }
    public string? CommissionAsset { get; init; }
    public long TradeTimeMilliseconds { get; init; }
    public long OrderId { get; init; }

    public Fill ToCoreFill(UtcTimestamp receiveTime, string intentId)
    {
        return ToCoreFill(receiveTime, receiveTime, Math.Max(TradeId, 0L), intentId);
    }

    public Fill ToCoreFill(UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, string intentId)
    {
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability cannot precede receive time.",
                nameof(availabilityTime));
        }
        if (Quantity <= 0m || Price <= 0m)
        {
            throw new BinancePrivateProtocolException(
                "A private fill must contain positive quantity and price.");
        }

        UtcTimestamp eventTime = new(BinanceTimestamp.ToUtc(TradeTimeMilliseconds));
        Money? fee = Commission is { } commission && CommissionAsset is { Length: > 0 } asset
            ? new Money(commission, asset)
            : null;
        if (string.IsNullOrWhiteSpace(Symbol))
        {
            throw new BinancePrivateProtocolException(
                "A private fill must contain a symbol before it can enter Core.");
        }

        return new Fill($"binance-trade:{TradeId}", intentId, new InstrumentId(Symbol),
            new Quantity(Quantity), new Price(Price), eventTime, receiveTime, availabilityTime,
            stableSequence, fee, eventTime);
    }
}

public sealed record BinancePrivateStreamMetadata
{
    public BinancePrivateStreamMetadata(string sessionId, long stableSequence,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability cannot precede receive time.",
                nameof(availabilityTime));
        }

        SessionId = sessionId;
        StableSequence = stableSequence;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
    }

    public string SessionId { get; }
    public long StableSequence { get; }
    public UtcTimestamp ReceiveTime { get; }
    public UtcTimestamp AvailabilityTime { get; }
}

public abstract record BinancePrivateStreamEvent
{
    protected BinancePrivateStreamEvent(string eventType, long eventTimeMilliseconds,
        long? transactionTimeMilliseconds, BinancePrivateStreamMetadata metadata,
        string rawJson)
    {
        EventType = eventType;
        EventTimeMilliseconds = eventTimeMilliseconds;
        TransactionTimeMilliseconds = transactionTimeMilliseconds;
        SessionId = metadata.SessionId;
        StableSequence = metadata.StableSequence;
        ReceiveTime = metadata.ReceiveTime;
        AvailabilityTime = metadata.AvailabilityTime;
        RawJson = rawJson;
    }

    public string EventType { get; }
    public long EventTimeMilliseconds { get; }

    public UtcTimestamp EventTime => new(BinanceTimestamp.ToUtc(EventTimeMilliseconds));
    public long? TransactionTimeMilliseconds { get; }

    public UtcTimestamp? TransactionTime => TransactionTimeMilliseconds is long value
        ? new UtcTimestamp(BinanceTimestamp.ToUtc(value))
        : null;
    public string SessionId { get; }
    public long StableSequence { get; }
    public UtcTimestamp ReceiveTime { get; }
    public UtcTimestamp AvailabilityTime { get; }
    public string RawJson { get; }
}

public sealed record BinanceOrderTradeUpdateEvent : BinancePrivateStreamEvent
{
    public BinanceOrderTradeUpdateEvent(BinancePrivateOrder order,
        IReadOnlyList<BinancePrivateFill> fills, long eventTimeMilliseconds,
        long transactionTimeMilliseconds, BinancePrivateStreamMetadata metadata,
        string rawJson)
        : base("ORDER_TRADE_UPDATE", eventTimeMilliseconds, transactionTimeMilliseconds,
            metadata, rawJson)
    {
        Order = order;
        Fills = fills;
    }

    public BinancePrivateOrder Order { get; }
    public IReadOnlyList<BinancePrivateFill> Fills { get; }

    public IReadOnlyList<Fill> ToCoreFills(string intentId)
    {
        return Fills.Select(fill => fill.ToCoreFill(ReceiveTime, AvailabilityTime,
            StableSequence, intentId)).ToArray();
    }
}

public sealed record BinanceAccountUpdateEvent : BinancePrivateStreamEvent
{
    public BinanceAccountUpdateEvent(IReadOnlyList<BinancePrivatePositionUpdate> positions,
        long eventTimeMilliseconds, long transactionTimeMilliseconds,
        BinancePrivateStreamMetadata metadata, string rawJson)
        : base("ACCOUNT_UPDATE", eventTimeMilliseconds, transactionTimeMilliseconds,
            metadata, rawJson)
    {
        Positions = positions;
    }

    public IReadOnlyList<BinancePrivatePositionUpdate> Positions { get; }
    public bool IsDelta => EventType == "ACCOUNT_UPDATE";
}

public sealed record BinanceAlgoUpdateEvent : BinancePrivateStreamEvent
{
    public BinanceAlgoUpdateEvent(JsonElement opaqueOrder, long eventTimeMilliseconds,
        long transactionTimeMilliseconds, BinancePrivateStreamMetadata metadata,
        string rawJson)
        : base("ALGO_UPDATE", eventTimeMilliseconds, transactionTimeMilliseconds,
            metadata, rawJson)
    {
        OpaqueOrder = opaqueOrder;
    }

    public JsonElement OpaqueOrder { get; }
    public bool RequiresReconciliation => EventType == "ALGO_UPDATE";
}

public sealed record BinanceListenKeyExpiredEvent : BinancePrivateStreamEvent
{
    public BinanceListenKeyExpiredEvent(string listenKey, long eventTimeMilliseconds,
        BinancePrivateStreamMetadata metadata, string rawJson)
        : base("listenKeyExpired", eventTimeMilliseconds, null, metadata, rawJson)
    {
        ListenKey = listenKey;
    }

    public string ListenKey { get; }
}

public sealed record BinanceOpaquePrivateStreamEvent : BinancePrivateStreamEvent
{
    public BinanceOpaquePrivateStreamEvent(string eventType, JsonElement payload,
        long eventTimeMilliseconds, long? transactionTimeMilliseconds,
        BinancePrivateStreamMetadata metadata, string rawJson)
        : base(eventType, eventTimeMilliseconds, transactionTimeMilliseconds, metadata, rawJson)
    {
        Payload = payload;
    }

    public JsonElement Payload { get; }
}
