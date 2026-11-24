using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoOpenOrdersTests
{
    [Fact]
    public async Task ReadsEveryPageAndSignsTheExactEncodedCursor()
    {
        QueueHandler handler = new(Page("order-1", "token%3D1"), Page("order-2", ""));
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", new FixedTimeProvider(), 5000);
        BybitDemoOpenOrderSnapshot snapshot = await client.GetOpenOrdersAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Orders.Count);
        Assert.Equal("order-1", snapshot.Orders[0].ExchangeOrderId);
        Assert.Equal("order-2", snapshot.Orders[1].ExchangeOrderId);
        Assert.Null(snapshot.Orders[0].ParentClientOrderId);
        Assert.True(snapshot.Orders[0].ReduceOnly);
        Assert.Equal("https://api-demo.bybit.com/v5/order/realtime?category=linear&settleCoin=USDT&openOnly=0&limit=50&cursor=token%253D1", handler.Requests[1].Uri);
        string expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-secret"),
            Encoding.UTF8.GetBytes("1700000000000test-key5000category=linear&settleCoin=USDT&openOnly=0&limit=50&cursor=token%253D1"))).ToLowerInvariant();
        Assert.Equal(expected, handler.Requests[1].Signature);
    }

    [Fact]
    public async Task RepeatedCursorAndConflictingSnapshotFailClosed()
    {
        using BybitDemoRestClient looping = new(new QueueHandler(Page("order-1", "cursor"), Page("order-2", "cursor")),
            "test-key", "test-secret", new FixedTimeProvider(), 5000);
        await Assert.ThrowsAsync<InvalidDataException>(() => looping.GetOpenOrdersAsync(TestContext.Current.CancellationToken));
        using BybitDemoRestClient duplicate = new(new QueueHandler(Page("order-1", "cursor"), Page("order-1", "")),
            "test-key", "test-secret", new FixedTimeProvider(), 5000);
        await Assert.ThrowsAsync<InvalidDataException>(() => duplicate.GetOpenOrdersAsync(TestContext.Current.CancellationToken));
    }

    private static string Page(string id, string cursor) => $$$"""
        {"retCode":0,"result":{"category":"linear","nextPageCursor":"{{{cursor}}}","list":[{
         "orderId":"{{{id}}}","orderLinkId":"","symbol":"BTCUSDT","orderStatus":"Untriggered",
         "stopOrderType":"StopLoss","positionIdx":0,"side":"Sell","qty":"1","triggerPrice":"95","triggerBy":"LastPrice","reduceOnly":true,"closeOnTrigger":true}]}}
        """;

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
    }

    private sealed class QueueHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<(string Uri, string Signature)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.GetValues("X-BAPI-SIGN").Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }
}
