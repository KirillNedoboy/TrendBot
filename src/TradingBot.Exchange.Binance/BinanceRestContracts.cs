using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

/// <summary>Configuration for the public USD-M REST adapter.</summary>
public sealed record BinanceRestOptions
{
    public Uri BaseAddress { get; init; } = new("https://fapi.binance.com");

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public long MaxResponseBytes { get; init; } = 4 * 1024 * 1024;

    public BinanceRestOptions()
    {
    }

    public BinanceRestOptions(Uri baseAddress)
    {
        BaseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
    }
}

/// <summary>Binance request failure, including the exchange error code when supplied.</summary>
public sealed class BinanceApiException : HttpRequestException
{
    public new HttpStatusCode StatusCode { get; }

    public int? Code { get; }

    public string ResponseBody { get; }

    public Uri? RequestUri { get; }

    public TimeSpan? RetryAfter { get; }

    internal BinanceApiException(HttpStatusCode statusCode, int? code, string message,
        string responseBody, Uri? requestUri, TimeSpan? retryAfter)
        : base(message, null, statusCode)
    {
        StatusCode = statusCode;
        Code = code;
        ResponseBody = responseBody;
        RequestUri = requestUri;
        RetryAfter = retryAfter;
    }
}

public sealed record BinanceRateLimit
{
    [JsonConstructor]
    public BinanceRateLimit(string rateLimitType, string interval, int intervalNum, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rateLimitType);
        ArgumentException.ThrowIfNullOrWhiteSpace(interval);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        RateLimitType = rateLimitType;
        Interval = interval;
        IntervalNum = intervalNum;
        Limit = limit;
    }

    public string RateLimitType { get; }

    public string Interval { get; }

    public int IntervalNum { get; }

    public int Limit { get; }
}

public sealed record BinanceAssetInfo
{
    [JsonConstructor]
    public BinanceAssetInfo(string asset, bool marginAvailable, decimal? autoAssetExchange = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset);
        Asset = asset;
        MarginAvailable = marginAvailable;
        AutoAssetExchange = autoAssetExchange;
    }

    public string Asset { get; }

    public bool MarginAvailable { get; }

    public decimal? AutoAssetExchange { get; }
}

public sealed record BinanceSymbolFilter
{
    [JsonConstructor]
    public BinanceSymbolFilter(string filterType, decimal? minPrice = null,
        decimal? maxPrice = null, decimal? tickSize = null, decimal? minQuantity = null,
        decimal? maxQuantity = null, decimal? stepSize = null, decimal? multiplierUp = null,
        decimal? multiplierDown = null, int? limit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filterType);
        FilterType = filterType;
        MinPrice = minPrice;
        MaxPrice = maxPrice;
        TickSize = tickSize;
        MinQuantity = minQuantity;
        MaxQuantity = maxQuantity;
        StepSize = stepSize;
        MultiplierUp = multiplierUp;
        MultiplierDown = multiplierDown;
        Limit = limit;
    }

    public string FilterType { get; }

    [JsonPropertyName("minPrice")]
    public decimal? MinPrice { get; }

    [JsonPropertyName("maxPrice")]
    public decimal? MaxPrice { get; }

    [JsonPropertyName("tickSize")]
    public decimal? TickSize { get; }

    [JsonPropertyName("minQty")]
    public decimal? MinQuantity { get; }

    [JsonPropertyName("maxQty")]
    public decimal? MaxQuantity { get; }

    [JsonPropertyName("stepSize")]
    public decimal? StepSize { get; }

    public decimal? MultiplierUp { get; }

    public decimal? MultiplierDown { get; }

    public int? Limit { get; }
}

public sealed record BinanceSymbolInfo
{
    [JsonConstructor]
    public BinanceSymbolInfo(string symbol, string pair, string contractType,
        long deliveryDateMilliseconds, long onboardDateMilliseconds, string status, decimal maintMarginPercent,
        decimal requiredMarginPercent, string baseAsset, string quoteAsset,
        string marginAsset, int pricePrecision, int quantityPrecision,
        int baseAssetPrecision, int quotePrecision,
        IReadOnlyList<BinanceSymbolFilter>? filters = null,
        IReadOnlyList<string>? orderTypes = null, IReadOnlyList<string>? timeInForce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(pair);
        ArgumentException.ThrowIfNullOrWhiteSpace(contractType);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(quoteAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(marginAsset);
        if (pricePrecision < 0 || quantityPrecision < 0 || baseAssetPrecision < 0
            || quotePrecision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pricePrecision));
        }

