using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.App;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoRestClientTests
{
    [Fact]
    public async Task MarketEntryUsesDemoOnlyWithExplicitProtectionAndStableOrderId()
    {
        CaptureHandler handler = new();
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret",
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)), 5000);

        BybitDemoOrderAcknowledgement result = await client.PlaceMarketEntryAsync(
            "BTCUSDT", Direction.Long, 0.01m, 95m, 0.5m, "fixture-order-1",
            TestContext.Current.CancellationToken);

        Assert.Equal("exchange-order-1", result.ExchangeOrderId);
        Assert.Equal("fixture-order-1", result.ClientOrderId);
        Assert.Equal("https://api-demo.bybit.com/v5/order/create", handler.Uri);
        Assert.Equal("POST", handler.Method);
        using JsonDocument payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("linear", payload.RootElement.GetProperty("category").GetString());
        Assert.Equal("Market", payload.RootElement.GetProperty("orderType").GetString());
        Assert.Equal("Buy", payload.RootElement.GetProperty("side").GetString());
        Assert.Equal("0.01", payload.RootElement.GetProperty("qty").GetString());
        Assert.Equal("95", payload.RootElement.GetProperty("stopLoss").GetString());
        Assert.Equal("0.5", payload.RootElement.GetProperty("slippageTolerance").GetString());
        Assert.Equal("fixture-order-1", payload.RootElement.GetProperty("orderLinkId").GetString());
        string expectedSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("test-secret"),
            Encoding.UTF8.GetBytes("1700000000000test-key5000" + handler.Body))).ToLowerInvariant();
        Assert.Equal(expectedSignature, handler.Signature);
    }

    [Theory]
    [InlineData(Direction.Long, "Sell", 1, 105)]
    [InlineData(Direction.Short, "Buy", 2, 95)]
    public async Task ConditionalTargetIsReduceOnlyAndUsesTheCorrectTrigger(
        Direction positionDirection, string expectedSide, int expectedTrigger, decimal target)
    {
        CaptureHandler handler = new();
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);
        await client.PlaceTakeProfitAsync("BTCUSDT", positionDirection, 0.5m, target, 100m,
            "fixture-order-1", TestContext.Current.CancellationToken);
        using JsonDocument payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expectedSide, payload.RootElement.GetProperty("side").GetString());
        Assert.Equal(expectedTrigger, payload.RootElement.GetProperty("triggerDirection").GetInt32());
        Assert.True(payload.RootElement.GetProperty("reduceOnly").GetBoolean());
        Assert.False(payload.RootElement.TryGetProperty("slippageTolerance", out _));
        Assert.False(payload.RootElement.TryGetProperty("stopLoss", out _));
    }

    [Fact]
    public async Task InvalidTargetDoesNotSubmitAndMarketExitIsReduceOnly()
    {
        CaptureHandler handler = new();
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);
        await Assert.ThrowsAsync<ArgumentException>(() => client.PlaceTakeProfitAsync("BTCUSDT", Direction.Long,
            0.5m, 95m, 100m, "fixture-order-1", TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Count);
        await client.PlaceMarketExitAsync("BTCUSDT", Direction.Short, 0.5m, 1m,
            "fixture-order-1", TestContext.Current.CancellationToken);
        using JsonDocument payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Buy", payload.RootElement.GetProperty("side").GetString());
        Assert.True(payload.RootElement.GetProperty("reduceOnly").GetBoolean());
        Assert.Equal("1", payload.RootElement.GetProperty("slippageTolerance").GetString());
        Assert.False(payload.RootElement.TryGetProperty("triggerPrice", out _));
    }

    [Fact]
    public async Task FullStopUpdateRejectsZeroAndTargetsOnlyDemo()
    {
        CaptureHandler handler = new() { Response = "{\"retCode\":0,\"result\":{}}" };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);
        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestFullStopAsync("BTCUSDT", 0m, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Count);
        await client.RequestFullStopAsync("BTCUSDT", 95m, TestContext.Current.CancellationToken);
        Assert.Equal("https://api-demo.bybit.com/v5/position/trading-stop", handler.Uri);
        using JsonDocument payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Full", payload.RootElement.GetProperty("tpslMode").GetString());
        Assert.Equal("95", payload.RootElement.GetProperty("stopLoss").GetString());
        Assert.False(payload.RootElement.TryGetProperty("takeProfit", out _));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task FailedStopResponseIsUnknownAndIsNotRetried()
    {
        CaptureHandler handler = new() { Response = "{\"retCode\":10006,\"result\":{}}" };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);
        await Assert.ThrowsAsync<BybitDemoProtectionUnknownException>(() =>
            client.RequestFullStopAsync("BTCUSDT", 95m, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task AccountInformationUsesSignedDemoGet()
    {
        CaptureHandler handler = new() { Response = "{\"retCode\":0,\"result\":{\"marginMode\":\"ISOLATED_MARGIN\"}}" };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret",
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)), 5000);

        BybitDemoAccountInfo result = await client.GetAccountInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ISOLATED_MARGIN", result.MarginMode);
        Assert.Equal("https://api-demo.bybit.com/v5/account/info", handler.Uri);
        Assert.Equal("GET", handler.Method);
        Assert.Null(handler.Body);
        string expectedSignature = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("test-secret"),
            Encoding.UTF8.GetBytes("1700000000000test-key5000"))).ToLowerInvariant();
        Assert.Equal(expectedSignature, handler.Signature);
    }

    [Fact]
    public async Task MissingAccountModeCannotAuthorizeTrading()
    {
        CaptureHandler handler = new() { Response = "{\"retCode\":0,\"result\":{}}" };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAccountInfoAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PositionReadChecksSymbolModeAndSignsTheExactQuery()
    {
        CaptureHandler handler = new() { Response = """
            {"retCode":0,"result":{"category":"linear","list":[{"symbol":"BTCUSDT","positionIdx":0,
            "positionStatus":"Normal","side":"Buy","size":"0.1","leverage":"5","avgPrice":"100",
            "stopLoss":"95","isReduceOnly":false}]}}
            """ };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret",
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)), 5000);
        BybitDemoPositionInfo result = await client.GetPositionAsync("BTCUSDT", TestContext.Current.CancellationToken);
        Assert.Equal(Direction.Long, result.Direction);
        Assert.Equal(0.1m, result.Quantity);
        Assert.Equal(95m, result.StopLoss);
        Assert.Equal("https://api-demo.bybit.com/v5/position/list?category=linear&symbol=BTCUSDT", handler.Uri);
        string expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-secret"),
            Encoding.UTF8.GetBytes("1700000000000test-key5000category=linear&symbol=BTCUSDT"))).ToLowerInvariant();
        Assert.Equal(expected, handler.Signature);
    }

    [Theory]
    [InlineData(1, "BTCUSDT")]
    [InlineData(0, "ETHUSDT")]
    public async Task HedgeModeOrWrongSymbolCannotBeTreatedAsAValidPosition(int positionIndex, string responseSymbol)
    {
        CaptureHandler handler = new() { Response = $$$"""
            {"retCode":0,"result":{"category":"linear","list":[{"symbol":"{{{responseSymbol}}}","positionIdx":{{{positionIndex}}},
            "positionStatus":"Normal","side":"","size":"0","leverage":"5","avgPrice":"",
            "stopLoss":"","isReduceOnly":false}]}}
            """ };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetPositionAsync("BTCUSDT", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NonSuccessResponseRemainsAnUnknownSubmissionOutcome()
    {
        CaptureHandler handler = new() { Response = "{\"retCode\":10006,\"retMsg\":\"rate limit\",\"result\":{}}" };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);

        await Assert.ThrowsAsync<BybitDemoSubmissionUnknownException>(() => client.PlaceMarketEntryAsync(
            "BTCUSDT", Direction.Short, 1m, 105m, 0.5m, "fixture-order-2",
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task MissingOrUnsupportedSlippageNeverSubmits(decimal slippage)
    {
        CaptureHandler handler = new();
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PlaceMarketEntryAsync(
            "BTCUSDT", Direction.Long, 1m, 95m, slippage, "fixture-order-3",
            TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Count);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? Method { get; private set; }
        public string? Body { get; private set; }
        public string? Signature { get; private set; }
        public int Count { get; private set; }
        public string Response { get; init; } =
            "{\"retCode\":0,\"result\":{\"orderId\":\"exchange-order-1\",\"orderLinkId\":\"fixture-order-1\"}}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            Uri = request.RequestUri!.AbsoluteUri;
            Method = request.Method.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Signature = request.Headers.GetValues("X-BAPI-SIGN").Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Response, Encoding.UTF8, "application/json")
            };
        }
    }
}
