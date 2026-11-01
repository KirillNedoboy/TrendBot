using System.Text.Json;
using TradingBot.App;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ExchangeTestConfigurationTests
{
    [Fact]
    public void ConfigurationRoundTripsOnlyTestEnvironmentMetadata()
    {
        ExchangeTestConfiguration configuration = CreateConfiguration();

        ExchangeTestConfiguration restored = ExchangeTestConfiguration.Parse(
            configuration.ToJson());

        Assert.Equal(configuration, restored);
        Assert.Equal("binance-usdm-test", restored.EnvironmentId);
        Assert.Equal("https://demo-fapi.binance.com/", restored.RestBaseAddress.AbsoluteUri);
        Assert.Equal("wss://demo-fstream.binance.com/private/ws/",
            restored.PrivateWebSocketBaseAddress.AbsoluteUri);
        Assert.Equal("environment", restored.Credentials.Provider);
        Assert.Equal("binance-usdm-test", restored.Credentials.Scope);
        Assert.False(restored.Policies.AllowProductionEndpoints);
        Assert.False(restored.Policies.AllowProductionCredentials);
        Assert.False(restored.Policies.RealMoneyActivationAvailable);
        Assert.False(restored.Policies.NetworkCallsAllowedInFixtures);
    }

    [Theory]
    [InlineData("credentials")]
    [InlineData("apiKey")]
    [InlineData("apiSecret")]
    [InlineData("activationCommand")]
    [InlineData("productionEndpoint")]
    [InlineData("unknownSetting")]
    public void ConfigurationRejectsUnknownInlineSecretOrActivationFields(string field)
    {
        string json = CreateConfiguration().ToJson();
        int insertion = json.LastIndexOf('}');
        json = json.Insert(insertion, $",\"{field}\":\"secret-marker\"");

        JsonException exception = Assert.Throws<JsonException>(
            () => ExchangeTestConfiguration.Parse(json));

        Assert.DoesNotContain("secret-marker", exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationRejectsDuplicateFieldsAndUnsupportedVersion()
    {
        string json = CreateConfiguration().ToJson();

        Assert.Throws<JsonException>(() => ExchangeTestConfiguration.Parse(
            json.Insert(json.IndexOf('{') + 1,
                "\"mode\":\"exchange-test\",")));
        Assert.Throws<JsonException>(() => ExchangeTestConfiguration.Parse(
            json.Replace("\"formatVersion\": 1", "\"formatVersion\": 2",
                StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("https://fapi.binance.com/")]
    [InlineData("https://demo-fapi.binance.com.evil.example/")]
    [InlineData("https://demo-fapi.binance.com/?activation=true")]
    public void ConfigurationRejectsProductionOrUnsafeRestEndpoint(string endpoint)
    {
        string json = CreateConfiguration().ToJson().Replace(
            "https://demo-fapi.binance.com/", endpoint, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => ExchangeTestConfiguration.Parse(json));
    }

    [Fact]
    public void ConfigurationRejectsProductionOrUnknownCredentialScope()
    {
        string json = CreateConfiguration().ToJson()
            .Replace("binance-usdm-test", "binance-usdm-production",
                StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => ExchangeTestConfiguration.Parse(json));
    }

    [Theory]
    [InlineData("https://evil.example/exchange-test")]
    [InlineData("exchange test profile")]
    [InlineData("credential:value")]
    public void ConfigurationRejectsNonIdentifierConfigurationIds(string configurationId)
    {
        string json = CreateConfiguration().ToJson().Replace(
            "exchange-test-profile-1", configurationId, StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => ExchangeTestConfiguration.Parse(json));
    }

    [Fact]
    public void ConfigurationSupportsExternalSecretStoreReferencesAsMetadata()
    {
        ExchangeTestConfiguration configuration = new(
            "exchange-test-external-store",
            new ExchangeCredentialReferences(
                "external-secret-store",
                "api-key",
                "api-secret",
                "secretstore://binance-usdm-test",
                "binance-usdm-test"),
            CreatePolicies());

        ExchangeTestConfiguration restored = ExchangeTestConfiguration.Parse(
            configuration.ToJson());

        Assert.Equal("external-secret-store", restored.Credentials.Provider);
        Assert.Equal("secretstore://binance-usdm-test",
            restored.Credentials.StoreIdentity);
        Assert.Equal("api-key", restored.Credentials.ApiKeyReference);
        Assert.Equal("api-secret", restored.Credentials.ApiSecretReference);
    }

    [Fact]
    public void ConfigurationFileLoadReadsOnlyTheExplicitFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "exchange-test.json");
        try
        {
            File.WriteAllText(path, CreateConfiguration().ToJson());

            ExchangeTestConfiguration loaded = ExchangeTestConfiguration.Load(path);

            Assert.Equal(CreateConfiguration(), loaded);
            Assert.Equal(["exchange-test.json"], Directory.GetFiles(directory)
                .Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ExchangeTestConfiguration CreateConfiguration() =>
        new(
            "exchange-test-profile-1",
            new ExchangeCredentialReferences(
                "environment",
                "TRADINGBOT_BINANCE_TEST_API_KEY",
                "TRADINGBOT_BINANCE_TEST_API_SECRET",
                null,
                "binance-usdm-test"),
            CreatePolicies());

    private static ExchangeTestOperationalPolicies CreatePolicies() =>
        new(
                "exchange-documented-rate-limits",
                "caller-supplied-freshness",
                "reconciliation-before-retry",
                "reduce-only-partial-fill-protection",
                "fail-closed-unknown-or-manual-position",
                allowProductionEndpoints: false,
                allowProductionCredentials: false,
                realMoneyActivationAvailable: false,
                networkCallsAllowedInFixtures: false);
}
