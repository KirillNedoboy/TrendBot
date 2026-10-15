using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

/// <summary>
/// Binance public market-stream reader. Transport, timing, reconnect, and
/// deduplication policy stay outside Core and are observable through diagnostics.
/// </summary>
public sealed class BinanceMarketWebSocketClient : IDisposable
{
    private static readonly JsonSerializerOptions CommandJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IBinanceWebSocketTransportFactory _transportFactory;
    private IBinanceWebSocketTransport? _activeTransport;
    private readonly BinanceWebSocketOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly List<BinanceStreamDiagnostic> _diagnostics = [];
    private bool _disposed;
    private int _commandId;

    public BinanceMarketWebSocketClient()
        : this(new ClientWebSocketTransportFactory(), new BinanceWebSocketOptions(),
            TimeProvider.System)
    {
    }

    public BinanceMarketWebSocketClient(IBinanceWebSocketTransport transport,
        BinanceWebSocketOptions options, TimeProvider timeProvider)
        : this(new DelegateBinanceWebSocketTransportFactory(() => transport), options,
            timeProvider)
    {
    }

    public BinanceMarketWebSocketClient(Func<IBinanceWebSocketTransport> transportFactory,
        BinanceWebSocketOptions options, TimeProvider timeProvider)
        : this(new DelegateBinanceWebSocketTransportFactory(transportFactory), options,
            timeProvider)
    {
    }

