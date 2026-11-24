using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Reconnects privately to Demo only; any gap invalidates entry readiness until REST recovery.</summary>
internal sealed class BybitDemoSession(string key, string secret, TimeProvider clock, int freshnessSeconds)
{
    private readonly ConcurrentQueue<byte[]> _events = new();
    private readonly object _sync = new();
    private bool _ready;
    private DateTimeOffset _received;
    private long _revision;
    private int _queuedBytes;

    public BybitDemoPrivateHealth Health()
    {
        lock (_sync) return new(_ready, _received, _revision);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (_sync) { _ready = false; _revision++; }
            try
            {
                using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                using BybitDemoPrivateStream stream = await BybitDemoPrivateStream.ConnectAsync(key, secret, clock, connect.Token).ConfigureAwait(false);
                lock (_sync) { _received = clock.GetUtcNow(); _ready = true; _revision++; }
                using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task reader = ReadAsync(stream, session.Token);
                Task ping = PingAsync(stream, session.Token);
                await Task.WhenAny(reader, ping).ConfigureAwait(false);
                session.Cancel();
                try { await Task.WhenAll(reader, ping).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException
                or System.Net.WebSockets.WebSocketException or InvalidOperationException)
            {
                if (cancellationToken.IsCancellationRequested) break;
            }
            finally { lock (_sync) { _ready = false; _revision++; } }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool ValidateEvents(SqliteBybitDemoReservationStore store)
    {
        IReadOnlyList<BybitDemoReservationSnapshot> owned = store.ReadAll();
        bool valid = true;
        int pending = _events.Count;
        for (int index = 0; index < pending && _events.TryDequeue(out byte[]? bytes); index++)
        {
            Interlocked.Add(ref _queuedBytes, -bytes.Length);
            bool known = false;
            bool awaitingHistory = false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(bytes);
                JsonElement root = document.RootElement;
                string topic = root.GetProperty("topic").GetString()!;
                known = root.GetProperty("data").EnumerateArray().All(item => IsKnown(topic, item));
                awaitingHistory = !known && topic == "execution.linear"
                    && root.GetProperty("data").EnumerateArray().All(item =>
                        item.GetProperty("category").GetString() == "linear"
                        && owned.Any(position => position.Reservation.Symbol == item.GetProperty("symbol").GetString()));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException) { }
            if (!known)
            {
                if (awaitingHistory)
                {
                    _events.Enqueue(bytes);
                    Interlocked.Add(ref _queuedBytes, bytes.Length);
                }
                else store.RecordStreamFault(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), clock.GetUtcNow());
                valid = false;
            }
        }
        return valid;

        bool IsKnown(string topic, JsonElement item)
        {
            if (item.GetProperty("category").GetString() != "linear") return false;
            string? symbol = item.GetProperty("symbol").GetString();
            if (topic == "position.linear") return owned.Any(position => position.Reservation.Symbol == symbol);
            string? client = item.GetProperty("orderLinkId").GetString();
            string? order = item.GetProperty("orderId").GetString();
            foreach (BybitDemoReservationSnapshot position in owned.Where(position => position.Reservation.Symbol == symbol))
            {
                if (client == position.Reservation.ClientOrderId || order == position.ExchangeOrderId) return true;
                if (store.ReadExitOrders(position.Reservation.SignalId).Any(exit => exit.ExchangeOrderId == order
                    || exit.Intent.ClientOrderId == client)) return true;
                if (topic == "order.linear" && item.TryGetProperty("parentOrderLinkId", out JsonElement parent)
                    && parent.GetString() == position.Reservation.ClientOrderId
                    && item.GetProperty("stopOrderType").GetString() == "StopLoss"
                    && item.GetProperty("reduceOnly").GetBoolean()) return true;
            }
            return false;
        }
    }

    private async Task ReadAsync(BybitDemoPrivateStream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[] frame = await stream.ReadAsync(cancellationToken).ConfigureAwait(false);
            ObserveFrame(frame);
        }
    }

    internal void ObserveFrame(byte[] frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        JsonElement root = document.RootElement;
        SignalOnlyConfiguration.RejectDuplicateProperties(root);
        lock (_sync) _received = clock.GetUtcNow();
        if (root.TryGetProperty("topic", out JsonElement topic)
            && topic.GetString() is "order.linear" or "execution.linear" or "position.linear")
        {
            if (Interlocked.Add(ref _queuedBytes, frame.Length) > 16 * 1024 * 1024)
                throw new InvalidDataException("The demo event queue exceeded its bound.");
            _events.Enqueue(frame);
            lock (_sync) _revision++;
        }
        else if (!(root.TryGetProperty("op", out JsonElement op) && op.GetString() is "pong" or "ping"))
            throw new InvalidDataException("The demo private protocol is unknown.");
    }

    private async Task PingAsync(BybitDemoPrivateStream stream, CancellationToken cancellationToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Min(20d, freshnessSeconds / 2d));
        while (true)
        {
            await Task.Delay(interval, clock, cancellationToken).ConfigureAwait(false);
            BybitDemoPrivateHealth health = Health();
            if (clock.GetUtcNow() - health.ConfirmedAt > TimeSpan.FromSeconds(freshnessSeconds))
                throw new InvalidDataException("The demo private stream is stale.");
            await stream.PingAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
