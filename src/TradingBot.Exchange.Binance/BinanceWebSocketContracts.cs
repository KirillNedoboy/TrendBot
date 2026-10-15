using System.Collections.ObjectModel;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

public enum BinanceWebSocketEventKind
{
    AggregateTrade,
    Kline,
    Ticker,
    MarkPrice,
    Liquidation,
    Depth
}

public enum BinanceStreamDiagnosticKind
{
    Duplicate,
    IdentityConflict,
    Late,
    Stale,
    Disconnect,
    Gap,
    ParseError,
    FrameTooLarge,
    Reconnect,
    BufferOverflow,
    SnapshotRejected,
    Recovery
}

/// <summary>Bounds and timing policy for the public market stream.</summary>
public sealed record BinanceWebSocketOptions
{
    public Uri BaseAddress { get; init; } = new("wss://fstream.binance.com/market");

    public bool UseCombinedStreams { get; init; }

    public bool AutoReconnect { get; init; } = true;

    public int MaxReconnectAttempts { get; init; } = 3;

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxFrameBytes { get; init; } = 1 * 1024 * 1024;

    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;

    public int DeduplicationCapacity { get; init; } = 10_000;
}

/// <summary>One Binance stream subscription expressed in the live SUBSCRIBE shape.</summary>
public sealed record BinanceSubscription
{
    public BinanceSubscription(string symbol, BinanceWebSocketEventKind kind, string? interval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (kind == BinanceWebSocketEventKind.Kline)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(interval);
        }
        else if (kind == BinanceWebSocketEventKind.Depth)
        {
            if (interval is not null && interval is not ("100ms" or "1s"))
            {
                throw new ArgumentException(
                    "Depth update speed must be 100ms or 1s.", nameof(interval));
            }
        }
        else if (interval is not null)
        {
            throw new ArgumentException("Only kline subscriptions accept an interval.", nameof(interval));
        }

        Symbol = symbol.ToUpperInvariant();
        Kind = kind;
        Interval = interval;
        StreamName = BuildStreamName(Symbol, kind, interval);
    }

    public string Symbol { get; }

    public BinanceWebSocketEventKind Kind { get; }

    public string? Interval { get; }

    public string StreamName { get; }

    public static BinanceSubscription AggregateTrade(string symbol) =>
        new(symbol, BinanceWebSocketEventKind.AggregateTrade);

    public static BinanceSubscription Kline(string symbol, string interval) =>
        new(symbol, BinanceWebSocketEventKind.Kline, interval);

    public static BinanceSubscription Ticker(string symbol) =>
        new(symbol, BinanceWebSocketEventKind.Ticker);

    public static BinanceSubscription MarkPrice(string symbol) =>
        new(symbol, BinanceWebSocketEventKind.MarkPrice);

    public static BinanceSubscription Liquidation(string symbol) =>
        new(symbol, BinanceWebSocketEventKind.Liquidation);

    public static BinanceSubscription Depth(string symbol, string? updateSpeed = null) =>
        new(symbol, BinanceWebSocketEventKind.Depth, updateSpeed);

    public static BinanceSubscription Depth100Milliseconds(string symbol) =>
        Depth(symbol, "100ms");

    private static string BuildStreamName(string symbol, BinanceWebSocketEventKind kind,
        string? interval)
    {
        string lowerSymbol = symbol.ToLowerInvariant();
        return kind switch
        {
            BinanceWebSocketEventKind.AggregateTrade => $"{lowerSymbol}@aggTrade",
            BinanceWebSocketEventKind.Kline => $"{lowerSymbol}@kline_{interval}",
            BinanceWebSocketEventKind.Ticker => $"{lowerSymbol}@ticker",
            BinanceWebSocketEventKind.MarkPrice => $"{lowerSymbol}@markPrice@1s",
            BinanceWebSocketEventKind.Liquidation => $"{lowerSymbol}@forceOrder",
            BinanceWebSocketEventKind.Depth => interval is null
                ? $"{lowerSymbol}@depth"
                : $"{lowerSymbol}@depth@{interval}",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}

public sealed record BinanceWebSocketReceiveResult(WebSocketMessageType MessageType,
    byte[] Payload, bool EndOfMessage)
{
    public BinanceWebSocketReceiveResult(WebSocketMessageType messageType, ReadOnlyMemory<byte> payload,
        bool endOfMessage)
        : this(messageType, payload.ToArray(), endOfMessage)
    {
    }
}

/// <summary>Transport seam used by the stream client and fixture tests.</summary>
public interface IBinanceWebSocketTransport : IDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken);

    Task<BinanceWebSocketReceiveResult> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription,
        CancellationToken cancellationToken);

    void Abort();
}

