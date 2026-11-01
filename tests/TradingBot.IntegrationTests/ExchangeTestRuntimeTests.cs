using System.Net;
using System.Net.WebSockets;
using System.Text;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ExchangeTestRuntimeTests
{
    [Fact]
    public void RuntimeMustComposeAnIntegratedTestOnlyTransport()
    {
        Assert.NotNull(typeof(ExchangeTestRuntime).GetMethod("CreateTransport"));
        Assert.NotNull(typeof(ExchangeTestRuntime).GetMethod("CreateExecutor"));
    }

    [Fact]
    public void PortfolioStoreMustExposeTheAtomicExchangeObservationBoundary()
    {
        Assert.NotNull(typeof(SqlitePortfolioOperationalStore).GetMethod(
            "ApplyExchangeTestObservation"));
    }

    [Fact]
    public async Task RuntimeCompositionResolvesCredentialsInsideProcessAndUsesOnlyTestRestOrigin()
    {
        CountingProvider provider = new();
        RecordingHandler handler = new();
        using ExchangeTestRuntime runtime = await ExchangeTestRuntime.CreateAsync(
            CreateConfiguration(), provider, handler,
            new DelegateBinanceWebSocketTransportFactory(() =>
                throw new InvalidOperationException("network transport must be injected by the caller")),
            TestContext.Current.CancellationToken);

        Assert.Equal("fixture-listen-key", await runtime.RestClient.StartUserDataStreamAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(1, provider.Calls);
        Assert.Equal("demo-fapi.binance.com", handler.RequestUri!.Host);
        Assert.Equal("runtime-test-key", handler.ApiKey);
        Assert.DoesNotContain("runtime-test-secret", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IntegratedTransportMapsDurableClientIdThroughRestAndFreshPrivateStream()
    {
        const string clientOrderId = "integrated-client-order";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string eventJson = string.Concat(
            "{\"e\":\"ORDER_TRADE_UPDATE\",\"E\":", now,
            ",\"T\":", now,
            ",\"o\":{\"X\":\"NEW\",\"i\":42,\"c\":\"", clientOrderId,
            "\",\"s\":\"BTCUSDT\",\"S\":\"BUY\",\"ps\":\"BOTH\",\"o\":\"MARKET\",",
            "\"q\":\"1\",\"z\":\"0\",\"p\":\"0\",\"ap\":\"0\",\"R\":false,",
            "\"cp\":false,\"T\":", now, ",\"l\":\"0\"}}");
        ExchangeTransportHandler handler = new(eventJson);
        QueuePrivateTransport webSocket = new(eventJson);
        ExchangeTestRunMetadata metadata = new("integrated-run", "runtime-profile-1",
            "binance-usdm-test", DateTimeOffset.UtcNow);
        using ExchangeTestRuntime runtime = await ExchangeTestRuntime.CreateAsync(
            CreateConfiguration(), new CountingProvider(), handler,
            new DelegateBinanceWebSocketTransportFactory(() => webSocket),
            TestContext.Current.CancellationToken);
        BinanceExchangeTestTransport transport = runtime.CreateTransport(metadata,
            new PortfolioId("integrated-portfolio"), TimeSpan.FromMinutes(1));
        OrderIntent intent = new("integrated-intent", "integrated-run", "setup-integrated",
            clientOrderId, new InstrumentId("BTCUSDT"), Direction.Long, OrderRole.Entry, 1,
            new Quantity(1m), new Price(100m));
        ExecutionDispatchAttempt attempt = new("dispatch:integrated-intent:1",
            intent.IntentId, 1, 0, new UtcTimestamp(DateTimeOffset.UtcNow));

        ExchangeTestTransportResult result = await transport.SubmitAsync(intent, attempt,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionObservationType.Accepted, result.Observation.Type);
        Assert.Equal(clientOrderId, handler.SubmittedClientOrderId);
        Assert.NotEmpty(handler.RequestHosts);
        Assert.All(handler.RequestHosts, host => Assert.Equal("demo-fapi.binance.com", host));
        Assert.All(handler.RequestHosts, host => Assert.NotEqual("fapi.binance.com", host));
        Assert.Contains(handler.RequestBodies, body => body.Split('&').Last()
            .StartsWith("signature=", StringComparison.Ordinal));
        Assert.Equal("demo-fstream.binance.com", webSocket.ConnectedUri!.Host);
        Assert.Equal("/private/ws/fixture-listen-key", webSocket.ConnectedUri.AbsolutePath);
        Assert.Single(result.PrivateEvents);
    }

    [Fact]
    public async Task IntegratedTransportConnectsPrivateStreamBeforeSubmittingOrder()
    {
        const string clientOrderId = "immediate-client-order";
        string eventJson = OrderUpdate(clientOrderId, "NEW", "1", "0", "0", "0");
        StreamOrderCoordination coordination = new(eventJson);
        CoordinatedExchangeHandler handler = new(coordination);
        using ExchangeTestRuntime runtime = await ExchangeTestRuntime.CreateAsync(
            CreateConfiguration(), new CountingProvider(), handler,
            new DelegateBinanceWebSocketTransportFactory(() =>
                new CoordinatedPrivateTransport(coordination)),
            TestContext.Current.CancellationToken);
        ExchangeTestRunMetadata metadata = new("immediate-run", "runtime-profile-1",
            "binance-usdm-test", DateTimeOffset.UtcNow);
        BinanceExchangeTestTransport transport = runtime.CreateTransport(metadata,
            new PortfolioId("immediate-portfolio"), TimeSpan.FromMinutes(1));
        OrderIntent intent = CreateEntryIntent("immediate-intent", clientOrderId);
        ExecutionDispatchAttempt attempt = new("dispatch:immediate-intent:1",
            intent.IntentId, 1, 0, new UtcTimestamp(DateTimeOffset.UtcNow));

        ExchangeTestTransportResult result = await transport.SubmitAsync(intent, attempt,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionObservationType.Accepted, result.Observation.Type);
        Assert.True(coordination.StreamWasConnectedBeforeOrder,
            "The private stream must be connected before the order side effect.");
    }

    [Fact]
    public async Task PartialFillPlacesActualReduceOnlyAlgoProtectionBeforeSafeResult()
    {
        const string clientOrderId = "partial-client-order";
        string eventJson = OrderUpdate(clientOrderId, "PARTIALLY_FILLED", "1", "0.4", "100", "0.4");
        StreamOrderCoordination coordination = new(eventJson);
        ProtectionExchangeHandler handler = new(coordination);
        using ExchangeTestRuntime runtime = await ExchangeTestRuntime.CreateAsync(
            CreateConfiguration(), new CountingProvider(), handler,
            new DelegateBinanceWebSocketTransportFactory(() =>
                new CoordinatedPrivateTransport(coordination)),
            TestContext.Current.CancellationToken);
        ExchangeTestRunMetadata metadata = new("partial-run", "runtime-profile-1",
            "binance-usdm-test", DateTimeOffset.UtcNow);
        BinanceExchangeTestTransport transport = runtime.CreateTransport(metadata,
            new PortfolioId("partial-portfolio"), TimeSpan.FromMinutes(1));
        OrderIntent intent = CreateEntryIntent("partial-intent", clientOrderId);
        ExecutionDispatchAttempt attempt = new("dispatch:partial-intent:1",
            intent.IntentId, 1, 0, new UtcTimestamp(DateTimeOffset.UtcNow));

        ExchangeTestTransportResult result = await transport.SubmitAsync(intent, attempt,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionObservationType.PartialFill, result.Observation.Type);
        Assert.True(result.ReduceOnlyProtectionConfirmed);
        Assert.Equal("/fapi/v1/algoOrder", handler.AlgoPath);
        Assert.Contains("reduceOnly=true", handler.AlgoBody, StringComparison.Ordinal);
        Assert.Contains("side=SELL", handler.AlgoBody, StringComparison.Ordinal);
        Assert.Contains("quantity=0.4", handler.AlgoBody, StringComparison.Ordinal);
        Assert.StartsWith("tbp-", handler.ClientAlgoId, StringComparison.Ordinal);
        Assert.Single(result.PositionSnapshots);
        Assert.Equal("binance:BTCUSDT:Both", result.PositionSnapshots[0].Position.PositionId);
    }

    [Fact]
    public async Task PartialFillProtectionFailureDoesNotReturnSafeResult()
    {
        const string clientOrderId = "partial-failure-order";
        string eventJson = OrderUpdate(clientOrderId, "PARTIALLY_FILLED", "1", "0.4", "100", "0.4");
        StreamOrderCoordination coordination = new(eventJson);
        ProtectionExchangeHandler handler = new(coordination) { FailAlgoOrder = true };
        using ExchangeTestRuntime runtime = await ExchangeTestRuntime.CreateAsync(
            CreateConfiguration(), new CountingProvider(), handler,
            new DelegateBinanceWebSocketTransportFactory(() =>
                new CoordinatedPrivateTransport(coordination)),
            TestContext.Current.CancellationToken);
        ExchangeTestRunMetadata metadata = new("partial-failure-run", "runtime-profile-1",
            "binance-usdm-test", DateTimeOffset.UtcNow);
        BinanceExchangeTestTransport transport = runtime.CreateTransport(metadata,
            new PortfolioId("partial-failure-portfolio"), TimeSpan.FromMinutes(1));
        OrderIntent intent = CreateEntryIntent("partial-failure-intent", clientOrderId);
        ExecutionDispatchAttempt attempt = new("dispatch:partial-failure-intent:1",
            intent.IntentId, 1, 0, new UtcTimestamp(DateTimeOffset.UtcNow));

        await Assert.ThrowsAnyAsync<Exception>(() => transport.SubmitAsync(intent, attempt,
            TestContext.Current.CancellationToken).AsTask());
        Assert.True(handler.AlgoAttempted);
    }

    [Fact]
    public async Task ReconciliationDoesNotTreatMissingOrderOrManualPositionAsKnownOrder()
    {
        const string clientOrderId = "reconcile-manual-order";
        ReconciliationExchangeHandler handler = new(manualPosition: true);
        using ExchangeTestRuntime runtime = await ExchangeTestRuntime.CreateAsync(
            CreateConfiguration(), new CountingProvider(), handler,
            new DelegateBinanceWebSocketTransportFactory(() =>
                throw new InvalidOperationException("stream must not be used for missing order")),
            TestContext.Current.CancellationToken);
        ExchangeTestRunMetadata metadata = new("reconcile-manual-run", "runtime-profile-1",
            "binance-usdm-test", DateTimeOffset.UtcNow);
        PortfolioId portfolioId = new("reconcile-manual-portfolio");
        BinanceExchangeTestTransport transport = runtime.CreateTransport(metadata, portfolioId,
            TimeSpan.FromMinutes(1));
        OrderIntent intent = CreateEntryIntent("reconcile-manual-intent", clientOrderId);
        ExecutionStateSnapshot state = ExecutionStateMachine.Start(intent, 0);
        ReconciliationCheckpoint checkpoint = new(portfolioId, 0, [], [], [], []);

        ExecutionRecoveryEvidence evidence = await transport.ReconcileAsync(intent, portfolioId,
            state, checkpoint, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionRecoveryEvidenceStatus.Manual, evidence.Status);
        Assert.False(evidence.IsComplete);
        Assert.False(evidence.OrderKnown);
        Assert.False(evidence.PositionsKnown);
        Assert.False(evidence.CanAuthorizeRetry);
        Assert.Contains("binance:BTCUSDT:Both", evidence.PositionIds);
    }

    [Fact]
    public async Task ReconciliationReturnsCompleteKnownOrderAndPositionIdentities()
    {
        const string clientOrderId = "reconcile-known-order";
        string eventJson = OrderUpdate(clientOrderId, "FILLED", "1", "1", "100", "1");
        ReconciliationExchangeHandler handler = new(manualPosition: false, eventJson);
        using ExchangeTestRuntime runtime = await ExchangeTestRuntime.CreateAsync(
            CreateConfiguration(), new CountingProvider(), handler,
            new DelegateBinanceWebSocketTransportFactory(() => new QueuePrivateTransport(eventJson)),
            TestContext.Current.CancellationToken);
        ExchangeTestRunMetadata metadata = new("reconcile-known-run", "runtime-profile-1",
            "binance-usdm-test", DateTimeOffset.UtcNow);
        PortfolioId portfolioId = new("reconcile-known-portfolio");
        BinanceExchangeTestTransport transport = runtime.CreateTransport(metadata, portfolioId,
            TimeSpan.FromMinutes(1));
        OrderIntent intent = CreateEntryIntent("reconcile-known-intent", clientOrderId);
        ExecutionStateSnapshot state = ExecutionStateMachine.Start(intent, 0);
        ReconciliationCheckpoint checkpoint = new(portfolioId, 4, [], [], [], []);

        ExecutionRecoveryEvidence evidence = await transport.ReconcileAsync(intent, portfolioId,
            state, checkpoint, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionRecoveryEvidenceStatus.Fresh, evidence.Status);
        Assert.True(evidence.IsComplete);
        Assert.True(evidence.OrderKnown);
        Assert.True(evidence.OrderHistoryComplete);
        Assert.True(evidence.PositionsKnown);
        Assert.Contains("binance:BTCUSDT:Both", evidence.PositionIds);
        Assert.Contains("binance-trade:7", evidence.FillIds);
        Assert.Single(evidence.Observations);
    }

    private static ExchangeTestConfiguration CreateConfiguration() => new(
        "runtime-profile-1",
        new ExchangeCredentialReferences("environment", "test-key-reference",
            "test-secret-reference", null, "binance-usdm-test"),
        new ExchangeTestOperationalPolicies("documented-rate-limits", "stream-freshness",
            "reconcile-before-retry", "reduce-only-remainder", "unknown-blocks-entry",
            false, false, false, false));

    private static OrderIntent CreateEntryIntent(string intentId, string clientOrderId) => new(
        intentId, "runtime-run", "runtime-setup", clientOrderId, new InstrumentId("BTCUSDT"),
        Direction.Long, OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static string OrderUpdate(string clientOrderId, string status, string originalQuantity,
        string executedQuantity, string averagePrice, string lastQuantity)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return string.Concat(
            "{\"e\":\"ORDER_TRADE_UPDATE\",\"E\":", now,
            ",\"T\":", now,
            ",\"o\":{\"X\":\"", status, "\",\"i\":42,\"c\":\"", clientOrderId,
            "\",\"s\":\"BTCUSDT\",\"S\":\"BUY\",\"ps\":\"BOTH\",\"o\":\"MARKET\",",
            "\"q\":\"", originalQuantity, "\",\"z\":\"", executedQuantity,
            "\",\"p\":\"0\",\"ap\":\"", averagePrice,
            "\",\"R\":false,\"cp\":false,\"T\":", now,
            ",\"l\":\"", lastQuantity, "\",\"L\":\"", averagePrice,
            "\",\"t\":7,\"n\":\"0\",\"N\":\"USDT\"}}");
    }

    private sealed class CountingProvider : IExchangeTestCredentialProvider
    {
        public int Calls { get; private set; }

        public ValueTask<ExchangeTestCredentials> ResolveAsync(
            ExchangeTestConfiguration configuration, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new ExchangeTestCredentials("runtime-test-key",
                "runtime-test-secret"));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiKey = request.Headers.GetValues("X-MBX-APIKEY").Single();
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"listenKey\":\"fixture-listen-key\"}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ExchangeTransportHandler : HttpMessageHandler
    {
        public ExchangeTransportHandler(string eventJson)
        {
            _ = eventJson;
        }

        public List<string> RequestHosts { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public string? SubmittedClientOrderId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestHosts.Add(request.RequestUri!.Host);
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            if (request.Method == HttpMethod.Post
                && request.RequestUri.AbsolutePath.EndsWith("/listenKey",
                    StringComparison.Ordinal))
            {
                return Json("{\"listenKey\":\"fixture-listen-key\"}");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri.AbsolutePath.EndsWith("/order",
                    StringComparison.Ordinal))
            {
                SubmittedClientOrderId = Uri.UnescapeDataString(body.Split('&')
                    .Single(part => part.StartsWith("newClientOrderId=",
                        StringComparison.Ordinal)).Split('=', 2)[1]);
                return Json("{\"orderId\":42,\"clientOrderId\":\"integrated-client-order\",\"symbol\":\"BTCUSDT\",\"side\":\"BUY\",\"positionSide\":\"BOTH\",\"type\":\"MARKET\",\"status\":\"NEW\",\"origQty\":\"1\",\"executedQty\":\"0\",\"avgPrice\":\"0\"}");
            }

            return Json("{}");
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StreamOrderCoordination
    {
        public StreamOrderCoordination(string eventJson)
        {
            EventJson = eventJson;
        }

        public string EventJson { get; }
        public bool StreamConnected { get; set; }
        public bool StreamWasConnectedBeforeOrder { get; set; }
        public TaskCompletionSource<bool> OrderSubmitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CoordinatedExchangeHandler : HttpMessageHandler
    {
        private readonly StreamOrderCoordination _coordination;

        public CoordinatedExchangeHandler(StreamOrderCoordination coordination)
        {
            _coordination = coordination;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/listenKey",
                    StringComparison.Ordinal))
            {
                return Json("{\"listenKey\":\"fixture-listen-key\"}");
            }

            if (request.Method == HttpMethod.Post && path == "/fapi/v1/order")
            {
                _coordination.StreamWasConnectedBeforeOrder = _coordination.StreamConnected;
                _coordination.OrderSubmitted.TrySetResult(true);
                return Json("{\"orderId\":42,\"clientOrderId\":\"immediate-client-order\",\"symbol\":\"BTCUSDT\",\"side\":\"BUY\",\"positionSide\":\"BOTH\",\"type\":\"MARKET\",\"status\":\"NEW\",\"origQty\":\"1\",\"executedQty\":\"0\",\"avgPrice\":\"0\"}");
            }

            await Task.Yield();
            return Json("{}");
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CoordinatedPrivateTransport : IBinanceWebSocketTransport
    {
        private readonly StreamOrderCoordination _coordination;
        private bool _sent;

        public CoordinatedPrivateTransport(StreamOrderCoordination coordination)
        {
            _coordination = coordination;
        }

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            State = WebSocketState.Open;
            _coordination.StreamConnected = true;
            return Task.CompletedTask;
        }

        public async Task<BinanceWebSocketReceiveResult> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            await _coordination.OrderSubmitted.Task.WaitAsync(cancellationToken);
            if (!_sent)
            {
                _sent = true;
                return new BinanceWebSocketReceiveResult(WebSocketMessageType.Text,
                    Encoding.UTF8.GetBytes(_coordination.EventJson), true);
            }

            State = WebSocketState.Closed;
            return new BinanceWebSocketReceiveResult(WebSocketMessageType.Close,
                Array.Empty<byte>(), true);
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription,
            CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public void Abort() => State = WebSocketState.Aborted;
        public void Dispose() => State = WebSocketState.Closed;
    }

    private sealed class ProtectionExchangeHandler : HttpMessageHandler
    {
        private readonly StreamOrderCoordination _coordination;

        public ProtectionExchangeHandler(StreamOrderCoordination coordination)
        {
            _coordination = coordination;
        }

        public bool FailAlgoOrder { get; init; }
        public bool AlgoAttempted { get; private set; }
        public string? AlgoPath { get; private set; }
        public string AlgoBody { get; private set; } = string.Empty;
        public string? ClientAlgoId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Post && path.EndsWith("/listenKey",
                    StringComparison.Ordinal))
            {
                return Json("{\"listenKey\":\"fixture-listen-key\"}");
            }

            if (request.Method == HttpMethod.Post && path == "/fapi/v1/order")
            {
                _coordination.OrderSubmitted.TrySetResult(true);
                return Json("{\"orderId\":42,\"clientOrderId\":\"partial-client-order\",\"symbol\":\"BTCUSDT\",\"side\":\"BUY\",\"positionSide\":\"BOTH\",\"type\":\"MARKET\",\"status\":\"PARTIALLY_FILLED\",\"origQty\":\"1\",\"executedQty\":\"0.4\",\"avgPrice\":\"100\"}");
            }

            if (request.Method == HttpMethod.Post && path == "/fapi/v1/algoOrder")
            {
                AlgoAttempted = true;
                AlgoPath = path;
                AlgoBody = body;
                ClientAlgoId = Uri.UnescapeDataString(body.Split('&')
                    .Single(part => part.StartsWith("clientAlgoId=", StringComparison.Ordinal))
                    .Split('=', 2)[1]);
                if (FailAlgoOrder)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{\"msg\":\"Unknown error, please check your request or try again later.\"}", Encoding.UTF8, "application/json")
                    };
                }

                return AlgoResponse();
            }

            if (request.Method == HttpMethod.Get && path == "/fapi/v1/algoOrder")
            {
                return AlgoResponse();
            }

            if (request.Method == HttpMethod.Get && path == "/fapi/v1/positionSide/dual")
            {
                return Json("{\"dualSidePosition\":false}");
            }

            if (request.Method == HttpMethod.Delete && path.EndsWith("/listenKey",
                    StringComparison.Ordinal))
            {
                return Json("{}");
            }

            return Json("{}");
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

        private HttpResponseMessage AlgoResponse() => Json(string.Concat(
            "{\"algoId\":7001,\"clientAlgoId\":\"", ClientAlgoId,
            "\",\"algoType\":\"CONDITIONAL\",\"orderType\":\"STOP_MARKET\",",
            "\"symbol\":\"BTCUSDT\",\"side\":\"SELL\",\"positionSide\":\"BOTH\",",
            "\"quantity\":\"0.4\",\"algoStatus\":\"NEW\",\"triggerPrice\":\"100\",",
            "\"reduceOnly\":true}"));
    }

    private sealed class ReconciliationExchangeHandler : HttpMessageHandler
    {
        private readonly bool _manualPosition;
        private readonly string? _eventJson;

        public ReconciliationExchangeHandler(bool manualPosition, string? eventJson = null)
        {
            _manualPosition = manualPosition;
            _eventJson = eventJson;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/listenKey",
                    StringComparison.Ordinal))
            {
                return Json("{\"listenKey\":\"fixture-listen-key\"}");
            }

            if (request.Method == HttpMethod.Get && path == "/fapi/v1/order")
            {
                if (_manualPosition)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("{\"code\":-2013,\"msg\":\"Order does not exist.\"}", Encoding.UTF8, "application/json")
                    };
                }

                return Json("{\"orderId\":42,\"clientOrderId\":\"reconcile-known-order\",\"symbol\":\"BTCUSDT\",\"side\":\"BUY\",\"positionSide\":\"BOTH\",\"type\":\"MARKET\",\"status\":\"FILLED\",\"origQty\":\"1\",\"executedQty\":\"1\",\"avgPrice\":\"100\"}");
            }

            if (request.Method == HttpMethod.Get && path == "/fapi/v1/allOrders")
            {
                return Json("[]");
            }

            if (request.Method == HttpMethod.Get && path == "/fapi/v1/userTrades")
            {
                return Json("[{\"id\":7,\"orderId\":42,\"symbol\":\"BTCUSDT\",\"side\":\"BUY\",\"positionSide\":\"BOTH\",\"price\":\"100\",\"qty\":\"1\",\"quoteQty\":\"100\",\"commission\":\"0\",\"commissionAsset\":\"USDT\",\"time\":1700000000000}]");
            }

            if (request.Method == HttpMethod.Get && path == "/fapi/v1/positionSide/dual")
            {
                return Json("{\"dualSidePosition\":false}");
            }

            if (request.Method == HttpMethod.Get && path == "/fapi/v3/positionRisk")
            {
                return Json(_manualPosition
                    ? "[{\"symbol\":\"BTCUSDT\",\"positionAmt\":\"0.5\",\"positionSide\":\"BOTH\",\"entryPrice\":\"100\",\"updateTime\":1700000000000}]"
                    : "[{\"symbol\":\"BTCUSDT\",\"positionAmt\":\"1\",\"positionSide\":\"BOTH\",\"entryPrice\":\"100\",\"updateTime\":1700000000000}]");
            }

            if (request.Method == HttpMethod.Delete && path.EndsWith("/listenKey",
                    StringComparison.Ordinal))
            {
                return Json("{}");
            }

            await Task.Yield();
            return Json("{}");
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
    }

    private sealed class QueuePrivateTransport : IBinanceWebSocketTransport
    {
        private readonly byte[] _payload;
        private bool _sent;

        public QueuePrivateTransport(string eventJson)
        {
            _payload = Encoding.UTF8.GetBytes(eventJson);
        }

        public WebSocketState State { get; private set; } = WebSocketState.None;
        public Uri? ConnectedUri { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectedUri = uri;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<BinanceWebSocketReceiveResult> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            if (!_sent)
            {
                _sent = true;
                return Task.FromResult(new BinanceWebSocketReceiveResult(
                    WebSocketMessageType.Text, _payload, true));
            }

            State = WebSocketState.Closed;
            return Task.FromResult(new BinanceWebSocketReceiveResult(
                WebSocketMessageType.Close, Array.Empty<byte>(), true));
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
