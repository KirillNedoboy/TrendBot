using TradingBot.Exchange.Binance;

using Xunit;

namespace TradingBot.Exchange.Binance.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void ExchangeReferencesCoreOnly()
    {
        var references = typeof(AssemblyMarker).Assembly.GetReferencedAssemblies();

        Assert.Contains(references, assembly => assembly.Name == "TradingBot.Core");
        Assert.DoesNotContain(references, assembly => assembly.Name is "TradingBot.Infrastructure" or "TradingBot.Backtesting" or "TradingBot.App");
    }
}
