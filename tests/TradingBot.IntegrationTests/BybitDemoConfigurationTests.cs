using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoConfigurationTests
{
    [Fact]
    public void ExplicitDemoLimitsAndIndependentJournalsRoundTrip()
    {
        BybitDemoConfiguration config = BybitDemoConfiguration.Parse(ValidJson());

        Assert.Equal(250m, config.TargetNotionalUsdt);
        Assert.Equal(20, config.MaximumPositions);
        Assert.Equal(10m, config.MaximumPriceDifferencePercent);
        Assert.Equal(10m, config.MaximumSlippagePercent);
        Assert.Equal(10m, config.MaximumLeverage);
        Assert.Equal("isolated", config.MarginMode);
        Assert.Equal(3, config.SignalJournalPaths.Count);
        Assert.Equal(config, BybitDemoConfiguration.Parse(config.ToJson()));
    }

    [Fact]
    public void ProductionEndpointAndCredentialsCannotAppearInConfiguration()
    {
        JsonObject json = JsonNode.Parse(ValidJson())!.AsObject();
        json["restBaseUrl"] = "https://api.bybit.com";
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!.AsObject();
        json["apiKey"] = "synthetic-value";
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!.AsObject();
        json["mode"] = "production";
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
    }

    [Fact]
    public void MissingFreshnessOrInvalidExchangeBoundsFailsClosed()
    {
        JsonObject json = JsonNode.Parse(ValidJson())!.AsObject();
        json.Remove("maximumSignalAgeSeconds");
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!.AsObject();
        json["maximumSlippagePercent"] = 10.01m;
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!.AsObject();
        json["maximumPositions"] = 0;
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!.AsObject();
        json["marginMode"] = "cross";
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
    }

    [Fact]
    public void ConfigCannotExceedUserApprovedDemoLimits()
    {
        foreach ((string field, decimal value) in new[]
        {
            ("targetNotionalUsdt", 251m),
            ("maximumPositions", 21m),
            ("maximumPriceDifferencePercent", 10.01m),
            ("maximumLeverage", 10.01m)
        })
        {
            JsonObject json = JsonNode.Parse(ValidJson())!.AsObject();
            json[field] = value;
            Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
        }
    }

    [Fact]
    public void StateCannotReuseAnInputJournalOrDuplicateAnInput()
    {
        JsonObject json = JsonNode.Parse(ValidJson())!.AsObject();
        json["statePath"] = "demo-flow-input.sqlite";
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
        json = JsonNode.Parse(ValidJson())!.AsObject();
        json["signalJournalPaths"]![1] = "demo-flow-input.sqlite";
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json.ToJsonString()));
    }

    [Fact]
    public void DuplicateConfigurationPropertiesAreRejected()
    {
        string json = ValidJson().Replace("\"mode\":\"bybit-demo\"",
            "\"mode\":\"bybit-demo\",\"mode\":\"bybit-demo\"", StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => BybitDemoConfiguration.Parse(json));
    }

    private static string ValidJson() => """
        {"formatVersion":1,"mode":"bybit-demo","targetNotionalUsdt":250,"maximumPositions":20,
         "maximumPriceDifferencePercent":10,"maximumSlippagePercent":10,"maximumLeverage":10,
         "marginMode":"isolated","maximumSignalAgeSeconds":60,"maximumQuoteAgeSeconds":5,
         "privateStreamFreshnessSeconds":30,"receiveWindowMilliseconds":5000,
         "statePath":"demo-execution.sqlite",
         "signalJournalPaths":["demo-flow-input.sqlite","demo-donchian-30-input.sqlite","demo-donchian-1h-input.sqlite"]}
        """;
}
