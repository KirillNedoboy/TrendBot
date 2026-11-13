using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Writes recovered public depth and trade events to one causal capture stream.</summary>
public sealed class LaneCHistoryCapturePipeline
{
    private readonly ParquetCaptureStore _store;
    private readonly string _streamId;
    private long _nextSequence;
    private long? _readyRecoveryGeneration;
    private long? _lastCapturedDepthUpdateId;

    public LaneCHistoryCapturePipeline(ParquetCaptureStore store, string streamId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        if (!StringComparer.Ordinal.Equals(store.Checkpoint.StreamId, streamId))
        {
            throw new ArgumentException("The capture stream must match the store.", nameof(streamId));
        }

        _store = store;
        _streamId = streamId;
        _nextSequence = store.Checkpoint.NextCaptureSequence;
    }

    public CaptureEnvelope CaptureRecoveredSnapshot(BinanceOrderBookState state)
    {
        CaptureEnvelope envelope = CreateRecoveredSnapshot(state, _streamId, _nextSequence);
        _store.Append(envelope);
        _nextSequence = checked(_nextSequence + 1);
        _readyRecoveryGeneration = state.RecoveryGeneration;
        _lastCapturedDepthUpdateId = state.LastUpdateId;
        return envelope;
    }

    public CaptureEnvelope? CaptureStreamEvent(BinanceStreamEnvelope envelope,
        long recoveryGeneration)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        MarketEvent? marketEvent = envelope.ToCoreEvent();
        if (marketEvent is null)
        {
            return null;
        }

        CaptureDepthMetadata? depth = null;
        if (envelope.Payload is BinanceDepthUpdateEvent update)
        {
            if (_readyRecoveryGeneration != recoveryGeneration)
            {
                throw new InvalidOperationException(
                    "Depth cannot be captured until this recovery generation has a snapshot.");
            }

            if (update.FinalUpdateId <= _lastCapturedDepthUpdateId)
            {
                throw new InvalidOperationException(
                    "A depth update already included in the recovered book cannot be captured.");
            }

            depth = new CaptureDepthMetadata(update.FirstUpdateId, update.FinalUpdateId,
                update.PreviousUpdateId, recoveryGeneration, isSnapshot: false);
        }