        Symbol = symbol;
        Pair = pair;
        ContractType = contractType;
        DeliveryDateMilliseconds = deliveryDateMilliseconds;
        OnboardDateMilliseconds = onboardDateMilliseconds;
        Status = status;
        MaintMarginPercent = maintMarginPercent;
        RequiredMarginPercent = requiredMarginPercent;
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
        MarginAsset = marginAsset;
        PricePrecision = pricePrecision;
        QuantityPrecision = quantityPrecision;
        BaseAssetPrecision = baseAssetPrecision;
        QuotePrecision = quotePrecision;
        Filters = Snapshot(filters);
        OrderTypes = Snapshot(orderTypes);
        TimeInForce = Snapshot(timeInForce);
    }

    public string Symbol { get; }

    public string Pair { get; }

    public string ContractType { get; }

    [JsonPropertyName("deliveryDate")]
    public long DeliveryDateMilliseconds { get; }

    [JsonPropertyName("onboardDate")]
    public long OnboardDateMilliseconds { get; }

    public string Status { get; }

    public decimal MaintMarginPercent { get; }

    public decimal RequiredMarginPercent { get; }

    public string BaseAsset { get; }

    public string QuoteAsset { get; }

    public string MarginAsset { get; }

    public int PricePrecision { get; }

    public int QuantityPrecision { get; }

    public int BaseAssetPrecision { get; }

    public int QuotePrecision { get; }

    public IReadOnlyList<BinanceSymbolFilter> Filters { get; }

    public IReadOnlyList<string> OrderTypes { get; }

    public IReadOnlyList<string> TimeInForce { get; }

    private static T[] Snapshot<T>(IEnumerable<T>? values)
    {
        return values is null ? Array.Empty<T>() : values.ToArray();
    }
}

public sealed record BinanceExchangeInfo
{
    [JsonConstructor]
    public BinanceExchangeInfo(string timezone, long serverTimeMilliseconds,
        IReadOnlyList<BinanceRateLimit>? rateLimits = null,
        IReadOnlyList<BinanceAssetInfo>? assets = null,
        IReadOnlyList<BinanceSymbolInfo>? symbols = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timezone);
        Timezone = timezone;
        ServerTimeMilliseconds = serverTimeMilliseconds;
        RateLimits = Snapshot(rateLimits);
        Assets = Snapshot(assets);
        Symbols = Snapshot(symbols);
    }

    public string Timezone { get; }

    [JsonPropertyName("serverTime")]
    public long ServerTimeMilliseconds { get; }

    public IReadOnlyList<BinanceRateLimit> RateLimits { get; }

    public IReadOnlyList<BinanceAssetInfo> Assets { get; }

    public IReadOnlyList<BinanceSymbolInfo> Symbols { get; }

    private static T[] Snapshot<T>(IEnumerable<T>? values)
    {
        return values is null ? Array.Empty<T>() : values.ToArray();
    }
}

public sealed record BinanceServerTime
{
    [JsonConstructor]
    public BinanceServerTime(long serverTimeMilliseconds)
    {
        ServerTimeMilliseconds = serverTimeMilliseconds;
    }

    [JsonPropertyName("serverTime")]
    public long ServerTimeMilliseconds { get; }

    [JsonIgnore]
    public DateTimeOffset ServerTime => BinanceTimestamp.ToUtc(ServerTimeMilliseconds);
}

public sealed record BinancePriceTicker
{
    [JsonConstructor]
    public BinancePriceTicker(string symbol, decimal price,
        long? eventTimeMilliseconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(price, 0m);

        Symbol = symbol;
        Price = price;
        EventTimeMilliseconds = eventTimeMilliseconds;
    }

    public string Symbol { get; }

    public decimal Price { get; }

    [JsonPropertyName("time")]
    public long? EventTimeMilliseconds { get; }
}

