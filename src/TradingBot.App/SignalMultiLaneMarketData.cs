using TradingBot.Core;
using TradingBot.Exchange.Binance;

namespace TradingBot.App;

internal interface ISignalMultiLaneSource
{
    SignalMultiLanePolicy Policy { get; }

    SignalMultiLaneInput Capture(Direction direction, UtcTimestamp earliestDecisionAt);
}

internal interface ISignalMarketContextSource
{
    SignalObservedPoc CapturePoc(UtcTimestamp asOf, decimal binSize);
    SignalObservedLiquidation? CaptureLiquidation(UtcTimestamp asOf);
}

/// <summary>Per-symbol, bounded public derivatives and microstructure observations.</summary>
internal sealed class SignalMultiLaneMarketData : ISignalMultiLaneSource, ISignalMarketContextSource, IDisposable
{
    private readonly object _sync = new();
    private readonly string _symbol;
    private readonly InstrumentId _instrument;
    private readonly IBinanceDepthSnapshotSource _snapshotSource;
    private readonly SignalMultiLanePolicy _policy;
    private readonly IBinancePublicRestClient _rest;
    private readonly BinanceMarketWebSocketClient _market = new();
    private readonly BinanceMarketWebSocketClient _liquidationMarket;
    private readonly BinanceDepthWebSocketClient _depth = new();
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _onFirstObservation;
    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private readonly Queue<TradeSample> _trades = new();
    private readonly Queue<SignalMarkPriceObservation> _marks = new();
    private readonly Queue<DepthSample> _books = new();
    private readonly Queue<SignalObservedLiquidation> _liquidations = new();
    private BinanceLocalOrderBookRecovery _recovery;
    private SignalOpenInterestMarkSample? _previousInterest;
    private SignalOpenInterestMarkSample? _currentInterest;
    private DepthSample? _currentBook;
    private DateTimeOffset _depthRetryAt = DateTimeOffset.MinValue;
    private UtcTimestamp? _tradeCoverageStart;
    private long _interestSequence;
    private long _processingSequence;

