using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.App;

/// <summary>
/// Immutable metadata for the dedicated Binance USD-M exchange test environment.
/// This contract contains references to credentials, never credential values.
/// </summary>
public sealed record ExchangeTestConfiguration
{
    public const int CurrentFormatVersion = 1;
    public const string ExchangeTestMode = "exchange-test";
    public const string TestEnvironmentId = "binance-usdm-test";
    public const string TestRestBaseUrl = "https://demo-fapi.binance.com/";
    public const string TestPrivateWebSocketBaseUrl =
        "wss://demo-fstream.binance.com/private/ws/";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    public int FormatVersion { get; }
    public string ConfigurationId { get; }
    public string EnvironmentId { get; }
    public string Mode { get; }
    public Uri RestBaseAddress { get; }
    public Uri PrivateWebSocketBaseAddress { get; }
    public ExchangeCredentialReferences Credentials { get; }
    public ExchangeTestOperationalPolicies Policies { get; }

    public ExchangeTestConfiguration(string configurationId,
        ExchangeCredentialReferences credentials,
        ExchangeTestOperationalPolicies policies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(policies);

        ValidateMetadataId(configurationId, nameof(configurationId));
        ValidateEndpoint(new Uri(TestRestBaseUrl), isWebSocket: false);
        ValidateEndpoint(new Uri(TestPrivateWebSocketBaseUrl), isWebSocket: true);
        credentials.Validate();
        policies.Validate();

        FormatVersion = CurrentFormatVersion;
        ConfigurationId = configurationId;
        EnvironmentId = TestEnvironmentId;
        Mode = ExchangeTestMode;
        RestBaseAddress = new Uri(TestRestBaseUrl);
        PrivateWebSocketBaseAddress = new Uri(TestPrivateWebSocketBaseUrl);
        Credentials = credentials;
        Policies = policies;
    }

    private ExchangeTestConfiguration(string configurationId, string environmentId,
        string mode, Uri restBaseAddress, Uri privateWebSocketBaseAddress,
        ExchangeCredentialReferences credentials, ExchangeTestOperationalPolicies policies)
        : this(configurationId, credentials, policies)
    {
        if (!StringComparer.Ordinal.Equals(environmentId, TestEnvironmentId)
            || !StringComparer.Ordinal.Equals(mode, ExchangeTestMode))
        {
            throw new ArgumentException("The configuration is not for the Binance USD-M test environment.");
        }

        ValidateEndpoint(restBaseAddress, isWebSocket: false);
        ValidateEndpoint(privateWebSocketBaseAddress, isWebSocket: true);
        RestBaseAddress = new Uri(TestRestBaseUrl);
        PrivateWebSocketBaseAddress = new Uri(TestPrivateWebSocketBaseUrl);
    }

    public static ExchangeTestConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            WireConfiguration wire = JsonSerializer.Deserialize<WireConfiguration>(json,
                WireOptions) ?? throw new JsonException();

            if (wire.FormatVersion != CurrentFormatVersion)
            {
                throw new JsonException();
            }