public sealed record Binance24HourTicker
{
    [JsonConstructor]
    public Binance24HourTicker(string symbol, decimal priceChange, decimal priceChangePercent,
        decimal weightedAveragePrice, decimal lastPrice, decimal lastQuantity, decimal openPrice,
        decimal highPrice, decimal lowPrice, decimal volume, decimal quoteVolume,
        long openTimeMilliseconds, long closeTimeMilliseconds, long firstTradeId,
        long lastTradeId, long tradeCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        Symbol = symbol;
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

    public string Symbol { get; }

    public decimal PriceChange { get; }

    public decimal PriceChangePercent { get; }

    [JsonPropertyName("weightedAvgPrice")]
    public decimal WeightedAveragePrice { get; }

    public decimal LastPrice { get; }

    [JsonPropertyName("lastQty")]
    public decimal LastQuantity { get; }

    public decimal OpenPrice { get; }

    public decimal HighPrice { get; }

    public decimal LowPrice { get; }

    public decimal Volume { get; }

    public decimal QuoteVolume { get; }

    [JsonPropertyName("openTime")]
    public long OpenTimeMilliseconds { get; }

    [JsonPropertyName("closeTime")]
    public long CloseTimeMilliseconds { get; }

    [JsonPropertyName("firstId")]
    public long FirstTradeId { get; }

    [JsonPropertyName("lastId")]
    public long LastTradeId { get; }

    [JsonPropertyName("count")]
    public long TradeCount { get; }
}

public sealed record BinanceKline
{
    public BinanceKline(long openTimeMilliseconds, decimal open, decimal high,
        decimal low, decimal close, decimal volume, long closeTimeMilliseconds,
        decimal quoteAssetVolume, long tradeCount, decimal takerBuyBaseVolume,
        decimal takerBuyQuoteVolume, string ignore, bool isClosed)
    {
        if (openTimeMilliseconds < 0 || closeTimeMilliseconds < openTimeMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(openTimeMilliseconds));
        }

        OpenTimeMilliseconds = openTimeMilliseconds;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
        CloseTimeMilliseconds = closeTimeMilliseconds;
        QuoteAssetVolume = quoteAssetVolume;
        TradeCount = tradeCount;
        TakerBuyBaseVolume = takerBuyBaseVolume;
        TakerBuyQuoteVolume = takerBuyQuoteVolume;
        Ignore = ignore;
        IsClosed = isClosed;
    }

    public long OpenTimeMilliseconds { get; }

    public decimal Open { get; }

    public decimal High { get; }

    public decimal Low { get; }

    public decimal Close { get; }

    public decimal Volume { get; }

    public long CloseTimeMilliseconds { get; }

    public decimal QuoteAssetVolume { get; }

    public long TradeCount { get; }

    public decimal TakerBuyBaseVolume { get; }

    public decimal TakerBuyQuoteVolume { get; }

    public string Ignore { get; }

    public bool IsClosed { get; }

    public bool IsClosedAt(DateTimeOffset utcNow)
    {
        return CloseTimeMilliseconds <= utcNow.ToUnixTimeMilliseconds();
    }

    public Bar ToCoreBar(InstrumentId instrumentId)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        if (!IsClosed)
        {
            throw new InvalidOperationException("An open kline cannot become a Core Bar.");
        }

        return new Bar($"bar:{instrumentId.Value}:{OpenTimeMilliseconds}", instrumentId,
            new UtcTimestamp(BinanceTimestamp.ToUtc(CloseTimeMilliseconds)),
            new UtcTimestamp(BinanceTimestamp.ToUtc(OpenTimeMilliseconds)),
            new UtcTimestamp(BinanceTimestamp.ToUtc(CloseTimeMilliseconds)),
            new Price(Open), new Price(High),
            new Price(Low), new Price(Close), new Quantity(Volume));
    }
}

public sealed record BinanceRecentTrade
{
    [JsonConstructor]
    public BinanceRecentTrade(long id, decimal price, decimal quantity, decimal quoteQuantity,
        long tradeTimeMilliseconds, bool isBuyerMaker, bool isBestMatch)
    {
        if (id < 0 || tradeTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(id < 0 ? nameof(id) : nameof(tradeTimeMilliseconds));
        }

        Id = id;
        Price = price;
        Quantity = quantity;
        QuoteQuantity = quoteQuantity;
        TradeTimeMilliseconds = tradeTimeMilliseconds;
        IsBuyerMaker = isBuyerMaker;
        IsBestMatch = isBestMatch;
    }

    public long Id { get; }

    public decimal Price { get; }

