using TradingBot.Core;

namespace TradingBot.App;

internal enum BybitDemoEntryBlock
{
    None,
    InvalidInput,
    StaleSignal,
    StaleQuote,
    PrivateStateUnavailable,
    AccountNotIsolated,
    LeverageOutsideLimit,
    InstrumentUnavailable,
    PriceDifferenceExceeded
}

internal readonly record struct BybitDemoEntryObservation(
    DateTimeOffset Now, DateTimeOffset SignalAt, decimal SignalPrice,
    DateTimeOffset QuoteExchangeAt, DateTimeOffset QuoteReceivedAt, decimal Bid, decimal Ask,
    DateTimeOffset PrivateConfirmedAt, bool PrivateStreamReady, bool Reconciled,
    bool InstrumentTradingLinearUsdtPerpetual, string MarginMode, DateTimeOffset AccountObservedAt,
    decimal Leverage, DateTimeOffset PositionObservedAt, bool PositionReduceOnly);

/// <summary>Admission checks over observed data; this method never sends orders.</summary>
internal static class BybitDemoEntryGuard
{
    public static BybitDemoEntryBlock Check(BybitDemoConfiguration configuration,
        Direction direction, BybitDemoEntryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (direction is not (Direction.Long or Direction.Short)
            || observation.SignalPrice <= 0m || observation.Bid <= 0m
            || observation.Ask <= 0m || observation.Bid > observation.Ask
            || !Utc(observation.Now) || !Utc(observation.SignalAt)
            || !Utc(observation.QuoteExchangeAt) || !Utc(observation.QuoteReceivedAt)
            || !Utc(observation.PrivateConfirmedAt) || !Utc(observation.AccountObservedAt)
            || !Utc(observation.PositionObservedAt))
        {
            return BybitDemoEntryBlock.InvalidInput;
        }
        if (!Fresh(observation.Now, observation.SignalAt, configuration.MaximumSignalAgeSeconds))
        {
            return BybitDemoEntryBlock.StaleSignal;
        }
        if (observation.QuoteExchangeAt > observation.QuoteReceivedAt
            || !Fresh(observation.Now, observation.QuoteExchangeAt, configuration.MaximumQuoteAgeSeconds)
            || !Fresh(observation.Now, observation.QuoteReceivedAt, configuration.MaximumQuoteAgeSeconds))
        {
            return BybitDemoEntryBlock.StaleQuote;
        }
        if (!observation.PrivateStreamReady || !observation.Reconciled
            || !Fresh(observation.Now, observation.PrivateConfirmedAt, configuration.PrivateStreamFreshnessSeconds)
            || !Fresh(observation.Now, observation.AccountObservedAt, configuration.PrivateStreamFreshnessSeconds)
            || !Fresh(observation.Now, observation.PositionObservedAt, configuration.PrivateStreamFreshnessSeconds)
            || observation.PositionReduceOnly)
        {
            return BybitDemoEntryBlock.PrivateStateUnavailable;
        }
        if (!StringComparer.Ordinal.Equals(observation.MarginMode, "ISOLATED_MARGIN"))
        {
            return BybitDemoEntryBlock.AccountNotIsolated;
        }
        if (observation.Leverage < 1m || observation.Leverage > configuration.MaximumLeverage)
        {
            return BybitDemoEntryBlock.LeverageOutsideLimit;
        }
        if (!observation.InstrumentTradingLinearUsdtPerpetual)
        {
            return BybitDemoEntryBlock.InstrumentUnavailable;
        }
        decimal executableQuote = direction == Direction.Long ? observation.Ask : observation.Bid;
        try
        {
            if (checked(decimal.Abs(executableQuote / observation.SignalPrice - 1m) * 100m)
                > configuration.MaximumPriceDifferencePercent)
            {
                return BybitDemoEntryBlock.PriceDifferenceExceeded;
            }
        }
        catch (OverflowException)
        {
            return BybitDemoEntryBlock.InvalidInput;
        }
        return BybitDemoEntryBlock.None;
    }

    private static bool Utc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool Fresh(DateTimeOffset now, DateTimeOffset observed, int limitSeconds) =>
        observed <= now && now - observed <= TimeSpan.FromSeconds(limitSeconds);
}
