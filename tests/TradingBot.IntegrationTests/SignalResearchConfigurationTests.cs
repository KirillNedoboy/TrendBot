using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.App;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class SignalResearchConfigurationTests
{
    [Fact]
    public void MultiStrategyProfileRoundTripsWithIndependentTimeframes()
    {
        SignalOnlyConfiguration configuration = SignalOnlyConfiguration.Parse(ReadProfile());
        Assert.Equal(SignalOnlyConfiguration.PriceLevelsFormatVersion, configuration.FormatVersion);
        Assert.Equal(12, configuration.Symbols.Count);
        Assert.NotNull(configuration.ResearchStrategies);
        Assert.Equal(["15m", "1h", "30m", "5m"], configuration.ResearchStrategies.BoundaryTimeframes);
        Assert.Equal(["15m", "1h", "30m", "5m"], configuration.ResearchStrategies.CenterRetestTimeframes);
        Assert.Equal(["15m", "1h", "30m"], configuration.ResearchStrategies.FailedReclaimTimeframes);
        Assert.Equal(["15m", "1h", "30m"], configuration.ResearchStrategies.FlowRsiTimeframes);
        Assert.Equal(["1h", "30m"], configuration.ResearchStrategies.CompressionTimeframes);
        Assert.NotNull(configuration.PriceLevelsPolicy);
        Assert.Equal(14, configuration.PriceLevelsPolicy.AtrPeriodBars);
        Assert.Equal(1.5m, configuration.PriceLevelsPolicy.ForFamily(SetupType.BoundaryRejection).MinimumStopAtr);
        Assert.Equal(2m, configuration.PriceLevelsPolicy.ForFamily(SetupType.VolatilityCompressionExpansion).MinimumStopAtr);
        Assert.Equal(2.5m, configuration.PriceLevelsPolicy.ForFamily(SetupType.CenterRetest).TakeProfit2Risk);
        SignalOnlyConfiguration restored = SignalOnlyConfiguration.Parse(configuration.ToJson());
        Assert.Equal(configuration.ResearchStrategies, restored.ResearchStrategies);
        Assert.Equal(configuration.PriceLevelsPolicy, restored.PriceLevelsPolicy);
        Assert.Equal(configuration.Symbols, restored.Symbols);
    }

    [Fact]
    public void IncompleteOrMismatchedStrategyConfigurationIsRejected()
    {
        JsonObject root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["researchStrategies"]!["flowLookbackBars"] = 0;
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));
        root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["formatVersion"] = SignalOnlyConfiguration.MultiTimeframeFormatVersion;
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));
        root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["researchStrategies"]!.AsObject().Remove("compressionTimeframes");
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));
        root = JsonNode.Parse(ReadProfile())!.AsObject();
        JsonArray additional = root["additionalProfiles"]!.AsArray();
        JsonNode missing = additional.First(item =>
            item!["profile"]!["setup"]!["timeframe"]!.GetValue<string>() == "30m")!;
        additional.Remove(missing);
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));
    }

    [Fact]
    public void PriceLevelsRequireCompleteFamilyRulesAndMatchingAtrOnAllTimeframes()
    {
        JsonObject root = JsonNode.Parse(ReadProfile())!.AsObject();
        root.Remove("priceLevelsPolicy");
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));

        root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["priceLevelsPolicy"]!["families"]!.AsArray().RemoveAt(0);
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));

        root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["priceLevelsPolicy"]!["families"]![0]!["takeProfit2Risk"] = 0;
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));

        root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["priceLevelsPolicy"]!["atrPeriodBars"] = 13;
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));
    }

    [Fact]
    public void LegacyMultiStrategyProfileStillLoadsWithoutLevels()
    {
        JsonObject root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["formatVersion"] = SignalOnlyConfiguration.ResearchStrategiesFormatVersion;
        root.Remove("priceLevelsPolicy");
        SignalOnlyConfiguration legacy = SignalOnlyConfiguration.Parse(root.ToJsonString());
        Assert.Null(legacy.PriceLevelsPolicy);
        Assert.Equal(SignalOnlyConfiguration.ResearchStrategiesFormatVersion,
            SignalOnlyConfiguration.Parse(legacy.ToJson()).FormatVersion);
    }

    private static string ReadProfile()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TradingBot.slnx")))
        {
            directory = directory.Parent;
        }
        return File.ReadAllText(Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("The repository root was not found."),
            "signal-only.multitimeframe.example.json"));
    }
}