    [JsonPropertyName("qty")]
    public decimal Quantity { get; }

    [JsonPropertyName("quoteQty")]
    public decimal QuoteQuantity { get; }

    [JsonPropertyName("time")]
    public long TradeTimeMilliseconds { get; }

    public bool IsBuyerMaker { get; }

    public bool IsBestMatch { get; }

    public Trade ToCoreTrade(InstrumentId instrumentId)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        return new Trade($"trade:{instrumentId.Value}:{Id}", instrumentId,
            new UtcTimestamp(BinanceTimestamp.ToUtc(TradeTimeMilliseconds)), new Price(Price),
            new Quantity(Quantity), IsBuyerMaker ? TradeSide.Sell : TradeSide.Buy);
    }
}

public sealed record BinanceAggregateTrade
{
    [JsonConstructor]
    public BinanceAggregateTrade(long aggregateTradeId, decimal price, decimal quantity,
        long firstTradeId, long lastTradeId, long tradeTimeMilliseconds, bool isBuyerMaker,
        bool isBestMatch)
    {
        if (aggregateTradeId < 0 || firstTradeId < 0 || lastTradeId < firstTradeId
            || tradeTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateTradeId));
        }

        AggregateTradeId = aggregateTradeId;
        Price = price;
        Quantity = quantity;
        FirstTradeId = firstTradeId;
        LastTradeId = lastTradeId;
        TradeTimeMilliseconds = tradeTimeMilliseconds;
        IsBuyerMaker = isBuyerMaker;
        IsBestMatch = isBestMatch;
    }

    [JsonPropertyName("a")]
    public long AggregateTradeId { get; }

    [JsonPropertyName("p")]
    public decimal Price { get; }

    [JsonPropertyName("q")]
    public decimal Quantity { get; }

    [JsonPropertyName("f")]
    public long FirstTradeId { get; }

    [JsonPropertyName("l")]
    public long LastTradeId { get; }

    [JsonPropertyName("T")]
    public long TradeTimeMilliseconds { get; }

    [JsonPropertyName("m")]
    public bool IsBuyerMaker { get; }

    [JsonPropertyName("M")]
    public bool IsBestMatch { get; }

    public Trade ToCoreTrade(InstrumentId instrumentId)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        return new Trade($"aggTrade:{instrumentId.Value}:{AggregateTradeId}", instrumentId,
            new UtcTimestamp(BinanceTimestamp.ToUtc(TradeTimeMilliseconds)), new Price(Price),
            new Quantity(Quantity), IsBuyerMaker ? TradeSide.Sell : TradeSide.Buy);
    }
}

public sealed record BinanceMarkPrice
{
    [JsonConstructor]
    public BinanceMarkPrice(string symbol, decimal markPrice, decimal indexPrice,
        decimal estimatedSettlePrice, decimal lastFundingRate,
        long nextFundingTimeMilliseconds, long eventTimeMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        Symbol = symbol;
        MarkPrice = markPrice;
        IndexPrice = indexPrice;
        EstimatedSettlePrice = estimatedSettlePrice;
        LastFundingRate = lastFundingRate;
        NextFundingTimeMilliseconds = nextFundingTimeMilliseconds;
        EventTimeMilliseconds = eventTimeMilliseconds;
    }

    public string Symbol { get; }

    public decimal MarkPrice { get; }

    public decimal IndexPrice { get; }

    public decimal EstimatedSettlePrice { get; }

    public decimal LastFundingRate { get; }

    [JsonPropertyName("nextFundingTime")]
    public long NextFundingTimeMilliseconds { get; }

    [JsonPropertyName("time")]
    public long EventTimeMilliseconds { get; }

    public FundingUpdate ToCoreFundingUpdate()
    {
        return new FundingUpdate($"funding:{Symbol}:{EventTimeMilliseconds}",
            new InstrumentId(Symbol), new UtcTimestamp(BinanceTimestamp.ToUtc(EventTimeMilliseconds)),
            LastFundingRate, new UtcTimestamp(BinanceTimestamp.ToUtc(NextFundingTimeMilliseconds)));
    }
}

public sealed record BinanceFundingRate
{
    [JsonConstructor]
    public BinanceFundingRate(string symbol, decimal fundingRate, long fundingTimeMilliseconds,
        decimal markPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        Symbol = symbol;
        FundingRate = fundingRate;
        FundingTimeMilliseconds = fundingTimeMilliseconds;
        MarkPrice = markPrice;
    }

