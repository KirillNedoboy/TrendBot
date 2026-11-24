using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal readonly record struct BybitDemoExitPlan(string SignalId, SetupType Family, Direction Direction,
    decimal PositionQuantity, decimal AverageEntry, decimal StopLoss, decimal FirstTarget,
    decimal FirstQuantity, decimal? SecondTarget, decimal SecondQuantity, decimal? StopAfterFirstTarget);

/// <summary>Builds target quantities and prices from an actual terminal entry fill.</summary>
internal static class BybitDemoExitPlanner
{
    public static BybitDemoExitPlan Create(BybitDemoSignalCandidate signal, BybitDemoInstrument instrument,
        decimal actualQuantity, decimal actualAverageEntry)
    {
        if (signal.SignalId is not { Length: 64 }
            || signal.SignalId.Any(static c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || string.IsNullOrEmpty(signal.Symbol) || signal.Symbol != instrument.Symbol
            || actualQuantity <= 0m || actualAverageEntry <= 0m
            || instrument.QuantityStep <= 0m || instrument.MinimumQuantity <= 0m
            || actualQuantity < instrument.MinimumQuantity || actualQuantity > instrument.MaximumMarketQuantity
            || actualQuantity % instrument.QuantityStep != 0m)
        {
            throw new ArgumentException("The filled demo position does not satisfy the instrument limits.");
        }
        BybitDemoLevelPlan levels = BybitDemoLevelPlanner.Create(signal.Family, signal.Direction,
            signal.ReferencePrice, signal.Stop, signal.FirstTarget, signal.SecondTarget,
            actualAverageEntry, instrument.TickSize);
        if (!WithinFilter(actualAverageEntry, instrument) || !WithinFilter(levels.StopLoss, instrument)
            || !WithinFilter(levels.TakeProfit1, instrument)
            || levels.TakeProfit2 is { } second && !WithinFilter(second, instrument)
            || levels.StopAfterFirstTarget is { } breakEven && !WithinFilter(breakEven, instrument))
        {
            throw new ArgumentException("The filled demo position produces invalid price levels.");
        }
        decimal firstQuantity = actualQuantity;
        decimal secondQuantity = 0m;
        if (signal.Family is SetupType.FlowAtrRecovery or SetupType.FlowStochasticRecovery)
        {
            firstQuantity = decimal.Floor(actualQuantity / instrument.QuantityStep / 2m) * instrument.QuantityStep;
            secondQuantity = actualQuantity - firstQuantity;
            if (firstQuantity < instrument.MinimumQuantity || secondQuantity < instrument.MinimumQuantity)
            {
                throw new ArgumentException("The actual partial fill cannot be split into valid target quantities.");
            }
        }
        return new BybitDemoExitPlan(signal.SignalId, signal.Family, signal.Direction, actualQuantity,
            actualAverageEntry, levels.StopLoss, levels.TakeProfit1, firstQuantity,
            levels.TakeProfit2, secondQuantity, levels.StopAfterFirstTarget);
    }

    public static BybitDemoExitPlan PersistProtected(SqliteBybitDemoReservationStore store,
        BybitDemoSignalCandidate signal, BybitDemoInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(store);
        BybitDemoReservationSnapshot position = store.ReadActive().SingleOrDefault(
            item => item.Reservation.SignalId == signal.SignalId)
            ?? throw new InvalidOperationException("The demo position does not exist.");
        BybitDemoReservation reservation = position.Reservation;
        if (position.State != BybitDemoReservationState.Protected
            || position.PositionQuantity is not { } quantity || position.AverageEntry is not { } entry
            || reservation.SignalRecordHash != signal.RecordHash || reservation.Symbol != signal.Symbol
            || reservation.Family != signal.Family || reservation.Timeframe != signal.Timeframe
            || reservation.Direction != signal.Direction)
        {
            throw new InvalidOperationException("The signal does not match a protected demo position.");
        }
        BybitDemoExitPlan plan = Create(signal, instrument, quantity, entry);
        store.RecordExitPlan(new BybitDemoStoredExitPlan(plan.SignalId, plan.PositionQuantity, plan.AverageEntry,
            plan.StopLoss, plan.FirstTarget, plan.FirstQuantity, plan.SecondTarget, plan.SecondQuantity,
            plan.StopAfterFirstTarget, instrument.TickSize, instrument.QuantityStep, instrument.MinimumQuantity));
        return plan;
    }

    private static bool WithinFilter(decimal price, BybitDemoInstrument instrument) =>
        instrument.MinimumPrice > 0m && instrument.MaximumPrice >= instrument.MinimumPrice
        && price >= instrument.MinimumPrice && price <= instrument.MaximumPrice;
}
