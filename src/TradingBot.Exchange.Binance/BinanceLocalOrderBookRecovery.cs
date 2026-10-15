using System.Collections.ObjectModel;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance;

public enum BinanceOrderBookStateKind
{
    Recovering,
    Ready
}

public enum BinanceOrderBookRecoveryDiagnosticKind
{
    Gap,
    BufferOverflow,
    SnapshotRejected,
    StaleUpdate,
    RecoveryLimit
}

/// <summary>Explicit bounds for the snapshot/diff recovery state machine.</summary>
public sealed record BinanceOrderBookRecoveryOptions
{
    public int SnapshotLimit { get; init; } = 1000;

    public int MaxBufferedUpdates { get; init; } = 10_000;

    public int MaxSnapshotLevels { get; init; } = 1000;

    public int MaxRecoveryAttempts { get; init; } = 3;
}

public sealed record BinanceOrderBookRecoveryDiagnostic
{
    public BinanceOrderBookRecoveryDiagnostic(
        BinanceOrderBookRecoveryDiagnosticKind kind, string symbol, string message,
        long stableSequence, UtcTimestamp observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentOutOfRangeException.ThrowIfLessThan(stableSequence, -1);
        ArgumentNullException.ThrowIfNull(observedAt);
        Kind = kind;
        Symbol = symbol;
        Message = message;
        StableSequence = stableSequence;
        ObservedAt = observedAt;
    }

    public BinanceOrderBookRecoveryDiagnosticKind Kind { get; }

    public string Symbol { get; }

    public string Message { get; }

    public long StableSequence { get; }

    public UtcTimestamp ObservedAt { get; }
}

/// <summary>
/// Immutable local order-book view. A recovering view contains no levels so a
/// caller cannot mistake a stale or partially applied book for current state.
/// </summary>
public sealed record BinanceOrderBookState
{
    public BinanceOrderBookState(string symbol, BinanceOrderBookStateKind kind,
        long lastUpdateId, long snapshotLastUpdateId,
        IEnumerable<BinanceDepthLevel> bids, IEnumerable<BinanceDepthLevel> asks,
        long recoveryGeneration, long lastAppliedStableSequence,
        long? lastEventTimeMilliseconds = null,
        long? lastTransactionTimeMilliseconds = null,
        UtcTimestamp? lastReceiveTime = null,
        UtcTimestamp? lastAvailabilityTime = null,
        UtcTimestamp? lastExchangeTime = null,
        UtcTimestamp? snapshotReceiveTime = null,
        UtcTimestamp? snapshotAvailabilityTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(lastUpdateId, -1);
        ArgumentOutOfRangeException.ThrowIfLessThan(snapshotLastUpdateId, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(recoveryGeneration);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastAppliedStableSequence, -1);
        if (lastAvailabilityTime is not null && lastReceiveTime is null)
        {
            throw new ArgumentException(
                "Book availability time requires a receive time.", nameof(lastAvailabilityTime));
        }

        if (lastReceiveTime is not null && lastAvailabilityTime is not null
            && lastAvailabilityTime.Value < lastReceiveTime.Value)
        {
            throw new ArgumentException(
                "Book availability cannot precede receive time.", nameof(lastAvailabilityTime));
        }

        Symbol = symbol;
        Kind = kind;
        LastUpdateId = lastUpdateId;
        SnapshotLastUpdateId = snapshotLastUpdateId;
        Bids = SnapshotLevels(bids, nameof(bids));
        Asks = SnapshotLevels(asks, nameof(asks));
        RecoveryGeneration = recoveryGeneration;
        LastAppliedStableSequence = lastAppliedStableSequence;
        LastEventTimeMilliseconds = lastEventTimeMilliseconds;
        LastTransactionTimeMilliseconds = lastTransactionTimeMilliseconds;
        LastReceiveTime = lastReceiveTime;
        LastAvailabilityTime = lastAvailabilityTime;
        LastExchangeTime = lastExchangeTime;
        SnapshotReceiveTime = snapshotReceiveTime;
        SnapshotAvailabilityTime = snapshotAvailabilityTime;
    }

    public string Symbol { get; }

    public BinanceOrderBookStateKind Kind { get; }

    public long LastUpdateId { get; }

    public long SnapshotLastUpdateId { get; }