    public BinanceMarketWebSocketClient(IBinanceWebSocketTransportFactory transportFactory,
        BinanceWebSocketOptions options, TimeProvider timeProvider)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ValidateOptions(_options);
    }

    public IReadOnlyList<BinanceStreamDiagnostic> Diagnostics => _diagnostics.ToArray();

    public event Action<BinanceStreamDiagnostic>? Diagnostic;

    public async IAsyncEnumerable<BinanceStreamEnvelope> ReadAsync(
        IReadOnlyList<BinanceSubscription> subscriptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (subscriptions.Count == 0)
        {
            throw new ArgumentException("At least one stream subscription is required.",
                nameof(subscriptions));
        }

        BinanceSubscription[] snapshot = subscriptions.ToArray();
        if (snapshot.Any(static subscription => subscription.Kind
            == BinanceWebSocketEventKind.Depth))
        {
            throw new ArgumentException(
                "Depth subscriptions must use BinanceDepthWebSocketClient on /public/stream.",
                nameof(subscriptions));
        }
        Dictionary<string, BinanceWebSocketEvent> deduplication = new(StringComparer.Ordinal);
        Queue<string> deduplicationOrder = new();
        Dictionary<string, long> latestEventTimeByStream = new(StringComparer.Ordinal);
        Dictionary<string, long> latestAggregateTradeBySymbol = new(StringComparer.Ordinal);
        int reconnectAttempts = 0;
        TimeSpan reconnectDelay = _options.InitialReconnectDelay;
        int sessionNumber = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            string sessionId = $"session-{++sessionNumber}";
            long stableSequence = 0;
            DateTimeOffset sessionStarted = _timeProvider.GetUtcNow();
            bool reconnect = false;
            string? receiveFailure = null;

            try
            {
                DisposeActiveTransport();
                _activeTransport = _transportFactory.Create();
                await Transport.ConnectAsync(_options.BaseAddress, cancellationToken)
                    .ConfigureAwait(false);
                await SendSubscriptionsAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                receiveFailure = "WebSocket connect timed out.";
                reconnect = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                receiveFailure = $"WebSocket connect failed: {exception.Message}";
                reconnect = true;
            }

            if (reconnect)
            {
                AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId, null, null,
                    receiveFailure!, -1);
            }
            else
            {
                byte[] messageBuffer = [];
                DateTimeOffset nextHeartbeat = NextHeartbeat(sessionStarted);
                while (!cancellationToken.IsCancellationRequested)
                {
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    if (_options.SessionLifetime != Timeout.InfiniteTimeSpan
                        && now - sessionStarted >= _options.SessionLifetime)
                    {
                        AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId, null, null,
                            "The configured WebSocket session lifetime elapsed.", stableSequence);
                        reconnect = true;
                        break;
                    }

                    BinanceWebSocketReceiveResult receiveResult;
                    try
                    {
                        receiveResult = await ReceiveWithTimeoutAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        AddDiagnostic(BinanceStreamDiagnosticKind.Stale, sessionId, null, null,
                            "No WebSocket message arrived before the receive timeout.", stableSequence);
                        AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId, null, null,
                            "The WebSocket receive timeout elapsed.", stableSequence);
                        reconnect = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        yield break;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId, null, null,
                            $"WebSocket receive failed: {exception.Message}", stableSequence);
                        reconnect = true;
                        break;
                    }

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId, null, null,
                            "The WebSocket peer closed the market stream.", stableSequence);
                        reconnect = true;
                        break;
                    }

                    if (receiveResult.MessageType is not WebSocketMessageType.Text
                        and not WebSocketMessageType.Binary)
                    {
                        continue;
                    }

                    if (receiveResult.Payload.Length > _options.MaxFrameBytes)
                    {
                        AddDiagnostic(BinanceStreamDiagnosticKind.FrameTooLarge, sessionId, null,
                            null, "A WebSocket frame exceeded MaxFrameBytes.", stableSequence);
                        messageBuffer = [];
                        reconnect = true;
                        break;
                    }

                    if (messageBuffer.Length + receiveResult.Payload.Length > _options.MaxMessageBytes)
                    {
                        AddDiagnostic(BinanceStreamDiagnosticKind.FrameTooLarge, sessionId, null,
                            null, "A WebSocket message exceeded MaxMessageBytes.", stableSequence);
                        messageBuffer = [];
                        reconnect = true;
                        break;
                    }

                    messageBuffer = Append(messageBuffer, receiveResult.Payload);
                    if (!receiveResult.EndOfMessage)
                    {
                        continue;
                    }

                    string rawJson = Encoding.UTF8.GetString(messageBuffer);
                    messageBuffer = [];
                    BinanceWebSocketEvent? marketEvent;
                    try
                    {
                        marketEvent = ParseEvent(rawJson);
                    }
                    catch (Exception exception) when (exception is JsonException
                        or FormatException or ArgumentException)
                    {
                        AddDiagnostic(BinanceStreamDiagnosticKind.ParseError, sessionId, null, null,
                            exception.Message, stableSequence);
                        continue;
                    }

                    DateTimeOffset receiveTime = _timeProvider.GetUtcNow();
                    long eventSequence = stableSequence++;
                    bool accepted = DiagnoseAndAccept(marketEvent, sessionId, eventSequence,
                        receiveTime, deduplication, deduplicationOrder,
                        latestEventTimeByStream, latestAggregateTradeBySymbol);
                    if (accepted)
                    {
                        DateTimeOffset availabilityTime = _timeProvider.GetUtcNow();
                        yield return new BinanceStreamEnvelope(sessionId, eventSequence, marketEvent,
                            new UtcTimestamp(receiveTime),
                            new UtcTimestamp(availabilityTime), marketEvent.ExchangeTime, rawJson);
                    }

                    DateTimeOffset afterEvent = _timeProvider.GetUtcNow();
                    if (_options.HeartbeatInterval != Timeout.InfiniteTimeSpan
                        && afterEvent >= nextHeartbeat)
                    {
                        await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                        nextHeartbeat = NextHeartbeat(afterEvent);
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (!reconnect || !_options.AutoReconnect
                || reconnectAttempts >= _options.MaxReconnectAttempts)
            {
                break;
            }

            reconnectAttempts++;
            AddDiagnostic(BinanceStreamDiagnosticKind.Reconnect, sessionId, null, null,
                $"Reconnecting and resubscribing (attempt {reconnectAttempts}).", -1);
            await DelayAsync(reconnectDelay, cancellationToken).ConfigureAwait(false);
            reconnectDelay = NextBackoff(reconnectDelay);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeActiveTransport();
    }

    private async Task<BinanceWebSocketReceiveResult> ReceiveWithTimeoutAsync(
        CancellationToken cancellationToken)
    {
        if (_options.ReceiveTimeout == Timeout.InfiniteTimeSpan)
        {
            return await Transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_options.ReceiveTimeout);
        return await Transport.ReceiveAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task SendSubscriptionsAsync(IReadOnlyList<BinanceSubscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var command = new
        {
            method = "SUBSCRIBE",
            @params = subscriptions.Select(static subscription => subscription.StreamName).ToArray(),
            id = Interlocked.Increment(ref _commandId)
        };
        await SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (_options.UseCombinedStreams)
        {
            var property = new
            {
                method = "SET_PROPERTY",
                @params = new object[] { "combined", true },
                id = Interlocked.Increment(ref _commandId)
            };
            await SendCommandAsync(property, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        var command = new
        {
            method = "LIST_SUBSCRIPTIONS",
            id = Interlocked.Increment(ref _commandId)
        };
        return SendCommandAsync(command, cancellationToken);
    }

    private async Task SendCommandAsync<T>(T command, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command, CommandJsonOptions);
        await Transport.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    private IBinanceWebSocketTransport Transport =>
        _activeTransport ?? throw new InvalidOperationException("The WebSocket transport is not active.");

    private void DisposeActiveTransport()
    {
        _activeTransport?.Dispose();
        _activeTransport = null;
    }

    private static BinanceWebSocketEvent ParseEvent(string rawJson)
    {
        using JsonDocument document = JsonDocument.Parse(rawJson);
        JsonElement root = document.RootElement;
        string? streamName = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Binance WebSocket message must be an object.");
        }

        if (root.TryGetProperty("data", out JsonElement combinedData))
        {
            streamName = ReadString(root, "stream");
            root = combinedData;
        }

        string eventType = ReadString(root, "e");
        long eventTime = ReadInt64(root, "E");
        return eventType switch
        {
            "aggTrade" => ParseAggregateTrade(root, streamName, eventTime),
            "kline" => ParseKline(root, streamName, eventTime),
            "24hrTicker" => ParseTicker(root, streamName, eventTime),
            "markPriceUpdate" => ParseMarkPrice(root, streamName, eventTime),
            "forceOrder" => ParseLiquidation(root, streamName, eventTime),
            _ => throw new JsonException($"Unsupported Binance WebSocket event type '{eventType}'.")
        };
    }

    private static BinanceAggregateTradeEvent ParseAggregateTrade(JsonElement root,
        string? streamName, long eventTime)
    {
        string symbol = ReadString(root, "s");
        return new BinanceAggregateTradeEvent(symbol, eventTime, ReadInt64(root, "a"),
            ReadDecimal(root, "p"), ReadDecimal(root, "q"), ReadInt64(root, "f"),
            ReadInt64(root, "l"), ReadInt64(root, "T"), ReadBoolean(root, "m"),
            streamName ?? new BinanceSubscription(symbol, BinanceWebSocketEventKind.AggregateTrade)
                .StreamName);
    }

    private static BinanceKlineEvent ParseKline(JsonElement root, string? streamName,
        long eventTime)
    {
        string symbol = ReadString(root, "s");
        JsonElement kline = Required(root, "k");
        string interval = ReadString(kline, "i");
        long openTime = ReadInt64(kline, "t");
        long closeTime = ReadInt64(kline, "T");
        BinanceKline value = new(openTime, ReadDecimal(kline, "o"), ReadDecimal(kline, "h"),
            ReadDecimal(kline, "l"), ReadDecimal(kline, "c"), ReadDecimal(kline, "v"),
            closeTime, ReadDecimal(kline, "q"), ReadInt64(kline, "n"),
            ReadDecimal(kline, "V"), ReadDecimal(kline, "Q"), ReadString(kline, "B"),
            ReadBoolean(kline, "x"));
        return new BinanceKlineEvent(symbol, eventTime, value,
            streamName ?? new BinanceSubscription(symbol, BinanceWebSocketEventKind.Kline, interval)
                .StreamName);
    }

    private static BinanceTickerEvent ParseTicker(JsonElement root, string? streamName,
        long eventTime)
    {
        string symbol = ReadString(root, "s");
        return new BinanceTickerEvent(symbol, eventTime, ReadDecimal(root, "p"),
            ReadDecimal(root, "P"), ReadDecimal(root, "w"), ReadDecimal(root, "c"),
            ReadDecimal(root, "Q"), ReadDecimal(root, "o"), ReadDecimal(root, "h"),
            ReadDecimal(root, "l"), ReadDecimal(root, "v"), ReadDecimal(root, "q"),
            ReadInt64(root, "O"), ReadInt64(root, "C"), ReadInt64(root, "F"),
            ReadInt64(root, "L"), ReadInt64(root, "n"),
            streamName ?? new BinanceSubscription(symbol, BinanceWebSocketEventKind.Ticker).StreamName);
    }

    private static BinanceMarkPriceEvent ParseMarkPrice(JsonElement root, string? streamName,
        long eventTime)
    {
        string symbol = ReadString(root, "s");
        return new BinanceMarkPriceEvent(symbol, eventTime, ReadDecimal(root, "p"),
            ReadDecimal(root, "i"), ReadDecimal(root, "r"), ReadInt64(root, "T"),
            streamName ?? new BinanceSubscription(symbol, BinanceWebSocketEventKind.MarkPrice)
                .StreamName);
    }

    private static BinanceLiquidationEvent ParseLiquidation(JsonElement root,
        string? streamName, long eventTime)
    {
        JsonElement order = Required(root, "o");
        string symbol = ReadString(order, "s");
        string side = ReadString(order, "S");
        TradeSide tradeSide = side switch
        {
            "BUY" => TradeSide.Buy,
            "SELL" => TradeSide.Sell,
            _ => throw new JsonException($"Unknown Binance liquidation side '{side}'.")
        };
        return new BinanceLiquidationEvent(symbol, eventTime, ReadInt64(order, "i"), tradeSide,
            ReadDecimal(order, "q"), ReadDecimal(order, "p"), ReadDecimal(order, "ap"),
            ReadDecimal(order, "l"), ReadDecimal(order, "z"), ReadString(order, "X"),
            ReadInt64(order, "T"), streamName ?? new BinanceSubscription(symbol,
                BinanceWebSocketEventKind.Liquidation).StreamName);
    }

    private bool DiagnoseAndAccept(BinanceWebSocketEvent marketEvent, string sessionId,
        long stableSequence, DateTimeOffset observedAt,
        Dictionary<string, BinanceWebSocketEvent> deduplication,
        Queue<string> deduplicationOrder, Dictionary<string, long> latestEventTimeByStream,
        Dictionary<string, long> latestAggregateTradeBySymbol)
    {
        string identity = $"{marketEvent.Kind}:{marketEvent.Symbol}:{marketEvent.EventId}";
        if (latestEventTimeByStream.TryGetValue(marketEvent.StreamName, out long latest)
            && marketEvent.EventTimeMilliseconds < latest)
        {
            AddDiagnostic(BinanceStreamDiagnosticKind.Late, sessionId, marketEvent.StreamName,
                marketEvent.EventId, "Event time moved backwards within a stream.", stableSequence);
        }
        else if (!latestEventTimeByStream.ContainsKey(marketEvent.StreamName)
            || marketEvent.EventTimeMilliseconds > latest)
        {
            latestEventTimeByStream[marketEvent.StreamName] = marketEvent.EventTimeMilliseconds;
        }

        if (_options.StaleAfter != Timeout.InfiniteTimeSpan
            && observedAt.ToUnixTimeMilliseconds() - marketEvent.EventTimeMilliseconds
            > _options.StaleAfter.TotalMilliseconds)
        {
            AddDiagnostic(BinanceStreamDiagnosticKind.Stale, sessionId, marketEvent.StreamName,
                marketEvent.EventId, "Event was older than the configured stale threshold.",
                stableSequence);
        }

        if (marketEvent is BinanceAggregateTradeEvent aggregate
            && latestAggregateTradeBySymbol.TryGetValue(aggregate.Symbol, out long previous)
            && aggregate.AggregateTradeId > previous
            && aggregate.AggregateTradeId - previous > 1)
        {
            AddDiagnostic(BinanceStreamDiagnosticKind.Gap, sessionId, marketEvent.StreamName,
                marketEvent.EventId,
                $"Aggregate trade sequence jumped from {previous} to {aggregate.AggregateTradeId}.",
                stableSequence);
        }

        if (marketEvent is BinanceAggregateTradeEvent aggregateEvent
            && (!latestAggregateTradeBySymbol.TryGetValue(aggregateEvent.Symbol, out long known)
                || aggregateEvent.AggregateTradeId > known))
        {
            latestAggregateTradeBySymbol[aggregateEvent.Symbol] = aggregateEvent.AggregateTradeId;
        }

        if (deduplication.TryGetValue(identity, out BinanceWebSocketEvent? previousEvent))
        {
            if (marketEvent is BinanceKlineEvent)
            {
                if (marketEvent.Equals(previousEvent))
                {
                    AddDiagnostic(BinanceStreamDiagnosticKind.Duplicate, sessionId,
                        marketEvent.StreamName, marketEvent.EventId,
                        "Identical kline update was deduplicated.", stableSequence);
                    return false;
                }

                deduplication[identity] = marketEvent;
                return true;
            }

            AddDiagnostic(marketEvent.Equals(previousEvent)
                ? BinanceStreamDiagnosticKind.Duplicate
                : BinanceStreamDiagnosticKind.IdentityConflict, sessionId,
                marketEvent.StreamName, marketEvent.EventId,
                marketEvent.Equals(previousEvent)
                    ? "Identical event was deduplicated."
                    : "Event identity was reused with different values.", stableSequence);
            return false;
        }

        deduplication[identity] = marketEvent;
        deduplicationOrder.Enqueue(identity);
        while (deduplicationOrder.Count > _options.DeduplicationCapacity)
        {
            deduplication.Remove(deduplicationOrder.Dequeue());
        }

        return true;
    }

    private void AddDiagnostic(BinanceStreamDiagnosticKind kind, string sessionId,
        string? streamName, string? eventId, string message, long stableSequence)
    {
        BinanceStreamDiagnostic diagnostic = new(kind, sessionId, streamName, eventId, message,
            stableSequence, new UtcTimestamp(_timeProvider.GetUtcNow()));
        _diagnostics.Add(diagnostic);
        Diagnostic?.Invoke(diagnostic);
    }

    private DateTimeOffset NextHeartbeat(DateTimeOffset from)
    {
        return _options.HeartbeatInterval == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue : from + _options.HeartbeatInterval;
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan NextBackoff(TimeSpan current)
    {
        if (current <= TimeSpan.Zero || current >= _options.MaxReconnectDelay)
        {
            return _options.MaxReconnectDelay;
        }

        long ticks = Math.Min(current.Ticks * 2, _options.MaxReconnectDelay.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private static byte[] Append(byte[] existing, byte[] next)
    {
        byte[] result = new byte[existing.Length + next.Length];
        existing.CopyTo(result, 0);
        next.CopyTo(result, existing.Length);
        return result;
    }

    private static JsonElement Required(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new JsonException($"Required Binance WebSocket property '{propertyName}' is missing.");
        }

        return value;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        JsonElement value = Required(root, propertyName);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException($"Binance WebSocket property '{propertyName}' must be text.");
        }

        return text;
    }

    private static bool ReadBoolean(JsonElement root, string propertyName)
    {
        JsonElement value = Required(root, propertyName);
        return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False
            ? value.GetBoolean()
            : throw new JsonException($"Binance WebSocket property '{propertyName}' must be boolean.");
    }

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        JsonElement value = Required(root, propertyName);
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.String => long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                _ => throw new JsonException($"Binance WebSocket property '{propertyName}' must be integer.")
            };
        }
        catch (FormatException exception)
        {
            throw new JsonException($"Binance WebSocket property '{propertyName}' is not integer.",
                exception);
        }
    }

    private static decimal ReadDecimal(JsonElement root, string propertyName)
    {
        JsonElement value = Required(root, propertyName);
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.String => decimal.Parse(value.GetString()!, NumberStyles.Float,
                    CultureInfo.InvariantCulture),
                _ => throw new JsonException($"Binance WebSocket property '{propertyName}' must be decimal.")
            };
        }
        catch (FormatException exception)
        {
            throw new JsonException($"Binance WebSocket property '{propertyName}' is not decimal.",
                exception);
        }
    }

    private static void ValidateOptions(BinanceWebSocketOptions options)
    {
        if (!options.BaseAddress.IsAbsoluteUri
            || !StringComparer.OrdinalIgnoreCase.Equals(options.BaseAddress.Scheme, "wss"))
        {
            throw new ArgumentException("The WebSocket base address must be an absolute wss URI.",
                nameof(options));
        }

        if (!options.BaseAddress.AbsolutePath.TrimEnd('/').EndsWith("/market",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Binance market stream must use the /market route.",
                nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxReconnectAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxFrameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DeduplicationCapacity);
        ValidateTimeout(options.ReceiveTimeout, nameof(options));
        ValidateTimeout(options.HeartbeatInterval, nameof(options));
        ValidateTimeout(options.SessionLifetime, nameof(options));
        ValidateTimeout(options.StaleAfter, nameof(options));
        if (options.InitialReconnectDelay < TimeSpan.Zero
            || options.MaxReconnectDelay < options.InitialReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>Production transport around the BCL ClientWebSocket.</summary>
public sealed class ClientWebSocketTransport : IBinanceWebSocketTransport
{
    private readonly ClientWebSocket _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _keepAliveInterval;

    public ClientWebSocketTransport(TimeSpan? keepAliveInterval = null)
        : this(new ClientWebSocket(), true, keepAliveInterval ?? TimeSpan.FromSeconds(30))
    {
    }

    public ClientWebSocketTransport(ClientWebSocket client, TimeSpan? keepAliveInterval = null)
        : this(client, false, keepAliveInterval ?? TimeSpan.FromSeconds(30))
    {
    }

    private ClientWebSocketTransport(ClientWebSocket client, bool ownsClient,
        TimeSpan keepAliveInterval)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveInterval, TimeSpan.Zero);

        _keepAliveInterval = keepAliveInterval;
    }

    public WebSocketState State => _client.State;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        _client.Options.KeepAliveInterval = _keepAliveInterval;
        await _client.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        return _client.SendAsync(payload, messageType, endOfMessage, cancellationToken).AsTask();
    }

    public async Task<BinanceWebSocketReceiveResult> ReceiveAsync(
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        WebSocketReceiveResult result = await _client.ReceiveAsync(buffer, cancellationToken)
            .ConfigureAwait(false);
        return new BinanceWebSocketReceiveResult(result.MessageType,
            buffer.AsMemory(0, result.Count), result.EndOfMessage);
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription,
        CancellationToken cancellationToken)
    {
        return _client.CloseAsync(closeStatus, statusDescription, cancellationToken);
    }

    public void Abort() => _client.Abort();

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