    public string Symbol { get; }

    public decimal FundingRate { get; }

    [JsonPropertyName("fundingTime")]
    public long FundingTimeMilliseconds { get; }

    public decimal MarkPrice { get; }

    public FundingUpdate ToCoreFundingUpdate()
    {
        return new FundingUpdate($"funding:{Symbol}:{FundingTimeMilliseconds}",
            new InstrumentId(Symbol), new UtcTimestamp(BinanceTimestamp.ToUtc(FundingTimeMilliseconds)),
            FundingRate);
    }
}

public sealed record BinanceFundingInfo
{
    [JsonConstructor]
    public BinanceFundingInfo(string symbol, decimal adjustedFundingRateCap,
        decimal adjustedFundingRateFloor, int fundingIntervalHours, bool disclaimer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fundingIntervalHours);

        Symbol = symbol;
        AdjustedFundingRateCap = adjustedFundingRateCap;
        AdjustedFundingRateFloor = adjustedFundingRateFloor;
        FundingIntervalHours = fundingIntervalHours;
        Disclaimer = disclaimer;
    }

    public string Symbol { get; }

    public decimal AdjustedFundingRateCap { get; }

    public decimal AdjustedFundingRateFloor { get; }

    public int FundingIntervalHours { get; }

    public bool Disclaimer { get; }
}

public sealed record BinanceOpenInterest
{
    [JsonConstructor]
    public BinanceOpenInterest(string symbol, decimal openInterest, long eventTimeMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentOutOfRangeException.ThrowIfNegative(openInterest);
        ArgumentOutOfRangeException.ThrowIfNegative(eventTimeMilliseconds);

        Symbol = symbol;
        OpenInterest = openInterest;
        EventTimeMilliseconds = eventTimeMilliseconds;
    }

    public string Symbol { get; }

    public decimal OpenInterest { get; }

    [JsonPropertyName("time")]
    public long EventTimeMilliseconds { get; }

    public OIUpdate ToCoreUpdate()
    {
        return new OIUpdate($"oi:{Symbol}:{EventTimeMilliseconds}",
            new InstrumentId(Symbol), new UtcTimestamp(BinanceTimestamp.ToUtc(EventTimeMilliseconds)),
            OpenInterest);
    }
}

/// <summary>A single exact-decimal Binance order-book price level.</summary>
public record BinanceOrderBookLevel
{
    public BinanceOrderBookLevel(decimal price, decimal quantity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(price, 0m);
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        Price = price;
        Quantity = quantity;
    }

    public decimal Price { get; }

    public decimal Quantity { get; }
}

/// <summary>Alias-shaped depth level used by the public depth contract.</summary>
public sealed record BinanceDepthLevel : BinanceOrderBookLevel
{
    public BinanceDepthLevel(decimal price, decimal quantity)
        : base(price, quantity)
    {
    }
}

/// <summary>
/// Immutable USD-M REST depth snapshot. Exchange timestamps remain raw Unix
/// milliseconds and optional local timestamps retain HTTP provenance.
/// </summary>
public record BinanceOrderBookSnapshot
{
    public BinanceOrderBookSnapshot(long lastUpdateId, long eventTimeMilliseconds,
        long transactionTimeMilliseconds, IEnumerable<BinanceDepthLevel> bids,
        IEnumerable<BinanceDepthLevel> asks, string? symbol = null,
        UtcTimestamp? receiveTime = null, UtcTimestamp? availabilityTime = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastUpdateId);
        ArgumentOutOfRangeException.ThrowIfNegative(eventTimeMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(transactionTimeMilliseconds);
        if (availabilityTime is not null && receiveTime is null)
        {
            throw new ArgumentException(
                "Snapshot availability time requires a receive time.", nameof(availabilityTime));
        }

        if (receiveTime is not null && availabilityTime is not null
            && availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Snapshot availability cannot precede receive time.",
                nameof(availabilityTime));
        }

        if (symbol is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        }

