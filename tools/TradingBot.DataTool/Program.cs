namespace TradingBot.DataTool;

/// <summary>Inert data-tool status used to verify tool composition.</summary>
public static class Bootstrap
{
    public static string StatusMessage => "TradingBot.DataTool scaffold ready.";

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
