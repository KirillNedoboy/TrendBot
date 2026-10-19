namespace TradingBot.Core;

/// <summary>
/// Shared decimal kernels used by rolling-v1 and price-context-v2. Keep the
/// operation order stable because the rolling-v1 outputs are a compatibility
/// contract.
/// </summary>
internal static class RollingIndicatorMath
{
    internal static decimal[] TrueRanges(ScreenerBar[] bars)
    {
        decimal[] ranges = new decimal[bars.Length];
        for (int index = 0; index < bars.Length; index++)
        {
            decimal previousClose = index == 0
                ? bars[index].Close.Value
                : bars[index - 1].Close.Value;
            decimal highLow = checked(bars[index].Bar.High.Value - bars[index].Bar.Low.Value);
            decimal highGap = Abs(checked(bars[index].Bar.High.Value - previousClose));
            decimal lowGap = Abs(checked(bars[index].Bar.Low.Value - previousClose));
            ranges[index] = Math.Max(highLow, Math.Max(highGap, lowGap));
        }

        return ranges;
    }

    internal static decimal RecursiveMean(IEnumerable<decimal> values, int period, bool isRma)
    {
        decimal[] snapshot = values.ToArray();
        decimal previous = Mean(snapshot[..period]);
        decimal divisor = isRma ? period : period + 1m;
        decimal factor = isRma ? 0m : checked(2m / divisor);
        for (int index = period; index < snapshot.Length; index++)
        {
            decimal adjustment = isRma
                ? checked((snapshot[index] - previous) / divisor)
                : checked(factor * (snapshot[index] - previous));
            previous = checked(previous + adjustment);
        }

        return previous;
    }

    internal static decimal Mean(IEnumerable<decimal> values)
    {
        decimal sum = 0m;
        int count = 0;
        foreach (decimal value in values)
        {
            sum = checked(sum + value);
            count = checked(count + 1);
        }

        return count == 0 ? throw new DivideByZeroException() : checked(sum / count);
    }

    internal static decimal WeightedMean(IEnumerable<decimal> values)
    {
        decimal numerator = 0m;
        decimal denominator = 0m;
        int weight = 0;
        foreach (decimal value in values)
        {
            weight = checked(weight + 1);
            numerator = checked(numerator + checked(value * weight));
            denominator = checked(denominator + weight);
        }

        return denominator == 0m
            ? throw new DivideByZeroException()
            : checked(numerator / denominator);
    }

    private static decimal Abs(decimal value) => value < 0m ? checked(-value) : value;
}