/// <summary>Creates an independent transport for each WebSocket session.</summary>
public interface IBinanceWebSocketTransportFactory
{
    IBinanceWebSocketTransport Create();
}

/// <summary>Delegate-backed transport factory used by production and tests.</summary>
public sealed class DelegateBinanceWebSocketTransportFactory : IBinanceWebSocketTransportFactory
{
    private readonly Func<IBinanceWebSocketTransport> _factory;

    public DelegateBinanceWebSocketTransportFactory(Func<IBinanceWebSocketTransport> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public IBinanceWebSocketTransport Create()
    {
        return _factory() ?? throw new InvalidOperationException(
            "The WebSocket transport factory returned null.");
    }
}

/// <summary>Default factory that creates a fresh BCL ClientWebSocket transport.</summary>
public sealed class ClientWebSocketTransportFactory : IBinanceWebSocketTransportFactory
{
    private readonly TimeSpan _keepAliveInterval;

    public ClientWebSocketTransportFactory(TimeSpan? keepAliveInterval = null)
    {
        _keepAliveInterval = keepAliveInterval ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_keepAliveInterval, TimeSpan.Zero);
    }

    public IBinanceWebSocketTransport Create() =>
        new ClientWebSocketTransport(_keepAliveInterval);
}

public abstract record BinanceWebSocketEvent
{
    protected BinanceWebSocketEvent(BinanceWebSocketEventKind kind, string eventId, string symbol,
        long eventTimeMilliseconds, string streamName, long? exchangeTimeMilliseconds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eventTimeMilliseconds);

        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamName);
        Kind = kind;
        EventId = eventId;
        Symbol = symbol;
        EventTimeMilliseconds = eventTimeMilliseconds;
        StreamName = streamName;
        ExchangeTimeMilliseconds = exchangeTimeMilliseconds;
    }

    public BinanceWebSocketEventKind Kind { get; }

    public string EventId { get; }

    public string Symbol { get; }

    public long EventTimeMilliseconds { get; }

    public long? ExchangeTimeMilliseconds { get; }

    public string StreamName { get; }

    public UtcTimestamp EventTime => new(BinanceTimestamp.ToUtc(EventTimeMilliseconds));

    public UtcTimestamp? ExchangeTime => ExchangeTimeMilliseconds is long milliseconds
        ? new UtcTimestamp(BinanceTimestamp.ToUtc(milliseconds)) : null;

    public abstract MarketEvent? ToCoreEvent();
}

public sealed record BinanceAggregateTradeEvent : BinanceWebSocketEvent
{
    public BinanceAggregateTradeEvent(string symbol, long eventTimeMilliseconds,
        long aggregateTradeId, decimal price, decimal quantity, long firstTradeId,
        long lastTradeId, long tradeTimeMilliseconds, bool isBuyerMaker, string streamName)
        : base(BinanceWebSocketEventKind.AggregateTrade,
            $"aggTrade:{symbol}:{aggregateTradeId}", symbol, eventTimeMilliseconds, streamName,
            tradeTimeMilliseconds)
    {
        AggregateTradeId = aggregateTradeId;
        Price = price;
        Quantity = quantity;
        FirstTradeId = firstTradeId;
        LastTradeId = lastTradeId;
        TradeTimeMilliseconds = tradeTimeMilliseconds;
        IsBuyerMaker = isBuyerMaker;
    }

    public long AggregateTradeId { get; }

    public decimal Price { get; }

    public decimal Quantity { get; }

    public long FirstTradeId { get; }

    public long LastTradeId { get; }

    public long TradeTimeMilliseconds { get; }

    public bool IsBuyerMaker { get; }

    public override Trade ToCoreEvent()
    {
        return new Trade(EventId, new InstrumentId(Symbol),
            new UtcTimestamp(BinanceTimestamp.ToUtc(TradeTimeMilliseconds)), new Price(Price),
            new Quantity(Quantity), IsBuyerMaker ? TradeSide.Sell : TradeSide.Buy);
    }
}

public sealed record BinanceKlineEvent : BinanceWebSocketEvent
{
    public BinanceKlineEvent(string symbol, long eventTimeMilliseconds, BinanceKline kline,
        string streamName)
        : base(BinanceWebSocketEventKind.Kline, $"kline:{symbol}:{kline.OpenTimeMilliseconds}",
            symbol, eventTimeMilliseconds, streamName, kline.CloseTimeMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(kline);
        Kline = kline;
    }

