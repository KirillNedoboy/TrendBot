namespace TradingBot.App;

/// <summary>Order quantities aligned to current exchange lot limits.</summary>
internal readonly record struct BybitDemoQuantityPlan(decimal EntryQuantity,
    decimal FirstTargetQuantity, decimal RemainderQuantity);

internal static class BybitDemoQuantityPlanner
{
    public static BybitDemoQuantityPlan Create(decimal targetNotional, decimal referencePrice,
        decimal quantityStep, decimal minimumQuantity, decimal minimumNotional,
        decimal maximumMarketQuantity, bool splitFirstTarget)
    {
        if (targetNotional <= 0m || referencePrice <= 0m || quantityStep <= 0m
            || minimumQuantity <= 0m || minimumNotional < 0m
            || maximumMarketQuantity < minimumQuantity)
        {
            throw new ArgumentException("The Bybit demo sizing inputs are invalid.");
        }

        checked
        {
            decimal quantity = decimal.Floor(targetNotional / referencePrice / quantityStep) * quantityStep;
            if (quantity < minimumQuantity || quantity > maximumMarketQuantity
                || quantity * referencePrice < minimumNotional)
            {
                throw new ArgumentException("The target notional cannot satisfy the Bybit quantity limits.");
            }
            if (!splitFirstTarget)
            {
                return new BybitDemoQuantityPlan(quantity, quantity, 0m);
            }

            decimal first = decimal.Floor(quantity / quantityStep / 2m) * quantityStep;
            decimal remainder = quantity - first;
            if (first < minimumQuantity || remainder < minimumQuantity)
            {
                throw new ArgumentException("The position cannot be split into two valid quantities.");
            }
            return new BybitDemoQuantityPlan(quantity, first, remainder);
        }
    }
}
