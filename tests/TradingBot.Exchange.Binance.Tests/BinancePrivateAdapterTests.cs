using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class BinancePrivateAdapterTests
{
    private static readonly Uri BaseAddress = new("https://fixture.test");
    private static readonly DateTimeOffset Now =
        new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PlaceOrderSignsDocumentedRequestAndParsesResponseWithoutLoggingSecret()
    {
        RecordingSigner signer = new();
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/order" => Json("""
                {"orderId":123,"clientOrderId":"intent-1","symbol":"BTCUSDT","side":"BUY",
                 "positionSide":"BOTH","type":"LIMIT","status":"NEW","timeInForce":"GTC",
                 "origQty":"0.010","executedQty":"0","price":"100.25","avgPrice":"0",
                 "reduceOnly":false,"closePosition":false,"updateTime":1700000000123,"time":1700000000111}
                """),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePrivateRestClient client = CreateClient(handler, signer);

        BinancePrivateOrder order = await client.PlaceOrderAsync(
            new BinancePrivateOrderRequest("BTCUSDT", BinanceOrderSide.Buy,
                BinanceOrderType.Limit, new Quantity(0.010m), "intent-1",
                new Price(100.25m), BinanceTimeInForce.GoodTillCancel,
                BinancePositionSide.Both, false), TestContext.Current.CancellationToken);

        Assert.Equal(123L, order.OrderId);
        Assert.Equal("intent-1", order.ClientOrderId);
        Assert.Equal(BinancePositionSide.Both, order.PositionSide);
        Assert.Equal("fixture-key", handler.Requests[0].Headers.GetValues("X-MBX-APIKEY").Single());
        Assert.Equal(1, signer.Calls);
        string body = handler.Bodies[0];
        Assert.Contains($"timestamp={Now.ToUnixTimeMilliseconds()}", body,
            StringComparison.Ordinal);
        Assert.Contains("signature=fixture-signature", body, StringComparison.Ordinal);
        Assert.DoesNotContain("signature=fixture-signature", signer.LastPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryCancelOpenOrdersAndTradesUsePrivatePathsAndExactDecimals()
    {
        RecordingSigner signer = new();
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/order" => Json("""
                {"orderId":123,"clientOrderId":"intent-1","symbol":"BTCUSDT","side":"BUY",
                 "positionSide":"BOTH","type":"LIMIT","status":"PARTIALLY_FILLED","timeInForce":"GTC",
                 "origQty":"0.01000000000000000001","executedQty":"0.00400000000000000001",
                 "price":"100.25","avgPrice":"100.125","reduceOnly":false,"closePosition":false,
                 "updateTime":1700000000123,"time":1700000000111}
                """),
            "/fapi/v1/openOrders" => Json("""[{"orderId":124,"clientOrderId":"intent-2","symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","type":"STOP_MARKET","status":"NEW","origQty":"0.02","executedQty":"0","reduceOnly":true,"closePosition":true,"updateTime":1700000000133,"time":1700000000122}]"""),
            "/fapi/v1/userTrades" => Json("""[{"id":77,"orderId":123,"symbol":"BTCUSDT","side":"BUY","positionSide":"BOTH","price":"100.12500000000000000001","qty":"0.00400000000000000001","quoteQty":"0.40050000000000000000004","commission":"0.000001","commissionAsset":"USDT","realizedPnl":"0.0002","buyer":true,"maker":false,"time":1700000000123}]"""),
            _ => request.Method == HttpMethod.Delete
                ? Json("""{"code":200,"msg":"The operation was successful."}""")
                : throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePrivateRestClient client = CreateClient(handler, signer);

        BinancePrivateOrder queried = await client.QueryOrderAsync("BTCUSDT", 123L, null,
            TestContext.Current.CancellationToken);
        IReadOnlyList<BinancePrivateOrder> open = await client.GetOpenOrdersAsync("BTCUSDT",
            TestContext.Current.CancellationToken);
        IReadOnlyList<BinancePrivateTrade> trades = await client.GetUserTradesAsync("BTCUSDT",
            TestContext.Current.CancellationToken);
        await client.CancelOrderAsync("BTCUSDT", 123L, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0.00400000000000000001m, queried.ExecutedQuantity);
        Assert.True(Assert.Single(open).ReduceOnly);
        Assert.Equal(100.12500000000000000001m, Assert.Single(trades).Price);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete
            && request.RequestUri!.AbsolutePath == "/fapi/v1/order");
        Assert.True(signer.Calls >= 4);
    }

    [Fact]
    public async Task AlgoOrderKeepsOpaqueFieldsAndUnknownStatusWithoutInventingEnum()
    {
        RecordingSigner signer = new();
        FixtureHandler handler = new(_ => Json("""
            {"algoId":7001,"clientAlgoId":"algo-1","algoType":"CONDITIONAL","orderType":"STOP_MARKET",
             "symbol":"BTCUSDT","side":"SELL","positionSide":"BOTH","algoStatus":"FUTURE_STATUS",
             "quantity":"0.01","triggerPrice":"99","reduceOnly":true,"futureField":{"opaque":true}}
            """));
        BinancePrivateRestClient client = CreateClient(handler, signer);

        BinancePrivateAlgoOrder order = await client.PlaceAlgoOrderAsync(
            new BinancePrivateAlgoOrderRequest("BTCUSDT", BinanceOrderSide.Sell,
                BinanceAlgoOrderType.StopMarket, new Quantity(0.01m), "algo-1",
                new Price(99m), BinancePositionSide.Both, true),
            TestContext.Current.CancellationToken);

        Assert.Equal(7001L, order.AlgoId);
        Assert.Equal("FUTURE_STATUS", order.Status);
        Assert.True(order.UnknownFields.ContainsKey("futureField"));
        Assert.True(order.RequiresReconciliation);
    }

    [Fact]
    public async Task AlgoQueryCancelAndOpenOrdersUseDocumentedPrivateRoutes()
    {
        RecordingSigner signer = new();
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/algoOrder" when request.Method == HttpMethod.Get => Json("""
                {"algoId":7001,"clientAlgoId":"algo-1","algoType":"CONDITIONAL",
                 "orderType":"STOP_MARKET","symbol":"BTCUSDT","side":"SELL",
                 "positionSide":"BOTH","algoStatus":"WORKING","quantity":"0.01",
                 "triggerPrice":"99","reduceOnly":true}
                """),
            "/fapi/v1/openAlgoOrders" => Json("""
                [{"algoId":7002,"clientAlgoId":"algo-2","algoType":"CONDITIONAL",
                  "orderType":"TAKE_PROFIT_MARKET","symbol":"BTCUSDT","side":"SELL",
                  "positionSide":"BOTH","algoStatus":"NEW","quantity":"0.01",
                  "triggerPrice":"102","reduceOnly":true}]
                """),
            "/fapi/v1/algoOrder" when request.Method == HttpMethod.Delete => Json("""
                {"code":200,"msg":"The operation was successful."}
                """),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePrivateRestClient client = CreateClient(handler, signer);

        BinancePrivateAlgoOrder queried = await client.QueryAlgoOrderAsync("BTCUSDT", 7001L,
            null, TestContext.Current.CancellationToken);
        IReadOnlyList<BinancePrivateAlgoOrder> open = await client.GetOpenAlgoOrdersAsync(
            "BTCUSDT", TestContext.Current.CancellationToken);
        await client.CancelAlgoOrderAsync("BTCUSDT", 7001L, null,
            TestContext.Current.CancellationToken);

        Assert.Equal("STOP_MARKET", queried.OrderTypeText);
        Assert.True(queried.ReduceOnly);
        Assert.Equal("TAKE_PROFIT_MARKET", Assert.Single(open).OrderTypeText);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete
            && request.RequestUri!.AbsolutePath == "/fapi/v1/algoOrder");
        Assert.True(signer.Calls >= 3);
    }

    [Fact]
    public async Task Unknown503OutcomeRequiresReconciliationWithoutEchoingResponse()
    {
        RecordingSigner signer = new();
        FixtureHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(
                "{\"code\":-1000,\"msg\":\"Unknown error, please check your request or try again later. private-secret\"}",
                Encoding.UTF8, "application/json")
        });
        BinancePrivateRestClient client = CreateClient(handler, signer);

        BinanceApiException exception = await Assert.ThrowsAsync<BinanceApiException>(() =>
            client.QueryOrderAsync("BTCUSDT", 123L, null,
                TestContext.Current.CancellationToken));

        Assert.Contains("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconciliation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-secret", exception.Message, StringComparison.Ordinal);
        Assert.Empty(exception.ResponseBody);
    }

    [Fact]
    public async Task PositionModeRequiresOneWayAndMapsBothPositionRiskToCoreSnapshot()
    {
        RecordingSigner signer = new();
        FixtureHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/fapi/v1/positionSide/dual" => Json("""{"dualSidePosition":false}"""),
            "/fapi/v3/positionRisk" => Json("""[{"symbol":"BTCUSDT","positionAmt":"-0.010","positionSide":"BOTH","entryPrice":"101.25","markPrice":"100.00","unRealizedProfit":"0.0123","updateTime":1700000000123}]"""),
            _ => throw new InvalidOperationException(request.RequestUri!.ToString())
        });
        BinancePrivateRestClient client = CreateClient(handler, signer);

        Assert.Equal(BinancePositionMode.OneWay, await client.GetPositionModeAsync(
            TestContext.Current.CancellationToken));
        BinancePrivatePosition position = Assert.Single(await client.GetPositionsAsync(
            "BTCUSDT", TestContext.Current.CancellationToken));
        PortfolioPositionSnapshot snapshot = position.ToCoreSnapshot(new PortfolioId("p-1"),
            "intent-1", new UtcTimestamp(Now));

        Assert.Equal(Direction.Short, snapshot.Position.Direction);
        Assert.Equal(0.010m, snapshot.Position.Quantity.Value);
        Assert.Equal(PositionOrigin.Known, snapshot.Origin);
        Assert.Equal("intent-1", snapshot.IntentId);
    }

    [Fact]
    public async Task HedgeModeAndUnknownPositionSideAreRejectedFailClosed()
    {
        RecordingSigner signer = new();
        FixtureHandler hedge = new(_ => Json("""{"dualSidePosition":true}"""));
        BinancePrivateRestClient hedgeClient = CreateClient(hedge, signer);
        await Assert.ThrowsAsync<BinancePrivateProtocolException>(() => hedgeClient.GetPositionModeAsync(
            TestContext.Current.CancellationToken));

        FixtureHandler unknown = new(_ => Json("""[{"symbol":"BTCUSDT","positionAmt":"1","positionSide":"FUTURE"}]"""));
        BinancePrivateRestClient unknownClient = CreateClient(unknown, signer);
        await Assert.ThrowsAsync<BinancePrivateProtocolException>(() => unknownClient.GetPositionsAsync(
            "BTCUSDT", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListenKeyLifecycleUsesApiKeyWithoutSignature()
    {
        RecordingSigner signer = new();
        FixtureHandler handler = new(request => request.Method switch
        {
            var method when method == HttpMethod.Post => Json("""{"listenKey":"fixture-listen-key"}"""),
            var method when method == HttpMethod.Put => Json("""{"listenKey":"fixture-listen-key"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
        });
        BinancePrivateRestClient client = CreateClient(handler, signer);

        Assert.Equal("fixture-listen-key", await client.StartUserDataStreamAsync(
            TestContext.Current.CancellationToken));
        await client.KeepAliveUserDataStreamAsync("fixture-listen-key",
            TestContext.Current.CancellationToken);
        await client.CloseUserDataStreamAsync("fixture-listen-key",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, signer.Calls);
        Assert.All(handler.Requests, request => Assert.Equal("fixture-key",
            request.Headers.GetValues("X-MBX-APIKEY").Single()));
    }

    [Fact]
    public void PrivateStreamParserRetainsCausalFieldsAndMapsOrderFill()
    {
        BinancePrivateStreamMetadata metadata = new("session-a", 7,
            new UtcTimestamp(Now), new UtcTimestamp(Now.AddMilliseconds(10)));
        BinancePrivateStreamEvent parsed = BinancePrivateStreamParser.Parse("""
            {"e":"ORDER_TRADE_UPDATE","E":1700000000123,"T":1700000000120,"o":
             {"s":"BTCUSDT","c":"intent-1","S":"BUY","o":"LIMIT","f":"GTC","q":"0.01",
              "p":"100.25","ap":"100.25","x":"TRADE","X":"PARTIALLY_FILLED","i":123,
              "l":"0.004","z":"0.004","L":"100.25","ma":"USDT","N":"USDT","n":"0.001",
              "T":1700000000123,"t":77,"rp":"0.002","m":false,"R":false,"ps":"BOTH"}}
            """, metadata);

        BinanceOrderTradeUpdateEvent update = Assert.IsType<BinanceOrderTradeUpdateEvent>(parsed);
        Assert.Equal(7, update.StableSequence);
        Assert.Equal(Now.AddMilliseconds(10), update.AvailabilityTime.Value);
        Assert.Equal(BinanceOrderStatus.PartiallyFilled, update.Order.Status);
        Assert.Equal(0.004m, Assert.Single(update.Fills).Quantity);
        Fill fill = Assert.Single(update.Fills).ToCoreFill(new UtcTimestamp(Now), "intent-1");
        Assert.Equal(77L, fill.StableSequence);
        Assert.Equal(0.004m, fill.Quantity.Value);
        Fill streamFill = Assert.Single(update.ToCoreFills("intent-1"));
        Assert.Equal(update.StableSequence, streamFill.StableSequence);
        Assert.Equal(update.AvailabilityTime, streamFill.AvailabilityTime);
    }

    [Fact]
    public void AccountUpdateIsPositionDeltaAndAlgoUpdateRemainsOpaque()
    {
        BinancePrivateStreamMetadata metadata = new("session-a", 8,
            new UtcTimestamp(Now), new UtcTimestamp(Now.AddMilliseconds(5)));
        BinanceAccountUpdateEvent account = Assert.IsType<BinanceAccountUpdateEvent>(
            BinancePrivateStreamParser.Parse("""
                {"e":"ACCOUNT_UPDATE","E":1700000000200,"T":1700000000199,
                 "a":{"m":"ORDER","P":[{"s":"BTCUSDT","pa":"-0.01","ep":"101.25","bep":"101.20","up":"0.01","mt":"isolated","iw":"0","ps":"BOTH"}]}}
                """, metadata));
        Assert.True(account.IsDelta);
        Assert.Equal(Direction.Short, Assert.Single(account.Positions).Direction);
        Assert.Equal(BinancePositionSide.Both, Assert.Single(account.Positions).PositionSide);

        BinanceAlgoUpdateEvent algo = Assert.IsType<BinanceAlgoUpdateEvent>(
            BinancePrivateStreamParser.Parse("""
                {"e":"ALGO_UPDATE","E":1700000000300,"T":1700000000299,
                 "o":{"algoId":7001,"status":"FUTURE_STATUS","future":{"opaque":true}}}
                """, metadata));
        Assert.Equal("FUTURE_STATUS", algo.OpaqueOrder.GetProperty("status").GetString());
        Assert.True(algo.RequiresReconciliation);
    }

    [Fact]
    public void PrivateStreamParserRejectsUnknownEventsAndInvalidAvailability()
    {
        Assert.Throws<ArgumentException>(() => new BinancePrivateStreamMetadata("session-a", 1,
            new UtcTimestamp(Now.AddSeconds(1)), new UtcTimestamp(Now)));

        BinancePrivateStreamMetadata metadata = new("session-a", 1,
            new UtcTimestamp(Now), new UtcTimestamp(Now));
        Assert.Throws<JsonException>(() => BinancePrivateStreamParser.Parse(
            "{\"e\":\"FUTURE_EVENT\",\"E\":1700000000}", metadata));
    }

    [Fact]
    public async Task PrivateWebSocketUsesInjectedTransportAndAssignsStableCausalSequence()
    {
        FakePrivateTransport transport = new([
            new BinanceWebSocketReceiveResult(WebSocketMessageType.Text,
                Encoding.UTF8.GetBytes("{\"e\":\"ACCOUNT_UPDATE\",\"E\":1700000000200,\"T\":1700000000199,\"a\":{\"P\":[]}}"), true),
            new BinanceWebSocketReceiveResult(WebSocketMessageType.Close, [], true)
        ]);
        using BinancePrivateWebSocketClient client = new(transport,
            new BinancePrivateWebSocketOptions
            {
                BaseAddress = new Uri("wss://fixture.test/ws/"),
                ReceiveTimeout = TimeSpan.FromSeconds(1),
                SessionLifetime = TimeSpan.FromMinutes(1)
            }, new FixedTimeProvider(Now));

        List<BinancePrivateStreamEnvelope> envelopes = [];
        await foreach (BinancePrivateStreamEnvelope envelope in client.ReadAsync(
            "fixture-listen-key", TestContext.Current.CancellationToken))
        {
            envelopes.Add(envelope);
        }

        BinancePrivateStreamEnvelope only = Assert.Single(envelopes);
        Assert.Equal(0, only.StableSequence);
        Assert.Equal("wss://fixture.test/ws/fixture-listen-key", transport.ConnectedUri!.ToString());
        Assert.Equal(BinancePrivateStreamDiagnosticKind.Disconnect,
            Assert.Single(client.Diagnostics).Kind);
    }

    private static BinancePrivateRestClient CreateClient(FixtureHandler handler,
        RecordingSigner signer)
    {
        HttpClient httpClient = new(handler) { BaseAddress = BaseAddress };
        return new BinancePrivateRestClient(httpClient,
            new BinancePrivateRestOptions(BaseAddress, "fixture-key", "private-secret"),
            signer, new FixedTimeProvider(Now));
    }

    private static HttpResponseMessage Json(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingSigner : IBinancePrivateRequestSigner
    {
        public int Calls { get; private set; }

        public string LastPayload { get; private set; } = string.Empty;

        public string Sign(string canonicalQuery)
        {
            Calls++;
            LastPayload = canonicalQuery;
            return "fixture-signature";
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        public FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter()
                .GetResult() ?? string.Empty);
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FakePrivateTransport : IBinanceWebSocketTransport
    {
        private readonly Queue<BinanceWebSocketReceiveResult> _results;

        public FakePrivateTransport(IEnumerable<BinanceWebSocketReceiveResult> results)
        {
            _results = new Queue<BinanceWebSocketReceiveResult>(results);
        }

        public Uri? ConnectedUri { get; private set; }

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectedUri = uri;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BinanceWebSocketReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            BinanceWebSocketReceiveResult result = _results.Dequeue();
            if (result.MessageType == WebSocketMessageType.Close)
            {
                State = WebSocketState.Closed;
            }

            return Task.FromResult(result);
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription,
            CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public void Abort() => State = WebSocketState.Aborted;

        public void Dispose() => State = WebSocketState.Closed;
    }
}
