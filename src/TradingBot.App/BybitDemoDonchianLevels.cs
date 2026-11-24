using System.Globalization;
using TradingBot.Core;

namespace TradingBot.App;

internal readonly record struct BybitDemoDonchianLevels(decimal Stop, decimal Target);

/// <summary>Reads unambiguous model levels from committed Donchian evidence.</summary>
internal static class BybitDemoDonchianLevelReader
{
    public static bool TryRead(IEnumerable<string> reasons, Direction direction, decimal reference,
        out BybitDemoDonchianLevels levels)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        levels = default;
        if (direction is not (Direction.Long or Direction.Short) || reference <= 0m)
        {
            return false;
        }
        string? stopPeriod = null;
        string? targetPeriod = null;
        string? stop = null;
        string? target = null;
        foreach (string reason in reasons)
        {
            if (reason is null)
            {
                return false;
            }
            if (!Capture(reason, "model-stop-period:", ref stopPeriod)
                || !Capture(reason, "model-target-period:", ref targetPeriod)
                || !Capture(reason, "model-stop:", ref stop)
                || !Capture(reason, "model-target:", ref target))
            {
                return false;
            }
        }
        if (stopPeriod is not ("13" or "21") || targetPeriod is not ("89" or "144")
            || !decimal.TryParse(stop, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal stopPrice)
            || !decimal.TryParse(target, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal targetPrice)
            || stopPrice <= 0m || targetPrice <= 0m
            || (direction == Direction.Long ? stopPrice >= reference || targetPrice <= reference
                : stopPrice <= reference || targetPrice >= reference))
        {
            return false;
        }
        levels = new BybitDemoDonchianLevels(stopPrice, targetPrice);
        return true;
    }

    private static bool Capture(string reason, string prefix, ref string? result)
    {
        if (!reason.StartsWith(prefix, StringComparison.Ordinal))
        {
            return true;
        }
        if (result is not null)
        {
            return false;
        }
        result = reason[prefix.Length..];
        return true;
    }
}
