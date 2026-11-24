using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal enum BybitDemoPlanningStatus
{
    Ready,
    InvalidInput,
    InstrumentUnavailable,
    PositionNotFlat,
    SafetyRejected,
    InvalidLevelsOrQuantity
}

internal readonly record struct BybitDemoEntryInputs(BybitDemoSignalCandidate Signal,
    BybitDemoPublicQuote Quote, BybitDemoInstrument Instrument, BybitDemoAccountInfo Account,
    BybitDemoPositionInfo Position, BybitDemoAccountComparisonResult AccountComparison,
    bool PrivateStreamReady, bool PrivateStreamGapFree, DateTimeOffset PrivateConfirmedAt,
    DateTimeOffset Now);

internal readonly record struct BybitDemoPreparedEntry(BybitDemoReservation Reservation,
    BybitDemoLevelPlan IndicativeLevels, BybitDemoQuantityPlan Quantities,
    BybitDemoSignalCandidate Source);

/// <summary>Prepares a demo entry; no order is authorized or sent by this calculation.</summary>
internal static class BybitDemoEntryPlanner
{
    public static BybitDemoPlanningStatus TryPrepare(BybitDemoConfiguration configuration,
        BybitDemoEntryInputs input, out BybitDemoPreparedEntry prepared, out BybitDemoEntryBlock safetyReason)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        prepared = default;
        safetyReason = BybitDemoEntryBlock.None;
        BybitDemoSignalCandidate signal = input.Signal;
        if (!Hash(signal.SignalId) || !Hash(signal.RecordHash) || string.IsNullOrEmpty(signal.Symbol)
            || signal.Symbol.Length > 20 || signal.Symbol.Any(static c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            || signal.Direction is not (Direction.Long or Direction.Short) || !Selected(signal.Family, signal.Timeframe)
            || input.Now == default || input.Now.Offset != TimeSpan.Zero)
        {
            return BybitDemoPlanningStatus.InvalidInput;
        }
        if (input.Quote.Symbol != signal.Symbol || input.Instrument.Symbol != signal.Symbol
            || input.Position.Symbol != signal.Symbol || input.Instrument.ObservedAt == default
            || input.Instrument.ObservedAt.Offset != TimeSpan.Zero || input.Instrument.ObservedAt > input.Now
            || input.Now - input.Instrument.ObservedAt > TimeSpan.FromSeconds(configuration.MaximumQuoteAgeSeconds)
            || input.Instrument.MaximumLeverage < input.Position.Leverage)
        {
            return BybitDemoPlanningStatus.InstrumentUnavailable;
        }
        if (input.Position.Quantity != 0m || input.Position.Direction is not null || input.Position.StopLoss is not null)
        {
            return BybitDemoPlanningStatus.PositionNotFlat;
        }
        BybitDemoEntryObservation observation = new(input.Now, signal.DecisionAt, signal.ReferencePrice,
            input.Quote.ExchangeAt, input.Quote.ReceivedAt, input.Quote.Bid, input.Quote.Ask,
            input.PrivateConfirmedAt, input.PrivateStreamReady,
            input.PrivateStreamGapFree && input.AccountComparison == BybitDemoAccountComparisonResult.NoMismatchObserved,
            true, input.Account.MarginMode, input.Account.ObservedAt, input.Position.Leverage,
            input.Position.ObservedAt, input.Position.ReduceOnly);
        safetyReason = BybitDemoEntryGuard.Check(configuration, signal.Direction, observation);
        if (safetyReason != BybitDemoEntryBlock.None)
        {
            return BybitDemoPlanningStatus.SafetyRejected;
        }
        decimal quote = signal.Direction == Direction.Long ? input.Quote.Ask : input.Quote.Bid;
        try
        {
            BybitDemoLevelPlan levels = BybitDemoLevelPlanner.Create(signal.Family, signal.Direction,
                signal.ReferencePrice, signal.Stop, signal.FirstTarget, signal.SecondTarget, quote, input.Instrument.TickSize);
            if (!WithinPriceFilter(quote, input.Instrument) || !WithinPriceFilter(levels.StopLoss, input.Instrument)
                || !WithinPriceFilter(levels.TakeProfit1, input.Instrument)
                || levels.TakeProfit2 is { } second && !WithinPriceFilter(second, input.Instrument))
            {
                return BybitDemoPlanningStatus.InvalidLevelsOrQuantity;
            }
            bool split = signal.Family is SetupType.FlowAtrRecovery or SetupType.FlowStochasticRecovery;
            BybitDemoQuantityPlan quantities = BybitDemoQuantityPlanner.Create(configuration.TargetNotionalUsdt,
                quote, input.Instrument.QuantityStep, input.Instrument.MinimumQuantity, input.Instrument.MinimumNotional,
                input.Instrument.MaximumMarketQuantity, split);
            BybitDemoReservation reservation = new(signal.SignalId, signal.RecordHash, signal.Symbol,
                "bd-" + signal.SignalId[..32], signal.Family, signal.Timeframe, signal.Direction,
                quantities.EntryQuantity, quote, levels.StopLoss);
            prepared = new BybitDemoPreparedEntry(reservation, levels, quantities, signal);
            return BybitDemoPlanningStatus.Ready;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException or DivideByZeroException)
        {
            return BybitDemoPlanningStatus.InvalidLevelsOrQuantity;
        }
    }

    private static bool WithinPriceFilter(decimal price, BybitDemoInstrument instrument) =>
        instrument.MinimumPrice > 0m && instrument.MaximumPrice >= instrument.MinimumPrice
        && price >= instrument.MinimumPrice && price <= instrument.MaximumPrice;

    private static bool Selected(SetupType family, string timeframe) => family switch
    {
        SetupType.FlowAtrRecovery => timeframe is "15m" or "30m",
        SetupType.FlowStochasticRecovery => timeframe == "15m",
        SetupType.DonchianFlowBreakout => timeframe is "30m" or "1h",
        _ => false
    };

    private static bool Hash(string? value) => value is { Length: 64 }
        && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
