namespace TradingBot.Core;

/// <summary>Supported native setup intervals and their closed higher-timeframe context.</summary>
public static class PublicSignalTimeframes
{
    public static string? ContextForSetup(string? setup) => setup switch
    {
        "5m" => "15m",
        "15m" => "1h",
        "30m" => "1h",
        "1h" => "4h",
        _ => null
    };

    public static bool TryGetDurationMilliseconds(string? timeframe, out long duration)
    {
        duration = timeframe switch
        {
            "5m" => 300_000,
            "15m" => 900_000,
            "30m" => 1_800_000,
            "1h" => 3_600_000,
            "4h" => 14_400_000,
            _ => 0
        };
        return duration > 0;
    }
}
