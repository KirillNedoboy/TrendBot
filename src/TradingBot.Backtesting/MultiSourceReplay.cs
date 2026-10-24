using System.Collections.ObjectModel;
using TradingBot.Core;

namespace TradingBot.Backtesting;

public enum MultiSourceReplayDiagnosticKind
{
    Duplicate,
    IdentityConflict,
    MissingRecord,
    InvalidRecord,
    HandlerFailure
}

public sealed record MultiSourceReplayDiagnostic
{
    public MultiSourceReplayDiagnosticKind Kind { get; }
    public string Message { get; }
    public string? SourceId { get; }
    public string? EventId { get; }

    public MultiSourceReplayDiagnostic(MultiSourceReplayDiagnosticKind kind,
        string message, string? sourceId = null, string? eventId = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Kind = kind;
        Message = message;
        SourceId = sourceId;
        EventId = eventId;
    }
}

/// <summary>One typed source payload tied to its persisted dataset ordering row.</summary>
public sealed record MultiSourceReplayRecord
{
    public MultiSourceReplayOrderEntry Ordering { get; }
    public object Payload { get; }

    public MultiSourceReplayRecord(MultiSourceReplayOrderEntry ordering, object payload)
    {
        ArgumentNullException.ThrowIfNull(ordering);
        ArgumentNullException.ThrowIfNull(payload);
        ValidatePayload(ordering, payload);
        Ordering = ordering;
        Payload = payload;
    }

    private static void ValidatePayload(MultiSourceReplayOrderEntry ordering, object payload)
    {
        (string sourceId, string eventId, long sequence, UtcTimestamp eventTime,
            UtcTimestamp? exchangeTime, UtcTimestamp receiveTime,
            UtcTimestamp availabilityTime) = payload switch
            {
                ReplayEnvelope envelope => (envelope.StreamId,
                    envelope.CausalEvent.Payload.EventId, envelope.CausalEvent.StableSequence,
                    envelope.CausalEvent.Payload.EventTime, envelope.CausalEvent.ExchangeTime,
                    envelope.CausalEvent.ReceiveTime, envelope.CausalEvent.AvailabilityTime),
                CaptureEnvelope capture => (capture.StreamId, CaptureEventId(capture),
                    capture.StableSequence, capture.EventTime, capture.ExchangeTime,
                    capture.ReceiveTime, capture.AvailabilityTime),
                ILaneBCausalObservation observation => (observation.SourceId,
                    observation.EventId, observation.StableSequence, observation.EventTime,
                    observation.ExchangeTime, observation.ReceiveTime,
                    observation.AvailabilityTime),
                _ => throw new ArgumentException(
                    "Replay payload must be a market event, capture record, or Lane B observation.",
                    nameof(payload))
            };

        if (!StringComparer.Ordinal.Equals(sourceId, ordering.SourceId)
            || !StringComparer.Ordinal.Equals(eventId, ordering.EventId)
            || sequence != ordering.SourceSequence
            || eventTime != ordering.EventTime
            || exchangeTime != ordering.ExchangeTime
            || receiveTime != ordering.ReceiveTime
            || availabilityTime != ordering.AvailabilityTime)
        {
            throw new ArgumentException(
                "Replay payload causal fields do not match the manifest ordering entry.",
                nameof(payload));
        }
    }

    private static string CaptureEventId(CaptureEnvelope capture) =>
        capture.Payload?.EventId ?? capture.Provenance.SourceEventId
            ?? $"diagnostic:{capture.StreamId}:{capture.CaptureSequence}";
}

public sealed class MultiSourceReplayStepContext
{
    internal MultiSourceReplayStepContext(MultiSourceReplayRecord current,
        UtcTimestamp virtualTime, CausalEventCursor cursor,
        IEnumerable<MultiSourceReplayRecord> prefix,
        ImmutableValueList<MultiSourceDatasetSource> sources)
    {
        Current = current;
        VirtualTime = virtualTime;
        Cursor = cursor;
        Prefix = new ReadOnlyCollection<MultiSourceReplayRecord>(prefix.ToList());
        Sources = sources;
    }