            return new ExchangeTestConfiguration(
                wire.ConfigurationId,
                wire.EnvironmentId,
                wire.Mode,
                ParseEndpoint(wire.RestBaseUrl, isWebSocket: false),
                ParseEndpoint(wire.PrivateWebSocketBaseUrl, isWebSocket: true),
                new ExchangeCredentialReferences(
                    wire.Credentials.Provider,
                    wire.Credentials.ApiKeyReference,
                    wire.Credentials.ApiSecretReference,
                    wire.Credentials.StoreIdentity,
                    wire.Credentials.Scope),
                new ExchangeTestOperationalPolicies(
                    wire.Policies.RateLimitPolicy,
                    wire.Policies.FreshnessPolicy,
                    wire.Policies.ReconciliationPolicy,
                    wire.Policies.PartialFillPolicy,
                    wire.Policies.UnknownStatePolicy,
                    wire.Policies.AllowProductionEndpoints,
                    wire.Policies.AllowProductionCredentials,
                    wire.Policies.RealMoneyActivationAvailable,
                    wire.Policies.NetworkCallsAllowedInFixtures));
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException or InvalidOperationException
            or NotSupportedException or OverflowException or NullReferenceException)
        {
            throw new JsonException("The exchange test configuration is invalid.");
        }
    }

    public static ExchangeTestConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            throw new IOException("The exchange test configuration could not be read.");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new WireConfiguration(
        FormatVersion,
        ConfigurationId,
        EnvironmentId,
        Mode,
        RestBaseAddress.AbsoluteUri,
        PrivateWebSocketBaseAddress.AbsoluteUri,
        new WireCredentials(Credentials.Provider, Credentials.ApiKeyReference,
            Credentials.ApiSecretReference, Credentials.StoreIdentity, Credentials.Scope),
        new WirePolicies(Policies.RateLimitPolicy, Policies.FreshnessPolicy,
            Policies.ReconciliationPolicy, Policies.PartialFillPolicy,
            Policies.UnknownStatePolicy, Policies.AllowProductionEndpoints,
            Policies.AllowProductionCredentials, Policies.RealMoneyActivationAvailable,
            Policies.NetworkCallsAllowedInFixtures)), WireOptions);

    private static Uri ParseEndpoint(string value, bool isWebSocket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException("An endpoint must be an absolute URI.");
        }

        ValidateEndpoint(uri, isWebSocket);
        return uri;
    }

    private static void ValidateEndpoint(Uri uri, bool isWebSocket)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The endpoint contains unsupported URI components.");
        }

        Uri expected = new(isWebSocket ? TestPrivateWebSocketBaseUrl : TestRestBaseUrl);
        if (!StringComparer.OrdinalIgnoreCase.Equals(uri.Scheme, expected.Scheme)
            || !StringComparer.OrdinalIgnoreCase.Equals(uri.Host, expected.Host)
            || uri.Port != expected.Port
            || !StringComparer.Ordinal.Equals(uri.AbsolutePath, expected.AbsolutePath))
        {
            throw new ArgumentException("Only the Binance USD-M test endpoint is accepted.");
        }
    }

    private static void ValidateMetadataId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(char.IsControl)
            || !char.IsLetterOrDigit(value[0])
            || !char.IsLetterOrDigit(value[^1])
            || value.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            || ForbiddenPolicyTerm(value))
        {
            throw new ArgumentException("The metadata identifier is not valid.", parameterName);
        }
    }

    internal static bool ForbiddenPolicyTerm(string value)
    {
        string normalized = value.Replace('-', ' ').Replace('_', ' ')
            .Replace(':', ' ').Replace('/', ' ');
        normalized = normalized.Replace("real money", " ",
            StringComparison.OrdinalIgnoreCase);
        string[] terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.Any(static term =>
            term.Equals("prod", StringComparison.OrdinalIgnoreCase)
            || term.Equals("production", StringComparison.OrdinalIgnoreCase)
            || term.Equals("live", StringComparison.OrdinalIgnoreCase)
            || term.Equals("mainnet", StringComparison.OrdinalIgnoreCase)
            || term.Equals("activation", StringComparison.OrdinalIgnoreCase));
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException();
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private sealed record WireConfiguration(int FormatVersion,
        string ConfigurationId, string EnvironmentId, string Mode,
        string RestBaseUrl, string PrivateWebSocketBaseUrl,
        WireCredentials Credentials, WirePolicies Policies);

    private sealed record WireCredentials(string Provider, string ApiKeyReference,
        string ApiSecretReference, string? StoreIdentity, string Scope);

    private sealed record WirePolicies(string RateLimitPolicy, string FreshnessPolicy,
        string ReconciliationPolicy, string PartialFillPolicy, string UnknownStatePolicy,
        bool AllowProductionEndpoints, bool AllowProductionCredentials,
        bool RealMoneyActivationAvailable, bool NetworkCallsAllowedInFixtures);
}

/// <summary>References runtime-held test credentials without resolving or storing them.</summary>
public sealed record ExchangeCredentialReferences
{
    public string Provider { get; }
    public string ApiKeyReference { get; }
    public string ApiSecretReference { get; }
    public string? StoreIdentity { get; }
    public string Scope { get; }

