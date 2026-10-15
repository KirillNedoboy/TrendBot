using System.Net;
using System.Net.WebSockets;
using System.Text;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class BinanceDepthAndOrderBookTests
{
    private static readonly Uri BaseAddress = new("https://fixture.test");

    [Fact]
    public async Task DepthSnapshotUsesOfficialEndpointAndPreservesExactValuesAndTimes()
    {
        FixtureHandler handler = new(request =>
        {
            Assert.Equal("/fapi/v1/depth", request.RequestUri!.AbsolutePath);
            Assert.Equal("symbol=BTCUSDT&limit=1000", request.RequestUri.Query.TrimStart('?'));
            return Json("""
                {
                  "lastUpdateId":1027024,
                  "E":1589436922972,
                  "T":1589436922959,
                  "bids":[["4.00000000000000000001","431.00000000000000000002"]],
                  "asks":[["4.00000200000000000001","12.00000000000000000002"]]
                }
                """);
        });
        BinancePublicRestClient client = new(new HttpClient(handler) { BaseAddress = BaseAddress },
            new BinanceRestOptions(BaseAddress));

        BinanceDepthSnapshot snapshot = await client.GetDepthSnapshotAsync("BTCUSDT", 1000,
            TestContext.Current.CancellationToken);

        Assert.Equal(1027024, snapshot.LastUpdateId);
        Assert.Equal(1589436922972, snapshot.EventTimeMilliseconds);
        Assert.Equal(1589436922959, snapshot.TransactionTimeMilliseconds);
        Assert.Equal(4.00000000000000000001m, snapshot.Bids.Single().Price);
        Assert.Equal(431.00000000000000000002m, snapshot.Bids.Single().Quantity);
        Assert.Equal(4.00000200000000000001m, snapshot.Asks.Single().Price);
        Assert.Equal(12.00000000000000000002m, snapshot.Asks.Single().Quantity);
    }

    [Fact]
    public async Task DepthStreamUsesPublicStreamRouteAndRetainsCausalEnvelope()
    {
        FakeTransport transport = new(
            [Text("{\"stream\":\"btcusdt@depth@100ms\",\"data\":{\"e\":\"depthUpdate\",\"E\":1700000000123,\"T\":1700000000119,\"s\":\"BTCUSDT\",\"U\":101,\"u\":102,\"pu\":100,\"b\":[[\"100.00\",\"2.5\"]],\"a\":[[\"101.00\",\"3.5\"]]}}"), Close()]);
        FixedTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000001000));
        BinanceDepthWebSocketClient client = new(transport,
            new BinanceDepthWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan
            }, time);

        List<BinanceStreamEnvelope> envelopes = [];
        await foreach (BinanceStreamEnvelope envelope in client.ReadAsync(
            [BinanceDepthSubscription.Diff("BTCUSDT", "100ms")],
            TestContext.Current.CancellationToken))
        {
            envelopes.Add(envelope);
        }

        BinanceStreamEnvelope result = Assert.Single(envelopes);
        BinanceDepthUpdateEvent depth = Assert.IsType<BinanceDepthUpdateEvent>(result.Payload);
        Assert.Equal("wss://fstream.binance.com/public/stream", transport.ConnectedUris.Single().ToString());
        Assert.Contains(transport.SentTexts,
            text => text.Contains("btcusdt@depth@100ms", StringComparison.Ordinal));
        Assert.Equal(101, depth.FirstUpdateId);
        Assert.Equal(102, depth.FinalUpdateId);
        Assert.Equal(100, depth.PreviousUpdateId);
        Assert.Equal(new UtcTimestamp(time.GetUtcNow()), result.ReceiveTime);
        Assert.Equal(new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000119)),
            result.ExchangeTime);
        Assert.Equal(0, result.StableSequence);
        Assert.IsType<BookDelta>(result.ToCoreEvent());
    }

    [Fact]
    public async Task RecoveryBuffersThenAlignsSnapshotAndAppliesContiguousDiffs()
    {
        BinanceDepthSnapshot snapshot = Snapshot(100,
            bids: [(100m, 1m)], asks: [(101m, 2m)]);
        SnapshotSource source = new(snapshot);
        BinanceLocalOrderBookRecovery recovery = new(source,
            new BinanceOrderBookRecoveryOptions { MaxBufferedUpdates = 8 });

        BinanceStreamEnvelope buffered = Envelope(98, 99, 97,
            bids: [(99m, 3m)], asks: []);
        BinanceStreamEnvelope overlapping = Envelope(99, 101, 99,
            bids: [(100m, 4m), (99m, 3m)], asks: [(101m, 0m)]);

        BinanceOrderBookState recovering = await recovery.AcceptAsync(buffered,
            TestContext.Current.CancellationToken);
        BinanceOrderBookState state = await recovery.AcceptAsync(overlapping,
            TestContext.Current.CancellationToken);

        Assert.Equal(BinanceOrderBookStateKind.Ready, state.Kind);
        Assert.Equal(101, state.LastUpdateId);
        Assert.Equal(4m, state.Bids.Single(level => level.Price == 100m).Quantity);
        Assert.Equal(3m, state.Bids.Single(level => level.Price == 99m).Quantity);
        Assert.Empty(state.Asks);
        Assert.Equal(101, state.LastAppliedStableSequence);
        Assert.Equal(1700000000101, state.LastEventTimeMilliseconds);
        Assert.NotSame(recovering.Bids, state.Bids);
    }

    [Fact]
    public async Task RecoveryMarksSequenceGapAndNeverPublishesPartiallyAppliedBook()
    {
        SnapshotSource source = new(Snapshot(10, bids: [(100m, 1m)], asks: [(101m, 2m)]));
        BinanceLocalOrderBookRecovery recovery = new(source,
            new BinanceOrderBookRecoveryOptions { MaxBufferedUpdates = 8 });

        BinanceOrderBookState ready = await recovery.AcceptAsync(Envelope(10, 11, 10,
            bids: [(100m, 3m)], asks: []), TestContext.Current.CancellationToken);
        Assert.Equal(BinanceOrderBookStateKind.Ready, ready.Kind);
        Assert.Equal(3m, ready.Bids.Single().Quantity);

        BinanceOrderBookState recovering = await recovery.AcceptAsync(Envelope(20, 21, 11,
            bids: [(99m, 5m)], asks: []), TestContext.Current.CancellationToken);

        Assert.Equal(BinanceOrderBookStateKind.Recovering, recovering.Kind);
        Assert.Empty(recovering.Bids);
        Assert.Empty(recovering.Asks);
        Assert.Contains(recovery.Diagnostics, diagnostic => diagnostic.Kind
            == BinanceOrderBookRecoveryDiagnosticKind.Gap);
    }

    [Fact]
    public async Task RecoveryBoundsBufferedUpdatesAndKeepsStateImmutable()
    {
        FailingSnapshotSource source = new();
        BinanceLocalOrderBookRecovery recovery = new(source,
            new BinanceOrderBookRecoveryOptions { MaxBufferedUpdates = 1 });

        await recovery.AcceptAsync(Envelope(1, 1, null, bids: [], asks: []),
            TestContext.Current.CancellationToken);
        BinanceOrderBookState overflow = await recovery.AcceptAsync(
            Envelope(2, 2, null, bids: [], asks: []), TestContext.Current.CancellationToken);

        Assert.Equal(BinanceOrderBookStateKind.Recovering, overflow.Kind);
        Assert.Contains(recovery.Diagnostics, diagnostic => diagnostic.Kind
            == BinanceOrderBookRecoveryDiagnosticKind.BufferOverflow);
        Assert.Throws<NotSupportedException>(() => ((IList<BinanceDepthLevel>)overflow.Bids).Add(
            new BinanceDepthLevel(1m, 1m)));
    }

    [Fact]
    public async Task ReconnectFactoryCreatesFreshTransportForEachSession()
    {
        List<FakeTransport> transports = [];
        BinanceMarketWebSocketClient client = new(
            () =>
            {
                FakeTransport transport = new([Close()]);
                transports.Add(transport);
                return transport;
            },
            new BinanceWebSocketOptions
            {
                AutoReconnect = true,
                MaxReconnectAttempts = 1,
                InitialReconnectDelay = TimeSpan.Zero,
                MaxReconnectDelay = TimeSpan.Zero,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan
            },
            TimeProvider.System);

        _ = await ReadAllAsync(client, [BinanceSubscription.AggregateTrade("BTCUSDT")]);

        Assert.Equal(2, transports.Count);
        Assert.NotSame(transports[0], transports[1]);
        Assert.Contains(client.Diagnostics, item => item.Kind == BinanceStreamDiagnosticKind.Reconnect);
    }

    private static BinanceStreamEnvelope Envelope(long first, long last, long? previous,
        IReadOnlyList<(decimal Price, decimal Quantity)> bids,
        IReadOnlyList<(decimal Price, decimal Quantity)> asks)
    {
        BinanceDepthUpdateEvent payload = new("BTCUSDT", 1700000000000 + last,
            first, last, previous, bids.Select(static level => new BinanceDepthLevel(level.Price,
                level.Quantity)), asks.Select(static level => new BinanceDepthLevel(level.Price,
                level.Quantity)), "btcusdt@depth");
        UtcTimestamp now = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000000100 + last));
        return new BinanceStreamEnvelope("session-1", last, payload, now, now,
            payload.ExchangeTime, "{\"e\":\"depthUpdate\"}");
    }

    private static BinanceDepthSnapshot Snapshot(long lastUpdateId,
        IReadOnlyList<(decimal Price, decimal Quantity)> bids,
        IReadOnlyList<(decimal Price, decimal Quantity)> asks)
    {
        return new BinanceDepthSnapshot(lastUpdateId, 1700000000000 + lastUpdateId,
            1700000000000 + lastUpdateId - 1,
            bids.Select(static level => new BinanceDepthLevel(level.Price, level.Quantity)),
            asks.Select(static level => new BinanceDepthLevel(level.Price, level.Quantity)));
    }

    private static async Task<List<BinanceStreamEnvelope>> ReadAllAsync(
        BinanceMarketWebSocketClient client, IReadOnlyList<BinanceSubscription> subscriptions)
    {
        List<BinanceStreamEnvelope> values = [];
        await foreach (BinanceStreamEnvelope value in client.ReadAsync(subscriptions,
            TestContext.Current.CancellationToken))
        {
            values.Add(value);
        }

        return values;
    }

    private static FakeFrame Text(string json) =>
        new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json), true);

    private static FakeFrame Close() => new(WebSocketMessageType.Close, [], true);

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class SnapshotSource : IBinanceDepthSnapshotSource
    {
        private readonly BinanceDepthSnapshot _snapshot;

        public SnapshotSource(BinanceDepthSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<BinanceDepthSnapshot> GetDepthSnapshotAsync(string symbol, int limit,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class FailingSnapshotSource : IBinanceDepthSnapshotSource
    {
        public Task<BinanceDepthSnapshot> GetDepthSnapshotAsync(string symbol, int limit,
            CancellationToken cancellationToken)
        {
            return Task.FromException<BinanceDepthSnapshot>(
                new InvalidOperationException("fixture snapshot unavailable"));
        }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_handler(request));
    }

    private sealed class FakeTransport : IBinanceWebSocketTransport
    {
        private readonly Queue<FakeFrame> _frames;

        public FakeTransport(IReadOnlyList<FakeFrame> frames)
        {
            _frames = new Queue<FakeFrame>(frames);
        }

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public List<Uri> ConnectedUris { get; } = [];

        public List<string> SentTexts { get; } = [];

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectedUris.Add(uri);
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            SentTexts.Add(Encoding.UTF8.GetString(payload.Span));
            return Task.CompletedTask;
        }

        public Task<BinanceWebSocketReceiveResult> ReceiveAsync(CancellationToken cancellationToken)
        {
            FakeFrame frame = _frames.Count == 0 ? Close() : _frames.Dequeue();
            State = frame.MessageType == WebSocketMessageType.Close
                ? WebSocketState.Closed : WebSocketState.Open;
            return Task.FromResult(new BinanceWebSocketReceiveResult(frame.MessageType,
                frame.Payload, frame.EndOfMessage));
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

    private sealed record FakeFrame(WebSocketMessageType MessageType, byte[] Payload,
        bool EndOfMessage);

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
