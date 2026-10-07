using TradingBot.App;

using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class CompositionTests
{
    [Fact]
    public void AppStartupIsInertAndComposed()
    {
        Assert.Equal("TradingBot.App scaffold ready.", Bootstrap.StatusMessage);

        var references = typeof(Bootstrap).Assembly.GetReferencedAssemblies();
        Assert.Contains(references, assembly => assembly.Name == "TradingBot.Core");
        Assert.Contains(references, assembly => assembly.Name == "TradingBot.Exchange.Binance");
        Assert.Contains(references, assembly => assembly.Name == "TradingBot.Infrastructure");
        Assert.Contains(references, assembly => assembly.Name == "TradingBot.Backtesting");
    }
}
