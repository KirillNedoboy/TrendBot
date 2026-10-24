using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

public sealed record BinancePrivateWebSocketOptions
{
    public Uri BaseAddress { get; init; } = new("wss://fstream.binance.com/ws/");

    public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(24);

    public int MaxFrameBytes { get; init; } = 1 * 1024 * 1024;

    public int MaxMessageBytes { get; init; } = 4 * 1024 * 1024;
}

public enum BinancePrivateStreamDiagnosticKind
{
    ParseError,
    FrameTooLarge,
    MessageTooLarge,
    Disconnect,
    SessionExpired
}

public sealed record BinancePrivateStreamDiagnostic(
    BinancePrivateStreamDiagnosticKind Kind,
    string SessionId,
    long StableSequence,
    string Message);

public sealed record BinancePrivateStreamEnvelope(
    string SessionId,
    long StableSequence,
    BinancePrivateStreamEvent Payload,
    UtcTimestamp ReceiveTime,
    UtcTimestamp AvailabilityTime,
    string RawJson);

/// <summary>Reads an injected Binance private user-data transport without reconnect policy.</summary>
public sealed class BinancePrivateWebSocketClient : IDisposable
{
    private readonly IBinanceWebSocketTransportFactory _transportFactory;
    private readonly BinancePrivateWebSocketOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly List<BinancePrivateStreamDiagnostic> _diagnostics = [];
    private IBinanceWebSocketTransport? _activeTransport;
    private bool _disposed;
    private int _sessionNumber;

    public BinancePrivateWebSocketClient()
        : this(new ClientWebSocketTransportFactory(), new BinancePrivateWebSocketOptions(),
            TimeProvider.System)
    {
    }

    public BinancePrivateWebSocketClient(IBinanceWebSocketTransport transport,
        BinancePrivateWebSocketOptions options, TimeProvider timeProvider)
        : this(new DelegateBinanceWebSocketTransportFactory(() => transport), options,
            timeProvider)
    {
    }

    public BinancePrivateWebSocketClient(IBinanceWebSocketTransportFactory transportFactory,
        BinancePrivateWebSocketOptions options, TimeProvider timeProvider)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ValidateOptions(_options);
    }

    public IReadOnlyList<BinancePrivateStreamDiagnostic> Diagnostics => _diagnostics.ToArray();

    public event Action<BinancePrivateStreamDiagnostic>? Diagnostic;

    public async IAsyncEnumerable<BinancePrivateStreamEnvelope> ReadAsync(string listenKey,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(listenKey);
        if (listenKey.Contains('&') || listenKey.Contains('='))
        {
            throw new ArgumentException("A listenKey must be a single opaque token.",
                nameof(listenKey));
        }

        string sessionId = $"private-session-{Interlocked.Increment(ref _sessionNumber)}";
        long stableSequence = 0;
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        byte[] messageBuffer = [];
        try
        {
            _activeTransport = _transportFactory.Create();
            Uri endpoint = new(_options.BaseAddress, listenKey);
            await _activeTransport.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_options.SessionLifetime != Timeout.InfiniteTimeSpan
                    && _timeProvider.GetUtcNow() - startedAt >= _options.SessionLifetime)
                {
                    AddDiagnostic(BinancePrivateStreamDiagnosticKind.SessionExpired, sessionId,
                        stableSequence, "The private user-data session lifetime elapsed.");
                    break;
                }

                BinanceWebSocketReceiveResult result;
                try
                {
                    using CancellationTokenSource timeout = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_options.ReceiveTimeout);
                    result = await _activeTransport.ReceiveAsync(timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    AddDiagnostic(BinancePrivateStreamDiagnosticKind.Disconnect, sessionId,
                        stableSequence, "Private user-data receive timed out.");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    AddDiagnostic(BinancePrivateStreamDiagnosticKind.Disconnect, sessionId,
                        stableSequence, "Private user-data transport closed.");
                    break;
                }

                if (result.Payload.Length > _options.MaxFrameBytes)
                {
                    AddDiagnostic(BinancePrivateStreamDiagnosticKind.FrameTooLarge, sessionId,
                        stableSequence, "A private WebSocket frame exceeded MaxFrameBytes.");
                    messageBuffer = [];
                    continue;
                }

                if (messageBuffer.Length + result.Payload.Length > _options.MaxMessageBytes)
                {
                    AddDiagnostic(BinancePrivateStreamDiagnosticKind.MessageTooLarge, sessionId,
                        stableSequence, "A private WebSocket message exceeded MaxMessageBytes.");
                    messageBuffer = [];
                    continue;
                }

                byte[] combined = new byte[messageBuffer.Length + result.Payload.Length];
                Buffer.BlockCopy(messageBuffer, 0, combined, 0, messageBuffer.Length);
                Buffer.BlockCopy(result.Payload, 0, combined, messageBuffer.Length,
                    result.Payload.Length);
                messageBuffer = result.EndOfMessage ? [] : combined;
                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    AddDiagnostic(BinancePrivateStreamDiagnosticKind.ParseError, sessionId,
                        stableSequence, "A private user-data message was not text.");
                    continue;
                }

                string rawJson = Encoding.UTF8.GetString(combined);
                DateTimeOffset receiveTime = _timeProvider.GetUtcNow();
                long sequence = stableSequence++;
                BinancePrivateStreamEvent parsed;
                try
                {
                    parsed = BinancePrivateStreamParser.Parse(rawJson,
                        new BinancePrivateStreamMetadata(sessionId, sequence,
                            new UtcTimestamp(receiveTime),
                            new UtcTimestamp(_timeProvider.GetUtcNow())));
                }
                catch (Exception exception) when (exception is JsonException
                    or FormatException or ArgumentException or BinancePrivateProtocolException)
                {
                    AddDiagnostic(BinancePrivateStreamDiagnosticKind.ParseError, sessionId,
                        sequence, exception.Message);
                    continue;
                }

                DateTimeOffset availabilityTime = _timeProvider.GetUtcNow();
                yield return new BinancePrivateStreamEnvelope(sessionId, sequence, parsed,
                    new UtcTimestamp(receiveTime), new UtcTimestamp(availabilityTime),
                    rawJson);
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
    }

    private void AddDiagnostic(BinancePrivateStreamDiagnosticKind kind, string sessionId,
        long stableSequence, string message)
    {
        BinancePrivateStreamDiagnostic diagnostic = new(kind, sessionId, stableSequence, message);
        _diagnostics.Add(diagnostic);
        Diagnostic?.Invoke(diagnostic);
    }

    private void DisposeActiveTransport()
    {
        _activeTransport?.Dispose();
        _activeTransport = null;
    }

    private static void ValidateOptions(BinancePrivateWebSocketOptions options)
    {
        if (!options.BaseAddress.IsAbsoluteUri || options.BaseAddress.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("The private WebSocket base address must be absolute ws/wss.",
                nameof(options));
        }

        if (options.ReceiveTimeout <= TimeSpan.Zero
            && options.ReceiveTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.SessionLifetime <= TimeSpan.Zero
            && options.SessionLifetime != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.MaxFrameBytes <= 0 || options.MaxMessageBytes < options.MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