    public IReadOnlyList<BinanceDepthLevel> Bids { get; }

    public IReadOnlyList<BinanceDepthLevel> Asks { get; }

    public long RecoveryGeneration { get; }

    public long LastAppliedStableSequence { get; }

    public long? LastEventTimeMilliseconds { get; }

    public long? LastTransactionTimeMilliseconds { get; }

    public UtcTimestamp? LastReceiveTime { get; }

    public UtcTimestamp? LastAvailabilityTime { get; }

    public UtcTimestamp? LastExchangeTime { get; }

    public UtcTimestamp? SnapshotReceiveTime { get; }

    public UtcTimestamp? SnapshotAvailabilityTime { get; }

    public UtcTimestamp? LastEventTime => LastEventTimeMilliseconds is long milliseconds
        ? new UtcTimestamp(BinanceTimestamp.ToUtc(milliseconds))
        : null;

    private static ReadOnlyCollection<BinanceDepthLevel> SnapshotLevels(
        IEnumerable<BinanceDepthLevel> levels, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(levels, parameterName);
        BinanceDepthLevel[] snapshot = levels.ToArray();
        if (snapshot.Any(static level => level is null))
        {
            throw new ArgumentException("Book levels cannot contain null values.", parameterName);
        }

        return new ReadOnlyCollection<BinanceDepthLevel>(snapshot);
    }
}

/// <summary>
/// Applies Binance buffered diff-depth updates to a REST snapshot. The class
/// keeps all source causal metadata on the immutable published state.
/// </summary>
public sealed class BinanceLocalOrderBookRecovery
{
    private readonly IBinanceDepthSnapshotSource _snapshotSource;
    private readonly BinanceOrderBookRecoveryOptions _options;
    private readonly List<BinanceOrderBookRecoveryDiagnostic> _diagnostics = [];
    private readonly List<BinanceStreamEnvelope> _buffer = [];
    private readonly Dictionary<decimal, decimal> _bids = [];
    private readonly Dictionary<decimal, decimal> _asks = [];
    private readonly TimeProvider _timeProvider;
    private string? _symbol;
    private BinanceDepthSnapshot? _snapshot;
    private long _currentUpdateId = -1;
    private bool _aligned;
    private long _recoveryGeneration;
    private int _recoveryAttempts;
    private BinanceOrderBookState _state;

    public BinanceLocalOrderBookRecovery(IBinanceDepthSnapshotSource snapshotSource,
        BinanceOrderBookRecoveryOptions? options = null, string? symbol = null,
        TimeProvider? timeProvider = null)
    {
        _snapshotSource = snapshotSource
            ?? throw new ArgumentNullException(nameof(snapshotSource));
        _options = options ?? new BinanceOrderBookRecoveryOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);
        if (symbol is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
            _symbol = symbol.ToUpperInvariant();
        }

