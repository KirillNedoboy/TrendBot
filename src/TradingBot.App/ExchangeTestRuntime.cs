using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Runtime-only credential values held inside the exchange-test process.</summary>
public sealed class ExchangeTestCredentials
{
    public ExchangeTestCredentials(string apiKey, string apiSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        ApiKey = apiKey;
        ApiSecret = apiSecret;
    }

    public string ApiKey { get; }
    public string ApiSecret { get; }

    public override string ToString() => "<redacted exchange-test credentials>";
}

/// <summary>Resolves credential values only at runtime; configuration carries references only.</summary>
public interface IExchangeTestCredentialProvider
{
    ValueTask<ExchangeTestCredentials> ResolveAsync(ExchangeTestConfiguration configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>Runtime provider for environment-variable credential references.</summary>
public sealed class EnvironmentExchangeTestCredentialProvider : IExchangeTestCredentialProvider
{
    public ValueTask<ExchangeTestCredentials> ResolveAsync(
        ExchangeTestConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(configuration.Credentials.Provider, "environment"))
        {
            throw new InvalidOperationException(
                "The configuration does not select the environment credential provider.");
        }

        string? apiKey = Environment.GetEnvironmentVariable(
            configuration.Credentials.ApiKeyReference);
        string? apiSecret = Environment.GetEnvironmentVariable(
            configuration.Credentials.ApiSecretReference);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
        {
            throw new InvalidOperationException(
                "The exchange-test credential references are not available at runtime.");
        }

        return ValueTask.FromResult(new ExchangeTestCredentials(apiKey, apiSecret));
    }
}

/// <summary>Minimal external secret-store seam; implementations own secret handling.</summary>
public interface IExchangeTestSecretStore
{
    ValueTask<ExchangeTestCredentials> ResolveAsync(string storeIdentity,
        string apiKeyReference, string apiSecretReference,
        CancellationToken cancellationToken = default);
}

/// <summary>Adapter for an injected external secret-store implementation.</summary>
public sealed class ExternalSecretStoreExchangeTestCredentialProvider
    : IExchangeTestCredentialProvider
{
    private readonly IExchangeTestSecretStore _store;

    public ExternalSecretStoreExchangeTestCredentialProvider(IExchangeTestSecretStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public ValueTask<ExchangeTestCredentials> ResolveAsync(
        ExchangeTestConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(configuration.Credentials.Provider,
                "external-secret-store")
            || string.IsNullOrWhiteSpace(configuration.Credentials.StoreIdentity))
        {
            throw new InvalidOperationException(
                "The configuration does not select a valid external secret store.");
        }

        return _store.ResolveAsync(configuration.Credentials.StoreIdentity,
            configuration.Credentials.ApiKeyReference,
            configuration.Credentials.ApiSecretReference, cancellationToken);
    }
}

/// <summary>
/// Explicit Binance private-client composition for the dedicated test
/// environment. The factory disables redirects, resolves secrets once inside
/// the process, and never constructs a production endpoint.
/// </summary>
public sealed class ExchangeTestRuntime : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly HmacSha256BinanceRequestSigner _signer;
    private readonly BinancePrivateWebSocketClient _privateWebSocketClient;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    private ExchangeTestRuntime(HttpClient httpClient,
        HmacSha256BinanceRequestSigner signer,
        BinancePrivateRestClient restClient,
        BinancePrivateWebSocketClient privateWebSocketClient,
        ExchangeTestConfiguration configuration, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _signer = signer;
        RestClient = restClient;
        _privateWebSocketClient = privateWebSocketClient;
        Configuration = configuration;
        _timeProvider = timeProvider;
    }

    public ExchangeTestConfiguration Configuration { get; }
    public BinancePrivateRestClient RestClient { get; }
    public BinancePrivateWebSocketClient PrivateWebSocketClient => _privateWebSocketClient;

