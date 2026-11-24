using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Core;

namespace TradingBot.App;

internal readonly record struct BybitDemoOrderAcknowledgement(string ExchangeOrderId, string ClientOrderId);
internal readonly record struct BybitDemoAccountInfo(string MarginMode, DateTimeOffset ObservedAt);
internal readonly record struct BybitDemoPositionInfo(string Symbol, decimal Quantity, Direction? Direction,
    decimal Leverage, decimal? AverageEntry, decimal? StopLoss, bool ReduceOnly, DateTimeOffset ObservedAt);
internal readonly record struct BybitDemoOpenOrder(string ExchangeOrderId, string ClientOrderId,
    string? ParentClientOrderId, string Symbol, string Status, string StopOrderType,
    int PositionIndex, Direction Side, decimal Quantity, decimal? TriggerPrice, string TriggerBy,
    bool ReduceOnly, bool CloseOnTrigger);
internal sealed record BybitDemoOpenOrderSnapshot(DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<BybitDemoOpenOrder> Orders);
internal readonly record struct BybitDemoAccountPosition(string Symbol, int PositionIndex, Direction Direction,
    decimal Quantity, decimal? Leverage, decimal AverageEntry, decimal? StopLoss,
    string Status, bool ReduceOnly, long Sequence);
internal sealed record BybitDemoPositionSnapshot(DateTimeOffset StartedAt, DateTimeOffset CompletedAt,
    IReadOnlyList<BybitDemoAccountPosition> Positions);

/// <summary>The submission may have reached Bybit; reconcile before any retry.</summary>
internal sealed class BybitDemoSubmissionUnknownException : Exception
{
    public BybitDemoSubmissionUnknownException() : base("The Bybit demo order outcome requires reconciliation.") { }
}

/// <summary>A stop update may have reached Bybit and must be reconciled before retry.</summary>
internal sealed class BybitDemoProtectionUnknownException : Exception
{
    public BybitDemoProtectionUnknownException() : base("The Bybit demo protection outcome requires reconciliation.") { }
}

/// <summary>Bybit Demo REST boundary. An acknowledgement is not a fill.</summary>
internal sealed partial class BybitDemoRestClient : IDisposable
{
    private static readonly Uri OrderEndpoint = new("https://api-demo.bybit.com/v5/order/create");
    private static readonly Uri AccountEndpoint = new("https://api-demo.bybit.com/v5/account/info");
    private static readonly Uri TradingStopEndpoint = new("https://api-demo.bybit.com/v5/position/trading-stop");
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly byte[] _secret;
    private readonly TimeProvider _clock;
    private readonly int _receiveWindowMilliseconds;
    private bool _disposed;

