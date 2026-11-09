using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class PublicSignalProfileConfigurationTests
{
    [Fact]
    public void PublicNativeFifteenMinuteAndOneHourSourcesAreAcceptedButUnknownParametersBlockReadiness()
    {
        const string json = """
            {
              "formatVersion": 1,
              "profileVersion": "caller-declared-profile",
              "setup": {
                "timeframe": "15m",
                "source": "binanceUsdMNativeKlines",
                "sourceVersion": "caller-declared-source"
              },
              "context": {
                "timeframe": "1h",
                "source": "binanceUsdMNativeKlines",
                "sourceVersion": "caller-declared-source"
              },
              "indicators": { "atrValueUnit": "price" },
              "universe": {}
            }
            """;

        PublicSignalProfileConfiguration profile =
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(json);
        PublicSignalProfileConfiguration restored =
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(
                CoreJsonSerializer.Serialize(profile));
        Assert.Equal(profile.UnknownReasons, restored.UnknownReasons);
        Assert.DoesNotContain("\"state\"", CoreJsonSerializer.Serialize(profile));

        Assert.Equal(PublicSignalProfileState.UnknownConfiguration, profile.State);
        Assert.False(profile.IsReady);
        Assert.Equal("15m", profile.Setup?.Timeframe);
        Assert.Equal("1h", profile.Context?.Timeframe);
        Assert.Equal(PublicSignalValueUnit.Price, profile.Indicators?.AtrValueUnit);
        Assert.Equal(PublicSignalDataSource.BinanceUsdMNativeKlines, profile.Setup?.Source);
        Assert.Contains("setup-history-start-unknown", profile.UnknownReasons);
        Assert.Contains("channel-width-value-unit-unknown", profile.UnknownReasons);
        Assert.Contains("pivot-left-bars-unknown", profile.UnknownReasons);
        Assert.Contains("universe-minimum-symbols-unknown", profile.UnknownReasons);
    }

    [Fact]
    public void MissingProfileAndRequiredSectionsRemainDiagnosticUnknownConfiguration()
    {
        PublicSignalProfileConfiguration profile =
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>("{\"formatVersion\":1}");

        Assert.Equal(PublicSignalProfileState.UnknownConfiguration, profile.State);
        Assert.False(profile.IsReady);
        Assert.Contains("profile-version-unknown", profile.UnknownReasons);
        Assert.Contains("setup-timeframe-unknown", profile.UnknownReasons);
        Assert.Contains("context-timeframe-unknown", profile.UnknownReasons);
        Assert.Contains("indicator-configuration-unknown", profile.UnknownReasons);
        Assert.Contains("universe-configuration-unknown", profile.UnknownReasons);
        Assert.Equal(PublicSignalProfileState.UnknownConfiguration,
            PublicSignalProfileConfiguration.Missing.State);
    }
    [Fact]
    public void UnsupportedProfileFormatVersionIsRejected()
    {
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(
                "{\"formatVersion\":2}"));
    }

    [Theory]
    [InlineData("privateApiKey")]
    [InlineData("privateApiSecret")]
    [InlineData("activationEnabled")]
    [InlineData("quantity")]
    [InlineData("leverage")]
    [InlineData("portfolioId")]
    [InlineData("portfolioRisk")]
    public void PrivateActivationAndExecutionFieldsAreRejected(string fieldName)
    {
        string json = $"{{\"formatVersion\":1,\"{fieldName}\":null}}";

        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(json));
    }

    [Fact]
    public void PrivateFieldsInsideTimeframeConfigurationAreRejected()
    {
        const string json = """
            {
              "formatVersion": 1,
              "setup": { "privateApiSecret": null }
            }
            """;

        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(json));
    }

    [Fact]
    public void UnsupportedFeatureUnitsAreRejected()
    {
        const string json = """
            {
              "formatVersion": 1,
              "indicators": { "atrValueUnit": "contracts" }
            }
            """;

        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(json));
    }

    [Fact]
    public void NonNativeTimeframeIsRejectedInsteadOfBecomingReady()
    {
        const string json = """
            {
              "formatVersion": 1,
              "setup": { "timeframe": "5m" }
            }
            """;

        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(json));
    }
}
