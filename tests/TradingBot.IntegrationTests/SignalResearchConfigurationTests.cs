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
        Assert.Equal(SignalOnlyConfiguration.ScaledPriceLevelsFormatVersion, configuration.FormatVersion);
        Assert.Equal(12, configuration.Symbols.Count);
        Assert.NotNull(configuration.ResearchStrategies);
        Assert.Equal(["15m", "1h", "30m", "5m"], configuration.ResearchStrategies.BoundaryTimeframes);
        Assert.Equal(["15m", "1h", "30m", "5m"], configuration.ResearchStrategies.CenterRetestTimeframes);
        Assert.Equal(["15m", "1h", "30m"], configuration.ResearchStrategies.FailedReclaimTimeframes);
        Assert.Equal(["15m", "1h", "30m"], configuration.ResearchStrategies.FlowRsiTimeframes);
        Assert.Equal(["15m", "1h", "30m"], configuration.ResearchStrategies.FlowStochasticTimeframes);
        Assert.Equal(["15m", "1h", "30m"], configuration.ResearchStrategies.FlowAtrTimeframes);
        Assert.Equal(["1h", "30m"], configuration.ResearchStrategies.CompressionTimeframes);
        Assert.NotNull(configuration.PriceLevelsPolicy);
        Assert.Equal(14, configuration.PriceLevelsPolicy.AtrPeriodBars);
        Assert.Equal(SignalPriceLevelPolicy.ScaledFormatVersion,
            configuration.PriceLevelsPolicy.FormatVersion);
        Assert.Equal(2m, configuration.PriceLevelsPolicy.ForFamily(SetupType.FlowAtrRecovery).StopDistanceMultiplier);
        Assert.Equal(2m, configuration.PriceLevelsPolicy.ForFamily(SetupType.FlowStochasticRecovery).StopDistanceMultiplier);
        Assert.Equal(1m, configuration.PriceLevelsPolicy.ForFamily(SetupType.FlowRsiRecovery).StopDistanceMultiplier);
        Assert.Equal(1.5m, configuration.PriceLevelsPolicy.ForFamily(SetupType.BoundaryRejection).MinimumStopAtr);
        Assert.Equal(2m, configuration.PriceLevelsPolicy.ForFamily(SetupType.VolatilityCompressionExpansion).MinimumStopAtr);
        Assert.Equal(2.5m, configuration.PriceLevelsPolicy.ForFamily(SetupType.CenterRetest).TakeProfit2Risk);
        SignalOnlyConfiguration restored = SignalOnlyConfiguration.Parse(configuration.ToJson());
        Assert.Equal(configuration.ResearchStrategies, restored.ResearchStrategies);
        Assert.Equal(configuration.PriceLevelsPolicy, restored.PriceLevelsPolicy);
        Assert.Equal(configuration.Symbols, restored.Symbols);
    }

    [Fact]
    public void FlowTwentyFiveProfileIsIsolatedAndEnablesOnlyRequestedFamilies()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        SignalOnlyConfiguration flow = SignalOnlyConfiguration.Load(
            Path.Combine(root, "signal-only.flow-25.example.json"));
        SignalOnlyConfiguration baseline = SignalOnlyConfiguration.Parse(ReadProfile());

        Assert.Equal(25, flow.Symbols.Count);
        Assert.Equal(baseline.Symbols.ToArray(), flow.Symbols.Take(baseline.Symbols.Count).ToArray());
        Assert.Equal(75, flow.StreamIds().Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(baseline.InputLedgerPath, flow.InputLedgerPath);
        Assert.NotEqual(baseline.SignalJournalPath, flow.SignalJournalPath);
        SignalResearchStrategyPolicy policy = Assert.IsType<SignalResearchStrategyPolicy>(flow.ResearchStrategies);
        Assert.Equal(["15m", "1h", "30m"], policy.FlowAtrTimeframes);
        Assert.Equal(["15m", "1h", "30m"], policy.FlowStochasticTimeframes);
        Assert.Empty(policy.BoundaryTimeframes);
        Assert.Empty(policy.CenterRetestTimeframes);
        Assert.Empty(policy.FailedReclaimTimeframes);
        Assert.Empty(policy.BreakoutRetestTimeframes);
        Assert.Empty(policy.FlowRsiTimeframes);
        Assert.Empty(policy.CompressionTimeframes);
        Assert.Equal(2m, flow.PriceLevelsPolicy!.ForFamily(SetupType.FlowAtrRecovery).StopDistanceMultiplier);
        Assert.Equal(2m, flow.PriceLevelsPolicy.ForFamily(SetupType.FlowStochasticRecovery).StopDistanceMultiplier);
        Assert.Equal(flow.PriceLevelsPolicy, SignalOnlyConfiguration.Parse(flow.ToJson()).PriceLevelsPolicy);
    }

    [Fact]
    public void LegacyPriceLevelProfileStillLoadsWithoutRiskMultiplier()
    {
        JsonObject root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["formatVersion"] = SignalOnlyConfiguration.PriceLevelsFormatVersion;
        JsonObject policy = root["priceLevelsPolicy"]!.AsObject();
        policy["formatVersion"] = SignalPriceLevelPolicy.CurrentFormatVersion;
        policy["policyVersion"] = "multistrategy-atr-levels-research-v1";
        policy["formulaVersion"] = SignalPriceLevelPolicy.CurrentFormulaVersion;
        foreach (JsonNode? family in policy["families"]!.AsArray())
        {
            family!.AsObject().Remove("stopDistanceMultiplier");
        }

        SignalOnlyConfiguration legacy = SignalOnlyConfiguration.Parse(root.ToJsonString());

        Assert.Equal(SignalOnlyConfiguration.PriceLevelsFormatVersion, legacy.FormatVersion);
        Assert.Null(legacy.PriceLevelsPolicy!.ForFamily(SetupType.FlowAtrRecovery).StopDistanceMultiplier);
        Assert.Equal(legacy.PriceLevelsPolicy,
            SignalOnlyConfiguration.Parse(legacy.ToJson()).PriceLevelsPolicy);
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
        JsonObject flowAtr = root["priceLevelsPolicy"]!["families"]!.AsArray()
            .Select(static family => family!.AsObject())
            .Single(static family => family["family"]!.GetValue<string>() == "flowAtrRecovery");
        flowAtr.Remove("stopDistanceMultiplier");
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));

        root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["formatVersion"] = SignalOnlyConfiguration.PriceLevelsFormatVersion;
        Assert.Throws<JsonException>(() => SignalOnlyConfiguration.Parse(root.ToJsonString()));

        root = JsonNode.Parse(ReadProfile())!.AsObject();
        root["priceLevelsPolicy"]!["formatVersion"] = SignalPriceLevelPolicy.CurrentFormatVersion;
        root["priceLevelsPolicy"]!["formulaVersion"] = SignalPriceLevelPolicy.CurrentFormulaVersion;
        foreach (JsonNode? family in root["priceLevelsPolicy"]!["families"]!.AsArray())
        {
            family!.AsObject().Remove("stopDistanceMultiplier");
        }
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
