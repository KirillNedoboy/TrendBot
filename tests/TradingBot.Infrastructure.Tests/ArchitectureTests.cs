using TradingBot.Infrastructure;

using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void InfrastructureReferencesCoreAndNoRuntimeModules()
    {
        var references = typeof(AssemblyMarker).Assembly.GetReferencedAssemblies();

        Assert.Contains(references, assembly => assembly.Name == "TradingBot.Core");
        Assert.DoesNotContain(references, assembly => assembly.Name is "TradingBot.Exchange.Binance" or "TradingBot.Backtesting" or "TradingBot.App");
    }
}
