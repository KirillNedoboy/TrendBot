using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

/// <summary>Connection policy for the routed high-frequency public depth stream.</summary>
public sealed record BinanceDepthWebSocketOptions
{
    public Uri BaseAddress { get; init; } =
        new("wss://fstream.binance.com/public/stream");

    public bool UseCombinedStreams { get; init; } = true;

    public bool AutoReconnect { get; init; } = true;

    public int MaxReconnectAttempts { get; init; } = 3;

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxFrameBytes { get; init; } = 1 * 1024 * 1024;

    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;

    public int DeduplicationCapacity { get; init; } = 10_000;
}

/// <summary>Factory helpers that make the depth route explicit at call sites.</summary>
public static class BinanceDepthSubscription
{
    public static BinanceSubscription Diff(string symbol, string? updateSpeed = null) =>
        BinanceSubscription.Depth(symbol, updateSpeed);
}

/// <summary>
/// Reads Binance USD-M diff-depth streams on the isolated /public/stream route.
/// It emits immutable envelopes; local book recovery is a separate boundary.
/// </summary>
public class BinanceDepthWebSocketClient : IDisposable
{
    private static readonly JsonSerializerOptions CommandJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    private readonly IBinanceWebSocketTransportFactory _transportFactory;
    private readonly BinanceDepthWebSocketOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly List<BinanceStreamDiagnostic> _diagnostics = [];
    private IBinanceWebSocketTransport? _activeTransport;
    private bool _disposed;
    private int _commandId;

    public BinanceDepthWebSocketClient()
        : this(new ClientWebSocketTransportFactory(), new BinanceDepthWebSocketOptions(),
            TimeProvider.System)
    {
    }

    public BinanceDepthWebSocketClient(IBinanceWebSocketTransport transport,
        BinanceDepthWebSocketOptions options, TimeProvider timeProvider)
        : this(new DelegateBinanceWebSocketTransportFactory(() => transport), options,
            timeProvider)
    {
    }

    public BinanceDepthWebSocketClient(Func<IBinanceWebSocketTransport> transportFactory,
        BinanceDepthWebSocketOptions options, TimeProvider timeProvider)
        : this(new DelegateBinanceWebSocketTransportFactory(transportFactory), options,
            timeProvider)
    {
    }

    public BinanceDepthWebSocketClient(IBinanceWebSocketTransportFactory transportFactory,
        BinanceDepthWebSocketOptions options, TimeProvider timeProvider)
    {
        _transportFactory = transportFactory
            ?? throw new ArgumentNullException(nameof(transportFactory));
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
            throw new ArgumentException("At least one depth subscription is required.",
                nameof(subscriptions));
        }

        BinanceSubscription[] snapshot = subscriptions.ToArray();
        if (snapshot.Any(static subscription =>
            subscription.Kind != BinanceWebSocketEventKind.Depth))
        {
            throw new ArgumentException("The depth client accepts only depth subscriptions.",
                nameof(subscriptions));
        }

