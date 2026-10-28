using System.Text.Json;
using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LocalPaperConfigurationTests
{
    [Fact]
    public void ConfigurationRoundTripsWithoutChangingDecimalValues()
    {
        LocalPaperConfiguration configuration = CreateConfiguration();

        LocalPaperConfiguration restored = LocalPaperConfiguration.Parse(
            configuration.ToJson());

        Assert.Equal(configuration, restored);
        Assert.Equal(12345.6789012345678901234567m,
            restored.InitialEquity.Amount);
        Assert.Equal(0.0123456789012345678901234567m,
            restored.RiskFraction);
        Assert.Equal(0.0001234567890123456789012345m,
            restored.FillAssumptions.FeePerQuantityUnit);
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("apiKey")]
    [InlineData("endpoint")]
    [InlineData("activateRealOrders")]
    [InlineData("unknownSetting")]
    public void ConfigurationRejectsUnknownOrActivationFields(string field)
    {
        string json = CreateConfiguration().ToJson();
        int insertion = json.LastIndexOf('}');
        json = json.Insert(insertion, $",\"{field}\":\"secret-marker\"");

        JsonException exception = Assert.Throws<JsonException>(
            () => LocalPaperConfiguration.Parse(json));

        Assert.DoesNotContain("secret-marker", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsDuplicatesAndUnsupportedModes()
    {
        string json = CreateConfiguration().ToJson();

        Assert.Throws<JsonException>(() => LocalPaperConfiguration.Parse(
            json.Insert(json.IndexOf('{') + 1, "\"mode\":\"local-paper\",")));
        Assert.Throws<JsonException>(() => LocalPaperConfiguration.Parse(
            json.Replace("local-paper", "production", StringComparison.Ordinal)));

        Assert.Throws<JsonException>(() => LocalPaperConfiguration.Parse(
            json.Replace("\"provenance\": \"explicit test fixture\"",
                "\"provenance\": \"duplicate\",\n      \"provenance\": \"explicit test fixture\"",
                StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => LocalPaperConfiguration.Parse(
            json.Replace("\"riskFraction\": 0.0123456789012345678901234567,", "",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void ConfigurationFileLoadOnlyReadsAndDoesNotCreateOtherState()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "paper.json");
        try
        {
            File.WriteAllText(path, CreateConfiguration().ToJson());
            LocalPaperConfiguration loaded = LocalPaperConfiguration.Load(path);
            Assert.Equal(CreateConfiguration(), loaded);
            Assert.Equal(["paper.json"], Directory.GetFiles(directory)
                .Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalPaperConfiguration CreateConfiguration() => new(
        "paper-profile-1", new Money(12345.6789012345678901234567m, "USDT"),
        0.0123456789012345678901234567m,
        new RiskEvaluationConfiguration(RiskEvaluationVersion.Current,
            LinearPerpetualRiskMathCalculator.CurrentVersion, null, null,
            "explicit test fixture"),
        new SimulatedFillAssumptions(0.0001234567890123456789012345m,
            0.0000000000000000000000000001m,
            new FillSimulationAssumptions("declared fee", "declared slippage",
                "unknown latency", "unknown queue", "unknown partial fills",
                "stop-first", "synthetic fixture"),
            new FillSimulationVersion("ohlcv-fill-v1")));
}
