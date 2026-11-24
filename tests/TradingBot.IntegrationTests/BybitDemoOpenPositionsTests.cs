using System.Net;
using System.Text;
using TradingBot.App;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoOpenPositionsTests
{
    [Fact]
    public async Task ReadsAllPagesWithoutHidingHedgeOrUnknownLeverage()
    {
        QueueHandler handler = new(Page("BTCUSDT", 0, "5", "next%3D1"), Page("ETHUSDT", 2, "", ""));
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", TimeProvider.System, 5000);
        BybitDemoPositionSnapshot snapshot = await client.GetOpenPositionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, snapshot.Positions.Count);
        Assert.Equal(Direction.Long, snapshot.Positions[0].Direction);
        Assert.Equal(5m, snapshot.Positions[0].Leverage);
        Assert.Equal("ETHUSDT", snapshot.Positions[1].Symbol);
        Assert.Equal(2, snapshot.Positions[1].PositionIndex);
        Assert.Null(snapshot.Positions[1].Leverage);
        Assert.Contains("cursor=next%253D1", handler.Uris[1]);
        Assert.All(handler.Uris, uri => Assert.StartsWith("https://api-demo.bybit.com/v5/position/list?", uri));
    }

    [Fact]
    public async Task DuplicatePositionOrCursorCannotProduceACompleteSnapshot()
    {
        using BybitDemoRestClient duplicate = new(new QueueHandler(Page("BTCUSDT", 0, "5", "next"), Page("BTCUSDT", 0, "5", "")),
            "test-key", "test-secret", TimeProvider.System, 5000);
        await Assert.ThrowsAsync<InvalidDataException>(() => duplicate.GetOpenPositionsAsync(TestContext.Current.CancellationToken));
        using BybitDemoRestClient looping = new(new QueueHandler(Page("BTCUSDT", 0, "5", "next"), Page("ETHUSDT", 0, "5", "next")),
            "test-key", "test-secret", TimeProvider.System, 5000);
        await Assert.ThrowsAsync<InvalidDataException>(() => looping.GetOpenPositionsAsync(TestContext.Current.CancellationToken));
    }

    private static string Page(string symbol, int index, string leverage, string cursor) => $$$"""
        {"retCode":0,"result":{"category":"linear","nextPageCursor":"{{{cursor}}}","list":[{
         "symbol":"{{{symbol}}}","positionIdx":{{{index}}},"side":"Buy","size":"1","avgPrice":"100",
         "leverage":"{{{leverage}}}","stopLoss":"95","positionStatus":"Normal","isReduceOnly":false,"seq":1}]}}
        """;

    private sealed class QueueHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<string> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }
}
