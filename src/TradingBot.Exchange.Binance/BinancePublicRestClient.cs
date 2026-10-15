using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

/// <summary>Public Binance USD-M market-data queries owned by the exchange adapter.</summary>
public interface IBinancePublicRestClient
{
    Task<BinanceExchangeInfo> GetExchangeInfoAsync(CancellationToken cancellationToken = default);

    Task<BinanceServerTime> GetServerTimeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinancePriceTicker>> GetPriceTickersAsync(string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Binance24HourTicker>> Get24HourTickersAsync(string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceKline>> GetKlinesAsync(string symbol, string interval,
        int? limit = null, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceRecentTrade>> GetRecentTradesAsync(string symbol,
        int? limit = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol,
        long? fromId = null, long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceMarkPrice>> GetMarkPricesAsync(string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceFundingRate>> GetFundingHistoryAsync(string symbol,
        long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BinanceFundingInfo>> GetFundingInfoAsync(string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<BinanceOpenInterest> GetOpenInterestAsync(string symbol,
        CancellationToken cancellationToken = default);

    Task<BinanceDepthSnapshot> GetDepthSnapshotAsync(string symbol, int? limit = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Bar>> GetClosedBarsAsync(string symbol, string interval,
        int? limit = null, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Trade>> GetRecentTradeEventsAsync(string symbol,
        int? limit = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Trade>> GetAggregateTradeEventsAsync(string symbol,
        long? fromId = null, long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default);

    Task<FundingUpdate> GetMarkFundingEventAsync(string symbol,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FundingUpdate>> GetFundingHistoryEventsAsync(string symbol,
        long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default);

    Task<OIUpdate> GetOpenInterestEventAsync(string symbol,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Small public REST adapter for current Binance USD-M market data. It owns wire
/// paths and schemas while returning passive DTOs or Core market events.
/// </summary>
public sealed class BinancePublicRestClient : IBinancePublicRestClient, IBinanceDepthSnapshotSource
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HttpClient _httpClient;
    private readonly BinanceRestOptions _options;
    private readonly TimeProvider _timeProvider;
    private long _lastOpenInterestRawTimeMilliseconds = -1;

    public BinancePublicRestClient(HttpClient httpClient)
        : this(httpClient, CreateDefaultOptions(httpClient), TimeProvider.System)
    {
    }

    public BinancePublicRestClient(HttpClient httpClient, BinanceRestOptions options)
        : this(httpClient, options, TimeProvider.System)
    {
    }

    public BinancePublicRestClient(HttpClient httpClient, BinanceRestOptions options,
        TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ValidateOptions(_options);
    }

    public long LastOpenInterestRawTimeMilliseconds => Interlocked.Read(
        ref _lastOpenInterestRawTimeMilliseconds);

    public Task<BinanceExchangeInfo> GetExchangeInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<BinanceExchangeInfo>("/fapi/v1/exchangeInfo",
            new Dictionary<string, string?>(StringComparer.Ordinal), cancellationToken);
    }

    public Task<BinanceServerTime> GetServerTimeAsync(
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<BinanceServerTime>("/fapi/v1/time",
            new Dictionary<string, string?>(StringComparer.Ordinal), cancellationToken);
    }

    public Task<IReadOnlyList<BinancePriceTicker>> GetPriceTickersAsync(string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        return GetArrayOrObjectAsync<BinancePriceTicker>("/fapi/v1/ticker/price",
            SymbolQuery(symbol), cancellationToken);
    }

    public Task<IReadOnlyList<Binance24HourTicker>> Get24HourTickersAsync(string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        return GetArrayOrObjectAsync<Binance24HourTicker>("/fapi/v1/ticker/24hr",
            SymbolQuery(symbol), cancellationToken);
    }

    public async Task<IReadOnlyList<BinanceKline>> GetKlinesAsync(string symbol, string interval,
        int? limit = null, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null,
        CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(interval);
        ValidateLimit(limit, 1500);
        ValidateTimeRange(startTime, endTime);

        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol,
            ["interval"] = interval,
            ["limit"] = limit?.ToString(CultureInfo.InvariantCulture),
            ["startTime"] = ToUnixMilliseconds(startTime),
            ["endTime"] = ToUnixMilliseconds(endTime)
        };
        using JsonDocument document = await GetDocumentAsync("/fapi/v1/klines", query,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The Binance kline response must be an array.");
        }

        List<BinanceKline> result = new(document.RootElement.GetArrayLength());
        DateTimeOffset now = _timeProvider.GetUtcNow();
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            result.Add(ParseKline(row, now));
        }

        return result;
    }

    public Task<IReadOnlyList<BinanceRecentTrade>> GetRecentTradesAsync(string symbol,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        ValidateLimit(limit, 1000);
        return GetJsonArrayAsync<BinanceRecentTrade>("/fapi/v1/trades",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["symbol"] = symbol,
                ["limit"] = limit?.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
    }

    public Task<IReadOnlyList<BinanceAggregateTrade>> GetAggregateTradesAsync(string symbol,
        long? fromId = null, long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        ValidateNonNegative(fromId, nameof(fromId));
        ValidateNonNegative(startTimeMilliseconds, nameof(startTimeMilliseconds));
        ValidateNonNegative(endTimeMilliseconds, nameof(endTimeMilliseconds));
        ValidateTimeRange(startTimeMilliseconds, endTimeMilliseconds);
        ValidateLimit(limit, 1000);
        return GetJsonArrayAsync<BinanceAggregateTrade>("/fapi/v1/aggTrades",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["symbol"] = symbol,
                ["fromId"] = fromId?.ToString(CultureInfo.InvariantCulture),
                ["startTime"] = startTimeMilliseconds?.ToString(CultureInfo.InvariantCulture),
                ["endTime"] = endTimeMilliseconds?.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit?.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
    }

    public Task<IReadOnlyList<BinanceMarkPrice>> GetMarkPricesAsync(string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        return GetArrayOrObjectAsync<BinanceMarkPrice>("/fapi/v1/premiumIndex",
            SymbolQuery(symbol), cancellationToken);
    }

    public Task<IReadOnlyList<BinanceFundingRate>> GetFundingHistoryAsync(string symbol,
        long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        ValidateNonNegative(startTimeMilliseconds, nameof(startTimeMilliseconds));
        ValidateNonNegative(endTimeMilliseconds, nameof(endTimeMilliseconds));
        ValidateTimeRange(startTimeMilliseconds, endTimeMilliseconds);
        ValidateLimit(limit, 1000);
        return GetJsonArrayAsync<BinanceFundingRate>("/fapi/v1/fundingRate",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["symbol"] = symbol,
                ["startTime"] = startTimeMilliseconds?.ToString(CultureInfo.InvariantCulture),
                ["endTime"] = endTimeMilliseconds?.ToString(CultureInfo.InvariantCulture),
                ["limit"] = limit?.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken);
    }

    public Task<IReadOnlyList<BinanceFundingInfo>> GetFundingInfoAsync(string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        return GetJsonArrayAsync<BinanceFundingInfo>("/fapi/v1/fundingInfo",
            SymbolQuery(symbol), cancellationToken);
    }

    public Task<BinanceOpenInterest> GetOpenInterestAsync(string symbol,
        CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        return GetJsonAsync<BinanceOpenInterest>("/fapi/v1/openInterest",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["symbol"] = symbol },
            cancellationToken);
    }

    public async Task<BinanceDepthSnapshot> GetDepthSnapshotAsync(string symbol, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        ValidateDepthLimit(limit);
        DateTimeOffset receiveTime = _timeProvider.GetUtcNow();
        using JsonDocument document = await GetDocumentAsync("/fapi/v1/depth",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["symbol"] = symbol,
                ["limit"] = limit?.ToString(CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The Binance depth response must be an object.");
        }

        long lastUpdateId = ReadInt64(root, "lastUpdateId");
        long eventTimeMilliseconds = root.TryGetProperty("E", out JsonElement eventTime)
            ? ReadInt64(eventTime)
            : 0;
        long transactionTimeMilliseconds = root.TryGetProperty("T", out JsonElement transactionTime)
            ? ReadInt64(transactionTime)
            : eventTimeMilliseconds;
        BinanceDepthLevel[] bids = ParseDepthLevels(root, "bids");
        BinanceDepthLevel[] asks = ParseDepthLevels(root, "asks");
        DateTimeOffset availabilityTime = _timeProvider.GetUtcNow();
        return new BinanceDepthSnapshot(lastUpdateId, eventTimeMilliseconds,
            transactionTimeMilliseconds, bids, asks, symbol,
            new UtcTimestamp(receiveTime), new UtcTimestamp(availabilityTime));
    }

    /// <summary>Alias for callers that use the Binance endpoint name.</summary>
    public Task<BinanceDepthSnapshot> GetOrderBookAsync(string symbol, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return GetDepthSnapshotAsync(symbol, limit, cancellationToken);
    }

    Task<BinanceDepthSnapshot> IBinanceDepthSnapshotSource.GetDepthSnapshotAsync(
        string symbol, int limit, CancellationToken cancellationToken)
    {
        return GetDepthSnapshotAsync(symbol, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<Bar>> GetClosedBarsAsync(string symbol, string interval,
        int? limit = null, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BinanceKline> klines = await GetKlinesAsync(symbol, interval, limit,
            startTime, endTime, cancellationToken).ConfigureAwait(false);
        InstrumentId instrumentId = new(symbol);
        return klines.Where(static kline => kline.IsClosed)
            .Select(kline => kline.ToCoreBar(instrumentId)).ToArray();
    }

    public async Task<IReadOnlyList<Trade>> GetRecentTradeEventsAsync(string symbol,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BinanceRecentTrade> trades = await GetRecentTradesAsync(symbol, limit,
            cancellationToken).ConfigureAwait(false);
        InstrumentId instrumentId = new(symbol);
        return trades.Select(trade => trade.ToCoreTrade(instrumentId)).ToArray();
    }

    public async Task<IReadOnlyList<Trade>> GetAggregateTradeEventsAsync(string symbol,
        long? fromId = null, long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BinanceAggregateTrade> trades = await GetAggregateTradesAsync(symbol,
            fromId, startTimeMilliseconds, endTimeMilliseconds, limit, cancellationToken)
            .ConfigureAwait(false);
        InstrumentId instrumentId = new(symbol);
        return trades.Select(trade => trade.ToCoreTrade(instrumentId)).ToArray();
    }

    public async Task<FundingUpdate> GetMarkFundingEventAsync(string symbol,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BinanceMarkPrice> prices = await GetMarkPricesAsync(symbol,
            cancellationToken).ConfigureAwait(false);
        BinanceMarkPrice markPrice = prices.SingleOrDefault()
            ?? throw new JsonException("The Binance mark-price response did not contain one symbol.");
        return markPrice.ToCoreFundingUpdate();
    }

    public async Task<IReadOnlyList<FundingUpdate>> GetFundingHistoryEventsAsync(string symbol,
        long? startTimeMilliseconds = null, long? endTimeMilliseconds = null,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BinanceFundingRate> rates = await GetFundingHistoryAsync(symbol,
            startTimeMilliseconds, endTimeMilliseconds, limit, cancellationToken)
            .ConfigureAwait(false);
        return rates.Select(static rate => rate.ToCoreFundingUpdate()).ToArray();
    }

    public async Task<OIUpdate> GetOpenInterestEventAsync(string symbol,
        CancellationToken cancellationToken = default)
    {
        BinanceOpenInterest openInterest = await GetOpenInterestAsync(symbol,
            cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _lastOpenInterestRawTimeMilliseconds,
            openInterest.EventTimeMilliseconds);
        return openInterest.ToCoreUpdate();
    }

    private async Task<T> GetJsonAsync<T>(string path, IReadOnlyDictionary<string, string?> query,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetDocumentAsync(path, query, cancellationToken)
            .ConfigureAwait(false);
        T? value = document.RootElement.Deserialize<T>(JsonOptions);
        return value ?? throw new JsonException($"The Binance response for {path} was null.");
    }

    private async Task<IReadOnlyList<T>> GetJsonArrayAsync<T>(string path,
        IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetDocumentAsync(path, query, cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"The Binance response for {path} must be an array.");
        }

        T[] values = document.RootElement.Deserialize<T[]>(JsonOptions)
            ?? throw new JsonException($"The Binance response for {path} was null.");
        return values;
    }

    private async Task<IReadOnlyList<T>> GetArrayOrObjectAsync<T>(string path,
        IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetDocumentAsync(path, query, cancellationToken)
            .ConfigureAwait(false);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.Deserialize<T[]>(JsonOptions)
                ?? throw new JsonException($"The Binance response for {path} was null."),
            JsonValueKind.Object => [document.RootElement.Deserialize<T>(JsonOptions)
                ?? throw new JsonException($"The Binance response for {path} was null.")],
            _ => throw new JsonException($"The Binance response for {path} must be an object or array.")
        };
    }

    private async Task<JsonDocument> GetDocumentAsync(string path,
        IReadOnlyDictionary<string, string?> query, CancellationToken cancellationToken)
    {
        Uri requestUri = BuildUri(path, query);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        using HttpResponseMessage response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        string responseBody = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response, responseBody);
        }

        try
        {
            return JsonDocument.Parse(responseBody);
        }
        catch (JsonException exception)
        {
            throw new JsonException($"Binance returned malformed JSON for {requestUri}.",
                exception);
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var contentLength
            && contentLength > _options.MaxResponseBytes)
        {
            throw new InvalidOperationException("The Binance response exceeds the configured size limit.");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (bytes.LongLength > _options.MaxResponseBytes)
        {
            throw new InvalidOperationException("The Binance response exceeds the configured size limit.");
        }

        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static BinanceApiException CreateApiException(HttpResponseMessage response,
        string responseBody)
    {
        int? code = null;
        string message = $"Binance public REST request failed with HTTP {(int)response.StatusCode}.";
        try
        {
            using JsonDocument error = JsonDocument.Parse(responseBody);
            if (error.RootElement.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.TryGetInt32(out int parsedCode))
            {
                code = parsedCode;
            }

            if (error.RootElement.TryGetProperty("msg", out JsonElement messageElement)
                && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                message = $"{message} {responseBody}";
            }
        }

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        return new BinanceApiException(response.StatusCode, code, message, responseBody,
            response.RequestMessage?.RequestUri, retryAfter);
    }

    private Uri BuildUri(string path, IReadOnlyDictionary<string, string?> query)
    {
        UriBuilder builder = new(new Uri(_options.BaseAddress, path));
        IEnumerable<string> parts = query.Where(static pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");
        builder.Query = string.Join('&', parts);
        return builder.Uri;
    }

    private static BinanceKline ParseKline(JsonElement row, DateTimeOffset now)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 12)
        {
            throw new JsonException("A Binance kline row must contain at least twelve values.");
        }

        JsonElement[] values = row.EnumerateArray().ToArray();
        long openTime = ReadInt64(values[0]);
        long closeTime = ReadInt64(values[6]);
        return new BinanceKline(openTime, ReadDecimal(values[1]), ReadDecimal(values[2]),
            ReadDecimal(values[3]), ReadDecimal(values[4]), ReadDecimal(values[5]), closeTime,
            ReadDecimal(values[7]), ReadInt64(values[8]), ReadDecimal(values[9]),
            ReadDecimal(values[10]), ReadString(values[11]),
            closeTime <= now.ToUnixTimeMilliseconds());
    }

    private static decimal ReadDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => decimal.Parse(value.GetString()!, NumberStyles.Float,
                CultureInfo.InvariantCulture),
            _ => throw new JsonException("A Binance decimal field must be a JSON number or string.")
        };
    }

    private static BinanceDepthLevel[] ParseDepthLevels(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement levels)
            || levels.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"A Binance depth response must contain an array '{propertyName}'.");
        }

        List<BinanceDepthLevel> result = new(levels.GetArrayLength());
        foreach (JsonElement level in levels.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Array || level.GetArrayLength() < 2)
            {
                throw new JsonException("A Binance depth level must contain price and quantity.");
            }

            JsonElement[] values = level.EnumerateArray().ToArray();
            result.Add(new BinanceDepthLevel(ReadDecimal(values[0]), ReadDecimal(values[1])));
        }

        return result.ToArray();
    }

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new JsonException($"A Binance depth response is missing '{propertyName}'.");
        }

        return ReadInt64(value);
    }

    private static long ReadInt64(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.String => long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => throw new JsonException("A Binance integer field must be a JSON number or string.")
        };
    }

    private static string ReadString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new JsonException("A Binance text field must be a JSON string.");
    }

