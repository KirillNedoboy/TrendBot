using System.Threading.Channels;
using TradingBot.Core;
using TradingBot.Exchange.Binance;

namespace TradingBot.App;

/// <summary>
/// Adapts the existing public Binance REST, market, and depth clients into the
/// causal observation seam used by the operational shadow runtime.
/// </summary>
public sealed class BinanceOperationalShadowMarketDataSource :
    IOperationalShadowMarketDataSource, IDisposable
{
    private readonly IBinancePublicRestClient _restClient;
    private readonly BinanceMarketWebSocketClient _marketClient;
    private readonly BinanceDepthWebSocketClient _depthClient;
    private readonly string _symbol;
    private readonly int _depthLimit;
    private readonly IReadOnlyList<BinanceSubscription> _marketSubscriptions;
    private int _dataFresh = 1;
    private int _reconnectReady = 1;
    private int _recoveryComplete;
    private long _nextStableSequence;
    private bool _disposed;

    public BinanceOperationalShadowMarketDataSource(IBinancePublicRestClient restClient,
        BinanceMarketWebSocketClient marketClient, BinanceDepthWebSocketClient depthClient,
        string symbol, IReadOnlyList<BinanceSubscription> marketSubscriptions,
        int depthLimit = 100, long initialStableSequence = -1)
    {
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _marketClient = marketClient ?? throw new ArgumentNullException(nameof(marketClient));
        _depthClient = depthClient ?? throw new ArgumentNullException(nameof(depthClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(marketSubscriptions);
        if (marketSubscriptions.Count == 0)
        {
            throw new ArgumentException("At least one public market subscription is required.",
                nameof(marketSubscriptions));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depthLimit);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialStableSequence, -1);
        _symbol = symbol.ToUpperInvariant();
        _depthLimit = depthLimit;
        _nextStableSequence = initialStableSequence;
        _marketSubscriptions = marketSubscriptions.ToArray();
        if (_marketSubscriptions.Any(static subscription =>
            subscription.Kind == BinanceWebSocketEventKind.Depth))
        {
            throw new ArgumentException(
                "Depth subscriptions must be supplied to the dedicated depth client.",
                nameof(marketSubscriptions));
        }

        _marketClient.Diagnostic += OnDiagnostic;
        _depthClient.Diagnostic += OnDiagnostic;
    }

    public async IAsyncEnumerable<OperationalShadowMarketObservation> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        BinanceDepthSnapshot snapshot = await _restClient.GetDepthSnapshotAsync(_symbol,
            _depthLimit, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _recoveryComplete, 1);
        yield return SnapshotObservation(snapshot);

        Channel<OperationalShadowMarketObservation> channel =
            Channel.CreateBounded<OperationalShadowMarketObservation>(
                new BoundedChannelOptions(256)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task marketPump = PumpMarketAsync(channel.Writer, linked.Token);
        Task depthPump = PumpDepthAsync(channel.Writer, linked.Token);
        Task completion = CompleteChannelAsync(channel.Writer, marketPump, depthPump);
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out OperationalShadowMarketObservation? observation))
                {
                    yield return observation;
                }
            }

            await completion.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            await completion.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _marketClient.Diagnostic -= OnDiagnostic;
        _depthClient.Diagnostic -= OnDiagnostic;
        _marketClient.Dispose();
        _depthClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task PumpMarketAsync(ChannelWriter<OperationalShadowMarketObservation> writer,
        CancellationToken cancellationToken)
    {
        await foreach (BinanceStreamEnvelope envelope in _marketClient.ReadAsync(
            _marketSubscriptions, cancellationToken).ConfigureAwait(false))
        {
            if (envelope.ToCoreCausalEvent() is not { } causal)
            {
                continue;
            }

            await writer.WriteAsync(CreateObservation(envelope, causal), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PumpDepthAsync(ChannelWriter<OperationalShadowMarketObservation> writer,
        CancellationToken cancellationToken)
    {
        await foreach (BinanceStreamEnvelope envelope in _depthClient.ReadAsync(
            [BinanceDepthSubscription.Diff(_symbol)], cancellationToken)
            .ConfigureAwait(false))
        {
            if (envelope.ToCoreCausalEvent() is not { } causal)
            {
                continue;
            }

            await writer.WriteAsync(CreateObservation(envelope, causal), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task CompleteChannelAsync(
        ChannelWriter<OperationalShadowMarketObservation> writer, Task marketPump,
        Task depthPump)
    {
        try
        {
            await Task.WhenAll(marketPump, depthPump).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private OperationalShadowMarketObservation CreateObservation(BinanceStreamEnvelope envelope,
        CausalMarketEvent causal) => new("shadow", ReSequence(causal),
        Volatile.Read(ref _dataFresh) != 0,
        Volatile.Read(ref _reconnectReady) != 0, Volatile.Read(ref _recoveryComplete) != 0,
        killSwitchEnabled: false, killSwitchKnown: false);

    private OperationalShadowMarketObservation SnapshotObservation(BinanceDepthSnapshot snapshot)
    {
        string symbol = snapshot.Symbol ?? _symbol;
        UtcTimestamp receive = snapshot.ReceiveTime ?? snapshot.AvailabilityTime
            ?? new UtcTimestamp(DateTimeOffset.UtcNow);
        UtcTimestamp availability = snapshot.AvailabilityTime ?? receive;
        BookDelta payload = new($"depth-snapshot:{snapshot.LastUpdateId}",
            new InstrumentId(symbol), snapshot.EventTime,
            snapshot.Bids.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))),
            snapshot.Asks.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))));
        CausalMarketEvent causal = new(payload, receive, availability, NextStableSequence(),
            snapshot.EventTime);
        return new OperationalShadowMarketObservation("shadow", causal,
            dataFresh: true, reconnectReady: true, recoveryComplete: true,
            killSwitchEnabled: false, killSwitchKnown: false);
    }

    private CausalMarketEvent ReSequence(CausalMarketEvent causal) => new(causal.Payload,
        causal.ReceiveTime, causal.AvailabilityTime, NextStableSequence(), causal.ExchangeTime);

    private long NextStableSequence() => Interlocked.Increment(ref _nextStableSequence);

    private void OnDiagnostic(BinanceStreamDiagnostic diagnostic)
    {
        switch (diagnostic.Kind)
        {
            case BinanceStreamDiagnosticKind.Stale:
            case BinanceStreamDiagnosticKind.Gap:
            case BinanceStreamDiagnosticKind.IdentityConflict:
            case BinanceStreamDiagnosticKind.ParseError:
            case BinanceStreamDiagnosticKind.Late:
            case BinanceStreamDiagnosticKind.FrameTooLarge:
            case BinanceStreamDiagnosticKind.BufferOverflow:
            case BinanceStreamDiagnosticKind.SnapshotRejected:
                Interlocked.Exchange(ref _dataFresh, 0);
                break;
            case BinanceStreamDiagnosticKind.Disconnect:
                Interlocked.Exchange(ref _reconnectReady, 0);
                break;
            case BinanceStreamDiagnosticKind.Reconnect:
                Interlocked.Exchange(ref _reconnectReady, 0);
                break;
            case BinanceStreamDiagnosticKind.Recovery:
                Interlocked.Exchange(ref _dataFresh, 1);
                Interlocked.Exchange(ref _recoveryComplete, 1);
                Interlocked.Exchange(ref _reconnectReady, 1);
                break;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

