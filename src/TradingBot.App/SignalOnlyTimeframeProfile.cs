using TradingBot.Core;

namespace TradingBot.App;

internal sealed record SignalOnlyTimeframeProfile(
    PublicSignalProfileConfiguration Profile, SignalScenarioPolicySet ScenarioPolicies);

internal static class SignalOnlyStreamId
{
    public static string For(string symbol, string setupTimeframe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (PublicSignalTimeframes.ContextForSetup(setupTimeframe) is null)
        {
            throw new ArgumentOutOfRangeException(nameof(setupTimeframe));
        }
        return $"binance-usdm-native-kline:{symbol.ToUpperInvariant()}"
            + (setupTimeframe == "15m" ? string.Empty : $":{setupTimeframe}");
    }
}