        LastUpdateId = lastUpdateId;
        EventTimeMilliseconds = eventTimeMilliseconds;
        TransactionTimeMilliseconds = transactionTimeMilliseconds;
        Symbol = symbol;
        Bids = SnapshotLevels(bids, nameof(bids));
        Asks = SnapshotLevels(asks, nameof(asks));
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
    }

    public BinanceOrderBookSnapshot(long lastUpdateId, long eventTimeMilliseconds,
        long transactionTimeMilliseconds, IEnumerable<BinanceOrderBookLevel> bids,
        IEnumerable<BinanceOrderBookLevel> asks, string? symbol = null,
        UtcTimestamp? receiveTime = null, UtcTimestamp? availabilityTime = null)
        : this(lastUpdateId, eventTimeMilliseconds, transactionTimeMilliseconds,
            ConvertLevels(bids, nameof(bids)), ConvertLevels(asks, nameof(asks)), symbol,
            receiveTime, availabilityTime)
    {
    }

    public long LastUpdateId { get; }

    [JsonPropertyName("E")]
    public long EventTimeMilliseconds { get; }

    [JsonPropertyName("T")]
    public long TransactionTimeMilliseconds { get; }

    [JsonIgnore]
    public string? Symbol { get; }

    public IReadOnlyList<BinanceDepthLevel> Bids { get; }

    public IReadOnlyList<BinanceDepthLevel> Asks { get; }

    [JsonIgnore]
    public UtcTimestamp? ReceiveTime { get; }

    [JsonIgnore]
    public UtcTimestamp? AvailabilityTime { get; }

    [JsonIgnore]
    public UtcTimestamp EventTime => new(BinanceTimestamp.ToUtc(EventTimeMilliseconds));

    [JsonIgnore]
    public UtcTimestamp TransactionTime =>
        new(BinanceTimestamp.ToUtc(TransactionTimeMilliseconds));

    private static ReadOnlyCollection<BinanceDepthLevel> SnapshotLevels(
        IEnumerable<BinanceDepthLevel> levels,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(levels, parameterName);
        BinanceDepthLevel[] snapshot = levels.ToArray();
        if (snapshot.Any(static level => level is null))
        {
            throw new ArgumentException("Order-book levels cannot contain null values.",
                parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }

    private static ReadOnlyCollection<BinanceDepthLevel> ConvertLevels(
        IEnumerable<BinanceOrderBookLevel> levels,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(levels, parameterName);
        BinanceOrderBookLevel[] snapshot = levels.ToArray();
        if (snapshot.Any(static level => level is null))
        {
            throw new ArgumentException("Order-book levels cannot contain null values.",
                parameterName);
        }

        return Array.AsReadOnly(snapshot.Select(static level => level is BinanceDepthLevel depth
                    ? depth
                    : new BinanceDepthLevel(level.Price, level.Quantity))
                .ToArray());
    }
}

/// <summary>Named depth DTO retained for callers that use the endpoint term.</summary>
public sealed record BinanceDepthSnapshot : BinanceOrderBookSnapshot
{
    public BinanceDepthSnapshot(long lastUpdateId, long eventTimeMilliseconds,
        long transactionTimeMilliseconds, IEnumerable<BinanceDepthLevel> bids,
        IEnumerable<BinanceDepthLevel> asks, string? symbol = null,
        UtcTimestamp? receiveTime = null, UtcTimestamp? availabilityTime = null)
        : base(lastUpdateId, eventTimeMilliseconds, transactionTimeMilliseconds, bids, asks,
            symbol, receiveTime, availabilityTime)
    {
    }

    public BinanceDepthSnapshot(long lastUpdateId, long eventTimeMilliseconds,
        long transactionTimeMilliseconds, IEnumerable<BinanceOrderBookLevel> bids,
        IEnumerable<BinanceOrderBookLevel> asks, string? symbol = null,
        UtcTimestamp? receiveTime = null, UtcTimestamp? availabilityTime = null)
        : base(lastUpdateId, eventTimeMilliseconds, transactionTimeMilliseconds, bids, asks,
            symbol, receiveTime, availabilityTime)
    {
    }
}

/// <summary>Source seam used by local order-book recovery and fixture tests.</summary>
public interface IBinanceDepthSnapshotSource
{
    Task<BinanceDepthSnapshot> GetDepthSnapshotAsync(string symbol, int limit,
        CancellationToken cancellationToken = default);
}

internal static class BinanceTimestamp
{
    public static DateTimeOffset ToUtc(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FormatException("Binance timestamp is outside the Unix millisecond range.",
                exception);
        }
    }
}
