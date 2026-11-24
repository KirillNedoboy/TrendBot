using System.Globalization;
using System.Text.Json;

namespace TradingBot.App;

internal readonly record struct BybitDemoPublicQuote(string Symbol, decimal Bid, decimal Ask,
    DateTimeOffset ExchangeAt, DateTimeOffset ReceivedAt);
internal readonly record struct BybitDemoInstrument(string Symbol, decimal TickSize, decimal MinimumPrice,
    decimal MaximumPrice, decimal QuantityStep, decimal MinimumQuantity, decimal MinimumNotional,
    decimal MaximumMarketQuantity, decimal MaximumLeverage, DateTimeOffset ObservedAt);

/// <summary>Read-only Bybit Demo public data; no credentials are sent.</summary>
internal sealed class BybitDemoPublicMarketClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    internal BybitDemoPublicMarketClient(HttpMessageHandler handler, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(clock);
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.AllowAutoRedirect = false;
        }
        else if (handler is HttpClientHandler clientHandler)
        {
            clientHandler.AllowAutoRedirect = false;
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 128 * 1024 };
        _clock = clock;
    }

    public static BybitDemoPublicMarketClient Create(TimeProvider clock) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }, clock);

    public async Task<BybitDemoPublicQuote> GetQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        ValidateSymbol(symbol);
        Uri endpoint = new($"https://api-demo.bybit.com/v5/market/orderbook?category=linear&symbol={symbol}&limit=1");
        using HttpResponseMessage response = await _http.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        DateTimeOffset receivedAt = _clock.GetUtcNow();
        try
        {
            JsonElement root = document.RootElement;
            JsonElement result = root.GetProperty("result");
            if (root.GetProperty("retCode").GetInt32() != 0
                || !StringComparer.Ordinal.Equals(result.GetProperty("s").GetString(), symbol))
            {
                throw new InvalidDataException("The public order book response is invalid.");
            }
            decimal bid = ReadPrice(result.GetProperty("b"));
            decimal ask = ReadPrice(result.GetProperty("a"));
            DateTimeOffset exchangeAt = DateTimeOffset.FromUnixTimeMilliseconds(result.GetProperty("ts").GetInt64());
            if (bid > ask || exchangeAt > receivedAt)
            {
                throw new InvalidDataException("The public order book response is invalid.");
            }
            return new BybitDemoPublicQuote(symbol, bid, ask, exchangeAt, receivedAt);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The public order book response is invalid.");
        }
    }

    public async Task<BybitDemoInstrument> GetInstrumentAsync(string symbol, CancellationToken cancellationToken)
    {
        ValidateSymbol(symbol);
        DateTimeOffset observedAt = _clock.GetUtcNow();
        Uri endpoint = new($"https://api-demo.bybit.com/v5/market/instruments-info?category=linear&symbol={symbol}");
        using HttpResponseMessage response = await _http.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            JsonElement root = document.RootElement;
            SignalOnlyConfiguration.RejectDuplicateProperties(root);
            JsonElement result = root.GetProperty("result");
            JsonElement list = result.GetProperty("list");
            if (root.GetProperty("retCode").GetInt32() != 0 || result.GetProperty("category").GetString() != "linear"
                || result.GetProperty("nextPageCursor").GetString() != string.Empty || list.GetArrayLength() != 1)
            {
                throw new InvalidDataException("The public instrument response is invalid.");
            }
            JsonElement item = list[0];
            if (item.GetProperty("symbol").GetString() != symbol || item.GetProperty("status").GetString() != "Trading"
                || item.GetProperty("contractType").GetString() != "LinearPerpetual"
                || item.GetProperty("quoteCoin").GetString() != "USDT"
                || item.GetProperty("settleCoin").GetString() != "USDT"
                || !item.GetProperty("unifiedMarginTrade").GetBoolean() || item.GetProperty("isPreListing").GetBoolean())
            {
                throw new InvalidDataException("The demo instrument is not eligible.");
            }
            JsonElement price = item.GetProperty("priceFilter");
            JsonElement lot = item.GetProperty("lotSizeFilter");
            decimal tick = Number(price, "tickSize");
            decimal minPrice = Number(price, "minPrice");
            decimal maxPrice = Number(price, "maxPrice");
            decimal step = Number(lot, "qtyStep");
            decimal minQuantity = Number(lot, "minOrderQty");
            decimal minNotional = Number(lot, "minNotionalValue");
            decimal maxQuantity = Number(lot, "maxMktOrderQty");
            decimal maxLeverage = Number(item.GetProperty("leverageFilter"), "maxLeverage");
            if (tick <= 0m || minPrice <= 0m || maxPrice < minPrice || step <= 0m || minQuantity <= 0m
                || minNotional < 0m || maxQuantity < minQuantity || maxLeverage < 1m)
            {
                throw new InvalidDataException("The public instrument limits are invalid.");
            }
            return new BybitDemoInstrument(symbol, tick, minPrice, maxPrice, step, minQuantity,
                minNotional, maxQuantity, maxLeverage, observedAt);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException("The public instrument response is invalid.");
        }
    }

    public async Task<decimal> GetLastPriceAsync(string symbol, CancellationToken cancellationToken)
    {
        ValidateSymbol(symbol);
        using HttpResponseMessage response = await _http.GetAsync(
            $"https://api-demo.bybit.com/v5/market/tickers?category=linear&symbol={symbol}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        JsonElement list = root.GetProperty("result").GetProperty("list");
        if (root.GetProperty("retCode").GetInt32() != 0 || list.GetArrayLength() != 1
            || list[0].GetProperty("symbol").GetString() != symbol)
            throw new InvalidDataException("The demo last price is unavailable.");
        decimal price = Number(list[0], "lastPrice");
        if (price <= 0m) throw new InvalidDataException("The demo last price is invalid.");
        return price;
    }

    private static void ValidateSymbol(string symbol)
    {
        if (string.IsNullOrEmpty(symbol) || symbol.Length > 20
            || symbol.Any(static character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("The symbol is invalid.", nameof(symbol));
        }
    }

    private static decimal Number(JsonElement element, string property) =>
        decimal.Parse(element.GetProperty(property).GetString()!, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture);

    public void Dispose() => _http.Dispose();

    private static decimal ReadPrice(JsonElement levels)
    {
        if (levels.GetArrayLength() != 1)
        {
            throw new InvalidDataException("The public order book response is invalid.");
        }
        JsonElement level = levels[0];
        if (level.GetArrayLength() != 2
            || !decimal.TryParse(level[0].GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal price)
            || !decimal.TryParse(level[1].GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal quantity)
            || price <= 0m || quantity <= 0m)
        {
            throw new InvalidDataException("The public order book response is invalid.");
        }
        return price;
    }
}