        CaptureProvenance provenance = new("binance.usdm", envelope.Payload.StreamName,
            envelope.SessionId, envelope.Payload.EventId, envelope.RawJson,
            new ImmutableValueList<string>(Array.Empty<string>()), depth,
            envelope.StableSequence);
        CaptureEnvelope captured = CaptureEnvelope.FromMarketEvent(_streamId, _nextSequence,
            marketEvent, envelope.ReceiveTime, envelope.AvailabilityTime, _nextSequence,
            provenance, envelope.ExchangeTime);
        _store.Append(captured);
        _nextSequence = checked(_nextSequence + 1);
        if (depth is not null)
        {
            _lastCapturedDepthUpdateId = depth.FinalUpdateId;
        }
        return captured;
    }

    public CaptureEnvelope CaptureStreamDiagnostic(BinanceStreamDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        bool depthStream = diagnostic.StreamName?.Contains("depth",
            StringComparison.OrdinalIgnoreCase) == true;
        if (depthStream && diagnostic.Kind is BinanceStreamDiagnosticKind.Gap
            or BinanceStreamDiagnosticKind.Disconnect
            or BinanceStreamDiagnosticKind.BufferOverflow
            or BinanceStreamDiagnosticKind.SnapshotRejected
            or BinanceStreamDiagnosticKind.Recovery)
        {
            MarkRecoveryUnavailable();
        }

        string code = $"binance-stream-{diagnostic.Kind}";
        CaptureDiagnostic payload = new(code, diagnostic.Message,
            diagnostic.StreamName is null ? [diagnostic.Kind.ToString()]
                : [diagnostic.Kind.ToString(), diagnostic.StreamName]);
        CaptureProvenance provenance = new("binance.usdm", diagnostic.StreamName,
            diagnostic.SessionId, diagnostic.EventId, null,
            new ImmutableValueList<string>([diagnostic.Kind.ToString().ToLowerInvariant()]),
            sourceStableSequence: diagnostic.StableSequence < 0
                ? null : diagnostic.StableSequence);
        CaptureEnvelope captured = CaptureEnvelope.FromDiagnostic(_streamId, _nextSequence,
            diagnostic.ObservedAt, diagnostic.ObservedAt, _nextSequence, payload, provenance);
        _store.Append(captured);
        _nextSequence = checked(_nextSequence + 1);
        return captured;
    }

    public CaptureEnvelope CaptureRecoveryDiagnostic(
        BinanceOrderBookRecoveryDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        MarkRecoveryUnavailable();
        CaptureDiagnostic payload = new($"orderbook-{diagnostic.Kind}", diagnostic.Message,
            [diagnostic.Symbol, diagnostic.Kind.ToString()]);
        CaptureProvenance provenance = new("binance.usdm", "orderbook.recovery", null,
            $"{diagnostic.Kind}:{diagnostic.StableSequence}", null,
            new ImmutableValueList<string>([diagnostic.Kind.ToString().ToLowerInvariant()]),
            sourceStableSequence: diagnostic.StableSequence < 0
                ? null : diagnostic.StableSequence);
        CaptureEnvelope captured = CaptureEnvelope.FromDiagnostic(_streamId, _nextSequence,
            diagnostic.ObservedAt, diagnostic.ObservedAt, _nextSequence, payload, provenance);
        _store.Append(captured);
        _nextSequence = checked(_nextSequence + 1);
        return captured;
    }

    public void MarkRecoveryUnavailable()
    {
        _readyRecoveryGeneration = null;
        _lastCapturedDepthUpdateId = null;
    }

    internal static CaptureEnvelope CreateRecoveredSnapshot(BinanceOrderBookState state,
        string streamId, long sequence, UtcTimestamp? readyAt = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        UtcTimestamp? lastEventTime = state.LastEventTime;
        if (state.Kind != BinanceOrderBookStateKind.Ready
            || state.SnapshotReceiveTime is null || state.SnapshotAvailabilityTime is null
            || state.LastReceiveTime is null || state.LastAvailabilityTime is null
            || lastEventTime is null || state.LastAppliedStableSequence < 0)
        {
            throw new InvalidOperationException("Only a timestamped recovered book can be captured.");
        }

        long updateId = state.LastUpdateId;
        if (updateId < 0 || state.SnapshotLastUpdateId < 0
            || updateId < state.SnapshotLastUpdateId)
        {
            throw new InvalidOperationException("A recovered book requires valid update IDs.");
        }

        UtcTimestamp eventTime = Latest(lastEventTime,
            state.SnapshotEventTime ?? state.SnapshotAvailabilityTime);
        UtcTimestamp receiveTime = Latest(state.SnapshotReceiveTime, state.LastReceiveTime);
        UtcTimestamp availabilityTime = Latest(state.SnapshotAvailabilityTime,
            state.LastAvailabilityTime);
        if (readyAt is not null)
        {
            availabilityTime = Latest(availabilityTime, readyAt);
        }
        UtcTimestamp? exchangeTime = state.SnapshotExchangeTime is UtcTimestamp snapshotExchange
            && state.LastExchangeTime is UtcTimestamp lastExchange
                ? Latest(snapshotExchange, lastExchange) : null;
        BookDelta book = new($"checkpoint:{state.Symbol}:{updateId}",
            new InstrumentId(state.Symbol), eventTime,
            state.Bids.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))),
            state.Asks.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))));
        CaptureDepthMetadata depth = new(updateId, updateId, null,
            state.RecoveryGeneration, isSnapshot: true);
        CaptureProvenance provenance = new("binance.usdm", "orderbook.recovery", null,
            book.EventId, null, new ImmutableValueList<string>(Array.Empty<string>()), depth,
            state.LastAppliedStableSequence);
        return CaptureEnvelope.FromMarketEvent(streamId, sequence, book, receiveTime,
            availabilityTime, sequence, provenance, exchangeTime);
    }

    private static UtcTimestamp Latest(UtcTimestamp left, UtcTimestamp right) =>
        left.Value >= right.Value ? left : right;
}
