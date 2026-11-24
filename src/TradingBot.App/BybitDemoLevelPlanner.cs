using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Indicative exchange-tick-aligned levels calculated from an actual demo fill.</summary>
internal readonly record struct BybitDemoLevelPlan(decimal StopLoss, decimal TakeProfit1,
    decimal? TakeProfit2, decimal? StopAfterFirstTarget);

/// <summary>Rebases selected signal levels without submitting orders or modifying state.</summary>
internal static class BybitDemoLevelPlanner
{
    public static BybitDemoLevelPlan Create(SetupType family, Direction direction,
        decimal sourceReference, decimal sourceStop, decimal sourceTp1, decimal? sourceTp2,
        decimal averageFill, decimal tickSize)
    {
        bool flow = family is SetupType.FlowAtrRecovery or SetupType.FlowStochasticRecovery;
        if ((!flow && family != SetupType.DonchianFlowBreakout)
            || (flow != sourceTp2.HasValue)
            || direction is not (Direction.Long or Direction.Short)
            || sourceReference <= 0m || sourceStop <= 0m || sourceTp1 <= 0m
            || sourceTp2 is <= 0m || averageFill <= 0m || tickSize <= 0m
            || !Ordered(direction, sourceReference, sourceStop, sourceTp1, sourceTp2))
        {
            throw new ArgumentException("The source signal cannot define a demo execution plan.");
        }

        checked
        {
            decimal stop = Rebase(sourceStop, sourceReference, averageFill, tickSize,
                roundUp: direction == Direction.Long);
            decimal first = Rebase(sourceTp1, sourceReference, averageFill, tickSize,
                roundUp: direction == Direction.Short);
            decimal? second = sourceTp2 is { } target
                ? Rebase(target, sourceReference, averageFill, tickSize,
                    roundUp: direction == Direction.Short)
                : null;
            if (!Ordered(direction, averageFill, stop, first, second))
            {
                throw new ArgumentException("The instrument tick size makes the demo levels invalid.");
            }

            decimal? entryStop = flow
                ? RoundToTick(averageFill, tickSize, roundUp: direction == Direction.Long)
                : null;
            return new BybitDemoLevelPlan(stop, first, second, entryStop);
        }
    }

    private static decimal Rebase(decimal level, decimal reference, decimal fill,
        decimal tickSize, bool roundUp) =>
        RoundToTick(checked(fill * (level / reference)), tickSize, roundUp);

    private static decimal RoundToTick(decimal price, decimal tickSize, bool roundUp) =>
        checked((roundUp ? decimal.Ceiling(price / tickSize) : decimal.Floor(price / tickSize))
            * tickSize);

    private static bool Ordered(Direction direction, decimal reference, decimal stop,
        decimal first, decimal? second) => direction == Direction.Long
            ? stop > 0m && stop < reference && first > reference
                && (second is null || second > first)
            : first > 0m && first < reference && stop > reference
                && (second is null || second > 0m && second < first);
}
