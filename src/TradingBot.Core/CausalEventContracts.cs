using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Carries a market event with the local times and stable sequence needed to
/// reconstruct its causal processing order.
/// </summary>
public sealed record CausalMarketEvent
{
    public MarketEvent Payload { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public UtcTimestamp? ExchangeTime { get; }

    /// <summary>
    /// Creates a causal event envelope. Stable sequence assignment and its
    /// uniqueness within a processing stream belong to the recording boundary.
    /// </summary>
    [JsonConstructor]
    public CausalMarketEvent(MarketEvent payload, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, long stableSequence,
        UtcTimestamp? exchangeTime = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException(
                "Availability time cannot precede receive time.", nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Stable sequence cannot be negative.");
        }

        Payload = payload;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        ExchangeTime = exchangeTime;
    }
}

/// <summary>Defines the causal visibility boundary for an event view.</summary>
public sealed record CausalEventCursor
{
    public UtcTimestamp AsOf { get; }

    public long MaxStableSequence { get; }

    [JsonConstructor]
    public CausalEventCursor(UtcTimestamp asOf, long maxStableSequence)
    {
        ArgumentNullException.ThrowIfNull(asOf);
        if (maxStableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStableSequence), maxStableSequence,
                "Maximum stable sequence cannot be negative.");
        }

        AsOf = asOf;
        MaxStableSequence = maxStableSequence;
    }

    /// <summary>
    /// Returns whether the event was visible at this cursor, including all
    /// boundary values and without using receive time as event time.
    /// </summary>
    public bool IsEligible(CausalMarketEvent candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate.Payload.EventTime.Value.CompareTo(AsOf.Value) <= 0
            && candidate.AvailabilityTime.Value.CompareTo(AsOf.Value) <= 0
            && candidate.StableSequence.CompareTo(MaxStableSequence) <= 0;
    }

    /// <summary>Checks an event against a causal cursor without side effects.</summary>
    public static bool IsEligible(CausalMarketEvent candidate, CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return cursor.IsEligible(candidate);
    }
}

/// <summary>Compares causal events after eligibility has been established.</summary>
public static class CausalMarketEventOrdering
{
    /// <summary>
    /// Compares event time first and stable sequence second. A zero result only
    /// means that both ordering keys are equal.
    /// </summary>
    public static int Compare(CausalMarketEvent left, CausalMarketEvent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        int eventTimeComparison = left.Payload.EventTime.Value.CompareTo(
            right.Payload.EventTime.Value);
        return eventTimeComparison != 0
            ? eventTimeComparison
            : left.StableSequence.CompareTo(right.StableSequence);
    }

    /// <summary>Checks an event against a causal cursor without side effects.</summary>
    public static bool IsEligible(CausalMarketEvent candidate, CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return cursor.IsEligible(candidate);
    }
}
