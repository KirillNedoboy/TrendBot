namespace TradingBot.Backtesting;

/// <summary>Technical marker for replay and backtesting during bootstrap.</summary>
public static class AssemblyMarker
{
    public static Type CoreAssembly => typeof(TradingBot.Core.AssemblyMarker);
}
