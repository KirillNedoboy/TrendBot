using TradingBot.Core;

namespace TradingBot.Backtesting;

internal static class ReplayCausalVisibility
{
    public static bool IsEligible(UtcTimestamp eventTime, UtcTimestamp availabilityTime,
        long stableSequence, UtcTimestamp? closeTime, CausalEventCursor cursor)
    {
        DateTimeOffset asOf = cursor.AsOf.Value;
        return eventTime.Value <= asOf
            && availabilityTime.Value <= asOf
            && (closeTime is null || closeTime.Value <= asOf)
            && stableSequence <= cursor.MaxStableSequence;
    }

    public static DateTimeOffset AvailableAt(UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, UtcTimestamp? closeTime)
    {
        DateTimeOffset availableAt = eventTime.Value;
        if (availabilityTime.Value > availableAt)
        {
            availableAt = availabilityTime.Value;
        }

        if (closeTime is not null && closeTime.Value > availableAt)
        {
            availableAt = closeTime.Value;
        }

        return availableAt;
    }
}
