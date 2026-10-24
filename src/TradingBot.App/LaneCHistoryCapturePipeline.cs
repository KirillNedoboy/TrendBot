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
        ArgumentNullException.ThrowIfNull(state);
        if (state.Kind != BinanceOrderBookStateKind.Ready
            || state.SnapshotReceiveTime is null || state.SnapshotAvailabilityTime is null)
        {
            throw new InvalidOperationException("Only a timestamped recovered book can be captured.");
        }

        long updateId = state.SnapshotLastUpdateId;
        if (updateId < 0)
        {
            throw new InvalidOperationException("A recovered book snapshot requires its update ID.");
        }

        UtcTimestamp eventTime = state.SnapshotEventTime ?? state.SnapshotAvailabilityTime;
        BookDelta book = new($"snapshot:{state.Symbol}:{updateId}",
            new InstrumentId(state.Symbol), eventTime,
            state.Bids.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))),
            state.Asks.Select(static level => new BookLevel(new Price(level.Price),
                new Quantity(level.Quantity))));
        CaptureDepthMetadata depth = new(updateId, updateId, null,
            state.RecoveryGeneration, isSnapshot: true);
        CaptureProvenance provenance = new("binance.usdm", "rest.snapshot", null,
            book.EventId, null, new ImmutableValueList<string>(Array.Empty<string>()), depth);
        CaptureEnvelope envelope = CaptureEnvelope.FromMarketEvent(_streamId, _nextSequence,
            book, state.SnapshotReceiveTime, state.SnapshotAvailabilityTime, _nextSequence,
            provenance, state.SnapshotExchangeTime);
        _store.Append(envelope);
        _nextSequence = checked(_nextSequence + 1);
        _readyRecoveryGeneration = state.RecoveryGeneration;
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

    public void MarkRecoveryUnavailable() => _readyRecoveryGeneration = null;
}
