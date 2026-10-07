namespace TradingBot.Infrastructure;

/// <summary>Technical marker for infrastructure during bootstrap.</summary>
public static class AssemblyMarker
{
    public static Type CoreAssembly => typeof(TradingBot.Core.AssemblyMarker);
}
