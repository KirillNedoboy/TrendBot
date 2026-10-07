namespace TradingBot.Exchange.Binance;

/// <summary>Technical marker for the exchange boundary during bootstrap.</summary>
public static class AssemblyMarker
{
    public static Type CoreAssembly => typeof(TradingBot.Core.AssemblyMarker);
}
