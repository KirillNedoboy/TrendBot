using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingBot.App;

/// <summary>Demo-only account event transport; connection alone never authorizes an order.</summary>
internal sealed class BybitDemoPrivateStream : IDisposable
{
    private static readonly Uri Endpoint = new("wss://stream-demo.bybit.com/v5/private");
    private static readonly byte[] Subscription = "{\"op\":\"subscribe\",\"args\":[\"order.linear\",\"execution.linear\",\"position.linear\"]}"u8.ToArray();
    private static readonly byte[] Ping = "{\"op\":\"ping\"}"u8.ToArray();
    private const int MaximumMessageBytes = 1024 * 1024;
    private const int MaximumEarlyMessages = 16;
    private readonly WebSocket _socket;
    private readonly Queue<byte[]> _earlyMessages = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;
    private int _reading;
    private int _initializationAttempted;

    internal BybitDemoPrivateStream(WebSocket socket) => _socket = socket ?? throw new ArgumentNullException(nameof(socket));

    public static async Task<BybitDemoPrivateStream> ConnectAsync(string apiKey, string secret,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(clock);
        ClientWebSocket socket = new();
        BybitDemoPrivateStream stream = new(socket);
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(Endpoint, timeout.Token).ConfigureAwait(false);
            await stream.InitializeAsync(apiKey, secret, clock, timeout.Token).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal async Task InitializeAsync(string apiKey, string secret, TimeProvider clock, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentNullException.ThrowIfNull(clock);
        if (_socket.State != WebSocketState.Open || Interlocked.Exchange(ref _initializationAttempted, 1) != 0)
        {
            throw new InvalidOperationException("The demo private stream cannot be initialized.");
        }
        long expires = checked(clock.GetUtcNow().ToUnixTimeMilliseconds() + 10_000);
        byte[] key = Encoding.UTF8.GetBytes(secret);
        byte[] message = Encoding.UTF8.GetBytes("GET/realtime" + expires.ToString(CultureInfo.InvariantCulture));
        byte[]? signature = null;
        byte[]? auth = null;
        try
        {
            signature = HMACSHA256.HashData(key, message);
            auth = JsonSerializer.SerializeToUtf8Bytes(new
            {
                op = "auth",
                args = new object[] { apiKey, expires, Convert.ToHexString(signature).ToLowerInvariant() }
            });
            await SendAsync(auth, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(message);
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
            if (auth is not null) CryptographicOperations.ZeroMemory(auth);
        }
        byte[] authResponse = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
        if (Classify(authResponse) != "auth")
        {
            throw new InvalidDataException("The demo private stream authentication failed.");
        }
        await SendAsync(Subscription, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            byte[] frame = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            string kind = Classify(frame);
            if (kind == "subscribe")
            {
                _initialized = true;
                return;
            }
            if (kind != "data" || _earlyMessages.Count >= MaximumEarlyMessages)
            {
                throw new InvalidDataException("The demo private stream subscription failed.");
            }
            _earlyMessages.Enqueue(frame);
        }
    }

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || Interlocked.Exchange(ref _reading, 1) != 0)
        {
            throw new InvalidOperationException("The demo private stream is not ready for a single reader.");
        }
        try
        {
            return _earlyMessages.Count > 0 ? _earlyMessages.Dequeue()
                : await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _initialized = false;
            _socket.Abort();
            throw;
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("The demo private stream is not initialized.");
        }
        try
        {
            await SendAsync(Ping, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _initialized = false;
            _socket.Abort();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Abort();
        _socket.Dispose();
    }

    private async Task SendAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<byte[]> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream message = new();
        while (true)
        {
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaximumMessageBytes)
            {
                throw new InvalidDataException("The demo private stream message is invalid or disconnected.");
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static string Classify(byte[] message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            SignalOnlyConfiguration.RejectDuplicateProperties(root);
            if (root.TryGetProperty("topic", out JsonElement topic)
                && topic.GetString() is "order.linear" or "execution.linear" or "position.linear")
            {
                return "data";
            }
            if (root.TryGetProperty("op", out JsonElement op)
                && root.TryGetProperty("success", out JsonElement success)
                && success.ValueKind == JsonValueKind.True
                && op.GetString() is "auth" or "subscribe")
            {
                return op.GetString()!;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("The demo private stream response is invalid.");
        }
        throw new InvalidDataException("The demo private stream response is invalid.");
    }
}