        Dictionary<string, BinanceWebSocketEvent> deduplication = new(StringComparer.Ordinal);
        Queue<string> deduplicationOrder = new();
        Dictionary<string, long> latestEventTimeByStream = new(StringComparer.Ordinal);
        Dictionary<string, long> latestFinalUpdateBySymbol = new(StringComparer.Ordinal);
        int reconnectAttempts = 0;
        TimeSpan reconnectDelay = _options.InitialReconnectDelay;
        int sessionNumber = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string sessionId = $"depth-session-{++sessionNumber}";
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
                    await SendSubscriptionsAsync(snapshot, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    receiveFailure = "Depth WebSocket connect timed out.";
                    reconnect = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    receiveFailure = $"Depth WebSocket connect failed: {exception.Message}";
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
                            AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId,
                                null, null, "The configured depth session lifetime elapsed.",
                                stableSequence);
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
                            AddDiagnostic(BinanceStreamDiagnosticKind.Stale, sessionId, null,
                                null, "No depth message arrived before the receive timeout.",
                                stableSequence);
                            AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId,
                                null, null, "The depth receive timeout elapsed.",
                                stableSequence);
                            reconnect = true;
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            yield break;
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId,
                                null, null, $"Depth receive failed: {exception.Message}",
                                stableSequence);
                            reconnect = true;
                            break;
                        }

                        if (receiveResult.MessageType == WebSocketMessageType.Close)
                        {
                            AddDiagnostic(BinanceStreamDiagnosticKind.Disconnect, sessionId,
                                null, null, "The depth peer closed the stream.", stableSequence);
                            reconnect = true;
                            break;
                        }

                        if (receiveResult.MessageType is not WebSocketMessageType.Text
                            and not WebSocketMessageType.Binary)
                        {
                            continue;
                        }

                        if (receiveResult.Payload.Length > _options.MaxFrameBytes
                            || messageBuffer.Length + receiveResult.Payload.Length
                                > _options.MaxMessageBytes)
                        {
                            AddDiagnostic(BinanceStreamDiagnosticKind.FrameTooLarge, sessionId,
                                null, null, "A depth frame or message exceeded its configured limit.",
                                stableSequence);
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
                        BinanceDepthUpdateEvent depth;
                        try
                        {
                            depth = ParseEvent(rawJson);
                        }
                        catch (Exception exception) when (exception is JsonException
                            or FormatException or ArgumentException
                            or OverflowException)
                        {
                            AddDiagnostic(BinanceStreamDiagnosticKind.ParseError, sessionId,
                                null, null, exception.Message, stableSequence);
                            continue;
                        }

                        DateTimeOffset receiveTime = _timeProvider.GetUtcNow();
                        long eventSequence = stableSequence++;
                        bool accepted = DiagnoseAndAccept(depth, sessionId, eventSequence,
                            receiveTime, deduplication, deduplicationOrder,
                            latestEventTimeByStream, latestFinalUpdateBySymbol);
                        if (accepted)
                        {
                            DateTimeOffset availabilityTime = _timeProvider.GetUtcNow();
                            yield return new BinanceStreamEnvelope(sessionId, eventSequence,
                                depth, new UtcTimestamp(receiveTime),
                                new UtcTimestamp(availabilityTime), depth.ExchangeTime, rawJson);
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
                    $"Reconnecting and resubscribing depth (attempt {reconnectAttempts}).", -1);
                await DelayAsync(reconnectDelay, cancellationToken).ConfigureAwait(false);
                reconnectDelay = NextBackoff(reconnectDelay);
            }
        }
        finally
        {
            DisposeActiveTransport();
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
        GC.SuppressFinalize(this);
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

    private bool DiagnoseAndAccept(BinanceDepthUpdateEvent candidate, string sessionId,
        long stableSequence, DateTimeOffset receiveTime,
        Dictionary<string, BinanceWebSocketEvent> deduplication,
        Queue<string> deduplicationOrder, Dictionary<string, long> latestEventTimeByStream,
        Dictionary<string, long> latestFinalUpdateBySymbol)
    {
        if (deduplication.TryGetValue(candidate.EventId, out BinanceWebSocketEvent? previous))
        {
            if (previous.Equals(candidate))
            {
                AddDiagnostic(BinanceStreamDiagnosticKind.Duplicate, sessionId,
                    candidate.StreamName, candidate.EventId, "Duplicate depth update ignored.",
                    stableSequence);
            }
            else
            {
                AddDiagnostic(BinanceStreamDiagnosticKind.IdentityConflict, sessionId,
                    candidate.StreamName, candidate.EventId,
                    "A depth update reused an identity with different values.", stableSequence);
            }

            return false;
        }

        deduplication[candidate.EventId] = candidate;
        deduplicationOrder.Enqueue(candidate.EventId);
        while (deduplication.Count > _options.DeduplicationCapacity)
        {
            deduplication.Remove(deduplicationOrder.Dequeue());
        }

        if (latestEventTimeByStream.TryGetValue(candidate.StreamName, out long latestEventTime)
            && candidate.EventTimeMilliseconds < latestEventTime)
        {
            AddDiagnostic(BinanceStreamDiagnosticKind.Late, sessionId, candidate.StreamName,
                candidate.EventId, "Depth event time moved backwards.", stableSequence);
        }

        latestEventTimeByStream[candidate.StreamName] = Math.Max(
            latestEventTimeByStream.GetValueOrDefault(candidate.StreamName),
            candidate.EventTimeMilliseconds);
        if (_options.StaleAfter != Timeout.InfiniteTimeSpan
            && receiveTime.ToUnixTimeMilliseconds() - candidate.EventTimeMilliseconds
                > _options.StaleAfter.TotalMilliseconds)
        {
            AddDiagnostic(BinanceStreamDiagnosticKind.Stale, sessionId, candidate.StreamName,
                candidate.EventId, "Depth event exceeded the stale-age bound.", stableSequence);
        }

        if (candidate.PreviousUpdateId is long previousUpdateId
            && latestFinalUpdateBySymbol.TryGetValue(candidate.Symbol, out long latestFinal)
            && previousUpdateId != latestFinal)
        {
            AddDiagnostic(BinanceStreamDiagnosticKind.Gap, sessionId, candidate.StreamName,
                candidate.EventId, "The depth stream previous-update ID was not contiguous.",
                stableSequence);
        }

        latestFinalUpdateBySymbol[candidate.Symbol] = candidate.FinalUpdateId;
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

    private IBinanceWebSocketTransport Transport =>
        _activeTransport ?? throw new InvalidOperationException("The depth transport is not active.");

    private void DisposeActiveTransport()
    {
        _activeTransport?.Dispose();
        _activeTransport = null;
    }

    private static BinanceDepthUpdateEvent ParseEvent(string rawJson)
    {
        using JsonDocument document = JsonDocument.Parse(rawJson);
        JsonElement root = document.RootElement;
        string? streamName = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Binance depth message must be an object.");
        }

        if (root.TryGetProperty("data", out JsonElement combinedData))
        {
            streamName = ReadString(root, "stream");
            root = combinedData;
        }

        string eventType = ReadString(root, "e");
        if (!StringComparer.Ordinal.Equals(eventType, "depthUpdate"))
        {
            throw new JsonException($"Unsupported Binance depth event type '{eventType}'.");
        }

        string symbol = ReadString(root, "s");
        long eventTime = ReadRequiredInt64(root, "E");
        long transactionTime = root.TryGetProperty("T", out JsonElement transaction)
            ? ReadValueInt64(transaction, "T")
            : eventTime;
        long? previous = root.TryGetProperty("pu", out JsonElement previousId)
            ? ReadValueInt64(previousId, "pu")
            : null;
        return new BinanceDepthUpdateEvent(symbol, eventTime, ReadRequiredInt64(root, "U"),
            ReadRequiredInt64(root, "u"), previous, ParseLevels(root, "b"), ParseLevels(root, "a"),
            streamName ?? $"{symbol.ToLowerInvariant()}@depth", transactionTime);
    }

    private static BinanceDepthLevel[] ParseLevels(JsonElement root, string propertyName)
    {
        JsonElement values = Required(root, propertyName);
        if (values.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Depth property '{propertyName}' must be an array.");
        }

        List<BinanceDepthLevel> result = new(values.GetArrayLength());
        foreach (JsonElement level in values.EnumerateArray())
        {
            if (level.ValueKind != JsonValueKind.Array || level.GetArrayLength() < 2)
            {
                throw new JsonException("A depth update level must contain price and quantity.");
            }

            JsonElement[] pair = level.EnumerateArray().ToArray();
            result.Add(new BinanceDepthLevel(ReadDecimal(pair[0]), ReadDecimal(pair[1])));
        }

        return result.ToArray();
    }

    private static JsonElement Required(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new JsonException($"Required depth property '{propertyName}' is missing.");
        }

        return value;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        JsonElement value = Required(root, propertyName);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
            || string.IsNullOrWhiteSpace(text))
        {
            throw new JsonException($"Depth property '{propertyName}' must be text.");
        }

        return text;
    }

    private static long ReadRequiredInt64(JsonElement root, string propertyName) =>
        ReadValueInt64(Required(root, propertyName), propertyName);

    private static long ReadValueInt64(JsonElement value, string propertyName)
    {
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt64(),
                JsonValueKind.String => long.Parse(value.GetString()!,
                    CultureInfo.InvariantCulture),
                _ => throw new JsonException($"Depth property '{propertyName}' must be integer.")
            };
        }
        catch (FormatException exception)
        {
            throw new JsonException($"Depth property '{propertyName}' is not integer.", exception);
        }
    }

    private static decimal ReadDecimal(JsonElement value)
    {
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.String => decimal.Parse(value.GetString()!, NumberStyles.Float,
                    CultureInfo.InvariantCulture),
                _ => throw new JsonException("A depth decimal must be a JSON number or string.")
            };
        }
        catch (FormatException exception)
        {
            throw new JsonException("A depth decimal is invalid.", exception);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private DateTimeOffset NextHeartbeat(DateTimeOffset from)
    {
        return _options.HeartbeatInterval == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : from + _options.HeartbeatInterval;
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

    private static void ValidateOptions(BinanceDepthWebSocketOptions options)
    {
        if (!options.BaseAddress.IsAbsoluteUri
            || !StringComparer.OrdinalIgnoreCase.Equals(options.BaseAddress.Scheme, "wss")
            || !options.BaseAddress.AbsolutePath.TrimEnd('/').EndsWith("/public/stream",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The Binance depth stream must use the absolute wss /public/stream route.",
                nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxReconnectAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxFrameBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DeduplicationCapacity);
        ValidateTimeout(options.ReceiveTimeout, nameof(options.ReceiveTimeout));
        ValidateTimeout(options.HeartbeatInterval, nameof(options.HeartbeatInterval));
        ValidateTimeout(options.SessionLifetime, nameof(options.SessionLifetime));
        ValidateTimeout(options.StaleAfter, nameof(options.StaleAfter));
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
}

/// <summary>Alias retaining the public-route terminology used by Binance docs.</summary>
public sealed class BinancePublicDepthWebSocketClient : BinanceDepthWebSocketClient
{
    public BinancePublicDepthWebSocketClient()
    {
    }

    public BinancePublicDepthWebSocketClient(IBinanceWebSocketTransport transport,
        BinanceDepthWebSocketOptions options, TimeProvider timeProvider)
        : base(transport, options, timeProvider)
    {
    }

    public BinancePublicDepthWebSocketClient(Func<IBinanceWebSocketTransport> transportFactory,
        BinanceDepthWebSocketOptions options, TimeProvider timeProvider)
        : base(transportFactory, options, timeProvider)
    {
    }
}