        _state = CreateRecoveringState();
    }

    public BinanceOrderBookState State => _state;

    public IReadOnlyList<BinanceOrderBookRecoveryDiagnostic> Diagnostics =>
        _diagnostics.ToArray();

    public event Action<BinanceOrderBookRecoveryDiagnostic>? Diagnostic;

    public async Task<BinanceOrderBookState> AcceptAsync(BinanceStreamEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload is not BinanceDepthUpdateEvent update)
        {
            throw new ArgumentException(
                "The local order-book recovery accepts only depth updates.", nameof(envelope));
        }

        EnsureSymbol(update.Symbol);
        if (_state.Kind == BinanceOrderBookStateKind.Ready)
        {
            ApplyReady(update, envelope);
            return _state;
        }

        if (_buffer.Count >= _options.MaxBufferedUpdates)
        {
            ResetForRecovery(BinanceOrderBookRecoveryDiagnosticKind.BufferOverflow,
                "The depth update buffer reached its configured bound.", envelope.StableSequence);
            return _state;
        }

        _buffer.Add(envelope);
        if (_snapshot is null)
        {
            await TryLoadSnapshotAsync(envelope.StableSequence, cancellationToken)
                .ConfigureAwait(false);
        }

        ApplyBuffered();
        return _state;
    }

    public Task<BinanceOrderBookState> ApplyAsync(BinanceStreamEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        return AcceptAsync(envelope, cancellationToken);
    }

    private async Task TryLoadSnapshotAsync(long stableSequence,
        CancellationToken cancellationToken)
    {
        if (_recoveryAttempts >= _options.MaxRecoveryAttempts)
        {
            AddDiagnostic(BinanceOrderBookRecoveryDiagnosticKind.RecoveryLimit,
                "The configured snapshot recovery attempt bound was reached.", stableSequence);
            return;
        }

        _recoveryAttempts++;
        try
        {
            BinanceDepthSnapshot snapshot = await _snapshotSource.GetDepthSnapshotAsync(
                _symbol!, _options.SnapshotLimit, cancellationToken).ConfigureAwait(false);
            ValidateSnapshot(snapshot);
            _snapshot = snapshot;
            _currentUpdateId = snapshot.LastUpdateId;
            _aligned = false;
            _bids.Clear();
            _asks.Clear();
            CopyLevels(snapshot.Bids, _bids);
            CopyLevels(snapshot.Asks, _asks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or InvalidDataException
            or ArgumentOutOfRangeException or OverflowException)
        {
            AddDiagnostic(BinanceOrderBookRecoveryDiagnosticKind.SnapshotRejected,
                $"The depth snapshot was rejected: {exception.Message}", stableSequence);
            _snapshot = null;
        }
    }

    private void ApplyBuffered()
    {
        while (_snapshot is not null && _buffer.Count > 0)
        {
            BinanceStreamEnvelope envelope = _buffer[0];
            BinanceDepthUpdateEvent update = (BinanceDepthUpdateEvent)envelope.Payload;

            if (update.FinalUpdateId <= _currentUpdateId)
            {
                _buffer.RemoveAt(0);
                AddDiagnostic(BinanceOrderBookRecoveryDiagnosticKind.StaleUpdate,
                    "A depth update ended at or before the snapshot/book update ID.",
                    envelope.StableSequence);
                continue;
            }

            if (!_aligned)
            {
                if (update.FirstUpdateId > checked(_currentUpdateId + 1))
                {
                    ResetForRecovery(BinanceOrderBookRecoveryDiagnosticKind.Gap,
                        "The first post-snapshot depth update did not overlap the snapshot.",
                        envelope.StableSequence);
                    return;
                }

                ApplyUpdate(update, envelope);
                _buffer.RemoveAt(0);
                _aligned = true;
                continue;
            }

            if (IsGap(update))
            {
                ResetForRecovery(BinanceOrderBookRecoveryDiagnosticKind.Gap,
                    "A depth update skipped one or more update IDs.", envelope.StableSequence);
                return;
            }

            ApplyUpdate(update, envelope);
            _buffer.RemoveAt(0);
        }
    }

    private void ApplyReady(BinanceDepthUpdateEvent update, BinanceStreamEnvelope envelope)
    {
        if (update.FinalUpdateId <= _currentUpdateId)
        {
            AddDiagnostic(BinanceOrderBookRecoveryDiagnosticKind.StaleUpdate,
                "A depth update ended at or before the current book update ID.",
                envelope.StableSequence);
            return;
        }

        if (IsGap(update))
        {
            ResetForRecovery(BinanceOrderBookRecoveryDiagnosticKind.Gap,
                "A depth update skipped one or more update IDs.", envelope.StableSequence);
            return;
        }

        ApplyUpdate(update, envelope);
    }

    private void ApplyUpdate(BinanceDepthUpdateEvent update, BinanceStreamEnvelope envelope)
    {
        ApplyLevels(update.Bids, _bids);
        ApplyLevels(update.Asks, _asks);
        _currentUpdateId = update.FinalUpdateId;
        _state = new BinanceOrderBookState(_symbol!, BinanceOrderBookStateKind.Ready,
            _currentUpdateId, _snapshot!.LastUpdateId, SortLevels(_bids, descending: true),
            SortLevels(_asks, descending: false), _recoveryGeneration, envelope.StableSequence,
            update.EventTimeMilliseconds, update.TransactionTimeMilliseconds,
            envelope.ReceiveTime, envelope.AvailabilityTime, envelope.ExchangeTime,
            _snapshot.ReceiveTime, _snapshot.AvailabilityTime);
    }

    private bool IsGap(BinanceDepthUpdateEvent update)
    {
        if (update.PreviousUpdateId is long previous
            && previous != _currentUpdateId)
        {
            return true;
        }

        return update.FirstUpdateId > checked(_currentUpdateId + 1);
    }

    private void ResetForRecovery(BinanceOrderBookRecoveryDiagnosticKind kind,
        string message, long stableSequence)
    {
        AddDiagnostic(kind, message, stableSequence);
        _recoveryGeneration++;
        _snapshot = null;
        _currentUpdateId = -1;
        _aligned = false;
        _buffer.Clear();
        _bids.Clear();
        _asks.Clear();
        _recoveryAttempts = 0;
        _state = CreateRecoveringState();
    }

    private void ValidateSnapshot(BinanceDepthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Symbol is not null
            && !StringComparer.OrdinalIgnoreCase.Equals(snapshot.Symbol, _symbol))
        {
            throw new InvalidDataException("The depth snapshot symbol does not match the stream.");
        }

        if (snapshot.Bids.Count > _options.MaxSnapshotLevels
            || snapshot.Asks.Count > _options.MaxSnapshotLevels)
        {
            throw new InvalidDataException("The depth snapshot exceeded the configured level bound.");
        }

        EnsureUniqueLevels(snapshot.Bids);
        EnsureUniqueLevels(snapshot.Asks);
    }

    private static void EnsureUniqueLevels(IReadOnlyList<BinanceDepthLevel> levels)
    {
        HashSet<decimal> prices = [];
        foreach (BinanceDepthLevel level in levels)
        {
            if (!prices.Add(level.Price))
            {
                throw new InvalidDataException("A depth snapshot contained a duplicate price.");
            }
        }
    }

    private void EnsureSymbol(string symbol)
    {
        string normalized = symbol.ToUpperInvariant();
        if (_symbol is null)
        {
            _symbol = normalized;
        }
        else if (!StringComparer.Ordinal.Equals(_symbol, normalized))
        {
            throw new ArgumentException("The depth update symbol does not match the book.",
                nameof(symbol));
        }
    }

    private BinanceOrderBookState CreateRecoveringState()
    {
        return new BinanceOrderBookState(_symbol ?? "UNBOUND",
            BinanceOrderBookStateKind.Recovering, -1, -1, [], [], _recoveryGeneration, -1);
    }

    private void AddDiagnostic(BinanceOrderBookRecoveryDiagnosticKind kind, string message,
        long stableSequence)
    {
        string symbol = _symbol ?? "UNBOUND";
        BinanceOrderBookRecoveryDiagnostic diagnostic = new(kind, symbol, message,
            stableSequence, new UtcTimestamp(_timeProvider.GetUtcNow()));
        _diagnostics.Add(diagnostic);
        Diagnostic?.Invoke(diagnostic);
    }

    private static void CopyLevels(IReadOnlyList<BinanceDepthLevel> source,
        Dictionary<decimal, decimal> destination)
    {
        foreach (BinanceDepthLevel level in source)
        {
            if (level.Quantity > 0m)
            {
                destination[level.Price] = level.Quantity;
            }
        }
    }

    private static void ApplyLevels(IReadOnlyList<BinanceDepthLevel> updates,
        Dictionary<decimal, decimal> destination)
    {
        foreach (BinanceDepthLevel level in updates)
        {
            if (level.Quantity == 0m)
            {
                destination.Remove(level.Price);
            }
            else
            {
                destination[level.Price] = level.Quantity;
            }
        }
    }

    private static BinanceDepthLevel[] SortLevels(Dictionary<decimal, decimal> levels,
        bool descending)
    {
        IOrderedEnumerable<KeyValuePair<decimal, decimal>> ordered = descending
            ? levels.OrderByDescending(static pair => pair.Key)
            : levels.OrderBy(static pair => pair.Key);
        return ordered.Select(static pair => new BinanceDepthLevel(pair.Key, pair.Value))
            .ToArray();
    }

    private static void ValidateOptions(BinanceOrderBookRecoveryOptions options)
    {
        if (options.SnapshotLimit is not (5 or 10 or 20 or 50 or 100 or 500 or 1000))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxBufferedUpdates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxSnapshotLevels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxRecoveryAttempts);
        if (options.MaxSnapshotLevels < options.SnapshotLimit)
        {
            throw new ArgumentException(
                "The maximum snapshot levels must cover the requested snapshot limit.",
                nameof(options));
        }
    }
}
