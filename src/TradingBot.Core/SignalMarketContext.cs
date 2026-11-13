using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>UTC-session VWAP from exchange-reported closed-bar base and quote volumes.</summary>
public sealed record SignalSessionVwap
{
    public UtcTimestamp WindowStart { get; }
    public UtcTimestamp WindowEnd { get; }
    public decimal? Value { get; }
    public decimal BaseVolume { get; }
    public decimal QuoteVolume { get; }
    public int BarCount { get; }
    public string? UnknownReason { get; }

    [JsonConstructor]
    public SignalSessionVwap(UtcTimestamp windowStart, UtcTimestamp windowEnd,
        decimal? value, decimal baseVolume, decimal quoteVolume, int barCount,
        string? unknownReason)
    {
        ArgumentNullException.ThrowIfNull(windowStart);
        ArgumentNullException.ThrowIfNull(windowEnd);
        if (windowStart.Value >= windowEnd.Value || baseVolume < 0m || quoteVolume < 0m
            || barCount < 0 || (value is null) == string.IsNullOrWhiteSpace(unknownReason)
            || value is not null && (baseVolume <= 0m || quoteVolume <= 0m
                || value <= 0m || value != quoteVolume / baseVolume))
        {
            throw new ArgumentException("The session VWAP evidence is inconsistent.");
        }
        WindowStart = windowStart;
        WindowEnd = windowEnd;
        Value = value;
        BaseVolume = baseVolume;
        QuoteVolume = quoteVolume;
        BarCount = barCount;
        UnknownReason = unknownReason;
    }
}

/// <summary>Observed-trade POC; price is the lower edge of the winning price bin.</summary>
public sealed record SignalObservedPoc
{
    public UtcTimestamp WindowStart { get; }
    public UtcTimestamp WindowEnd { get; }
    public decimal BinSize { get; }
    public decimal? Price { get; }
    public int TradeCount { get; }
    public string? UnknownReason { get; }

    [JsonConstructor]
    public SignalObservedPoc(UtcTimestamp windowStart, UtcTimestamp windowEnd,
        decimal binSize, decimal? price, int tradeCount, string? unknownReason)
    {
        ArgumentNullException.ThrowIfNull(windowStart);
        ArgumentNullException.ThrowIfNull(windowEnd);
        if (windowStart.Value >= windowEnd.Value || binSize <= 0m || tradeCount < 0
            || (price is null) == string.IsNullOrWhiteSpace(unknownReason)
            || price is < 0m || price is not null && tradeCount == 0)
        {
            throw new ArgumentException("The observed POC evidence is inconsistent.");
        }
        WindowStart = windowStart;
        WindowEnd = windowEnd;
        BinSize = binSize;
        Price = price;
        TradeCount = tradeCount;
        UnknownReason = unknownReason;
    }
}

/// <summary>One sampled Binance force-order snapshot, not complete liquidation flow.</summary>
public sealed record SignalObservedLiquidation
{
    public string EventId { get; }
    public TradeSide Side { get; }
    public decimal Quantity { get; }
    public decimal Price { get; }
    public UtcTimestamp EventTime { get; }
    public UtcTimestamp ExchangeTime { get; }
    public UtcTimestamp ReceiveTime { get; }
    public UtcTimestamp AvailabilityTime { get; }

    [JsonConstructor]
    public SignalObservedLiquidation(string eventId, TradeSide side, decimal quantity,
        decimal price, UtcTimestamp eventTime, UtcTimestamp exchangeTime,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime)
    {
        ContractGuard.RequireText(eventId, nameof(eventId));
        ContractGuard.RequireDefined(side, nameof(side));
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(exchangeTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (quantity <= 0m || price <= 0m || availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("The observed liquidation is invalid.");
        }
        EventId = eventId;
        Side = side;
        Quantity = quantity;
        Price = price;
        EventTime = eventTime;
        ExchangeTime = exchangeTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
    }
}

/// <summary>Versioned optional, non-gating market context at a signal decision.</summary>
public sealed record SignalMarketContext
{
    public const int CurrentFormatVersion = 1;
    public const string CurrentAlgorithmVersion = "public-market-context-v1";
    public int FormatVersion { get; }
    public string AlgorithmVersion { get; }
    public InstrumentId InstrumentId { get; }
    public UtcTimestamp AsOf { get; }
    public SignalSessionVwap Vwap { get; }
    public SignalObservedPoc Poc { get; }
    public SignalObservedLiquidation? LatestLiquidation { get; }

    [JsonConstructor]
    public SignalMarketContext(int formatVersion, string algorithmVersion,
        InstrumentId instrumentId, UtcTimestamp asOf, SignalSessionVwap vwap,
        SignalObservedPoc poc, SignalObservedLiquidation? latestLiquidation)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentException("The market-context algorithm is unsupported.", nameof(algorithmVersion));
        }
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(asOf);
        ArgumentNullException.ThrowIfNull(vwap);
        ArgumentNullException.ThrowIfNull(poc);
        if (vwap.WindowEnd.Value > asOf.Value || poc.WindowEnd.Value != asOf.Value
            || latestLiquidation is not null && (latestLiquidation.EventTime.Value < poc.WindowStart.Value
                || latestLiquidation.EventTime.Value > asOf.Value
                || latestLiquidation.ExchangeTime.Value < poc.WindowStart.Value
                || latestLiquidation.ExchangeTime.Value > asOf.Value
                || latestLiquidation.AvailabilityTime.Value > asOf.Value))
        {
            throw new ArgumentException("The market context exceeds its causal cut.");
        }
        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        InstrumentId = instrumentId;
        AsOf = asOf;
        Vwap = vwap;
        Poc = poc;
        LatestLiquidation = latestLiquidation;
    }

    public static SignalMarketContext Create(InstrumentId instrumentId, UtcTimestamp asOf,
        SignalSessionVwap vwap, SignalObservedPoc poc, SignalObservedLiquidation? latestLiquidation) =>
        new(CurrentFormatVersion, CurrentAlgorithmVersion, instrumentId, asOf, vwap, poc, latestLiquidation);
}

