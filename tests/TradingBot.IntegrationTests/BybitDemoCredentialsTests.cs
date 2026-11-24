using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoCredentialsTests
{
    [Fact]
    public void MountedPairTakesPrecedenceAndAnIncompletePairNeverFallsBack()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bybit-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "bybit-demo-api-key"), "synthetic-mounted-key");
            File.WriteAllText(Path.Combine(directory, "bybit-demo-api-secret"), "synthetic-mounted-secret");
            BybitDemoCredentials pair = BybitDemoCredentials.Load(directory, "synthetic-environment-key", "synthetic-environment-secret");
            Assert.Equal("synthetic-mounted-key", pair.Key);
            Assert.Equal("synthetic-mounted-secret", pair.Secret);
            File.Delete(Path.Combine(directory, "bybit-demo-api-secret"));
            Assert.Throws<InvalidOperationException>(() => BybitDemoCredentials.Load(directory, "synthetic-environment-key", "synthetic-environment-secret"));
            File.WriteAllText(Path.Combine(directory, "bybit-demo-api-secret"), new string('x', 4097));
            Assert.Throws<InvalidOperationException>(() => BybitDemoCredentials.Load(directory, "synthetic-environment-key", "synthetic-environment-secret"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(null, "synthetic-secret")]
    [InlineData("synthetic-key", null)]
    [InlineData("synthetic-key", "has a space")]
    [InlineData("synthetic-key", "has\na-newline")]
    [InlineData("synthetic-key", "non-ascii-\u2603")]
    public void IncompleteOrMalformedEnvironmentPairFailsClosed(string? key, string? secret)
    {
        Assert.Throws<InvalidOperationException>(() => BybitDemoCredentials.Load(null, key, secret));
    }
}
