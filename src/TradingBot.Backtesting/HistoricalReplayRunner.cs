using System.Collections.ObjectModel;
using TradingBot.Core;

namespace TradingBot.Backtesting;

/// <summary>Identifies a replay runner diagnostic.</summary>
public enum ReplayDiagnosticKind
{
    Duplicate,
    IdentityConflict,
    StableSequenceConflict,
    InvalidInput,
    HandlerFailure
}

/// <summary>Describes a recoverable or terminal replay diagnostic.</summary>
public sealed record ReplayDiagnostic
{
    public ReplayDiagnosticKind Kind { get; }

    public string Message { get; }

    public string? EventId { get; }

    public long? StableSequence { get; }

    public ReplayDiagnostic(ReplayDiagnosticKind kind, string message,
        string? eventId = null, long? stableSequence = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), (int)kind,
                "The replay diagnostic kind is not defined.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Kind = kind;
        Message = message;
        EventId = eventId;
        StableSequence = stableSequence;
    }
}

/// <summary>
/// Immutable context delivered to a replay handler. Prefix contains only records
/// visible at the current virtual availability time and stable-sequence boundary.
/// </summary>
public sealed class ReplayStepContext
{
    internal ReplayStepContext(ReplayEnvelope envelope, UtcTimestamp virtualTime,
        CausalEventCursor cursor, ReplayStreamSnapshot prefix)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(virtualTime);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(prefix);

        Envelope = envelope;
        Event = envelope.CausalEvent.Payload;
        VirtualTime = virtualTime;
        Cursor = cursor;
        Prefix = prefix;
    }

    /// <summary>Gets the envelope currently being delivered.</summary>
    public ReplayEnvelope Envelope { get; }

    /// <summary>Gets the immutable market event currently being delivered.</summary>
    public MarketEvent Event { get; }

    /// <summary>Gets the monotonic virtual availability time.</summary>
    public UtcTimestamp VirtualTime { get; }

    /// <summary>Gets the causal cursor used to build <see cref="Prefix"/>.</summary>
    public CausalEventCursor Cursor { get; }

    /// <summary>Gets the immutable causal prefix available to this step.</summary>
    public ReplayStreamSnapshot Prefix { get; }

    /// <summary>Gets the receive timestamp retained by the persisted record.</summary>
    public UtcTimestamp ReceiveTime => Envelope.CausalEvent.ReceiveTime;

    /// <summary>Gets the optional exchange timestamp retained by the persisted record.</summary>
    public UtcTimestamp? ExchangeTime => Envelope.CausalEvent.ExchangeTime;

    /// <summary>Gets the persisted stable processing sequence.</summary>
    public long StableSequence => Envelope.CausalEvent.StableSequence;
}

/// <summary>One successfully committed replay delivery.</summary>
public sealed class ReplayStepResult
{
    internal ReplayStepResult(ReplayStepContext context, object? output)
    {
        Context = context;
        Output = output;
    }

    /// <summary>Gets the immutable context delivered to the handler.</summary>
    public ReplayStepContext Context { get; }

    /// <summary>Gets the optional handler output, when the value overload was used.</summary>
    public object? Output { get; }

    /// <summary>Gets the delivered envelope.</summary>
    public ReplayEnvelope Envelope => Context.Envelope;

    /// <summary>Gets the delivered market event.</summary>
    public MarketEvent Event => Context.Event;

    /// <summary>Gets the step's virtual availability time.</summary>
    public UtcTimestamp VirtualTime => Context.VirtualTime;

    /// <summary>Gets the step's causal prefix.</summary>
    public ReplayStreamSnapshot Prefix => Context.Prefix;
}

/// <summary>Immutable result of one historical replay attempt.</summary>
public sealed class ReplayRunResult
{
    internal ReplayRunResult(string streamId, IEnumerable<ReplayStepResult> steps,
        IEnumerable<ReplayDiagnostic> diagnostics, bool succeeded, Exception? failure,
        CausalEventCursor finalCursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(finalCursor);

        StreamId = streamId;
        Steps = new ReadOnlyCollection<ReplayStepResult>(steps.ToList());
        Diagnostics = new ReadOnlyCollection<ReplayDiagnostic>(diagnostics.ToList());
        Succeeded = succeeded;
        Failure = failure;
        FinalCursor = finalCursor;
    }

