using System.Net.WebSockets;
using System.Text;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class BinanceWebSocketClientTests
{
    [Fact]
    public async Task UsesMarketRouteAndSubscribesToRequestedStreams()
    {
        FakeTransport transport = new(
            [
                Text("{\"e\":\"aggTrade\",\"E\":1700000000123,\"s\":\"BTCUSDT\",\"a\":7,\"p\":\"100\",\"q\":\"1.5\",\"f\":10,\"l\":10,\"T\":1700000000122,\"m\":false}"),
                Close()
            ]);
        BinanceMarketWebSocketClient client = CreateClient(transport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan
            });

        List<BinanceStreamEnvelope> events = await ReadAsync(client,
            [BinanceSubscription.AggregateTrade("BTCUSDT")]);

        Assert.Single(events);
        Assert.Equal("wss://fstream.binance.com/market", transport.ConnectedUris.Single().ToString());
        Assert.Contains("\"method\":\"SUBSCRIBE\"", transport.SentTexts.Single());
        Assert.Contains("btcusdt@aggTrade", transport.SentTexts.Single());
        Assert.Equal(0, events[0].StableSequence);
        Assert.Equal("session-1", events[0].SessionId);
    }

    [Fact]
    public async Task ParsesRawAndCombinedEventsAcrossFragmentedFrames()
    {
        FakeTransport transport = new(
            [
                Text("{\"e\":\"aggTrade\",\"E\":1700000000123,\"s\":\"BTCUSDT\",\"a\":7,\"p\":\"100\",\"q\":\"1.5\",\"f\":10,\"l\":10,\"T\":1700000000122,\"m\":false}", endOfMessage: false),
                Text("", endOfMessage: true),
                Text("{\"stream\":\"btcusdt@ticker\",\"data\":{\"e\":\"24hrTicker\",\"E\":1700000000223,\"s\":\"BTCUSDT\",\"p\":\"1\",\"P\":\"1\",\"w\":\"100\",\"c\":\"101\",\"Q\":\"1\",\"o\":\"100\",\"h\":\"102\",\"l\":\"99\",\"v\":\"10\",\"q\":\"1000\",\"O\":1700000000000,\"C\":1700000000223,\"F\":1,\"L\":2,\"n\":2}}"),
                Close()
            ]);
        BinanceMarketWebSocketClient client = CreateClient(transport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan
            });

        List<BinanceStreamEnvelope> events = await ReadAsync(client,
            [BinanceSubscription.AggregateTrade("BTCUSDT"), BinanceSubscription.Ticker("BTCUSDT")]);

        Assert.Equal(2, events.Count);
        Assert.IsType<BinanceAggregateTradeEvent>(events[0].Payload);
        Assert.IsType<BinanceTickerEvent>(events[1].Payload);
        Assert.Equal("btcusdt@ticker", events[1].Payload.StreamName);
        Assert.Equal(2, events[1].RawJson.Length > 0 ? 2 : 0);
    }

    [Fact]
    public async Task KlineUpdatesShareIdentityWithoutConflictAndOnlyClosedUpdateMapsToBar()
    {
        FakeTransport transport = new(
            [
                Text(Kline(1700000000000, "100", "101", "99", "100.5", false, 1700000000123)),
                Text(Kline(1700000000000, "100", "102", "98", "101.5", false, 1700000000223)),
                Text(Kline(1700000000000, "100", "102", "98", "101.5", true, 1700000000323)),
                Close()
            ]);
        BinanceMarketWebSocketClient client = CreateClient(transport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan
            });

        List<BinanceStreamEnvelope> events = await ReadAsync(client,
            [BinanceSubscription.Kline("BTCUSDT", "1m")]);

        Assert.Equal(3, events.Count);
        Assert.DoesNotContain(client.Diagnostics,
            d => d.Kind == BinanceStreamDiagnosticKind.IdentityConflict);
        Assert.Null(events[0].ToCoreEvent());
        Assert.Null(events[1].ToCoreEvent());
        Bar closed = Assert.IsType<Bar>(events[2].ToCoreEvent());
        Assert.Equal(101.5m, closed.Close.Value);
    }

    [Fact]
    public async Task DedupConflictLateStaleAndAggregateGapAreExplicitDiagnostics()
    {
        FixedTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000100000));
        FakeTransport transport = new(
            [
                Text(AggTrade(1, 1700000099000, 1700000099000)),
                Text(AggTrade(1, 1700000099000, 1700000099000)),
                Text(AggTrade(1, 1700000099000, 1700000099000).Replace("\"q\":\"1\"", "\"q\":\"2\"", StringComparison.Ordinal)),
                Text(AggTrade(3, 1700000000000, 1700000000000)),
                Close()
            ]);
        BinanceMarketWebSocketClient client = CreateClient(transport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan,
                StaleAfter = TimeSpan.FromSeconds(5)
            }, time);

        List<BinanceStreamEnvelope> events = await ReadAsync(client,
            [BinanceSubscription.AggregateTrade("BTCUSDT")]);

        Assert.Equal(2, events.Count);
        Assert.Contains(client.Diagnostics, d => d.Kind == BinanceStreamDiagnosticKind.Duplicate);
        Assert.Contains(client.Diagnostics, d => d.Kind == BinanceStreamDiagnosticKind.IdentityConflict);
        Assert.Contains(client.Diagnostics, d => d.Kind == BinanceStreamDiagnosticKind.Gap);
        Assert.Contains(client.Diagnostics, d => d.Kind == BinanceStreamDiagnosticKind.Late);
        Assert.Contains(client.Diagnostics, d => d.Kind == BinanceStreamDiagnosticKind.Stale);
    }

    [Fact]
    public async Task ReconnectsWithBackoffAndResubscribesUsingNewSession()
    {
        FakeTransport transport = new(
            [
                [Text(AggTrade(1, 1700000000000, 1700000000000)), Close()],
                [Text(AggTrade(2, 1700000001000, 1700000001000)), Close()]
            ]);
        BinanceMarketWebSocketClient client = CreateClient(transport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = true,
                MaxReconnectAttempts = 1,
                InitialReconnectDelay = TimeSpan.Zero,
                MaxReconnectDelay = TimeSpan.Zero,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan
            });

        List<BinanceStreamEnvelope> events = await ReadAsync(client,
            [BinanceSubscription.AggregateTrade("BTCUSDT")]);

        Assert.Equal(2, events.Count);
        Assert.Equal("session-1", events[0].SessionId);
        Assert.Equal("session-2", events[1].SessionId);
        Assert.Equal(0, events[1].StableSequence);
        Assert.Equal(2, transport.ConnectedUris.Count);
        Assert.Equal(2, transport.SentTexts.Count(text => text.Contains("SUBSCRIBE", StringComparison.Ordinal)));
        Assert.Contains(client.Diagnostics, d => d.Kind == BinanceStreamDiagnosticKind.Disconnect);
    }

    [Fact]
    public async Task CausalityKeepsExchangeReceiveAvailabilityAndSessionSequence()
    {
        FixedTimeProvider time = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000200000));
        FakeTransport transport = new(
            [Text(AggTrade(1, 1700000000000, 1700000000123)), Close()]);
        BinanceMarketWebSocketClient client = CreateClient(transport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan,
                StaleAfter = Timeout.InfiniteTimeSpan
            }, time);

        BinanceStreamEnvelope envelope = Assert.Single(await ReadAsync(client,
            [BinanceSubscription.AggregateTrade("BTCUSDT")]));

        Assert.Equal(new UtcTimestamp(time.GetUtcNow()), envelope.ReceiveTime);
        Assert.Equal(envelope.ReceiveTime, envelope.AvailabilityTime);
        Assert.Equal(new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(1700000000123)),
            envelope.ExchangeTime);
        Assert.Equal(0, envelope.StableSequence);
    }

    [Fact]
    public async Task LiquidationIsSeparateFromCoreTrade()
    {
        FakeTransport transport = new(
            [Text("{\"e\":\"forceOrder\",\"E\":1700000000123,\"o\":{\"s\":\"BTCUSDT\",\"S\":\"SELL\",\"o\":\"LIMIT\",\"f\":\"IOC\",\"q\":\"1\",\"p\":\"100\",\"ap\":\"99\",\"X\":\"FILLED\",\"l\":\"1\",\"z\":\"1\",\"T\":1700000000122,\"i\":77}}"), Close()]);
        BinanceMarketWebSocketClient client = CreateClient(transport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = Timeout.InfiniteTimeSpan
            });

        BinanceStreamEnvelope envelope = Assert.Single(await ReadAsync(client,
            [BinanceSubscription.Liquidation("BTCUSDT")]));

        BinanceLiquidationEvent liquidation = Assert.IsType<BinanceLiquidationEvent>(envelope.Payload);
        Assert.Equal(77, liquidation.OrderId);
        Assert.Null(envelope.ToCoreEvent());
    }

    [Fact]
    public async Task HeartbeatAndSessionLifetimePoliciesAreConfigurable()
    {
        AdvancingTimeProvider heartbeatTime = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
        FakeTransport heartbeatTransport = new(
            [Text(AggTrade(1, 1700000000000, 1700000000000)), Close()]);
        BinanceMarketWebSocketClient heartbeatClient = CreateClient(heartbeatTransport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = TimeSpan.FromMilliseconds(1),
                SessionLifetime = Timeout.InfiniteTimeSpan
            }, heartbeatTime);

        _ = await ReadAsync(heartbeatClient,
            [BinanceSubscription.AggregateTrade("BTCUSDT")]);

        Assert.Contains(heartbeatTransport.SentTexts,
            text => text.Contains("LIST_SUBSCRIPTIONS", StringComparison.Ordinal));

        AdvancingTimeProvider lifetimeTime = new(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000));
        FakeTransport lifetimeTransport = new(
            [Text(AggTrade(1, 1700000000000, 1700000000000)), Close()]);
        BinanceMarketWebSocketClient lifetimeClient = CreateClient(lifetimeTransport,
            new BinanceWebSocketOptions
            {
                AutoReconnect = false,
                ReceiveTimeout = Timeout.InfiniteTimeSpan,
                HeartbeatInterval = Timeout.InfiniteTimeSpan,
                SessionLifetime = TimeSpan.FromSeconds(1)
            }, lifetimeTime);

        _ = await ReadAsync(lifetimeClient,
            [BinanceSubscription.AggregateTrade("BTCUSDT")]);

        Assert.Contains(lifetimeClient.Diagnostics,
            diagnostic => diagnostic.Kind == BinanceStreamDiagnosticKind.Disconnect
                && diagnostic.Message.Contains("lifetime", StringComparison.OrdinalIgnoreCase));
    }

    private static BinanceMarketWebSocketClient CreateClient(FakeTransport transport,
        BinanceWebSocketOptions? options = null, TimeProvider? timeProvider = null)
    {
        return new BinanceMarketWebSocketClient(transport, options ?? new BinanceWebSocketOptions(),
            timeProvider ?? TimeProvider.System);
    }

    private static async Task<List<BinanceStreamEnvelope>> ReadAsync(
        BinanceMarketWebSocketClient client, IReadOnlyList<BinanceSubscription> subscriptions)
    {
        List<BinanceStreamEnvelope> events = [];
        await foreach (BinanceStreamEnvelope envelope in client.ReadAsync(subscriptions,
            TestContext.Current.CancellationToken))
        {
            events.Add(envelope);
        }

        return events;
    }

    private static FakeFrame Text(string json, bool endOfMessage = true)
    {
        return new FakeFrame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(json), endOfMessage);
    }

    private static FakeFrame Close()
    {
        return new FakeFrame(WebSocketMessageType.Close, [], true);
    }

    private static string AggTrade(long id, long eventTime, long tradeTime)
    {
        return $"{{\"e\":\"aggTrade\",\"E\":{eventTime},\"s\":\"BTCUSDT\",\"a\":{id},\"p\":\"100\",\"q\":\"1\",\"f\":10,\"l\":10,\"T\":{tradeTime},\"m\":false}}";
    }

    private static string Kline(long openTime, string open, string high, string low,
        string close, bool isClosed, long eventTime)
    {
        long closeTime = openTime + 59999;
        return $"{{\"e\":\"kline\",\"E\":{eventTime},\"s\":\"BTCUSDT\",\"k\":{{\"t\":{openTime},\"T\":{closeTime},\"s\":\"BTCUSDT\",\"i\":\"1m\",\"f\":1,\"L\":2,\"o\":\"{open}\",\"c\":\"{close}\",\"h\":\"{high}\",\"l\":\"{low}\",\"v\":\"1\",\"n\":2,\"x\":{isClosed.ToString().ToLowerInvariant()},\"q\":\"100\",\"V\":\"0.5\",\"Q\":\"50\",\"B\":\"0\"}}}}";
    }

    private sealed class FakeTransport : IBinanceWebSocketTransport
    {
        private readonly Queue<IReadOnlyList<FakeFrame>> _sessions;
        private Queue<FakeFrame> _frames = new();

        public FakeTransport(IReadOnlyList<FakeFrame> frames)
        {
            _sessions = new Queue<IReadOnlyList<FakeFrame>>([frames]);
        }

        public FakeTransport(IReadOnlyList<IReadOnlyList<FakeFrame>> sessions)
        {
            _sessions = new Queue<IReadOnlyList<FakeFrame>>(sessions);
        }

        public WebSocketState State { get; private set; } = WebSocketState.None;

        public List<Uri> ConnectedUris { get; } = [];

        public List<string> SentTexts { get; } = [];

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectedUris.Add(uri);
            _frames = new Queue<FakeFrame>(_sessions.Count == 0 ? [] : _sessions.Dequeue());
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
            if (_frames.Count == 0)
            {
                State = WebSocketState.Closed;
                return Task.FromResult(new BinanceWebSocketReceiveResult(
                    WebSocketMessageType.Close, [], true));
            }

            FakeFrame frame = _frames.Dequeue();
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                State = WebSocketState.Closed;
            }

            return Task.FromResult(new BinanceWebSocketReceiveResult(frame.MessageType,
                frame.Payload, frame.EndOfMessage));
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription,
            CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public void Abort()
        {
            State = WebSocketState.Aborted;
        }

        public void Dispose()
        {
            State = WebSocketState.Closed;
        }
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

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _start;
        private long _calls;

        public AdvancingTimeProvider(DateTimeOffset start)
        {
            _start = start;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _start.AddSeconds(Interlocked.Increment(ref _calls) - 1);
        }
    }
}
