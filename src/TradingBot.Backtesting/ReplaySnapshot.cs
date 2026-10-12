using System.Collections.ObjectModel;
using TradingBot.Core;

namespace TradingBot.Backtesting;

/// <summary>Immutable, causally filtered view of one persisted replay stream.</summary>
public sealed class ReplayStreamSnapshot
{
    internal ReplayStreamSnapshot(string streamId, IEnumerable<ReplayEnvelope> events)
    {
        StreamId = streamId;
        ReadOnlyCollection<ReplayEnvelope> snapshot = new(events.ToList());
        Events = snapshot;
        Envelopes = snapshot;
    }

    /// <summary>Gets the single stream represented by this snapshot.</summary>
    public string StreamId { get; }

    /// <summary>Gets the eligible envelopes ordered by event time and stable sequence.</summary>
    public IReadOnlyList<ReplayEnvelope> Events { get; }

    /// <summary>Gets the same immutable envelope view using persistence terminology.</summary>
    public IReadOnlyList<ReplayEnvelope> Envelopes { get; }
}

/// <summary>Builds deterministic, no-lookahead snapshots from one replay stream.</summary>
public static class ReplaySnapshotBuilder
{
    /// <summary>
    /// Filters envelopes by causal visibility before ordering them by event time and
    /// stable processing sequence.
    /// </summary>
    public static ReplayStreamSnapshot Build(string streamId,
        IEnumerable<ReplayEnvelope> envelopes, CausalEventCursor cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(envelopes);
        ArgumentNullException.ThrowIfNull(cursor);

        List<ReplayEnvelope> source = envelopes.ToList();
        if (source.Any(static envelope => envelope is null))
        {
            throw new ArgumentException("A replay stream cannot contain a null envelope.",
                nameof(envelopes));
        }

        if (source.Any(envelope => !StringComparer.Ordinal.Equals(envelope.StreamId, streamId)))
        {
            throw new ArgumentException(
                "Every replay envelope must belong to the requested stream.", nameof(envelopes));
        }

        IEnumerable<ReplayEnvelope> ordered = source
            .Where(envelope => cursor.IsEligible(envelope.CausalEvent))
            .OrderBy(static envelope => envelope.CausalEvent,
                Comparer<CausalMarketEvent>.Create(CausalMarketEventOrdering.Compare));

        return new ReplayStreamSnapshot(streamId, ordered);
    }
}