    public SignalMultiLaneMarketData(string symbol, SignalMultiLanePolicy policy,
        IBinancePublicRestClient rest, TimeProvider? timeProvider = null,
        Action<string>? onFirstObservation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rest);
        _snapshotSource = rest as IBinanceDepthSnapshotSource
            ?? throw new ArgumentException("The REST client must provide depth snapshots.",
                nameof(rest));
        if (policy.Microstructure.MaximumInputRecords < 3)
        {
            throw new ArgumentException("Multi-lane capture requires room for two books and a trade.",
                nameof(policy));
        }
        _symbol = symbol;
        _instrument = new InstrumentId(symbol);
        _policy = policy;
        _rest = rest;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _liquidationMarket = new BinanceMarketWebSocketClient(
            new ClientWebSocketTransportFactory(),
            new BinanceWebSocketOptions { ReceiveTimeout = Timeout.InfiniteTimeSpan },
            _timeProvider);
        _onFirstObservation = onFirstObservation;
        _recovery = CreateRecovery();
        _market.Diagnostic += OnMarketDiagnostic;
        _liquidationMarket.Diagnostic += OnLiquidationDiagnostic;
        _depth.Diagnostic += OnDepthDiagnostic;
    }

    public SignalMultiLanePolicy Policy => _policy;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        Task[] tasks = [PumpMarketAsync(linked.Token), PumpDepthAsync(linked.Token),
            PumpOpenInterestAsync(linked.Token)];
        Task liquidations = PumpLiquidationsAsync(linked.Token);
        try
        {
            Task first = await Task.WhenAny(tasks.Append(liquidations)).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("A required public multi-lane feed ended unexpectedly.");
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await Task.WhenAll(tasks.Append(liquidations)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
        }
    }

    public SignalMultiLaneInput Capture(Direction direction, UtcTimestamp earliestDecisionAt)
    {
        ArgumentNullException.ThrowIfNull(earliestDecisionAt);
        SignalOpenInterestMarkSample? previousInterest;
        SignalOpenInterestMarkSample? currentInterest;
        SignalMarkPriceObservation? mark;
        DepthSample? previousBook;
        DepthSample? currentBook;
        TradeSample[] trades;
        UtcTimestamp? coverage;
        UtcTimestamp decisionAt;
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            decisionAt = new UtcTimestamp(now > earliestDecisionAt.Value
                ? now : earliestDecisionAt.Value);
            PruneTrades(now);
            previousInterest = _previousInterest;
            currentInterest = _currentInterest;
            mark = LatestMarkAt(decisionAt.Value);
            previousBook = null;
            currentBook = null;
            foreach (DepthSample book in _books)
            {
                if (BookIsVisible(book, decisionAt.Value))
                {
                    previousBook = currentBook;
                    currentBook = book;
                }
            }
            coverage = _tradeCoverageStart;
            trades = _trades.ToArray();
        }

        LaneCFeatureInput? microstructure = BuildMicrostructure(previousBook, currentBook,
            trades, decisionAt);
        return new SignalMultiLaneInput(_instrument, direction, decisionAt, _policy,
            previousInterest, currentInterest, mark, coverage, microstructure);
    }

    public SignalObservedPoc CapturePoc(UtcTimestamp asOf, decimal binSize)
    {
        ArgumentNullException.ThrowIfNull(asOf);
        lock (_sync)
        {
            return SignalMarketContextCalculator.CalculatePoc(asOf, _policy.Microstructure.Window,
                binSize, _tradeCoverageStart, _trades.Select(static trade =>
                    new SignalContextTrade(trade.Payload.Price.Value, trade.Payload.Quantity.Value,
                        trade.Payload.EventTime.Value, trade.AvailabilityTime.Value,
                        trade.Envelope.Payload.EventTime.Value)));
        }
    }

    public SignalObservedLiquidation? CaptureLiquidation(UtcTimestamp asOf)
    {
        ArgumentNullException.ThrowIfNull(asOf);
        DateTimeOffset start = asOf.Value.Subtract(_policy.Microstructure.Window);
        lock (_sync)
        {
            SignalObservedLiquidation? latest = null;
            foreach (SignalObservedLiquidation item in _liquidations)
            {
                if (item.EventTime.Value >= start && item.EventTime.Value <= asOf.Value
                    && item.ExchangeTime.Value >= start && item.ExchangeTime.Value <= asOf.Value
                    && item.AvailabilityTime.Value <= asOf.Value
                    && (latest is null || item.EventTime.Value >= latest.EventTime.Value))
                {
                    latest = item;
                }
            }
            return latest;
        }
    }

    public void Dispose()
    {
        _market.Diagnostic -= OnMarketDiagnostic;
        _liquidationMarket.Diagnostic -= OnLiquidationDiagnostic;
        _depth.Diagnostic -= OnDepthDiagnostic;
        _market.Dispose();
        _liquidationMarket.Dispose();
        _depth.Dispose();
    }

    private async Task PumpMarketAsync(CancellationToken cancellationToken)
    {
        await foreach (BinanceStreamEnvelope envelope in _market.ReadAsync(
            [BinanceSubscription.MarkPrice(_symbol), BinanceSubscription.AggregateTrade(_symbol)],
            cancellationToken).ConfigureAwait(false))
        {
            AcceptMarket(envelope);
        }
    }

    private async Task PumpLiquidationsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await foreach (BinanceStreamEnvelope envelope in _liquidationMarket.ReadAsync(
                    [BinanceSubscription.Liquidation(_symbol)], cancellationToken).ConfigureAwait(false))
                {
                    AcceptLiquidation(envelope);
                }
            }
            catch (Exception exception) when (exception is IOException
                or HttpRequestException or System.Net.WebSockets.WebSocketException or ArgumentException)
            {
                lock (_sync)
                {
                    Announce($"liquidation-feed-error:{exception.GetType().Name}");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _liquidations.Clear();
                Announce("liquidation-feed-unavailable");
            }
            await Task.Delay(_policy.OpenInterestPollInterval, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal void AcceptLiquidation(BinanceStreamEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload is not BinanceLiquidationEvent liquidation
            || !StringComparer.Ordinal.Equals(liquidation.Symbol, _symbol))
        {
            throw new ArgumentException("The liquidation snapshot belongs to another stream.", nameof(envelope));
        }
        lock (_sync)
        {
            if (liquidation.AccumulatedQuantity <= 0m || liquidation.AveragePrice <= 0m)
            {
                Announce("liquidation-invalid-snapshot");
                return;
            }
            DateTimeOffset now = _timeProvider.GetUtcNow();
            UtcTimestamp available = new(now > envelope.AvailabilityTime.Value
                ? now : envelope.AvailabilityTime.Value);
            _liquidations.Enqueue(new SignalObservedLiquidation(liquidation.EventId,
                liquidation.Side, liquidation.AccumulatedQuantity, liquidation.AveragePrice,
                liquidation.EventTime, new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(
                    liquidation.OrderTimeMilliseconds)), envelope.ReceiveTime, available));
            while (_liquidations.Count > 128)
            {
                _liquidations.Dequeue();
            }
            Announce("liquidation-snapshot");
        }
    }

    internal void OnLiquidationDiagnostic(BinanceStreamDiagnostic diagnostic)
    {
        if (InvalidatesFeed(diagnostic.Kind))
        {
            lock (_sync)
            {
                _liquidations.Clear();
                Announce($"liquidation-diagnostic-{diagnostic.Kind}");
            }
        }
    }

    private async Task PumpDepthAsync(CancellationToken cancellationToken)
    {
        await foreach (BinanceStreamEnvelope envelope in _depth.ReadAsync(
            [BinanceDepthSubscription.Diff(_symbol)], cancellationToken).ConfigureAwait(false))
        {
            BinanceLocalOrderBookRecovery recovery;
            lock (_sync)
            {
                if (_timeProvider.GetUtcNow() < _depthRetryAt)
                {
                    continue;
                }
                recovery = _recovery;
            }
            BinanceOrderBookState state;
            try
            {
                state = await recovery.AcceptAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (BinanceApiException exception) when (IsTransient(exception))
            {
                DelayDepthRecovery(recovery, RetryDelay(exception),
                    $"depth-http-{(int)exception.StatusCode}");
                continue;
            }
            catch (HttpRequestException exception) when (exception is not BinanceApiException)
            {
                DelayDepthRecovery(recovery, _policy.OpenInterestPollInterval, "depth-transport-error");
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DelayDepthRecovery(recovery, _policy.OpenInterestPollInterval, "depth-timeout");
                continue;
            }
            lock (_sync)
            {
                if (!ReferenceEquals(recovery, _recovery))
                {
                    continue;
                }
                AcceptBookCore(state);
            }
        }
    }

    internal async Task PumpOpenInterestAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay = _policy.OpenInterestPollInterval;
            try
            {
                BinanceOpenInterest interest = await _rest.GetOpenInterestAsync(_symbol,
                    cancellationToken).ConfigureAwait(false);
                AcceptOpenInterest(interest);
            }
            catch (BinanceApiException exception) when (IsTransient(exception))
            {
                MarkInterestUnavailable($"open-interest-http-{(int)exception.StatusCode}");
                delay = RetryDelay(exception);
            }
            catch (HttpRequestException exception) when (exception is not BinanceApiException)
            {
                MarkInterestUnavailable("open-interest-transport-error");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                MarkInterestUnavailable("open-interest-timeout");
            }
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void AcceptMarket(BinanceStreamEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!StringComparer.Ordinal.Equals(envelope.Payload.Symbol, _symbol))
        {
            throw new ArgumentException("The public input symbol differs from the consumer.",
                nameof(envelope));
        }
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            UtcTimestamp available = new(now > envelope.AvailabilityTime.Value
                ? now : envelope.AvailabilityTime.Value);
            if (envelope.Payload is BinanceMarkPriceEvent mark)
            {
                SignalMarkPriceObservation observation = new("binance.usdm.mark-price",
                    envelope.SessionId, mark.EventId, _instrument, new Price(mark.MarkPrice),
                    mark.FundingRate, mark.EventTime, envelope.ReceiveTime, available,
                    envelope.StableSequence);
                _marks.Enqueue(observation);
                if (_marks.Count > 32)
                {
                    _marks.Dequeue();
                }
                Announce("mark-price");
            }
            else if (envelope.Payload is BinanceAggregateTradeEvent trade)
            {
                Trade payload = trade.ToCoreEvent();
                PruneTrades(now);
                if (_trades.Count >= _policy.Microstructure.MaximumInputRecords - 2)
                {
                    Announce("trade-window-buffer-overflow");
                    _trades.Clear();
                    _tradeCoverageStart = null;
                }
                _tradeCoverageStart ??= available;
                _trades.Enqueue(new TradeSample(envelope, payload, available, _processingSequence++));
                if (!_announced.Contains("trade-window-observed")
                    && _tradeCoverageStart.Value <= now.Subtract(_policy.Microstructure.Window)
                    && _trades.Any(item => item.Payload.EventTime.Value <= now
                        && item.AvailabilityTime.Value <= now)
                    && _books.Count(book => BookIsVisible(book, now)) >= 2)
                {
                    Announce("trade-window-observed");
                }
                Announce("trade");
            }
        }
    }

    internal void AcceptOpenInterest(BinanceOpenInterest interest)
    {
        ArgumentNullException.ThrowIfNull(interest);
        if (!StringComparer.Ordinal.Equals(interest.Symbol, _symbol))
        {
            throw new ArgumentException("The open-interest symbol differs from the consumer.",
                nameof(interest));
        }
        lock (_sync)
        {
            UtcTimestamp available = new(_timeProvider.GetUtcNow());
            SignalMarkPriceObservation? mark = LatestMarkAt(available.Value);
            if (mark is null)
            {
                return;
            }
            OIUpdate update = interest.ToCoreUpdate();
            if (_currentInterest is { } existing
                && StringComparer.Ordinal.Equals(existing.OpenInterest.EventId, update.EventId))
            {
                if (existing.OpenInterest.Value != update.OpenInterest)
                {
                    _previousInterest = null;
                    _currentInterest = null;
                }
                return;
            }
            LaneBOpenInterestObservation observation = new("binance.usdm.open-interest", update,
                "exchange-reported-quantity", "binance-usdm-rest-open-interest",
                available, available, _interestSequence++);
            SignalOpenInterestMarkSample sample = new(observation, mark);
            _previousInterest = _currentInterest;
            _currentInterest = sample;
            if (_previousInterest is not null)
            {
                Announce("open-interest-pair-observed");
                if (!_announced.Contains("trade-window-observed"))
                {
                    DateTimeOffset windowStart = available.Value.Subtract(_policy.Microstructure.Window);
                    if (_tradeCoverageStart is null)
                    {
                        Announce("trade-window-awaiting-trades");
                    }
                    else if (_tradeCoverageStart.Value > windowStart)
                    {
                        Announce("trade-window-warming");
                    }
                    else if (_books.Count(book => BookIsVisible(book, available.Value)) < 2)
                    {
                        Announce("trade-window-awaiting-visible-depth");
                    }
                    else if (!_trades.Any(trade => trade.Payload.EventTime.Value <= available.Value
                        && trade.AvailabilityTime.Value <= available.Value))
                    {
                        Announce("trade-window-awaiting-visible-trade");
                    }
                }
            }
            Announce("open-interest");
        }
    }

    internal void AcceptRecoveredBook(BinanceOrderBookState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_sync)
        {
            AcceptBookCore(state);
        }
    }

    private void AcceptBookCore(BinanceOrderBookState state)
    {
        if (!StringComparer.Ordinal.Equals(state.Symbol, _symbol))
        {
            throw new ArgumentException("The recovered book belongs to another instrument.",
                nameof(state));
        }
        if (state.Kind != BinanceOrderBookStateKind.Ready)
        {
            _books.Clear();
            _currentBook = null;
            return;
        }
        if (_currentBook is { } existing && (existing.State.RecoveryGeneration != state.RecoveryGeneration
            || state.LastUpdateId < existing.State.LastUpdateId))
        {
            _books.Clear();
            _currentBook = null;
        }
        if (_currentBook?.State.LastUpdateId == state.LastUpdateId)
        {
            return;
        }
        UtcTimestamp readyAt = new(_timeProvider.GetUtcNow());
        _currentBook = new DepthSample(state, readyAt, _processingSequence++);
        _books.Enqueue(_currentBook);
        if (_books.Count > 64)
        {
            _books.Dequeue();
        }
        Announce("recovered-depth");
    }

    private LaneCFeatureInput? BuildMicrostructure(DepthSample? previous, DepthSample? current,
        TradeSample[] trades, UtcTimestamp decisionAt)
    {
        if (previous is null || current is null
            || previous.State.RecoveryGeneration != current.State.RecoveryGeneration)
        {
            return null;
        }
        DateTimeOffset start = decisionAt.Value.Subtract(_policy.Microstructure.Window);
        List<PendingCapture> pending =
        [new(previous.Order, previous, null), new(current.Order, current, null)];
        foreach (TradeSample trade in trades)
        {
            Trade payload = trade.Payload;
            if (payload.EventTime.Value >= start && payload.EventTime.Value <= decisionAt.Value
                && trade.AvailabilityTime.Value <= decisionAt.Value)
            {
                pending.Add(new PendingCapture(trade.Order, null, trade));
            }
        }
        if (pending.Count > _policy.Microstructure.MaximumInputRecords)
        {
            return null;
        }
        pending.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        string streamId = $"binance-usdm-multilane:{_symbol}";
        List<CaptureEnvelope> captured = new(pending.Count);
        foreach (PendingCapture item in pending)
        {
            long sequence = captured.Count;
            if (item.Book is { } book)
            {
                captured.Add(LaneCHistoryCapturePipeline.CreateRecoveredSnapshot(book.State,
                    streamId, sequence, book.ReadyAt));
                continue;
            }
            TradeSample trade = item.Trade!;
            BinanceStreamEnvelope envelope = trade.Envelope;
            Trade payload = trade.Payload;
            CaptureProvenance provenance = new("binance.usdm", envelope.Payload.StreamName,
                envelope.SessionId, envelope.Payload.EventId, envelope.RawJson,
                new ImmutableValueList<string>([]), null, envelope.StableSequence);
            captured.Add(CaptureEnvelope.FromMarketEvent(streamId, sequence, payload,
                envelope.ReceiveTime, trade.AvailabilityTime, sequence, provenance,
                envelope.ExchangeTime));
        }
        return new LaneCFeatureInput(_instrument, captured,
            new CausalEventCursor(decisionAt, captured.Count - 1), "binance-public-live");
    }

    private void PruneTrades(DateTimeOffset now)
    {
        DateTimeOffset start = now.Subtract(_policy.Microstructure.Window);
        while (_trades.TryPeek(out TradeSample? trade)
            && trade.AvailabilityTime.Value < start && trade.Payload.EventTime.Value < start)
        {
            _trades.Dequeue();
        }
    }

    private void MarkInterestUnavailable(string reason)
    {
        lock (_sync)
        {
            _previousInterest = null;
            _currentInterest = null;
            Announce(reason);
        }
    }

    private void DelayDepthRecovery(BinanceLocalOrderBookRecovery recovery, TimeSpan delay,
        string reason)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(recovery, _recovery))
            {
                return;
            }
            _books.Clear();
            _currentBook = null;
            _recovery = CreateRecovery();
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _depthRetryAt = delay >= DateTimeOffset.MaxValue - now
                ? DateTimeOffset.MaxValue : now.Add(delay);
            Announce(reason);
        }
    }

    private TimeSpan RetryDelay(BinanceApiException exception) =>
        exception.RetryAfter is { } retryAfter && retryAfter > _policy.OpenInterestPollInterval
            ? retryAfter : _policy.OpenInterestPollInterval;

    private static bool IsTransient(BinanceApiException exception)
    {
        int status = (int)exception.StatusCode;
        return status is 408 or 418 or 429 or >= 500 and <= 599;
    }

    internal void OnMarketDiagnostic(BinanceStreamDiagnostic diagnostic)
    {
        if (InvalidatesFeed(diagnostic.Kind))
        {
            lock (_sync)
            {
                Announce($"market-diagnostic-{diagnostic.Kind}");
                ResetMarket();
            }
        }
    }

    private void ResetMarket()
    {
        _marks.Clear();
        _previousInterest = null;
        _currentInterest = null;
        _trades.Clear();
        _tradeCoverageStart = null;
    }

    internal void OnDepthDiagnostic(BinanceStreamDiagnostic diagnostic)
    {
        if (InvalidatesFeed(diagnostic.Kind))
        {
            lock (_sync)
            {
                _books.Clear();
                _currentBook = null;
                Announce($"depth-diagnostic-{diagnostic.Kind}");
                _recovery = CreateRecovery();
            }
        }
    }

    private SignalMarkPriceObservation? LatestMarkAt(DateTimeOffset cut)
    {
        SignalMarkPriceObservation? result = null;
        foreach (SignalMarkPriceObservation mark in _marks)
        {
            if (mark.EventTime.Value <= cut && mark.AvailabilityTime.Value <= cut)
            {
                result = mark;
            }
        }
        return result;
    }

    private static bool BookIsVisible(DepthSample book, DateTimeOffset cut) =>
        book.ReadyAt.Value <= cut
        && book.State.LastEventTime is { } lastEvent && lastEvent.Value <= cut
        && book.State.LastAvailabilityTime is { } lastAvailability && lastAvailability.Value <= cut
        && book.State.SnapshotAvailabilityTime is { } snapshotAvailability
        && snapshotAvailability.Value <= cut
        && (book.State.SnapshotEventTime?.Value ?? snapshotAvailability.Value) <= cut;

    private void Announce(string kind)
    {
        if (_announced.Add(kind))
        {
            _onFirstObservation?.Invoke(kind);
        }
    }

    private BinanceLocalOrderBookRecovery CreateRecovery() => new(_snapshotSource,
        new BinanceOrderBookRecoveryOptions { SnapshotLimit = 100 }, _symbol, _timeProvider);

    private static bool InvalidatesFeed(BinanceStreamDiagnosticKind kind) => kind is
        BinanceStreamDiagnosticKind.IdentityConflict or BinanceStreamDiagnosticKind.Late
        or BinanceStreamDiagnosticKind.Stale or BinanceStreamDiagnosticKind.Disconnect
        or BinanceStreamDiagnosticKind.Gap or BinanceStreamDiagnosticKind.ParseError
        or BinanceStreamDiagnosticKind.FrameTooLarge or BinanceStreamDiagnosticKind.Reconnect
        or BinanceStreamDiagnosticKind.BufferOverflow or BinanceStreamDiagnosticKind.SnapshotRejected
        or BinanceStreamDiagnosticKind.Recovery;

    private sealed record TradeSample(BinanceStreamEnvelope Envelope, Trade Payload,
        UtcTimestamp AvailabilityTime, long Order);

    private sealed record DepthSample(BinanceOrderBookState State, UtcTimestamp ReadyAt, long Order);

    private sealed record PendingCapture(long Order, DepthSample? Book, TradeSample? Trade);
}
