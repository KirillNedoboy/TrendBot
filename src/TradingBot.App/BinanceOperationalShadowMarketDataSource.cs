using System.Threading.Channels;
using TradingBot.Core;
using TradingBot.Exchange.Binance;

namespace TradingBot.App;

/// <summary>
/// Adapts the existing public Binance REST, market, and depth clients into the
/// causal observation seam used by the operational shadow runtime.
/// </summary>
public sealed class BinanceOperationalShadowMarketDataSource :
    IOperationalShadowMarketDataSource, IOperationalShadowHealthSource, IDisposable
{
    private readonly IBinanceDepthSnapshotSource _snapshotSource;
    private readonly BinanceMarketWebSocketClient _marketClient;
    private readonly BinanceDepthWebSocketClient _depthClient;
    private readonly string _symbol;
    private readonly int _depthLimit;
    private readonly IReadOnlyList<BinanceSubscription> _marketSubscriptions;
    private readonly string[] _requiredMarketStreams;
    private readonly string[] _requiredFeedStreams;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _staleAfter;
    private BinanceLocalOrderBookRecovery _orderBookRecovery;
    private readonly object _healthLock = new();
    private readonly Dictionary<string, (DateTimeOffset Receive, DateTimeOffset Availability)>
        _marketTimes = new(StringComparer.Ordinal);
    private DateTimeOffset? _depthReceiveTime;
    private DateTimeOffset? _depthAvailabilityTime;
    private bool _marketGap;
    private bool _marketReconnecting = true;
    private bool _depthGap;
    private bool _depthReconnecting = true;
    private long _nextStableSequence;
    private bool _disposed;

    public BinanceOperationalShadowMarketDataSource(IBinancePublicRestClient restClient,
        BinanceMarketWebSocketClient marketClient, BinanceDepthWebSocketClient depthClient,
        string symbol, IReadOnlyList<BinanceSubscription> marketSubscriptions,
        int depthLimit = 100, long initialStableSequence = -1,
        TimeProvider? timeProvider = null, TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(restClient);
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
        _timeProvider = timeProvider ?? TimeProvider.System;
        _staleAfter = staleAfter ?? TimeSpan.FromMinutes(2);
        if (_staleAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }
        _nextStableSequence = initialStableSequence;
        _marketSubscriptions = marketSubscriptions.ToArray();
        _requiredMarketStreams = _marketSubscriptions.Select(static item => item.StreamName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _requiredFeedStreams = [.. _requiredMarketStreams, "depth"];
        if (_marketSubscriptions.Any(static subscription =>
            subscription.Kind == BinanceWebSocketEventKind.Depth))
        {
            throw new ArgumentException(
                "Depth subscriptions must be supplied to the dedicated depth client.",
                nameof(marketSubscriptions));
        }

        _marketClient.Diagnostic += OnMarketDiagnostic;
        _depthClient.Diagnostic += OnDepthDiagnostic;
        IBinanceDepthSnapshotSource snapshotSource = restClient as IBinanceDepthSnapshotSource
            ?? throw new ArgumentException("The Binance REST client must support depth snapshots.",
                nameof(restClient));
        _snapshotSource = snapshotSource;
        _orderBookRecovery = CreateOrderBookRecovery(snapshotSource);
    }

    public event Action? HealthChanged;

    public IReadOnlyCollection<string> RequiredFeedStreams => _requiredFeedStreams;

    public IReadOnlyList<OperationalShadowFeedHealth> GetHealth(DateTimeOffset checkedAt,
        TimeSpan staleAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);
        lock (_healthLock)
        {
            OperationalShadowFeedHealth[] market = _requiredMarketStreams.Select(stream =>
            {
                _marketTimes.TryGetValue(stream, out var times);
                bool seen = _marketTimes.ContainsKey(stream);
                bool fresh = seen && checkedAt - times.Availability <= staleAfter;
                return new OperationalShadowFeedHealth(stream,
                    seen && !_marketReconnecting, fresh, _marketGap,
                    _marketReconnecting || !seen, seen, times.Receive, times.Availability);
            }).ToArray();
            bool depthSeen = _depthAvailabilityTime is not null;
            bool depthFresh = depthSeen
                && checkedAt - _depthAvailabilityTime!.Value <= staleAfter;
            OperationalShadowFeedHealth depth = new("depth", depthSeen && !_depthReconnecting,
                depthFresh, _depthGap, _depthReconnecting || !depthSeen,
                _orderBookRecovery.State.Kind == BinanceOrderBookStateKind.Ready,
                _depthReceiveTime, _depthAvailabilityTime);
            return Array.AsReadOnly([.. market, depth]);
        }
    }

    public async IAsyncEnumerable<OperationalShadowMarketObservation> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
        Task completion = CompleteChannelAsync(channel.Writer, marketPump, depthPump, linked);
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
        _marketClient.Diagnostic -= OnMarketDiagnostic;
        _depthClient.Diagnostic -= OnDepthDiagnostic;
        _orderBookRecovery.Diagnostic -= OnRecoveryDiagnostic;
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
            lock (_healthLock)
            {
                _marketTimes[envelope.Payload.StreamName] =
                    (envelope.ReceiveTime.Value, envelope.AvailabilityTime.Value);
                if (_requiredMarketStreams.All(_marketTimes.ContainsKey))
                {
                    _marketReconnecting = false;
                    _marketGap = false;
                }
            }
            HealthChanged?.Invoke();
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
            BinanceLocalOrderBookRecovery recovery = _orderBookRecovery;
            BinanceOrderBookState state = await recovery.AcceptAsync(envelope,
                cancellationToken).ConfigureAwait(false);
            if (state.Kind != BinanceOrderBookStateKind.Ready
                || envelope.ToCoreCausalEvent() is not { } causal)
            {
                continue;
            }

            lock (_healthLock)
            {
                _depthReceiveTime = envelope.ReceiveTime.Value;
                _depthAvailabilityTime = envelope.AvailabilityTime.Value;
                _depthReconnecting = false;
                _depthGap = false;
            }
            HealthChanged?.Invoke();
            await writer.WriteAsync(CreateObservation(envelope, causal), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task CompleteChannelAsync(
        ChannelWriter<OperationalShadowMarketObservation> writer, Task marketPump,
        Task depthPump, CancellationTokenSource linked)
    {
        try
        {
            Task first = await Task.WhenAny(marketPump, depthPump).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!linked.IsCancellationRequested)
            {
                linked.Cancel();
                await Task.WhenAll(marketPump, depthPump).ConfigureAwait(false);
                throw new IOException("A required Binance public stream ended unexpectedly.");
            }

            await Task.WhenAll(marketPump, depthPump).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private OperationalShadowMarketObservation CreateObservation(BinanceStreamEnvelope envelope,
        CausalMarketEvent causal)
    {
        IReadOnlyList<OperationalShadowFeedHealth> feeds = GetHealth(
            _timeProvider.GetUtcNow(), _staleAfter);
        bool marketReady;
        lock (_healthLock)
        {
            marketReady = feeds.Where(static feed => feed.Stream != "depth")
                .All(static feed => feed.Connected && feed.Fresh && !feed.GapDetected
                    && !feed.Reconnecting);
        }

        bool depthReady = feeds.Any(static feed => feed.Stream == "depth"
            && feed.Connected && feed.Fresh && feed.RecoveryComplete
            && !feed.GapDetected && !feed.Reconnecting);
        return new OperationalShadowMarketObservation("shadow", ReSequence(causal),
            marketReady && depthReady, marketReady && depthReady, depthReady,
            killSwitchEnabled: false, killSwitchKnown: false);
    }

    private CausalMarketEvent ReSequence(CausalMarketEvent causal) => new(causal.Payload,
        causal.ReceiveTime, causal.AvailabilityTime, NextStableSequence(), causal.ExchangeTime);

    private long NextStableSequence() => Interlocked.Increment(ref _nextStableSequence);

    private void OnMarketDiagnostic(BinanceStreamDiagnostic diagnostic) =>
        OnDiagnostic(diagnostic, isDepth: false);

    private void OnDepthDiagnostic(BinanceStreamDiagnostic diagnostic) =>
        OnDiagnostic(diagnostic, isDepth: true);

    private void OnDiagnostic(BinanceStreamDiagnostic diagnostic, bool isDepth)
    {
        lock (_healthLock)
        {
            if (isDepth)
            {
                switch (diagnostic.Kind)
                {
                    case BinanceStreamDiagnosticKind.Disconnect:
                    case BinanceStreamDiagnosticKind.Reconnect:
                        _depthReconnecting = true;
                        _depthReceiveTime = null;
                        _depthAvailabilityTime = null;
                        ResetOrderBookRecovery();
                        break;
                    case BinanceStreamDiagnosticKind.Gap:
                    case BinanceStreamDiagnosticKind.Stale:
                    case BinanceStreamDiagnosticKind.IdentityConflict:
                    case BinanceStreamDiagnosticKind.ParseError:
                    case BinanceStreamDiagnosticKind.Late:
                    case BinanceStreamDiagnosticKind.FrameTooLarge:
                    case BinanceStreamDiagnosticKind.BufferOverflow:
                    case BinanceStreamDiagnosticKind.SnapshotRejected:
                        _depthGap = true;
                        _depthReceiveTime = null;
                        _depthAvailabilityTime = null;
                        break;
                }
            }
            else
            {
                switch (diagnostic.Kind)
                {
                    case BinanceStreamDiagnosticKind.Disconnect:
                    case BinanceStreamDiagnosticKind.Reconnect:
                        _marketReconnecting = true;
                        _marketTimes.Clear();
                        break;
                    case BinanceStreamDiagnosticKind.Gap:
                    case BinanceStreamDiagnosticKind.Stale:
                    case BinanceStreamDiagnosticKind.IdentityConflict:
                    case BinanceStreamDiagnosticKind.ParseError:
                    case BinanceStreamDiagnosticKind.Late:
                    case BinanceStreamDiagnosticKind.FrameTooLarge:
                    case BinanceStreamDiagnosticKind.BufferOverflow:
                        _marketGap = true;
                        _marketTimes.Clear();
                        break;
                }
            }
        }

        HealthChanged?.Invoke();
    }

    private void OnRecoveryDiagnostic(BinanceOrderBookRecoveryDiagnostic diagnostic)
    {
        lock (_healthLock)
        {
            _depthGap = true;
            _depthReceiveTime = null;
            _depthAvailabilityTime = null;
        }

        HealthChanged?.Invoke();
    }

    private BinanceLocalOrderBookRecovery CreateOrderBookRecovery(
        IBinanceDepthSnapshotSource snapshotSource)
    {
        BinanceLocalOrderBookRecovery recovery = new(snapshotSource,
            new BinanceOrderBookRecoveryOptions { SnapshotLimit = _depthLimit }, _symbol,
            _timeProvider);
        recovery.Diagnostic += OnRecoveryDiagnostic;
        return recovery;
    }

    private void ResetOrderBookRecovery()
    {
        _orderBookRecovery.Diagnostic -= OnRecoveryDiagnostic;
        _orderBookRecovery = CreateOrderBookRecovery(_snapshotSource);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