    public ExchangeCredentialReferences(string provider, string apiKeyReference,
        string apiSecretReference, string? storeIdentity, string scope)
    {
        Provider = provider;
        ApiKeyReference = apiKeyReference;
        ApiSecretReference = apiSecretReference;
        StoreIdentity = storeIdentity;
        Scope = scope;
        Validate();
    }

    internal void Validate()
    {
        if (!StringComparer.Ordinal.Equals(Scope, ExchangeTestConfiguration.TestEnvironmentId))
        {
            throw new ArgumentException("Credential scope must identify the exchange test environment.");
        }

        RequireReference(ApiKeyReference, nameof(ApiKeyReference));
        RequireReference(ApiSecretReference, nameof(ApiSecretReference));
        if (StringComparer.Ordinal.Equals(Provider, "environment"))
        {
            if (StoreIdentity is not null)
            {
                throw new ArgumentException("Environment credentials cannot specify a store identity.");
            }
        }
        else if (StringComparer.Ordinal.Equals(Provider, "external-secret-store"))
        {
            RequireReference(StoreIdentity, nameof(StoreIdentity));
        }
        else
        {
            throw new ArgumentException("Only environment or external secret-store references are supported.");
        }
    }

    private static void RequireReference(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(char.IsControl)
            || value.Contains('=') || value.Contains('&') || value.Contains('?')
            || value.Contains('#') || value.Contains('"')
            || ExchangeTestConfiguration.ForbiddenPolicyTerm(value))
        {
            throw new ArgumentException("A credential reference must be metadata only.", parameterName);
        }
    }
}

/// <summary>
/// Explicit policies for the exchange test lane. Policy identifiers are labels;
/// numeric trading defaults are intentionally absent.
/// </summary>
public sealed record ExchangeTestOperationalPolicies
{
    public string RateLimitPolicy { get; }
    public string FreshnessPolicy { get; }
    public string ReconciliationPolicy { get; }
    public string PartialFillPolicy { get; }
    public string UnknownStatePolicy { get; }
    public bool AllowProductionEndpoints { get; }
    public bool AllowProductionCredentials { get; }
    public bool RealMoneyActivationAvailable { get; }
    public bool NetworkCallsAllowedInFixtures { get; }

    public ExchangeTestOperationalPolicies(string rateLimitPolicy,
        string freshnessPolicy, string reconciliationPolicy, string partialFillPolicy,
        string unknownStatePolicy, bool allowProductionEndpoints,
        bool allowProductionCredentials, bool realMoneyActivationAvailable,
        bool networkCallsAllowedInFixtures)
    {
        RateLimitPolicy = rateLimitPolicy;
        FreshnessPolicy = freshnessPolicy;
        ReconciliationPolicy = reconciliationPolicy;
        PartialFillPolicy = partialFillPolicy;
        UnknownStatePolicy = unknownStatePolicy;
        AllowProductionEndpoints = allowProductionEndpoints;
        AllowProductionCredentials = allowProductionCredentials;
        RealMoneyActivationAvailable = realMoneyActivationAvailable;
        NetworkCallsAllowedInFixtures = networkCallsAllowedInFixtures;
        Validate();
    }

    internal void Validate()
    {
        RequirePolicy(RateLimitPolicy, nameof(RateLimitPolicy));
        RequirePolicy(FreshnessPolicy, nameof(FreshnessPolicy));
        RequirePolicy(ReconciliationPolicy, nameof(ReconciliationPolicy));
        RequirePolicy(PartialFillPolicy, nameof(PartialFillPolicy));
        RequirePolicy(UnknownStatePolicy, nameof(UnknownStatePolicy));
        if (AllowProductionEndpoints || AllowProductionCredentials
            || RealMoneyActivationAvailable || NetworkCallsAllowedInFixtures)
        {
            throw new ArgumentException("Exchange test policies must remain fail-closed.");
        }
    }

    private static void RequirePolicy(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(char.IsControl)
            || ExchangeTestConfiguration.ForbiddenPolicyTerm(value))
        {
            throw new ArgumentException("The policy identifier is not valid.", parameterName);
        }
    }
}