/// <summary>A public aggregate trade and the time at which it became available.</summary>
public readonly record struct SignalContextTrade(decimal Price, decimal Quantity,
    DateTimeOffset EventTime, DateTimeOffset AvailabilityTime, DateTimeOffset? SourceEventTime);

/// <summary>Pure, non-gating context calculations over caller-supplied causal inputs.</summary>
public static class SignalMarketContextCalculator
{
    public static SignalSessionVwap CalculateSessionVwap(string timeframe,
        UtcTimestamp through, UtcTimestamp asOf, IEnumerable<ScreenerBar> bars)
    {
        ArgumentNullException.ThrowIfNull(through);
        ArgumentNullException.ThrowIfNull(asOf);
        ArgumentNullException.ThrowIfNull(bars);
        if (!PublicSignalTimeframes.TryGetDurationMilliseconds(timeframe, out long milliseconds)
            || through.Value > asOf.Value)
        {
            throw new ArgumentException("The session VWAP cut or timeframe is invalid.");
        }
        DateTimeOffset end = through.Value.ToUniversalTime();
        DateTimeOffset start = new(end.Year, end.Month, end.Day, 0, 0, 0, TimeSpan.Zero);
        if (start == end)
        {
            start = start.AddDays(-1);
        }
        UtcTimestamp sessionStart = new(start);
        TimeSpan interval = TimeSpan.FromMilliseconds(milliseconds);
        DateTimeOffset expected = start;
        decimal baseVolume = 0m;
        decimal quoteVolume = 0m;
        int count = 0;
        try
        {
            foreach (ScreenerBar item in bars)
            {
                Bar bar = item.Bar;
                if (bar.OpenTime.Value < start || bar.OpenTime.Value >= end)
                {
                    continue;
                }
                if (bar.OpenTime.Value != expected || bar.CloseTime.Value != expected.Add(interval)
                    || bar.CloseTime.Value > end || item.EventTime.Value > asOf.Value
                    || item.AvailabilityTime.Value > asOf.Value)
                {
                    return new SignalSessionVwap(sessionStart, through, null, baseVolume,
                        quoteVolume, count, "session-history-incomplete");
                }
                baseVolume = checked(baseVolume + bar.Volume.Value);
                quoteVolume = checked(quoteVolume + item.QuoteVolume);
                count++;
                expected = bar.CloseTime.Value;
            }
            if (expected != end)
            {
                return new SignalSessionVwap(sessionStart, through, null, baseVolume,
                    quoteVolume, count, "session-history-incomplete");
            }
            if (baseVolume <= 0m || quoteVolume <= 0m)
            {
                return new SignalSessionVwap(sessionStart, through, null, baseVolume,
                    quoteVolume, count, "session-volume-unavailable");
            }
            return new SignalSessionVwap(sessionStart, through,
                checked(quoteVolume / baseVolume), baseVolume, quoteVolume, count, null);
        }
        catch (OverflowException)
        {
            return new SignalSessionVwap(sessionStart, through, null, 0m, 0m, count,
                "session-volume-overflow");
        }
    }

    public static SignalObservedPoc CalculatePoc(UtcTimestamp asOf, TimeSpan window,
        decimal binSize, UtcTimestamp? coverageStart, IEnumerable<SignalContextTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(asOf);
        ArgumentNullException.ThrowIfNull(trades);
        if (window <= TimeSpan.Zero || binSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "A positive window and price bin are required.");
        }
        UtcTimestamp start = new(asOf.Value.Subtract(window));
        if (coverageStart is null || coverageStart.Value > start.Value)
        {
            return new SignalObservedPoc(start, asOf, binSize, null, 0, "trade-window-incomplete");
        }
        Dictionary<decimal, decimal> bins = [];
        int count = 0;
        try
        {
            foreach (SignalContextTrade trade in trades)
            {
                if (trade.EventTime < start.Value || trade.EventTime > asOf.Value
                    || trade.AvailabilityTime > asOf.Value)
                {
                    continue;
                }
                if (trade.SourceEventTime is null)
                {
                    return new SignalObservedPoc(start, asOf, binSize, null, count, "trade-source-time-unavailable");
                }
                if (trade.SourceEventTime > asOf.Value)
                {
                    continue;
                }
                if (trade.Price <= 0m || trade.Quantity <= 0m)
                {
                    return new SignalObservedPoc(start, asOf, binSize, null, count, "trade-input-invalid");
                }
                decimal bin = checked(decimal.Floor(trade.Price / binSize) * binSize);
                bins[bin] = checked(bins.GetValueOrDefault(bin) + trade.Quantity);
                count++;
            }
            if (count == 0)
            {
                return new SignalObservedPoc(start, asOf, binSize, null, 0, "no-observed-trades");
            }
            decimal bestPrice = 0m;
            decimal bestVolume = -1m;
            foreach ((decimal price, decimal volume) in bins)
            {
                if (volume > bestVolume || volume == bestVolume && price < bestPrice)
                {
                    bestPrice = price;
                    bestVolume = volume;
                }
            }
            return new SignalObservedPoc(start, asOf, binSize, bestPrice, count, null);
        }
        catch (OverflowException)
        {
            return new SignalObservedPoc(start, asOf, binSize, null, count, "trade-volume-overflow");
        }
    }
}
