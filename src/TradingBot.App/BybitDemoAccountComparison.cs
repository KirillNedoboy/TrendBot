using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal enum BybitDemoAccountComparisonResult
{
    NoMismatchObserved,
    UnresolvedLocalOrder,
    UnknownPosition,
    PositionMismatch,
    MissingProtection,
    UnknownOrder
}

/// <summary>Compares sampled account data only; it does not authorize trading or prove stream continuity.</summary>
internal static class BybitDemoAccountComparison
{
    public static BybitDemoAccountComparisonResult Compare(
        IReadOnlyList<BybitDemoReservationSnapshot> local,
        BybitDemoPositionSnapshot exchangePositions,
        BybitDemoOpenOrderSnapshot exchangeOrders,
        decimal maximumLeverage)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(exchangePositions);
        ArgumentNullException.ThrowIfNull(exchangeOrders);
        if (maximumLeverage is < 1m or > 10m)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLeverage));
        }
        Dictionary<string, BybitDemoReservationSnapshot> protectedBySymbol = new(StringComparer.Ordinal);
        foreach (BybitDemoReservationSnapshot entry in local)
        {
            if (entry.State == BybitDemoReservationState.Reserved)
            {
                continue;
            }
            if (entry.State != BybitDemoReservationState.Protected
                || entry.PositionQuantity is not > 0m || entry.AverageEntry is not > 0m
                || entry.StopLoss is not > 0m || string.IsNullOrEmpty(entry.ExchangeOrderId)
                || !protectedBySymbol.TryAdd(entry.Reservation.Symbol, entry))
            {
                return BybitDemoAccountComparisonResult.UnresolvedLocalOrder;
            }
        }
        HashSet<string> seenPositions = new(StringComparer.Ordinal);
        foreach (BybitDemoAccountPosition position in exchangePositions.Positions)
        {
            if (!protectedBySymbol.TryGetValue(position.Symbol, out BybitDemoReservationSnapshot? entry))
            {
                return BybitDemoAccountComparisonResult.UnknownPosition;
            }
            if (!seenPositions.Add(position.Symbol) || position.PositionIndex != 0
                || position.Direction != entry.Reservation.Direction || position.Quantity != entry.PositionQuantity
                || position.AverageEntry != entry.AverageEntry || position.StopLoss != entry.StopLoss
                || position.Leverage is null or < 1m || position.Leverage > maximumLeverage
                || position.Status != "Normal" || position.ReduceOnly)
            {
                return BybitDemoAccountComparisonResult.PositionMismatch;
            }
        }
        if (seenPositions.Count != protectedBySymbol.Count)
        {
            return BybitDemoAccountComparisonResult.PositionMismatch;
        }
        HashSet<string> protectedSymbols = new(StringComparer.Ordinal);
        foreach (BybitDemoOpenOrder order in exchangeOrders.Orders)
        {
            if (!protectedBySymbol.TryGetValue(order.Symbol, out BybitDemoReservationSnapshot? entry)
                || !StringComparer.Ordinal.Equals(order.ParentClientOrderId, entry.Reservation.ClientOrderId)
                || order.PositionIndex != 0 || order.Status != "Untriggered" || order.StopOrderType != "StopLoss"
                || order.Side == entry.Reservation.Direction
                || order.Side is not (Direction.Long or Direction.Short)
                || order.Quantity != entry.PositionQuantity || order.TriggerPrice != entry.StopLoss || order.TriggerBy != "LastPrice"
                || !order.ReduceOnly || !order.CloseOnTrigger || !protectedSymbols.Add(order.Symbol))
            {
                return BybitDemoAccountComparisonResult.UnknownOrder;
            }
        }
        return protectedSymbols.Count == protectedBySymbol.Count
            ? BybitDemoAccountComparisonResult.NoMismatchObserved
            : BybitDemoAccountComparisonResult.MissingProtection;
    }
}
