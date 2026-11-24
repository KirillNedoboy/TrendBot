using System.Net;
using System.Text;
using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoPublicQuoteClientTests
{
    [Fact]
    public async Task ReadsOnlyTheRequestedPublicDemoOrderBook()
    {
        CaptureHandler handler = new("""
            {"retCode":0,"result":{"s":"BTCUSDT","a":[["101","1"]],"b":[["99","2"]],"ts":1700000000000}}
            """);
        using BybitDemoPublicMarketClient client = new(handler, new FixedTimeProvider());
        BybitDemoPublicQuote quote = await client.GetQuoteAsync("BTCUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(99m, quote.Bid);
        Assert.Equal(101m, quote.Ask);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), quote.ExchangeAt);
        Assert.Equal("https://api-demo.bybit.com/v5/market/orderbook?category=linear&symbol=BTCUSDT&limit=1", handler.Uri);
        Assert.False(handler.HasAuthentication);
    }

    [Theory]
    [InlineData("{\"retCode\":0,\"result\":{\"s\":\"ETHUSDT\",\"a\":[[\"101\",\"1\"]],\"b\":[[\"99\",\"2\"]],\"ts\":1700000000000}}")]
    [InlineData("{\"retCode\":0,\"result\":{\"s\":\"BTCUSDT\",\"a\":[[\"99\",\"1\"]],\"b\":[[\"101\",\"2\"]],\"ts\":1700000000000}}")]
    [InlineData("{\"retCode\":0,\"result\":{\"s\":\"BTCUSDT\",\"a\":[],\"b\":[[\"99\",\"2\"]],\"ts\":1700000000000}}")]
    public async Task RejectsWrongSymbolCrossedOrEmptyBook(string json)
    {
        using BybitDemoPublicMarketClient client = new(new CaptureHandler(json), new FixedTimeProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetQuoteAsync("BTCUSDT", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsInvalidSymbolBeforeSending()
    {
        CaptureHandler handler = new("{}");
        using BybitDemoPublicMarketClient client = new(handler, new FixedTimeProvider());
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetQuoteAsync("BTCUSDT&limit=1000", TestContext.Current.CancellationToken));
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task ReadsCurrentPerpetualFiltersWithoutCredentials()
    {
        CaptureHandler handler = new(InstrumentJson());
        using BybitDemoPublicMarketClient client = new(handler, new FixedTimeProvider());
        BybitDemoInstrument result = await client.GetInstrumentAsync("BTCUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(0.1m, result.TickSize);
        Assert.Equal(0.001m, result.QuantityStep);
        Assert.Equal(5m, result.MinimumNotional);
        Assert.Equal(150m, result.MaximumMarketQuantity);
        Assert.Equal("https://api-demo.bybit.com/v5/market/instruments-info?category=linear&symbol=BTCUSDT", handler.Uri);
        Assert.False(handler.HasAuthentication);
    }

    [Theory]
    [InlineData("\"status\":\"Trading\"", "\"status\":\"Settled\"")]
    [InlineData("\"settleCoin\":\"USDT\"", "\"settleCoin\":\"USDC\"")]
    [InlineData("\"qtyStep\":\"0.001\"", "\"qtyStep\":\"0\"")]
    public async Task RejectsUnavailableIncompatibleOrInvalidInstrument(string original, string replacement)
    {
        string json = InstrumentJson().Replace(original, replacement, StringComparison.Ordinal);
        using BybitDemoPublicMarketClient client = new(new CaptureHandler(json), new FixedTimeProvider());
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetInstrumentAsync("BTCUSDT", TestContext.Current.CancellationToken));
    }

    private static string InstrumentJson() => """
        {"retCode":0,"result":{"category":"linear","nextPageCursor":"","list":[{
        "symbol":"BTCUSDT","status":"Trading","contractType":"LinearPerpetual",
        "quoteCoin":"USDT","settleCoin":"USDT","unifiedMarginTrade":true,"isPreListing":false,
        "priceFilter":{"minPrice":"0.1","maxPrice":"1999999.8","tickSize":"0.1"},
        "lotSizeFilter":{"qtyStep":"0.001","minOrderQty":"0.001","minNotionalValue":"5","maxMktOrderQty":"150"},
        "leverageFilter":{"maxLeverage":"150"}}]}}
        """;

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_000);
    }

    private sealed class CaptureHandler(string json) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public bool HasAuthentication { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.AbsoluteUri;
            HasAuthentication = request.Headers.Contains("X-BAPI-API-KEY") || request.Headers.Authorization is not null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