    /// <summary>Gets the stream identity supplied to the runner.</summary>
    public string StreamId { get; }

    /// <summary>Gets the successfully committed steps.</summary>
    public IReadOnlyList<ReplayStepResult> Steps { get; }

    /// <summary>Gets the diagnostics collected during validation and delivery.</summary>
    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether every unique envelope was delivered successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets whether a validation or handler failure stopped the run.</summary>
    public bool Failed => !Succeeded;

    /// <summary>Gets the handler exception when delivery stopped because of a failure.</summary>
    public Exception? Failure { get; }

    /// <summary>Gets the last committed causal cursor.</summary>
    public CausalEventCursor FinalCursor { get; }

    /// <summary>Gets the count of successfully committed steps.</summary>
    public int ProcessedCount => Steps.Count;
}

/// <summary>
/// Runs one persisted replay stream in deterministic causal availability order.
/// The runner is a research boundary: it invokes a caller-supplied handler and
/// never creates decisions, orders, or authorization state.
/// </summary>
public sealed class ReplayRunner
{
#pragma warning disable CA1822 // The instance surface is intentional for dependency injection.
    /// <summary>Runs the stream and ignores handler return values.</summary>
    public ReplayRunResult Run(string streamId, IEnumerable<ReplayEnvelope> envelopes,
        Action<ReplayStepContext> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RunCore(streamId, envelopes, context =>
        {
            handler(context);
            return null;
        });
    }

    /// <summary>Runs the stream and retains each handler return value in the step.</summary>
    public ReplayRunResult Run(string streamId, IEnumerable<ReplayEnvelope> envelopes,
        Func<ReplayStepContext, object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RunCore(streamId, envelopes, handler);
    }
#pragma warning restore CA1822

    private static ReplayRunResult RunCore(string streamId,
        IEnumerable<ReplayEnvelope> envelopes, Func<ReplayStepContext, object?> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(envelopes);

        List<ReplayDiagnostic> diagnostics = [];
        Dictionary<ReplayIdentity, ReplayEnvelope> identities = [];
        Dictionary<long, ReplayEnvelope> sequences = [];
        List<ReplayEnvelope> unique = [];

        foreach (ReplayEnvelope? candidate in envelopes)
        {
            if (candidate is null)
            {
                diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticKind.InvalidInput,
                    "A replay stream cannot contain a null envelope."));
                return FailedResult(streamId, diagnostics);
            }

