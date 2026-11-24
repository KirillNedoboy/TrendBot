using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoClockTests
{
    [Fact]
    public async Task ServerClockCalibrationAcceptsAQuoteEvenWhenOsTimeIsDifferent()
    {
        DateTimeOffset server = new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using HttpClient timeHttp = new(new ResponseHandler(JsonSerializer.Serialize(new
        {
            retCode = 0,
            result = new { timeNano = (server.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture) }
        })));
        BybitDemoClock clock = new();
        await clock.RefreshAsync(timeHttp, 5000, TestContext.Current.CancellationToken);
        string book = "{\"retCode\":0,\"result\":{\"s\":\"BTCUSDT\",\"a\":[[\"101\",\"1\"]],\"b\":[[\"100\",\"1\"]],\"ts\":"
            + server.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "}}";
        using BybitDemoPublicMarketClient market = new(new ResponseHandler(book), clock);
        BybitDemoPublicQuote quote = await market.GetQuoteAsync("BTCUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(server, quote.ExchangeAt);
        Assert.True(quote.ReceivedAt >= server);
        Assert.True(clock.ClaimLowerBound() <= clock.GetUtcNow());
    }

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api-demo.bybit.com", request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("X-BAPI-API-KEY"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