    public BinanceKline Kline { get; }

    public bool IsClosed => Kline.IsClosed;

    public override Bar? ToCoreEvent()
    {
        return IsClosed ? Kline.ToCoreBar(new InstrumentId(Symbol)) : null;
    }
}

public sealed record BinanceTickerEvent : BinanceWebSocketEvent
{
    public BinanceTickerEvent(string symbol, long eventTimeMilliseconds, decimal priceChange,
        decimal priceChangePercent, decimal weightedAveragePrice, decimal lastPrice,
        decimal lastQuantity, decimal openPrice, decimal highPrice, decimal lowPrice,
        decimal volume, decimal quoteVolume, long openTimeMilliseconds,
        long closeTimeMilliseconds, long firstTradeId, long lastTradeId, long tradeCount,
        string streamName)
        : base(BinanceWebSocketEventKind.Ticker, $"ticker:{symbol}:{eventTimeMilliseconds}",
            symbol, eventTimeMilliseconds, streamName)
    {
        PriceChange = priceChange;
        PriceChangePercent = priceChangePercent;
        WeightedAveragePrice = weightedAveragePrice;
        LastPrice = lastPrice;
        LastQuantity = lastQuantity;
        OpenPrice = openPrice;
        HighPrice = highPrice;
        LowPrice = lowPrice;
        Volume = volume;
        QuoteVolume = quoteVolume;
        OpenTimeMilliseconds = openTimeMilliseconds;
        CloseTimeMilliseconds = closeTimeMilliseconds;
        FirstTradeId = firstTradeId;
        LastTradeId = lastTradeId;
        TradeCount = tradeCount;
    }

    public decimal PriceChange { get; }

    public decimal PriceChangePercent { get; }

    public decimal WeightedAveragePrice { get; }

    public decimal LastPrice { get; }

    public decimal LastQuantity { get; }

    public decimal OpenPrice { get; }

    public decimal HighPrice { get; }

    public decimal LowPrice { get; }

    public decimal Volume { get; }

    public decimal QuoteVolume { get; }

    public long OpenTimeMilliseconds { get; }

    public long CloseTimeMilliseconds { get; }

    public long FirstTradeId { get; }

    public long LastTradeId { get; }

    public long TradeCount { get; }

    public override MarketEvent? ToCoreEvent() => null;
}

public sealed record BinanceMarkPriceEvent : BinanceWebSocketEvent
{
    public BinanceMarkPriceEvent(string symbol, long eventTimeMilliseconds, decimal markPrice,
        decimal indexPrice, decimal fundingRate, long nextFundingTimeMilliseconds,
        string streamName)
        : base(BinanceWebSocketEventKind.MarkPrice,
            $"markPrice:{symbol}:{eventTimeMilliseconds}", symbol, eventTimeMilliseconds, streamName)
    {
        MarkPrice = markPrice;
        IndexPrice = indexPrice;
        FundingRate = fundingRate;
        NextFundingTimeMilliseconds = nextFundingTimeMilliseconds;
    }

    public decimal MarkPrice { get; }

    public decimal IndexPrice { get; }

    public decimal FundingRate { get; }

    public long NextFundingTimeMilliseconds { get; }

    public override FundingUpdate ToCoreEvent()
    {
        return new FundingUpdate(EventId, new InstrumentId(Symbol), EventTime, FundingRate,
            new UtcTimestamp(BinanceTimestamp.ToUtc(NextFundingTimeMilliseconds)));
    }
}

public sealed record BinanceLiquidationEvent : BinanceWebSocketEvent
{
    public BinanceLiquidationEvent(string symbol, long eventTimeMilliseconds, long orderId,
        TradeSide side, decimal originalQuantity, decimal price, decimal averagePrice,
        decimal lastFilledQuantity, decimal accumulatedQuantity, string orderStatus,
        long orderTimeMilliseconds, string streamName)
        : base(BinanceWebSocketEventKind.Liquidation,
            $"liquidation:{symbol}:{orderId}", symbol, eventTimeMilliseconds, streamName,
            orderTimeMilliseconds)
    {
        OrderId = orderId;
        Side = side;
        OriginalQuantity = originalQuantity;
        Price = price;
        AveragePrice = averagePrice;
        LastFilledQuantity = lastFilledQuantity;
        AccumulatedQuantity = accumulatedQuantity;
        OrderStatus = orderStatus;
        OrderTimeMilliseconds = orderTimeMilliseconds;
    }

