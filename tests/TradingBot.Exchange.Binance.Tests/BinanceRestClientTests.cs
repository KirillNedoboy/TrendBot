using System.Net;
using System.Net.Http.Json;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class BinanceRestClientTests
{
    private static readonly Uri BaseAddress = new("https://fixture.test");

    [Fact]
    public async Task ExchangeInfoAndServerTimeKeepCurrentPublicSchemasAndRawTimes()
    {
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/exchangeInfo" => Json("""
                {
                  "timezone":"UTC","serverTime":1700000000123,
                  "rateLimits":[{"rateLimitType":"REQUEST_WEIGHT","interval":"MINUTE","intervalNum":1,"limit":2400}],
                  "exchangeFilters":[],"assets":[{"asset":"USDT","marginAvailable":true,"autoAssetExchange":"-1"}],
                  "symbols":[{"symbol":"BTCUSDT","pair":"BTCUSDT","contractType":"PERPETUAL","deliveryDate":4133404800000,"onboardDate":1502942420000,"status":"TRADING","maintMarginPercent":"2.5000","requiredMarginPercent":"5.0000","baseAsset":"BTC","quoteAsset":"USDT","marginAsset":"USDT","pricePrecision":2,"quantityPrecision":3,"baseAssetPrecision":8,"quotePrecision":8,"filters":[{"filterType":"PRICE_FILTER","minPrice":"0.01000000","maxPrice":"1000000.00000000","tickSize":"0.01000000"}]}]
                }
                """),
            "/fapi/v1/time" => Json("""{"serverTime":1700000000123}"""),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePublicRestClient client = CreateClient(handler);

        BinanceExchangeInfo info = await client.GetExchangeInfoAsync(TestContext.Current.CancellationToken);
        BinanceServerTime time = await client.GetServerTimeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1700000000123L, info.ServerTimeMilliseconds);
        Assert.Equal(1700000000123L, time.ServerTimeMilliseconds);
        Assert.Equal(2.5000m, info.Symbols[0].MaintMarginPercent);
        Assert.Equal(0.01000000m, info.Symbols[0].Filters[0].MinPrice);
        Assert.Equal("BTCUSDT", info.Symbols[0].Symbol);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task TickersAcceptObjectAndArrayResponsesWithExactDecimals()
    {
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/ticker/price" => Json("[{\"symbol\":\"BTCUSDT\",\"price\":\"123456.78901234567890123456789\"}]"),
            "/fapi/v1/ticker/24hr" => Json("{\"symbol\":\"BTCUSDT\",\"priceChange\":\"1.25\",\"priceChangePercent\":\"0.001\",\"weightedAvgPrice\":\"100.00000000000000000001\",\"lastPrice\":\"101.25\",\"lastQty\":\"0.00010000\",\"openPrice\":\"100\",\"highPrice\":\"110\",\"lowPrice\":\"90\",\"volume\":\"10.5\",\"quoteVolume\":\"1050.5\",\"openTime\":1700000000000,\"closeTime\":1700000060000,\"firstId\":1,\"lastId\":2,\"count\":2}"),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePublicRestClient client = CreateClient(handler);

        IReadOnlyList<BinancePriceTicker> prices = await client.GetPriceTickersAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        IReadOnlyList<Binance24HourTicker> tickers = await client.Get24HourTickersAsync("BTCUSDT",
            TestContext.Current.CancellationToken);

        Assert.Equal(123456.78901234567890123456789m, prices.Single().Price);
        Assert.Equal(100.00000000000000000001m, tickers.Single().WeightedAveragePrice);
        Assert.Equal(1700000000000L, tickers.Single().OpenTimeMilliseconds);
        Assert.Contains("symbol=BTCUSDT", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task KlinesRetainRawTimesAndOnlyClosedKlinesBecomeCoreBars()
    {
        FixtureHandler handler = new(_ => Json("""
            [
              [1700000000000,"100.00000000000000000001","110","90","105.12345678901234567890","12.5",1700000059999,"1300",4,"6.25","650", "0"],
              [1700000060000,"105","106","104","105.5","1",1700000119999,"105.5",1,"0.5","52.75", "0"]
            ]
            """));
        BinancePublicRestClient client = CreateClient(handler,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1700000100000)));

        IReadOnlyList<BinanceKline> klines = await client.GetKlinesAsync("BTCUSDT", "1m",
            cancellationToken: TestContext.Current.CancellationToken);
        IReadOnlyList<Bar> bars = await client.GetClosedBarsAsync("BTCUSDT", "1m",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1700000000000L, klines[0].OpenTimeMilliseconds);
        Assert.Equal(1700000059999L, klines[0].CloseTimeMilliseconds);
        Assert.Equal(2, klines.Count);
        Assert.True(klines[0].IsClosed);
        Assert.False(klines[1].IsClosed);
        Assert.Single(bars);
        Assert.Equal(105.12345678901234567890m, bars[0].Close.Value);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 22, 14, 19, 999, TimeSpan.Zero),
            bars[0].CloseTime.Value);
    }

    [Fact]
    public async Task RecentAndAggregateTradesMapBuyerMakerToSellAndPreservePrecision()
    {
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/trades" => Json("[{\"id\":7,\"price\":\"100.00000000000000000001\",\"qty\":\"0.001234567890123456789\",\"quoteQty\":\"0.12345678901234567890123\",\"time\":1700000000123,\"isBuyerMaker\":false,\"isBestMatch\":true}]"),
            "/fapi/v1/aggTrades" => Json("[{\"a\":8,\"p\":\"101.25\",\"q\":\"2.5\",\"f\":10,\"l\":12,\"T\":1700000001123,\"m\":true,\"M\":true}]"),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePublicRestClient client = CreateClient(handler);

        IReadOnlyList<Trade> recent = await client.GetRecentTradeEventsAsync("BTCUSDT",
            cancellationToken: TestContext.Current.CancellationToken);
        IReadOnlyList<Trade> aggregate = await client.GetAggregateTradeEventsAsync("BTCUSDT",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(100.00000000000000000001m, recent.Single().Price.Value);
        Assert.Equal(0.001234567890123456789m, recent.Single().Quantity.Value);
        Assert.Equal(TradeSide.Buy, recent.Single().Side);
        Assert.Equal(TradeSide.Sell, aggregate.Single().Side);
        Assert.Equal("BTCUSDT", aggregate.Single().InstrumentId.Value);
    }

    [Fact]
    public async Task MarkFundingHistoryFundingInfoAndOpenInterestMapToCoreEvents()
    {
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/premiumIndex" => Json("{\"symbol\":\"BTCUSDT\",\"markPrice\":\"100\",\"indexPrice\":\"99\",\"estimatedSettlePrice\":\"98\",\"lastFundingRate\":\"0.0001234567890123456789\",\"nextFundingTime\":1700028800000,\"time\":1700000000123}"),
            "/fapi/v1/fundingRate" => Json("[{\"symbol\":\"BTCUSDT\",\"fundingRate\":\"-0.00010000000000000000001\",\"fundingTime\":1700000000123,\"markPrice\":\"100\"}]"),
            "/fapi/v1/fundingInfo" => Json("[{\"symbol\":\"BTCUSDT\",\"adjustedFundingRateCap\":\"0.00750000000000000001\",\"adjustedFundingRateFloor\":\"-0.0075\",\"fundingIntervalHours\":8,\"disclaimer\":false}]"),
            "/fapi/v1/openInterest" => Json("{\"symbol\":\"BTCUSDT\",\"openInterest\":\"123456.78901234567890123456789\",\"time\":1700000002123}"),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePublicRestClient client = CreateClient(handler);

        FundingUpdate mark = await client.GetMarkFundingEventAsync("BTCUSDT",
            TestContext.Current.CancellationToken);
        IReadOnlyList<FundingUpdate> history = await client.GetFundingHistoryEventsAsync("BTCUSDT",
            cancellationToken: TestContext.Current.CancellationToken);
        IReadOnlyList<BinanceFundingInfo> info = await client.GetFundingInfoAsync("BTCUSDT",
            TestContext.Current.CancellationToken);
        OIUpdate oi = await client.GetOpenInterestEventAsync("BTCUSDT",
            TestContext.Current.CancellationToken);

        Assert.Equal(0.0001234567890123456789m, mark.FundingRate);
        Assert.Equal(-0.00010000000000000000001m, history.Single().FundingRate);
        Assert.Equal(0.00750000000000000001m, info.Single().AdjustedFundingRateCap);
        Assert.Equal(123456.78901234567890123456789m, oi.OpenInterest);
        Assert.Equal(1700000002123L, client.LastOpenInterestRawTimeMilliseconds);
    }

    [Fact]
    public async Task CancellationAndApiErrorsRemainObservable()
    {
        using CancellationTokenSource cancellation = new();
        FixtureHandler hanging = new(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        BinancePublicRestClient client = CreateClient(hanging);
        Task request = client.GetServerTimeAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        FixtureHandler failed = new(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = JsonContent.Create(new { code = -1003, msg = "Too many requests" })
        });
        BinanceApiException error = await Assert.ThrowsAsync<BinanceApiException>(
            () => CreateClient(failed).GetServerTimeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(-1003, error.Code);
        Assert.Contains("Too many requests", error.Message);
    }

    private static BinancePublicRestClient CreateClient(FixtureHandler handler,
        TimeProvider? timeProvider = null)
    {
        HttpClient httpClient = new(handler) { BaseAddress = BaseAddress };
        return new BinancePublicRestClient(httpClient,
            new BinanceRestOptions(BaseAddress), timeProvider ?? TimeProvider.System);
    }

    private static HttpResponseMessage Json(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public List<HttpRequestMessage> Requests { get; } = [];

        public FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _handler(request, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
