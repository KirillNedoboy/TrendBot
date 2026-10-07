using TradingBot.Backtesting;

using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void BacktestingReferencesCoreAndNoLiveModules()
    {
        var references = typeof(AssemblyMarker).Assembly.GetReferencedAssemblies();

        Assert.Contains(references, assembly => assembly.Name == "TradingBot.Core");
        Assert.DoesNotContain(references, assembly => assembly.Name is "TradingBot.Exchange.Binance" or "TradingBot.Infrastructure" or "TradingBot.App");
    }
}