    public MultiSourceReplayRecord Current { get; }
    public UtcTimestamp VirtualTime { get; }
    public CausalEventCursor Cursor { get; }
    public IReadOnlyList<MultiSourceReplayRecord> Prefix { get; }
    public ImmutableValueList<MultiSourceDatasetSource> Sources { get; }
}

public sealed class MultiSourceReplayResult
{
    internal MultiSourceReplayResult(IEnumerable<MultiSourceReplayStepContext> steps,
        IEnumerable<MultiSourceReplayDiagnostic> diagnostics, bool succeeded,
        IEnumerable<MultiSourceDatasetSource> sources)
    {
        Steps = new ReadOnlyCollection<MultiSourceReplayStepContext>(steps.ToList());
        Diagnostics = new ReadOnlyCollection<MultiSourceReplayDiagnostic>(diagnostics.ToList());
        Succeeded = succeeded;
        ReadinessReasons = new ReadOnlyCollection<string>(sources
            .SelectMany(ReadinessReasonsFor).Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<MultiSourceReplayStepContext> Steps { get; }
    public IReadOnlyList<MultiSourceReplayDiagnostic> Diagnostics { get; }
    public bool Succeeded { get; }
    public IReadOnlyList<string> ReadinessReasons { get; }
    public bool DecisionReadinessConfirmed => Succeeded && ReadinessReasons.Count == 0;

    private static IEnumerable<string> ReadinessReasonsFor(MultiSourceDatasetSource source)
    {
        if (source.Coverage != MultiSourceCoverage.Complete)
        {
            yield return $"source-coverage:{source.SourceId}:{source.Coverage}";
        }

        if (source.Quality != MultiSourceQuality.Ready)
        {
            if (source.QualityReasons.Count == 0)
            {
                yield return $"source-quality:{source.SourceId}:{source.Quality}";
            }
            else
            {
                foreach (string reason in source.QualityReasons)
                {
                    yield return $"source-quality:{source.SourceId}:{source.Quality}:{reason}";
                }
            }
        }
    }
}

/// <summary>Delivers multiple source lanes on their persisted shared causal clock.</summary>
public static class MultiSourceReplayRunner
{
    public static MultiSourceReplayResult Run(MultiSourceDatasetManifest manifest,
        IEnumerable<MultiSourceReplayRecord> records,
        Action<MultiSourceReplayStepContext> handler)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(handler);

        List<MultiSourceReplayDiagnostic> diagnostics = [];
        Dictionary<RecordIdentity, MultiSourceReplayRecord> unique = [];
        Dictionary<RecordIdentity, MultiSourceReplayOrderEntry> expected = manifest.Ordering
            .ToDictionary(static entry => RecordIdentity.Create(entry));
        foreach (MultiSourceReplayRecord? record in records)
        {
            if (record is null)
            {
                diagnostics.Add(new MultiSourceReplayDiagnostic(
                    MultiSourceReplayDiagnosticKind.InvalidRecord,
                    "A multi-source replay cannot contain a null record."));
                return Failed(diagnostics, manifest.Sources);
            }

            RecordIdentity identity = RecordIdentity.Create(record.Ordering);
            if (!expected.TryGetValue(identity, out MultiSourceReplayOrderEntry? entry)
                || entry != record.Ordering)
            {
                diagnostics.Add(new MultiSourceReplayDiagnostic(
                    MultiSourceReplayDiagnosticKind.InvalidRecord,
                    "A replay record has no matching manifest ordering entry.",
                    record.Ordering.SourceId, record.Ordering.EventId));
                return Failed(diagnostics, manifest.Sources);
            }

            if (unique.TryGetValue(identity, out MultiSourceReplayRecord? prior))
            {
                if (Equals(prior.Payload, record.Payload))
                {
                    diagnostics.Add(new MultiSourceReplayDiagnostic(
                        MultiSourceReplayDiagnosticKind.Duplicate,
                        "An exact source record was supplied more than once.",
                        record.Ordering.SourceId, record.Ordering.EventId));
                    continue;
                }

                diagnostics.Add(new MultiSourceReplayDiagnostic(
                    MultiSourceReplayDiagnosticKind.IdentityConflict,
                    "A manifest record identity has conflicting payloads.",
                    record.Ordering.SourceId, record.Ordering.EventId));
                return Failed(diagnostics, manifest.Sources);
            }

            unique.Add(identity, record);
        }

        foreach (MultiSourceReplayOrderEntry entry in manifest.Ordering)
        {
            if (!unique.ContainsKey(RecordIdentity.Create(entry)))
            {
                diagnostics.Add(new MultiSourceReplayDiagnostic(
                    MultiSourceReplayDiagnosticKind.MissingRecord,
                    "A manifest ordering entry has no replay payload.", entry.SourceId,
                    entry.EventId));
            }
        }

        if (diagnostics.Any(static item => item.Kind == MultiSourceReplayDiagnosticKind.MissingRecord))
        {
            return Failed(diagnostics, manifest.Sources);
        }

        MultiSourceReplayRecord[] ordered = unique.Values
            .OrderBy(AvailableAt)
            .ThenBy(static record => record.Ordering.EventTime.Value)
            .ThenBy(static record => record.Ordering.GlobalStableSequence)
            .ToArray();
        List<MultiSourceReplayStepContext> steps = [];
        DateTimeOffset lastVirtualTime = DateTimeOffset.MinValue;
        long lastSequence = 0;
        foreach (MultiSourceReplayRecord record in ordered)
        {
            DateTimeOffset availableAt = AvailableAt(record);
            if (availableAt < lastVirtualTime)
            {
                availableAt = lastVirtualTime;
            }

            lastVirtualTime = availableAt;
            long sequence = Math.Max(lastSequence,
                record.Ordering.GlobalStableSequence);
            CausalEventCursor cursor = new(new UtcTimestamp(availableAt), sequence);
            MultiSourceReplayRecord[] prefix = ordered
                .Where(item => item.Ordering.EventTime.Value <= availableAt
                    && item.Ordering.AvailabilityTime.Value <= availableAt
                    && item.Ordering.GlobalStableSequence <= sequence)
                .OrderBy(static item => item.Ordering.EventTime.Value)
                .ThenBy(static item => item.Ordering.GlobalStableSequence)
                .ToArray();
            MultiSourceReplayStepContext context = new(record, new UtcTimestamp(availableAt),
                cursor, prefix, manifest.Sources);
            try
            {
                handler(context);
                steps.Add(context);
                lastSequence = sequence;
            }
            catch (Exception exception)
            {
                diagnostics.Add(new MultiSourceReplayDiagnostic(
                    MultiSourceReplayDiagnosticKind.HandlerFailure,
                    $"Replay handler failed for '{record.Ordering.EventId}': {exception.Message}",
                    record.Ordering.SourceId, record.Ordering.EventId));
                return new MultiSourceReplayResult(steps, diagnostics, succeeded: false,
                    manifest.Sources);
            }
        }

        return new MultiSourceReplayResult(steps, diagnostics, succeeded: true,
            manifest.Sources);
    }

    private static MultiSourceReplayResult Failed(
        IEnumerable<MultiSourceReplayDiagnostic> diagnostics,
        IEnumerable<MultiSourceDatasetSource> sources) =>
        new([], diagnostics, succeeded: false, sources);

    private static DateTimeOffset AvailableAt(MultiSourceReplayRecord record)
    {
        MultiSourceReplayOrderEntry order = record.Ordering;
        DateTimeOffset availableAt = order.EventTime.Value;
        if (order.AvailabilityTime.Value > availableAt)
        {
            availableAt = order.AvailabilityTime.Value;
        }

        if (record.Payload is ReplayEnvelope { CausalEvent.Payload: Bar bar }
            && bar.CloseTime.Value > availableAt)
        {
            availableAt = bar.CloseTime.Value;
        }

        return availableAt;
    }

    private readonly record struct RecordIdentity(string SourceId, string EventId,
        long SourceSequence)
    {
        public static RecordIdentity Create(MultiSourceReplayOrderEntry entry) =>
            new(entry.SourceId, entry.EventId, entry.SourceSequence);
    }
}