    internal BybitDemoRestClient(HttpMessageHandler handler, string apiKey, string secret,
        TimeProvider clock, int receiveWindowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(receiveWindowMilliseconds);
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.AllowAutoRedirect = false;
        }
        else if (handler is HttpClientHandler clientHandler)
        {
            clientHandler.AllowAutoRedirect = false;
        }
        _http = new HttpClient(handler) { MaxResponseContentBufferSize = 128 * 1024 };
        _apiKey = apiKey;
        _secret = Encoding.UTF8.GetBytes(secret);
        _clock = clock;
        _receiveWindowMilliseconds = receiveWindowMilliseconds;
    }

    public static BybitDemoRestClient Create(string apiKey, string secret,
        TimeProvider clock, int receiveWindowMilliseconds) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }, apiKey, secret,
            clock, receiveWindowMilliseconds);

    public async Task<BybitDemoOrderAcknowledgement> PlaceMarketEntryAsync(string symbol,
        Direction direction, decimal quantity, decimal stopLoss, decimal slippagePercent,
        string clientOrderId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(symbol) || symbol.Length > 20
            || symbol.Any(static character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character))
            || direction is not (Direction.Long or Direction.Short)
            || quantity <= 0m || stopLoss <= 0m
            || slippagePercent is < 0.01m or > 10m || decimal.Round(slippagePercent, 2) != slippagePercent
            || string.IsNullOrEmpty(clientOrderId) || clientOrderId.Length > 36
            || clientOrderId.Any(static character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_')))
        {
            throw new ArgumentException("The Bybit demo entry parameters are invalid.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string body = JsonSerializer.Serialize(new
        {
            category = "linear",
            symbol,
            side = direction == Direction.Long ? "Buy" : "Sell",
            orderType = "Market",
            qty = Number(quantity),
            positionIdx = 0,
            orderLinkId = clientOrderId,
            stopLoss = Number(stopLoss),
            slTriggerBy = "LastPrice",
            tpslMode = "Full",
            slOrderType = "Market",
            slippageToleranceType = "Percent",
            slippageTolerance = Number(slippagePercent)
        });
        return await SendOrderAsync(body, clientOrderId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BybitDemoOrderAcknowledgement> SendOrderAsync(string body,
        string clientOrderId, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Post, OrderEndpoint, body);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new BybitDemoSubmissionUnknownException();
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("retCode", out JsonElement code) || !code.TryGetInt32(out int value)
                || value != 0 || !root.TryGetProperty("result", out JsonElement result)
                || !result.TryGetProperty("orderId", out JsonElement orderId)
                || !result.TryGetProperty("orderLinkId", out JsonElement linkId)
                || orderId.ValueKind != JsonValueKind.String || linkId.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(orderId.GetString())
                || !StringComparer.Ordinal.Equals(linkId.GetString(), clientOrderId))
            {
                throw new BybitDemoSubmissionUnknownException();
            }
            return new BybitDemoOrderAcknowledgement(orderId.GetString()!, clientOrderId);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or OperationCanceledException or JsonException or InvalidOperationException)
        {
            throw new BybitDemoSubmissionUnknownException();
        }
    }

    /// <summary>Submits a reduce-only conditional market target; acknowledgement is not execution.</summary>
    public async Task<BybitDemoOrderAcknowledgement> PlaceTakeProfitAsync(string symbol, Direction positionDirection,
        decimal quantity, decimal triggerPrice, decimal currentLastPrice, string clientOrderId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateExit(symbol, positionDirection, quantity, clientOrderId);
        if (triggerPrice <= 0m || currentLastPrice <= 0m
            || (positionDirection == Direction.Long ? triggerPrice <= currentLastPrice : triggerPrice >= currentLastPrice))
        {
            throw new ArgumentException("The Bybit demo target is invalid.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string body = JsonSerializer.Serialize(new
        {
            category = "linear",
            symbol,
            side = positionDirection == Direction.Long ? "Sell" : "Buy",
            orderType = "Market",
            qty = Number(quantity),
            positionIdx = 0,
            orderLinkId = clientOrderId,
            triggerPrice = Number(triggerPrice),
            triggerDirection = positionDirection == Direction.Long ? 1 : 2,
            triggerBy = "LastPrice",
            reduceOnly = true
        });
        return await SendOrderAsync(body, clientOrderId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Submits a reduce-only market exit; a partial fill still requires reconciliation.</summary>
    public async Task<BybitDemoOrderAcknowledgement> PlaceMarketExitAsync(string symbol, Direction positionDirection,
        decimal quantity, decimal slippagePercent, string clientOrderId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateExit(symbol, positionDirection, quantity, clientOrderId);
        if (slippagePercent is < 0.01m or > 10m || decimal.Round(slippagePercent, 2) != slippagePercent)
        {
            throw new ArgumentException("The Bybit demo exit slippage is invalid.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string body = JsonSerializer.Serialize(new
        {
            category = "linear",
            symbol,
            side = positionDirection == Direction.Long ? "Sell" : "Buy",
            orderType = "Market",
            qty = Number(quantity),
            positionIdx = 0,
            orderLinkId = clientOrderId,
            reduceOnly = true,
            slippageToleranceType = "Percent",
            slippageTolerance = Number(slippagePercent)
        });
        return await SendOrderAsync(body, clientOrderId, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateExit(string symbol, Direction direction, decimal quantity, string clientOrderId)
    {
        if (string.IsNullOrEmpty(symbol) || symbol.Length > 20
            || symbol.Any(static c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            || direction is not (Direction.Long or Direction.Short) || quantity <= 0m
            || string.IsNullOrEmpty(clientOrderId) || clientOrderId.Length > 36
            || clientOrderId.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new ArgumentException("The Bybit demo exit parameters are invalid.");
        }
    }

    /// <summary>Requests a full-position stop change; an acknowledgement does not prove protection.</summary>
    public async Task RequestFullStopAsync(string symbol, decimal stopLoss, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(symbol) || symbol.Length > 20
            || symbol.Any(static character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character))
            || stopLoss <= 0m)
        {
            throw new ArgumentException("The Bybit demo stop parameters are invalid.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        string body = JsonSerializer.Serialize(new
        {
            category = "linear",
            symbol,
            tpslMode = "Full",
            positionIdx = 0,
            stopLoss = Number(stopLoss),
            slTriggerBy = "LastPrice"
        });
        try
        {
            using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Post, TradingStopEndpoint, body);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new BybitDemoProtectionUnknownException();
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("retCode", out JsonElement code) || !code.TryGetInt32(out int value) || value != 0)
            {
                throw new BybitDemoProtectionUnknownException();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or OperationCanceledException or JsonException or InvalidOperationException)
        {
            throw new BybitDemoProtectionUnknownException();
        }
    }

    public async Task<BybitDemoAccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Get, AccountEndpoint, string.Empty);
        DateTimeOffset observedAt = _clock.GetUtcNow();
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.GetProperty("retCode").GetInt32() != 0
                || root.GetProperty("result").GetProperty("marginMode").GetString() is not { } marginMode
                || marginMode is not ("ISOLATED_MARGIN" or "REGULAR_MARGIN" or "PORTFOLIO_MARGIN"))
            {
                throw new InvalidDataException("The Bybit demo account information is unavailable.");
            }
            return new BybitDemoAccountInfo(marginMode, observedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("The Bybit demo account information is unavailable.");
        }
    }

    public async Task<BybitDemoPositionInfo> GetPositionAsync(string symbol, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(symbol) || symbol.Length > 20
            || symbol.Any(static character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("The symbol is invalid.", nameof(symbol));
        }
        string query = $"category=linear&symbol={symbol}";
        Uri endpoint = new($"https://api-demo.bybit.com/v5/position/list?{query}");
        DateTimeOffset observedAt = _clock.GetUtcNow();
        using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Get, endpoint, query);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            JsonElement result = root.GetProperty("result");
            JsonElement list = result.GetProperty("list");
            if (root.GetProperty("retCode").GetInt32() != 0
                || result.GetProperty("category").GetString() != "linear"
                || list.GetArrayLength() != 1)
            {
                throw new InvalidDataException("The Bybit demo position information is unavailable.");
            }
            JsonElement position = list[0];
            decimal quantity = RequiredNumber(position, "size");
            decimal leverage = RequiredNumber(position, "leverage");
            string? side = position.GetProperty("side").GetString();
            decimal? entry = OptionalNumber(position, "avgPrice");
            decimal? stop = OptionalNumber(position, "stopLoss");
            if (!StringComparer.Ordinal.Equals(position.GetProperty("symbol").GetString(), symbol)
                || position.GetProperty("positionIdx").GetInt32() != 0
                || position.GetProperty("positionStatus").GetString() != "Normal"
                || quantity < 0m || leverage <= 0m
                || (quantity == 0m && side != string.Empty)
                || (quantity > 0m && (side is not ("Buy" or "Sell") || entry is not > 0m))
                || stop is < 0m)
            {
                throw new InvalidDataException("The Bybit demo position information is unavailable.");
            }
            Direction? direction = side switch { "Buy" => Direction.Long, "Sell" => Direction.Short, _ => null };
            return new BybitDemoPositionInfo(symbol, quantity, direction, leverage, entry,
                stop is > 0m ? stop : null, position.GetProperty("isReduceOnly").GetBoolean(), observedAt);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The Bybit demo position information is unavailable.");
        }
    }

    /// <summary>Reads open USDT-linear positions across the account; pages are not atomic.</summary>
    public async Task<BybitDemoPositionSnapshot> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset startedAt = _clock.GetUtcNow();
        List<BybitDemoAccountPosition> positions = [];
        HashSet<(string Symbol, int Index)> identities = [];
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string cursor = string.Empty;
        try
        {
            for (int page = 0; page < 1000; page++)
            {
                string query = "category=linear&settleCoin=USDT&limit=200";
                if (cursor.Length != 0)
                {
                    query += "&cursor=" + Uri.EscapeDataString(cursor);
                }
                Uri endpoint = new($"https://api-demo.bybit.com/v5/position/list?{query}");
                using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Get, endpoint, query);
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement root = document.RootElement;
                SignalOnlyConfiguration.RejectDuplicateProperties(root);
                JsonElement result = root.GetProperty("result");
                if (root.GetProperty("retCode").GetInt32() != 0 || result.GetProperty("category").GetString() != "linear")
                {
                    throw new InvalidDataException("The demo position snapshot is unavailable.");
                }
                foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
                {
                    string symbol = RequiredOrderText(item, "symbol");
                    string side = RequiredOrderText(item, "side");
                    string status = RequiredOrderText(item, "positionStatus");
                    int index = item.GetProperty("positionIdx").GetInt32();
                    decimal size = RequiredNumber(item, "size");
                    decimal entry = RequiredNumber(item, "avgPrice");
                    decimal? leverage = OptionalNumber(item, "leverage");
                    decimal? stop = OptionalNumber(item, "stopLoss");
                    if (symbol.Length > 20 || symbol.Any(static c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
                        || side is not ("Buy" or "Sell") || index is < 0 or > 2 || size <= 0m || entry <= 0m
                        || leverage is <= 0m || stop is < 0m || !identities.Add((symbol, index)))
                    {
                        throw new InvalidDataException("The demo position snapshot is unavailable.");
                    }
                    positions.Add(new BybitDemoAccountPosition(symbol, index, side == "Buy" ? Direction.Long : Direction.Short,
                        size, leverage, entry, stop is > 0m ? stop : null, status,
                        item.GetProperty("isReduceOnly").GetBoolean(), item.GetProperty("seq").GetInt64()));
                }
                cursor = result.GetProperty("nextPageCursor").GetString()
                    ?? throw new InvalidDataException("The demo position snapshot is unavailable.");
                if (cursor.Length == 0)
                {
                    return new BybitDemoPositionSnapshot(startedAt, _clock.GetUtcNow(), positions.AsReadOnly());
                }
                if (cursor.Length > 2048 || !cursors.Add(cursor))
                {
                    throw new InvalidDataException("The demo position pagination is invalid.");
                }
            }
            throw new InvalidDataException("The demo position pagination is incomplete.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The demo position snapshot is unavailable.");
        }
    }

    /// <summary>Reads all USDT-linear open orders. Pagination is not an atomic account snapshot.</summary>
    public async Task<BybitDemoOpenOrderSnapshot> GetOpenOrdersAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset startedAt = _clock.GetUtcNow();
        List<BybitDemoOpenOrder> orders = [];
        HashSet<string> orderIds = new(StringComparer.Ordinal);
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string cursor = string.Empty;
        try
        {
            for (int page = 0; page < 1000; page++)
            {
                string query = "category=linear&settleCoin=USDT&openOnly=0&limit=50";
                if (cursor.Length != 0)
                {
                    query += "&cursor=" + Uri.EscapeDataString(cursor);
                }
                Uri endpoint = new($"https://api-demo.bybit.com/v5/order/realtime?{query}");
                using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Get, endpoint, query);
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                JsonElement root = document.RootElement;
                SignalOnlyConfiguration.RejectDuplicateProperties(root);
                JsonElement result = root.GetProperty("result");
                if (root.GetProperty("retCode").GetInt32() != 0 || result.GetProperty("category").GetString() != "linear")
                {
                    throw new InvalidDataException("The demo open-order snapshot is unavailable.");
                }
                foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
                {
                    string orderId = RequiredOrderText(item, "orderId");
                    string clientId = item.GetProperty("orderLinkId").GetString()
                        ?? throw new InvalidDataException("The demo open-order snapshot is unavailable.");
                    string? parentId = item.TryGetProperty("parentOrderLinkId", out JsonElement parent)
                        ? parent.GetString() ?? throw new InvalidDataException("The demo open-order snapshot is unavailable.")
                        : null;
                    string symbol = RequiredOrderText(item, "symbol");
                    string status = RequiredOrderText(item, "orderStatus");
                    string stopType = item.GetProperty("stopOrderType").GetString()
                        ?? throw new InvalidDataException("The demo open-order snapshot is unavailable.");
                    int positionIndex = item.GetProperty("positionIdx").GetInt32();
                    string side = RequiredOrderText(item, "side");
                    decimal quantity = RequiredNumber(item, "qty");
                    decimal? triggerPrice = OptionalNumber(item, "triggerPrice");
                    if (symbol.Length > 20 || symbol.Any(static c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
                        || clientId.Length > 36 || parentId?.Length > 36 || positionIndex is < 0 or > 2
                        || side is not ("Buy" or "Sell") || quantity <= 0m || triggerPrice is < 0m
                        || status is not ("New" or "PartiallyFilled" or "Untriggered" or "Triggered")
                        || !orderIds.Add(orderId))
                    {
                        throw new InvalidDataException("The demo open-order snapshot is unavailable.");
                    }
                    orders.Add(new BybitDemoOpenOrder(orderId, clientId, parentId, symbol, status, stopType,
                        positionIndex, side == "Buy" ? Direction.Long : Direction.Short, quantity,
                        triggerPrice is > 0m ? triggerPrice : null, RequiredOrderText(item, "triggerBy"),
                        item.GetProperty("reduceOnly").GetBoolean(), item.GetProperty("closeOnTrigger").GetBoolean()));
                }
                cursor = result.GetProperty("nextPageCursor").GetString()
                    ?? throw new InvalidDataException("The demo open-order snapshot is unavailable.");
                if (cursor.Length == 0)
                {
                    return new BybitDemoOpenOrderSnapshot(startedAt, _clock.GetUtcNow(), orders.AsReadOnly());
                }
                if (cursor.Length > 2048 || !cursors.Add(cursor))
                {
                    throw new InvalidDataException("The demo open-order pagination is invalid.");
                }
            }
            throw new InvalidDataException("The demo open-order pagination is incomplete.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("The demo open-order snapshot is unavailable.");
        }
    }

    private static string RequiredOrderText(JsonElement item, string name)
    {
        string? value = item.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 128
            ? value : throw new InvalidDataException("The demo open-order snapshot is unavailable.");
    }

    private static decimal RequiredNumber(JsonElement element, string property) =>
        decimal.Parse(element.GetProperty(property).GetString()!, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture);

    private static decimal? OptionalNumber(JsonElement element, string property)
    {
        string? value = element.GetProperty(property).GetString();
        return string.IsNullOrEmpty(value) ? null
            : decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private HttpRequestMessage CreateSignedRequest(HttpMethod method, Uri endpoint, string payload)
    {
        string timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        string window = _receiveWindowMilliseconds.ToString(CultureInfo.InvariantCulture);
        byte[] message = Encoding.UTF8.GetBytes(timestamp + _apiKey + window + payload);
        byte[] signature;
        try
        {
            signature = HMACSHA256.HashData(_secret, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
        try
        {
            HttpRequestMessage request = new(method, endpoint);
            request.Headers.Add("X-BAPI-API-KEY", _apiKey);
            request.Headers.Add("X-BAPI-TIMESTAMP", timestamp);
            request.Headers.Add("X-BAPI-RECV-WINDOW", window);
            request.Headers.Add("X-BAPI-SIGN", Convert.ToHexString(signature).ToLowerInvariant());
            if (method == HttpMethod.Post)
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }
            return request;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _http.Dispose();
        CryptographicOperations.ZeroMemory(_secret);
    }

    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}
