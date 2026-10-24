using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Exchange.Binance;

/// <summary>Signed and API-key-only USD-M private REST operations.</summary>
public sealed class BinancePrivateRestClient : IBinancePrivateRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    private readonly HttpClient _httpClient;
    private readonly BinancePrivateRestOptions _options;
    private readonly IBinancePrivateRequestSigner _signer;
    private readonly TimeProvider _timeProvider;

    public BinancePrivateRestClient(HttpClient httpClient, BinancePrivateRestOptions options,
        IBinancePrivateRequestSigner signer, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_options.RequestTimeout <= TimeSpan.Zero
            && _options.RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (_options.MaxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (_options.ReceiveWindowMilliseconds is <= 0 or > 60_000)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public Task<BinancePrivateOrder> PlaceOrderAsync(BinancePrivateOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOneWay(request.PositionSide);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = request.Symbol,
            ["side"] = ToWire(request.Side),
            ["type"] = ToWire(request.Type),
            ["quantity"] = Decimal(request.Quantity.Value),
            ["newClientOrderId"] = request.ClientOrderId,
            ["positionSide"] = "BOTH",
            ["reduceOnly"] = Bool(request.ReduceOnly),
            ["price"] = request.Price is null ? null : Decimal(request.Price.Value),
            ["timeInForce"] = request.TimeInForce is null ? null : ToWire(request.TimeInForce.Value)
        };
        return SendSignedObjectAsync(HttpMethod.Post, "/fapi/v1/order", parameters,
            ParseOrder, cancellationToken);
    }

    public Task<BinancePrivateAlgoOrder> PlaceAlgoOrderAsync(
        BinancePrivateAlgoOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOneWay(request.PositionSide);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["algoType"] = "CONDITIONAL",
            ["symbol"] = request.Symbol,
            ["side"] = ToWire(request.Side),
            ["type"] = ToWire(request.Type),
            ["quantity"] = Decimal(request.Quantity.Value),
            ["clientAlgoId"] = request.ClientAlgoId,
            ["triggerPrice"] = Decimal(request.TriggerPrice.Value),
            ["positionSide"] = "BOTH",
            ["reduceOnly"] = Bool(request.ReduceOnly)
        };
        return SendSignedObjectAsync(HttpMethod.Post, "/fapi/v1/algoOrder", parameters,
            ParseAlgoOrder, cancellationToken);
    }

    public Task<BinancePrivateOrder> QueryOrderAsync(string symbol, long? orderId,
        string? clientOrderId, CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        RequireSelector(orderId, clientOrderId);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["orderId"] = orderId?.ToString(CultureInfo.InvariantCulture),
            ["origClientOrderId"] = clientOrderId
        };
        return SendSignedObjectAsync(HttpMethod.Get, "/fapi/v1/order", parameters,
            ParseOrder, cancellationToken);
    }

    public Task CancelOrderAsync(string symbol, long? orderId, string? clientOrderId,
        CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        RequireSelector(orderId, clientOrderId);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["orderId"] = orderId?.ToString(CultureInfo.InvariantCulture),
            ["origClientOrderId"] = clientOrderId
        };
        return SendSignedAsync(HttpMethod.Delete, "/fapi/v1/order", parameters,
            cancellationToken);
    }

    public Task<IReadOnlyList<BinancePrivateOrder>> GetOpenOrdersAsync(string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (symbol is not null)
        {
            RequireSymbol(symbol);
        }

        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol?.ToUpperInvariant()
        };
        return SendSignedArrayAsync(HttpMethod.Get, "/fapi/v1/openOrders", parameters,
            ParseOrder, cancellationToken);
    }

    public Task<IReadOnlyList<BinancePrivateOrder>> GetAllOrdersAsync(string symbol,
        long? orderId = null, DateTimeOffset? startTime = null, DateTimeOffset? endTime = null,
        int? limit = null, CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        ValidateRange(startTime, endTime);
        ValidateLimit(limit, 1000);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["orderId"] = orderId?.ToString(CultureInfo.InvariantCulture),
            ["startTime"] = Milliseconds(startTime),
            ["endTime"] = Milliseconds(endTime),
            ["limit"] = limit?.ToString(CultureInfo.InvariantCulture)
        };
        return SendSignedArrayAsync(HttpMethod.Get, "/fapi/v1/allOrders", parameters,
            ParseOrder, cancellationToken);
    }

    public Task<IReadOnlyList<BinancePrivateTrade>> GetUserTradesAsync(string symbol,
        CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol.ToUpperInvariant()
        };
        return SendSignedArrayAsync(HttpMethod.Get, "/fapi/v1/userTrades", parameters,
            ParseTrade, cancellationToken);
    }

    public Task<BinancePrivateAlgoOrder> QueryAlgoOrderAsync(string symbol, long? algoId,
        string? clientAlgoId, CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        RequireSelector(algoId, clientAlgoId);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["algoId"] = algoId?.ToString(CultureInfo.InvariantCulture),
            ["clientAlgoId"] = clientAlgoId
        };
        return SendSignedObjectAsync(HttpMethod.Get, "/fapi/v1/algoOrder", parameters,
            ParseAlgoOrder, cancellationToken);
    }

    public Task CancelAlgoOrderAsync(string symbol, long? algoId, string? clientAlgoId,
        CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        RequireSelector(algoId, clientAlgoId);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["algoId"] = algoId?.ToString(CultureInfo.InvariantCulture),
            ["clientAlgoId"] = clientAlgoId
        };
        return SendSignedAsync(HttpMethod.Delete, "/fapi/v1/algoOrder", parameters,
            cancellationToken);
    }

    public Task<IReadOnlyList<BinancePrivateAlgoOrder>> GetOpenAlgoOrdersAsync(string symbol,
        CancellationToken cancellationToken = default)
    {
        RequireSymbol(symbol);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol.ToUpperInvariant()
        };
        return SendSignedArrayAsync(HttpMethod.Get, "/fapi/v1/openAlgoOrders", parameters,
            ParseAlgoOrder, cancellationToken);
    }

    public Task<BinancePositionMode> GetPositionModeAsync(
        CancellationToken cancellationToken = default)
    {
        return SendSignedDocumentAsync(HttpMethod.Get, "/fapi/v1/positionSide/dual",
            new Dictionary<string, string?>(StringComparer.Ordinal), ParsePositionMode,
            cancellationToken);
    }

    public Task<IReadOnlyList<BinancePrivatePosition>> GetPositionsAsync(string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (symbol is not null)
        {
            RequireSymbol(symbol);
        }

        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["symbol"] = symbol?.ToUpperInvariant()
        };
        return SendSignedArrayAsync(HttpMethod.Get, "/fapi/v3/positionRisk", parameters,
            ParsePosition, cancellationToken);
    }

    public async Task<string> StartUserDataStreamAsync(
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await SendApiKeyDocumentAsync(HttpMethod.Post,
            "/fapi/v1/listenKey", null, cancellationToken).ConfigureAwait(false);
        return RequiredString(document.RootElement, "listenKey");
    }

    public Task KeepAliveUserDataStreamAsync(string listenKey,
        CancellationToken cancellationToken = default)
    {
        RequireListenKey(listenKey);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["listenKey"] = listenKey
        };
        return SendApiKeyAsync(HttpMethod.Put, "/fapi/v1/listenKey", parameters,
            cancellationToken);
    }

    public Task CloseUserDataStreamAsync(string listenKey,
        CancellationToken cancellationToken = default)
    {
        RequireListenKey(listenKey);
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["listenKey"] = listenKey
        };
        return SendApiKeyAsync(HttpMethod.Delete, "/fapi/v1/listenKey", parameters,
            cancellationToken);
    }

    private async Task<T> SendSignedObjectAsync<T>(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?> parameters, Func<JsonElement, T> parser,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendSignedDocumentAsync(method, path, parameters,
            cancellationToken).ConfigureAwait(false);
        return parser(document.RootElement);
    }

    private async Task<IReadOnlyList<T>> SendSignedArrayAsync<T>(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?> parameters, Func<JsonElement, T> parser,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendSignedDocumentAsync(method, path, parameters,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Binance private response for {path} must be an array.");
        }

        return document.RootElement.EnumerateArray().Select(parser).ToArray();
    }

    private async Task<T> SendSignedDocumentAsync<T>(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?> parameters, Func<JsonElement, T> parser,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendSignedDocumentAsync(method, path, parameters,
            cancellationToken).ConfigureAwait(false);
        return parser(document.RootElement);
    }

    private async Task<JsonDocument> SendSignedDocumentAsync(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        Dictionary<string, string?> withCommon = AddCommon(parameters);
        string canonical = Encode(withCommon);
        string signature = _signer.Sign(canonical);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        withCommon["signature"] = signature;
        return await SendAsync(method, path, withCommon, signed: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendSignedAsync(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendSignedDocumentAsync(method, path, parameters,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new JsonException($"Binance private response for {path} must be an object.");
        }
    }

    private async Task<JsonDocument> SendApiKeyDocumentAsync(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?>? parameters, CancellationToken cancellationToken)
    {
        return await SendAsync(method, path, parameters
            ?? new Dictionary<string, string?>(StringComparer.Ordinal), signed: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SendApiKeyAsync(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendApiKeyDocumentAsync(method, path, parameters,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new JsonException($"Binance private response for {path} must be an object.");
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path,
        IReadOnlyDictionary<string, string?> parameters, bool signed,
        CancellationToken cancellationToken)
    {
        string encoded = Encode(parameters);
        Uri requestUri = BuildUri(path, method == HttpMethod.Get ? encoded : null);
        using HttpRequestMessage request = new(method, requestUri);
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _options.ApiKey);
        if (method != HttpMethod.Get && encoded.Length > 0)
        {
            request.Content = new StringContent(encoded, Encoding.UTF8,
                "application/x-www-form-urlencoded");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using HttpResponseMessage response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        string body = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response, path, body, signed);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonDocument.Parse("{}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new JsonException($"Binance private response for {path} was malformed.",
                exception);
        }
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var contentLength
            && contentLength > _options.MaxResponseBytes)
        {
            throw new InvalidOperationException("The Binance private response exceeds the configured size limit.");
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (bytes.LongLength > _options.MaxResponseBytes)
        {
            throw new InvalidOperationException("The Binance private response exceeds the configured size limit.");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static BinanceApiException CreateApiException(HttpResponseMessage response,
        string path, string body, bool signed)
    {
        int? code = null;
        string message = $"Binance private request failed with HTTP {(int)response.StatusCode}.";
        try
        {
            using JsonDocument error = JsonDocument.Parse(body);
            if (error.RootElement.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.TryGetInt32(out int parsedCode))
            {
                code = parsedCode;
            }

            if (error.RootElement.TryGetProperty("msg", out JsonElement msg)
                && msg.ValueKind == JsonValueKind.String)
            {
                string exchangeMessage = msg.GetString() ?? string.Empty;
                if (exchangeMessage.Contains("Unknown error", StringComparison.OrdinalIgnoreCase))
                {
                    message = "Binance private request returned an unknown outcome.";
                }
                else if (exchangeMessage.Contains("Service Unavailable",
                    StringComparison.OrdinalIgnoreCase))
                {
                    message = "Binance private service is unavailable.";
                }
                else if (exchangeMessage.Contains("Internal error",
                    StringComparison.OrdinalIgnoreCase))
                {
                    message = "Binance private service reported an internal error.";
                }
                else if (exchangeMessage.Length > 0)
                {
                    message = "Binance private request was rejected by the exchange.";
                }
            }
        }
        catch (JsonException)
        {
            // Keep the private response out of the exception to avoid echoing credentials.
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable
            && (message.Contains("unknown outcome", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unknown error", StringComparison.OrdinalIgnoreCase)))
        {
            message = $"{message} Private execution outcome is unknown; reconciliation is required.";
        }

        _ = signed;
        return new BinanceApiException(response.StatusCode, code, message, string.Empty,
            new Uri(new Uri("https://fapi.binance.com"), path), response.Headers.RetryAfter?.Delta);
    }

    private Dictionary<string, string?> AddCommon(IReadOnlyDictionary<string, string?> parameters)
    {
        Dictionary<string, string?> result = new(parameters, StringComparer.Ordinal)
        {
            ["timestamp"] = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                .ToString(CultureInfo.InvariantCulture),
            ["recvWindow"] = _options.ReceiveWindowMilliseconds?.ToString(
                CultureInfo.InvariantCulture)
        };
        return result;
    }

    private Uri BuildUri(string path, string? query)
    {
        UriBuilder builder = new(new Uri(_options.BaseAddress, path));
        builder.Query = query ?? string.Empty;
        return builder.Uri;
    }

    private static string Encode(IReadOnlyDictionary<string, string?> values)
    {
        return string.Join('&', values.Where(static pair => pair.Value is not null)
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
    }

    private static BinancePrivateOrder ParseOrder(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Binance order response must be an object.");
        }

        string statusText = OptionalString(root, "status") ?? "UNKNOWN";
        return new BinancePrivateOrder
        {
            OrderId = RequiredInt64(root, "orderId"),
            ClientOrderId = OptionalString(root, "clientOrderId") ?? string.Empty,
            Symbol = RequiredString(root, "symbol"),
            Side = ParseSide(RequiredString(root, "side")),
            PositionSide = ParsePositionSide(OptionalString(root, "positionSide") ?? "BOTH"),
            Type = ParseOrderType(OptionalString(root, "type") ?? "MARKET"),
            Status = ParseStatus(statusText),
            StatusText = statusText,
            TimeInForce = OptionalString(root, "timeInForce") is { } tif
                ? ParseTimeInForce(tif) : null,
            OriginalQuantity = OptionalDecimal(root, "origQty") ?? 0m,
            ExecutedQuantity = OptionalDecimal(root, "executedQty") ?? 0m,
            Price = OptionalDecimal(root, "price") ?? 0m,
            AveragePrice = OptionalDecimal(root, "avgPrice") ?? 0m,
            StopPrice = OptionalDecimal(root, "stopPrice"),
            ReduceOnly = OptionalBoolean(root, "reduceOnly") ?? false,
            ClosePosition = OptionalBoolean(root, "closePosition") ?? false,
            OrderTimeMilliseconds = OptionalInt64(root, "time") ?? 0L,
            UpdateTimeMilliseconds = OptionalInt64(root, "updateTime") ?? 0L,
            Raw = root.Clone()
        };
    }

    private static BinancePrivateAlgoOrder ParseAlgoOrder(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Binance Algo order response must be an object.");
        }

        string status = OptionalString(root, "algoStatus")
            ?? OptionalString(root, "status") ?? "UNKNOWN";
        string orderTypeText = OptionalString(root, "orderType")
            ?? OptionalString(root, "type") ?? string.Empty;
        Dictionary<string, JsonElement> unknown = root.EnumerateObject()
            .Where(static property => property.Name is not ("algoId" or "clientAlgoId" or "symbol"
                or "side" or "positionSide" or "algoStatus" or "status" or "quantity"
                or "origQty" or "triggerPrice" or "reduceOnly" or "orderType" or "type"))
            .ToDictionary(static property => property.Name, static property => property.Value.Clone(),
                StringComparer.Ordinal);
        return new BinancePrivateAlgoOrder
        {
            AlgoId = OptionalInt64(root, "algoId") ?? 0L,
            ClientAlgoId = OptionalString(root, "clientAlgoId") ?? string.Empty,
            Symbol = OptionalString(root, "symbol") ?? string.Empty,
            Side = ParseSide(OptionalString(root, "side") ?? "BUY"),
            PositionSide = ParsePositionSide(OptionalString(root, "positionSide") ?? "BOTH"),
            OrderTypeText = orderTypeText,
            OrderType = string.IsNullOrWhiteSpace(orderTypeText)
                ? null : ParseAlgoOrderType(orderTypeText),
            Status = status,
            Quantity = OptionalDecimal(root, "quantity") ?? OptionalDecimal(root, "origQty") ?? 0m,
            TriggerPrice = OptionalDecimal(root, "triggerPrice"),
            ReduceOnly = OptionalBoolean(root, "reduceOnly") ?? false,
            RequiresReconciliation = !KnownAlgoStatuses.Contains(status),
            UnknownFields = unknown,
            Raw = root.Clone()
        };
    }

    private static BinancePrivateTrade ParseTrade(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Binance trade response must be an object.");
        }

        return new BinancePrivateTrade
        {
            TradeId = RequiredInt64(root, "id"),
            OrderId = RequiredInt64(root, "orderId"),
            Symbol = RequiredString(root, "symbol"),
            Side = ParseSide(OptionalString(root, "side") ?? "BUY"),
            PositionSide = ParsePositionSide(OptionalString(root, "positionSide") ?? "BOTH"),
            Price = RequiredDecimal(root, "price"),
            Quantity = RequiredDecimal(root, "qty"),
            QuoteQuantity = OptionalDecimal(root, "quoteQty") ?? 0m,
            Commission = OptionalDecimal(root, "commission") ?? 0m,
            CommissionAsset = OptionalString(root, "commissionAsset") ?? string.Empty,
            RealizedPnl = OptionalDecimal(root, "realizedPnl") ?? 0m,
            Buyer = OptionalBoolean(root, "buyer") ?? false,
            Maker = OptionalBoolean(root, "maker") ?? false,
            TradeTimeMilliseconds = RequiredInt64(root, "time")
        };
    }

    private static BinancePrivatePosition ParsePosition(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Binance position response must be an object.");
        }

        return new BinancePrivatePosition
        {
            Symbol = RequiredString(root, "symbol"),
            PositionAmount = RequiredDecimal(root, "positionAmt"),
            PositionSide = ParsePositionSide(OptionalString(root, "positionSide") ?? "BOTH"),
            EntryPrice = OptionalDecimal(root, "entryPrice") ?? 0m,
            BreakEvenPrice = OptionalDecimal(root, "breakEvenPrice"),
            MarkPrice = OptionalDecimal(root, "markPrice"),
            UnrealizedProfit = OptionalDecimal(root, "unRealizedProfit"),
            MarginType = OptionalString(root, "marginType"),
            UpdateTimeMilliseconds = OptionalInt64(root, "updateTime") ?? 0L
        };
    }

    private static BinancePositionMode ParsePositionMode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("dualSidePosition", out JsonElement value)
            || value.ValueKind != JsonValueKind.False && value.ValueKind != JsonValueKind.True)
        {
            throw new BinancePrivateProtocolException(
                "Binance position mode is absent or unknown; execution is blocked.");
        }

        if (value.GetBoolean())
        {
            throw new BinancePrivateProtocolException(
                "Binance Hedge Mode is unsupported; only One-way/BOTH is allowed.");
        }

        return BinancePositionMode.OneWay;
    }

    private static readonly HashSet<string> KnownAlgoStatuses = new(StringComparer.Ordinal)
    {
        "NEW", "WORKING", "TRIGGERED", "CANCELED", "EXPIRED"
    };

    private static BinanceOrderSide ParseSide(string value) => value switch
    {
        "BUY" => BinanceOrderSide.Buy,
        "SELL" => BinanceOrderSide.Sell,
        _ => throw new BinancePrivateProtocolException($"Unknown Binance side '{value}'.")
    };

    internal static BinancePositionSide ParsePositionSide(string value) => value switch
    {
        "BOTH" => BinancePositionSide.Both,
        "LONG" or "SHORT" => throw new BinancePrivateProtocolException(
            "Hedge Mode position side is unsupported; only BOTH is allowed."),
        _ => throw new BinancePrivateProtocolException(
            $"Unknown Binance position side '{value}'; execution is blocked.")
    };

    private static BinanceOrderType ParseOrderType(string value) => value switch
    {
        "MARKET" => BinanceOrderType.Market,
        "LIMIT" => BinanceOrderType.Limit,
        "STOP" => BinanceOrderType.Stop,
        "TAKE_PROFIT" => BinanceOrderType.TakeProfit,
        "STOP_MARKET" => BinanceOrderType.StopMarket,
        "TAKE_PROFIT_MARKET" => BinanceOrderType.TakeProfitMarket,
        "TRAILING_STOP_MARKET" => BinanceOrderType.TrailingStopMarket,
        _ => throw new BinancePrivateProtocolException($"Unknown Binance order type '{value}'.")
    };

    private static BinanceOrderStatus ParseStatus(string value) => value switch
    {
        "NEW" => BinanceOrderStatus.New,
        "PARTIALLY_FILLED" => BinanceOrderStatus.PartiallyFilled,
        "FILLED" => BinanceOrderStatus.Filled,
        "CANCELED" => BinanceOrderStatus.Canceled,
        "EXPIRED" => BinanceOrderStatus.Expired,
        "EXPIRED_IN_MATCH" => BinanceOrderStatus.ExpiredInMatch,
        _ => BinanceOrderStatus.Unknown
    };

    private static BinanceTimeInForce ParseTimeInForce(string value) => value switch
    {
        "GTC" => BinanceTimeInForce.GoodTillCancel,
        "IOC" => BinanceTimeInForce.ImmediateOrCancel,
        "FOK" => BinanceTimeInForce.FillOrKill,
        "GTX" => BinanceTimeInForce.GoodTillCrossing,
        "GTD" => BinanceTimeInForce.GoodTillDate,
        _ => throw new BinancePrivateProtocolException($"Unknown Binance time in force '{value}'.")
    };

    internal static BinanceOrderSide ParseWireSide(string value) => ParseSide(value);

    internal static BinanceOrderType ParseWireOrderType(string value) => ParseOrderType(value);

    internal static BinanceOrderStatus ParseWireStatus(string value) => ParseStatus(value);

    internal static BinanceTimeInForce? ParseWireTimeInForce(string? value) => value is null
        ? null : ParseTimeInForce(value);

    internal static BinancePrivateOrder ParseOrderElement(JsonElement root) => ParseOrder(root);

    internal static BinancePrivateTrade ParseTradeElement(JsonElement root) => ParseTrade(root);

    internal static BinancePrivatePosition ParsePositionElement(JsonElement root) => ParsePosition(root);

    internal static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException($"Binance private response requires string field '{name}'.");
        }

        return text;
    }

    internal static string? OptionalString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    internal static long RequiredInt64(JsonElement root, string name)
    {
        long? value = OptionalInt64(root, name);
        return value ?? throw new JsonException($"Binance private response requires int64 field '{name}'.");
    }

    internal static long? OptionalInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out number) ? number : null;
    }

    internal static decimal RequiredDecimal(JsonElement root, string name)
    {
        decimal? value = OptionalDecimal(root, name);
        return value ?? throw new JsonException($"Binance private response requires decimal field '{name}'.");
    }

    internal static decimal? OptionalDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        try
        {
            return value.ValueKind == JsonValueKind.Number ? value.GetDecimal()
                : value.ValueKind == JsonValueKind.String
                    ? decimal.Parse(value.GetString()!, NumberStyles.Float,
                        CultureInfo.InvariantCulture) : null;
        }
        catch (FormatException exception)
        {
            throw new JsonException($"Binance private response field '{name}' was not decimal.",
                exception);
        }
    }

    internal static bool? OptionalBoolean(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    }

    private static string ToWire(BinanceOrderSide value) => value switch
    {
        BinanceOrderSide.Buy => "BUY",
        BinanceOrderSide.Sell => "SELL",
        _ => throw new BinancePrivateProtocolException("Unknown order side.")
    };

    private static string ToWire(BinanceOrderType value) => value switch
    {
        BinanceOrderType.Market => "MARKET",
        BinanceOrderType.Limit => "LIMIT",
        BinanceOrderType.Stop => "STOP",
        BinanceOrderType.TakeProfit => "TAKE_PROFIT",
        BinanceOrderType.StopMarket => "STOP_MARKET",
        BinanceOrderType.TakeProfitMarket => "TAKE_PROFIT_MARKET",
        BinanceOrderType.TrailingStopMarket => "TRAILING_STOP_MARKET",
        _ => throw new BinancePrivateProtocolException("Unknown order type.")
    };

    private static string ToWire(BinanceAlgoOrderType value) => value switch
    {
        BinanceAlgoOrderType.Stop => "STOP",
        BinanceAlgoOrderType.TakeProfit => "TAKE_PROFIT",
        BinanceAlgoOrderType.StopMarket => "STOP_MARKET",
        BinanceAlgoOrderType.TakeProfitMarket => "TAKE_PROFIT_MARKET",
        BinanceAlgoOrderType.TrailingStopMarket => "TRAILING_STOP_MARKET",
        _ => throw new BinancePrivateProtocolException("Unknown Algo order type.")
    };

    private static BinanceAlgoOrderType ParseAlgoOrderType(string value) => value switch
    {
        "STOP" => BinanceAlgoOrderType.Stop,
        "TAKE_PROFIT" => BinanceAlgoOrderType.TakeProfit,
        "STOP_MARKET" => BinanceAlgoOrderType.StopMarket,
        "TAKE_PROFIT_MARKET" => BinanceAlgoOrderType.TakeProfitMarket,
        "TRAILING_STOP_MARKET" => BinanceAlgoOrderType.TrailingStopMarket,
        _ => throw new BinancePrivateProtocolException(
            $"Unknown Binance Algo order type '{value}'; execution is blocked.")
    };

    private static string ToWire(BinanceTimeInForce value) => value switch
    {
        BinanceTimeInForce.GoodTillCancel => "GTC",
        BinanceTimeInForce.ImmediateOrCancel => "IOC",
        BinanceTimeInForce.FillOrKill => "FOK",
        BinanceTimeInForce.GoodTillCrossing => "GTX",
        BinanceTimeInForce.GoodTillDate => "GTD",
        _ => throw new BinancePrivateProtocolException("Unknown time in force.")
    };

    private static string Decimal(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? Milliseconds(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds()
        .ToString(CultureInfo.InvariantCulture);

    private static void RequireOneWay(BinancePositionSide positionSide)
    {
        if (positionSide != BinancePositionSide.Both)
        {
            throw new BinancePrivateProtocolException(
                "Only One-way/BOTH position mode is supported.");
        }
    }

    private static void RequireSelector<T>(T? numeric, string? text) where T : struct
    {
        if (numeric is null && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("An exchange identifier is required.");
        }
    }

    private static void RequireSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
    }

    private static void RequireListenKey(string listenKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenKey);
        if (listenKey.Contains('&') || listenKey.Contains('='))
        {
            throw new ArgumentException("A listenKey must be a single opaque token.", nameof(listenKey));
        }
    }

    private static void ValidateRange(DateTimeOffset? startTime, DateTimeOffset? endTime)
    {
        if (startTime is { } start && endTime is { } end && end < start)
        {
            throw new ArgumentException("End time cannot precede start time.", nameof(endTime));
        }
    }

    private static void ValidateLimit(int? limit, int max)
    {
        if (limit is int value && (value <= 0 || value > max))
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }
}