    public long OrderId { get; }

    public TradeSide Side { get; }

    public decimal OriginalQuantity { get; }

    public decimal Price { get; }

    public decimal AveragePrice { get; }

    public decimal LastFilledQuantity { get; }

    public decimal AccumulatedQuantity { get; }

    public string OrderStatus { get; }

    public long OrderTimeMilliseconds { get; }

    public override MarketEvent? ToCoreEvent() => null;
}

/// <summary>Binance USD-M diff-depth update used to recover a local order book.</summary>
public sealed record BinanceDepthUpdateEvent : BinanceWebSocketEvent
{
    public BinanceDepthUpdateEvent(string symbol, long eventTimeMilliseconds,
        long firstUpdateId, long finalUpdateId, long? previousUpdateId,
        IEnumerable<BinanceDepthLevel> bids, IEnumerable<BinanceDepthLevel> asks,
        string streamName, long? transactionTimeMilliseconds = null)
        : base(BinanceWebSocketEventKind.Depth,
            $"depth:{symbol}:{firstUpdateId}:{finalUpdateId}", symbol, eventTimeMilliseconds,
            streamName, transactionTimeMilliseconds ?? eventTimeMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstUpdateId);
        ArgumentOutOfRangeException.ThrowIfLessThan(finalUpdateId, firstUpdateId);
        if (previousUpdateId is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previousUpdateId));
        }

        FirstUpdateId = firstUpdateId;
        FinalUpdateId = finalUpdateId;
        PreviousUpdateId = previousUpdateId;
        Bids = Snapshot(bids, nameof(bids));
        Asks = Snapshot(asks, nameof(asks));
        TransactionTimeMilliseconds = transactionTimeMilliseconds ?? eventTimeMilliseconds;
    }

    public long FirstUpdateId { get; }

    public long FinalUpdateId { get; }

    public long? PreviousUpdateId { get; }

    public long TransactionTimeMilliseconds { get; }

    public IReadOnlyList<BinanceDepthLevel> Bids { get; }

    public IReadOnlyList<BinanceDepthLevel> Asks { get; }

    public override BookDelta ToCoreEvent()
    {
        return new BookDelta(EventId, new InstrumentId(Symbol), EventTime,
            Bids.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))),
            Asks.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))));
    }

    private static ReadOnlyCollection<BinanceDepthLevel> Snapshot(
        IEnumerable<BinanceDepthLevel> levels, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(levels, parameterName);
        BinanceDepthLevel[] snapshot = levels.ToArray();
        if (snapshot.Any(static level => level is null))
        {
            throw new ArgumentException("Depth levels cannot contain null values.", parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }
}

public sealed record BinanceStreamEnvelope
{
    public BinanceStreamEnvelope(string sessionId, long stableSequence,
        BinanceWebSocketEvent payload, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        UtcTimestamp? exchangeTime, string rawJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability time cannot precede receive time.",
                nameof(availabilityTime));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);
        SessionId = sessionId;
        StableSequence = stableSequence;
        Payload = payload;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        ExchangeTime = exchangeTime;
        RawJson = rawJson;
    }

    public string SessionId { get; }

    public long StableSequence { get; }

    public BinanceWebSocketEvent Payload { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public UtcTimestamp? ExchangeTime { get; }

    public string RawJson { get; }

    public MarketEvent? ToCoreEvent() => Payload.ToCoreEvent();

    public CausalMarketEvent? ToCoreCausalEvent()
    {
        MarketEvent? marketEvent = ToCoreEvent();
        return marketEvent is null ? null : new CausalMarketEvent(marketEvent, ReceiveTime,
            AvailabilityTime, StableSequence, ExchangeTime);
    }
}

public sealed record BinanceStreamDiagnostic
{
    public BinanceStreamDiagnostic(BinanceStreamDiagnosticKind kind, string sessionId,
        string? streamName, string? eventId, string message, long stableSequence,
        UtcTimestamp observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(stableSequence, -1);
        ArgumentNullException.ThrowIfNull(observedAt);
        Kind = kind;
        SessionId = sessionId;
        StreamName = streamName;
        EventId = eventId;
        Message = message;
        StableSequence = stableSequence;
        ObservedAt = observedAt;
    }

    public BinanceStreamDiagnosticKind Kind { get; }

    public string SessionId { get; }

    public string? StreamName { get; }

    public string? EventId { get; }

    public string Message { get; }

    public long StableSequence { get; }

    public UtcTimestamp ObservedAt { get; }
}
