namespace TradingBot.App;

/// <summary>Inert startup status used to verify application composition.</summary>
public static class Bootstrap
{
    public static string StatusMessage => "TradingBot.App scaffold ready.";

    public static int ComposedModuleCount =>
        new[]
        {
            typeof(TradingBot.Core.AssemblyMarker),
            typeof(TradingBot.Exchange.Binance.AssemblyMarker),
            typeof(TradingBot.Infrastructure.AssemblyMarker),
            typeof(TradingBot.Backtesting.AssemblyMarker),
        }.Length;

    public static int Run()
    {
        Console.WriteLine(StatusMessage);
        return 0;
    }
}

internal static class Program
{
    public static int Main() => Bootstrap.Run();
}