            if (!StringComparer.Ordinal.Equals(candidate.StreamId, streamId))
            {
                diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticKind.InvalidInput,
                    "Every replay envelope must belong to the requested stream.",
                    candidate.CausalEvent.Payload.EventId,
                    candidate.CausalEvent.StableSequence));
                return FailedResult(streamId, diagnostics);
            }

            ReplayIdentity identity = ReplayIdentity.Create(candidate);
            if (identities.TryGetValue(identity, out ReplayEnvelope? existingIdentity))
            {
                if (existingIdentity.Equals(candidate))
                {
                    diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticKind.Duplicate,
                        "An exact replay envelope was recorded more than once.",
                        candidate.CausalEvent.Payload.EventId,
                        candidate.CausalEvent.StableSequence));
                    continue;
                }

                diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticKind.IdentityConflict,
                    "A replay event identity has conflicting payload or causal fields.",
                    candidate.CausalEvent.Payload.EventId,
                    candidate.CausalEvent.StableSequence));
                return FailedResult(streamId, diagnostics);
            }

            if (sequences.TryGetValue(candidate.CausalEvent.StableSequence,
                out ReplayEnvelope? existingSequence))
            {
                diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticKind.StableSequenceConflict,
                    "A stable processing sequence is assigned to different replay events.",
                    candidate.CausalEvent.Payload.EventId,
                    candidate.CausalEvent.StableSequence));
                return FailedResult(streamId, diagnostics);
            }

            identities.Add(identity, candidate);
            sequences.Add(candidate.CausalEvent.StableSequence, candidate);
            unique.Add(candidate);
        }

        List<ReplayEnvelope> ordered = unique
            .OrderBy(AvailableAt)
            .ThenBy(static envelope => envelope.CausalEvent.Payload.EventTime.Value)
            .ThenBy(static envelope => envelope.CausalEvent.StableSequence)
            .ThenBy(static envelope => envelope.CausalEvent.Payload.GetType().FullName,
                StringComparer.Ordinal)
            .ThenBy(static envelope => envelope.CausalEvent.Payload.EventId,
                StringComparer.Ordinal)
            .ToList();

        List<ReplayStepResult> steps = [];
        DateTimeOffset lastVirtualTime = DateTimeOffset.MinValue;
        DateTimeOffset lastCommittedTime = DateTimeOffset.MinValue;
        long lastStableSequence = 0;
        foreach (ReplayEnvelope envelope in ordered)
        {
            DateTimeOffset availableAt = AvailableAt(envelope);
            if (availableAt < lastVirtualTime)
            {
                availableAt = lastVirtualTime;
            }

            lastVirtualTime = availableAt;
            long effectiveSequence = Math.Max(lastStableSequence,
                envelope.CausalEvent.StableSequence);
            CausalEventCursor cursor = new(new UtcTimestamp(availableAt), effectiveSequence);
            ReplayStreamSnapshot prefix = ReplaySnapshotBuilder.Build(streamId, unique, cursor);
            ReplayStepContext context = new(envelope, new UtcTimestamp(availableAt), cursor, prefix);

            try
            {
                object? output = handler(context);
                steps.Add(new ReplayStepResult(context, output));
                lastStableSequence = effectiveSequence;
                lastCommittedTime = availableAt;
            }
            catch (Exception exception)
            {
                diagnostics.Add(new ReplayDiagnostic(ReplayDiagnosticKind.HandlerFailure,
                    $"Replay handler failed for event '{envelope.CausalEvent.Payload.EventId}': {exception.Message}",
                    envelope.CausalEvent.Payload.EventId,
                    envelope.CausalEvent.StableSequence));
                return new ReplayRunResult(streamId, steps, diagnostics, false, exception,
                    LastCursor(lastCommittedTime, lastStableSequence));
            }
        }

        return new ReplayRunResult(streamId, steps, diagnostics, true, null,
            LastCursor(lastVirtualTime, lastStableSequence));
    }

    private static ReplayRunResult FailedResult(string streamId,
        IEnumerable<ReplayDiagnostic> diagnostics) =>
        new(streamId, [], diagnostics, false, null, LastCursor(DateTimeOffset.MinValue, 0));

    private static CausalEventCursor LastCursor(DateTimeOffset time, long sequence) =>
        new(new UtcTimestamp(time), sequence);

    private static DateTimeOffset AvailableAt(ReplayEnvelope envelope)
    {
        CausalMarketEvent causal = envelope.CausalEvent;
        DateTimeOffset availableAt = causal.Payload.EventTime.Value;
        if (causal.AvailabilityTime.Value > availableAt)
        {
            availableAt = causal.AvailabilityTime.Value;
        }

        if (causal.Payload is Bar bar && bar.CloseTime.Value > availableAt)
        {
            availableAt = bar.CloseTime.Value;
        }

        return availableAt;
    }

    private readonly record struct ReplayIdentity(Type PayloadType, string InstrumentId,
        string EventId)
    {
        public static ReplayIdentity Create(ReplayEnvelope envelope)
        {
            MarketEvent payload = envelope.CausalEvent.Payload;
            return new(payload.GetType(), payload.InstrumentId.Value, payload.EventId);
        }
    }
}

/// <summary>Convenience entry point for one-shot historical replay.</summary>
public static class HistoricalReplayRunner
{
    /// <summary>Runs a stream and ignores handler return values.</summary>
    public static ReplayRunResult Run(string streamId, IEnumerable<ReplayEnvelope> envelopes,
        Action<ReplayStepContext> handler) => new ReplayRunner().Run(streamId, envelopes, handler);

    /// <summary>Runs a stream and retains handler return values.</summary>
    public static ReplayRunResult Run(string streamId, IEnumerable<ReplayEnvelope> envelopes,
        Func<ReplayStepContext, object?> handler) => new ReplayRunner().Run(streamId, envelopes, handler);
}