    private static Dictionary<string, string?> SymbolQuery(string? symbol)
    {
        if (symbol is not null)
        {
            RequireSymbol(symbol);
        }

        return new Dictionary<string, string?>(StringComparer.Ordinal) { ["symbol"] = symbol };
    }

    private static string? ToUnixMilliseconds(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
    }

    private static void ValidateTimeRange(DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        if (startTime is not null && endTime is not null && startTime > endTime)
        {
            throw new ArgumentException("startTime cannot be after endTime.", nameof(startTime));
        }
    }

    private static void ValidateTimeRange(long? startTime, long? endTime)
    {
        if (startTime is not null && endTime is not null && startTime > endTime)
        {
            throw new ArgumentException("startTime cannot be after endTime.", nameof(startTime));
        }
    }

    private static void ValidateLimit(int? limit, int maximum)
    {
        if (limit is < 1 || limit > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                $"Limit must be between one and {maximum}.");
        }
    }

    private static void ValidateDepthLimit(int? limit)
    {
        if (limit is null)
        {
            return;
        }

        int value = limit.Value;
        if (value is not (5 or 10 or 20 or 50 or 100 or 500 or 1000))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), value,
                "Depth limit must be one of 5, 10, 20, 50, 100, 500, or 1000.");
        }
    }

    private static void ValidateNonNegative(long? value, string parameterName)
    {
        if (value is < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A Binance timestamp or identifier cannot be negative.");
        }
    }

    private static void RequireSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
    }

    private static void ValidateOptions(BinanceRestOptions options)
    {
        if (!options.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The REST base address must be absolute.", nameof(options));
        }

        if (options.RequestTimeout <= TimeSpan.Zero && options.RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static BinanceRestOptions CreateDefaultOptions(HttpClient httpClient)
    {
        return new BinanceRestOptions(httpClient.BaseAddress ?? new Uri("https://fapi.binance.com"));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        };
    }
}