    /// <summary>
    /// Composes the only executor transport available from this runtime. The
    /// transport is pinned to this instance's validated test REST/WS origins.
    /// </summary>
    public BinanceExchangeTestTransport CreateTransport(ExchangeTestRunMetadata metadata,
        PortfolioId portfolioId, TimeSpan? freshnessWindow = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(portfolioId);
        if (!StringComparer.Ordinal.Equals(metadata.ConfigurationId,
                Configuration.ConfigurationId)
            || !StringComparer.Ordinal.Equals(metadata.EnvironmentId,
                Configuration.EnvironmentId))
        {
            throw new InvalidOperationException(
                "The exchange-test runtime metadata does not match its configuration.");
        }

        if (freshnessWindow is null)
        {
            throw new InvalidOperationException(
                "The exchange-test transport requires a caller-supplied freshness window.");
        }

        return new BinanceExchangeTestTransport(this, metadata, portfolioId,
            freshnessWindow.Value, _timeProvider);
    }

    /// <summary>
    /// Composes an exchange-test executor with the validated concrete REST/WS
    /// transport. The portfolio and exchange stores must share one SQLite file;
    /// the executor enforces that atomic boundary before any dispatch.
    /// </summary>
    public ExchangeTestExecutor CreateExecutor(ExchangeTestRunMetadata metadata,
        PortfolioId portfolioId, SqlitePortfolioOperationalStore portfolioStore,
        SqliteExchangeTestStore exchangeStore, TimeSpan? freshnessWindow = null)
    {
        ArgumentNullException.ThrowIfNull(portfolioStore);
        ArgumentNullException.ThrowIfNull(exchangeStore);
        return new ExchangeTestExecutor(Configuration, portfolioStore, exchangeStore,
            CreateTransport(metadata, portfolioId, freshnessWindow), _timeProvider);
    }

    public static async ValueTask<ExchangeTestRuntime> CreateAsync(
        ExchangeTestConfiguration configuration,
        IExchangeTestCredentialProvider credentialProvider,
        HttpMessageHandler? httpMessageHandler = null,
        IBinanceWebSocketTransportFactory? webSocketTransportFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        ExchangeTestCredentials credentials = await credentialProvider.ResolveAsync(
            configuration, cancellationToken).ConfigureAwait(false);

        HttpMessageHandler handler = httpMessageHandler
            ?? new HttpClientHandler { AllowAutoRedirect = false };
        handler = new TestOriginHttpMessageHandler(handler, configuration.RestBaseAddress);
        TimeProvider runtimeTimeProvider = TimeProvider.System;
        HttpClient httpClient = new(handler, disposeHandler: true)
        {
            BaseAddress = configuration.RestBaseAddress
        };
        HmacSha256BinanceRequestSigner signer = new(credentials.ApiSecret);
        BinancePrivateRestClient restClient = new(httpClient,
            new BinancePrivateRestOptions(configuration.RestBaseAddress, credentials.ApiKey),
            signer);
        BinancePrivateWebSocketClient privateWebSocketClient = new(
            webSocketTransportFactory ?? new ClientWebSocketTransportFactory(),
            new BinancePrivateWebSocketOptions
            {
                BaseAddress = configuration.PrivateWebSocketBaseAddress,
                SessionLifetime = TimeSpan.FromHours(24)
            }, runtimeTimeProvider);
        return new ExchangeTestRuntime(httpClient, signer, restClient, privateWebSocketClient,
            configuration, runtimeTimeProvider);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _privateWebSocketClient.Dispose();
        _httpClient.Dispose();
        _signer.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TestOriginHttpMessageHandler : DelegatingHandler
    {
        private readonly Uri _origin;

        public TestOriginHttpMessageHandler(HttpMessageHandler innerHandler, Uri origin)
        {
            InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
            _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } requestUri || !SameOrigin(_origin, requestUri))
            {
                throw new BinancePrivateProtocolException(
                    "The exchange-test runtime refused a request outside its validated test REST origin.");
            }

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } responseUri
                && !SameOrigin(_origin, responseUri))
            {
                response.Dispose();
                throw new BinancePrivateProtocolException(
                    "The exchange-test runtime refused a response from outside its validated test REST origin.");
            }

            return response;
        }

        private static bool SameOrigin(Uri expected, Uri actual) =>
            StringComparer.OrdinalIgnoreCase.Equals(expected.Scheme, actual.Scheme)
            && StringComparer.OrdinalIgnoreCase.Equals(expected.Host, actual.Host)
            && expected.Port == actual.Port;
    }
}
