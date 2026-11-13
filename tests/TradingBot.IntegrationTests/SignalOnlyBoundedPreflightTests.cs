using System.Security.Cryptography;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class SignalOnlyBoundedPreflightTests
{
    [Fact]
    public async Task PreflightAcceptsExplicitBoundsWithoutCreatingStoresAndHonorsCancellation()
    {
        using Fixture fixture = new();
        fixture.WriteEnvelope();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        using StringWriter error = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], output, error,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, result);
        using JsonDocument verdict = JsonDocument.Parse(output.ToString());
        Assert.Equal("ready", verdict.RootElement.GetProperty("status").GetString());
        Assert.False(File.Exists(fixture.InputPath));
        Assert.False(File.Exists(fixture.JournalPath));

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        using StringWriter cancelledOutput = new(CultureInfo.InvariantCulture);
        int cancelledResult = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath],
            cancelledOutput, TextWriter.Null, cancelled.Token);
        Assert.Equal(1, cancelledResult);
        Assert.Contains("cancelled", cancelledOutput.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    [Fact]
    public async Task MissingStorageBoundAndExistingStoreBlockReadiness()
    {
        using Fixture fixture = new();
        fixture.Envelope["maxStoreBytes"] = null;
        fixture.WriteEnvelope();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], output, TextWriter.Null,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains("max-store-bytes-unknown", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));

        fixture.Envelope["maxStoreBytes"] = 64L * 1024 * 1024;
        Directory.CreateDirectory(fixture.RunDirectory);
        File.WriteAllText(fixture.InputPath, "preserve-existing-user-file");
        fixture.WriteEnvelope();
        using StringWriter second = new(CultureInfo.InvariantCulture);
        result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], second, TextWriter.Null,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains("storage-not-empty", second.ToString(), StringComparison.Ordinal);
        Assert.Equal("preserve-existing-user-file", File.ReadAllText(fixture.InputPath));
    }

    [Fact]
    public async Task PrivateConfigurationAndInconsistentLimitsAreRejected()
    {
        using Fixture fixture = new();
        fixture.Envelope["configuration"]!["privateTransport"] = "forbidden";
        fixture.WriteEnvelope();
        int invalid = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], TextWriter.Null, TextWriter.Null,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, invalid);
        fixture.Envelope["configuration"]!.AsObject().Remove("privateTransport");
        fixture.Envelope["firstSetupCloseDeadlineSeconds"] = 5000;
        fixture.WriteEnvelope();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        int blocked = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], output, TextWriter.Null,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, blocked);
        Assert.Contains("first-close-deadline-exceeds-duration", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    [Theory]
    [InlineData("maxDurationSeconds", "max-duration-seconds-unknown")]
    [InlineData("requireFailureDrills", "require-failure-drills-unknown")]
    [InlineData("requiredDrills", "required-drills-unknown")]
    [InlineData("allowUnknownReady", "allow-unknown-ready-unknown")]
    [InlineData("universePolicy", "universe-policy-unknown")]
    public async Task MissingMandatoryBoundsCannotBecomeReady(string field, string reason)
    {
        using Fixture fixture = new();
        fixture.Envelope[field] = null;
        fixture.WriteEnvelope();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], output, TextWriter.Null,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains(reason, output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    [Fact]
    public async Task IncompleteFailureDrillPlanBlocksReadiness()
    {
        using Fixture fixture = new();
        fixture.Envelope["requiredDrills"] = new JsonArray("restart");
        fixture.WriteEnvelope();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], output, TextWriter.Null,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains("required-drills-incomplete", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }


    [Fact]
    public async Task BoundedRunRejectsChangedEnvelopeBeforeCreatingStores()
    {
        using Fixture fixture = new();
        fixture.WriteEnvelope();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "bounded-run", "--envelope", fixture.EnvelopePath, "--sha256", new string('0', 64)],
            output, TextWriter.Null, TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains("envelope-hash-mismatch", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    [Fact]
    public async Task BoundedRunHonorsCancellationBeforeCreatingStores()
    {
        using Fixture fixture = new();
        fixture.WriteEnvelope();
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.EnvelopePath))).ToLowerInvariant();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "bounded-run", "--envelope", fixture.EnvelopePath, "--sha256", hash],
            output, TextWriter.Null, cancellation.Token);
        Assert.Equal(1, result);
        Assert.Contains("cancelled", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    [Fact]
    public async Task RestartRejectsUnexpectedFilesWithoutChangingThem()
    {
        using Fixture fixture = new();
        fixture.WriteEnvelope();
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.EnvelopePath))).ToLowerInvariant();
        Directory.CreateDirectory(fixture.RunDirectory);
        string userFile = Path.Combine(fixture.RunDirectory, "user-work.txt");
        File.WriteAllText(userFile, "preserve");
        using StringWriter error = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "bounded-run", "--envelope", fixture.EnvelopePath, "--sha256", hash, "--resume"],
            TextWriter.Null, error, TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains("IOException", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("preserve", File.ReadAllText(userFile));
        Assert.False(File.Exists(fixture.InputPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task ImpossibleHistoryAndCloseCountsBlockBeforeStoresOpen()
    {
        using Fixture fixture = new();
        fixture.Envelope["maxSetupRestRows"] = 1;
        fixture.Envelope["requiredContextCloses"] = 2;
        fixture.WriteEnvelope();
        using StringWriter output = new(CultureInfo.InvariantCulture);
        int result = await SignalOnlyCommand.RunAsync(
            ["signal-only", "preflight", "--envelope", fixture.EnvelopePath], output, TextWriter.Null,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, result);
        Assert.Contains("setup-history-exceeds-row-bound", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("acceptance-window-exceeds-duration", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    [Fact]
    public async Task PublicDisconnectCancelsBoundedRunAndClearsReady()
    {
        using Fixture fixture = new();
        fixture.WriteEnvelope();
        SignalOnlyBoundedPreflight.BoundedSignalEnvelope envelope =
            SignalOnlyBoundedPreflight.Parse(File.ReadAllBytes(fixture.EnvelopePath));
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
        SignalOnlyBoundedRun.RunProgress progress = new(DateTimeOffset.UtcNow,
            envelope.Wire, envelope.Configuration!.Profile, stop)
        { Quality = "ready" };
        using BinanceMarketWebSocketClient market = new(new ClosingTransport(),
            new BinanceWebSocketOptions(), TimeProvider.System);
        SignalOnlyBoundedRun.AttachFailClosedDiagnostics(market, progress.Stop);
        List<BinanceStreamEnvelope> observations = [];
        await foreach (BinanceStreamEnvelope input in market.ReadAsync(
            [BinanceSubscription.Kline("BTCUSDT", "15m")], stop.Token))
            observations.Add(input);

        Assert.Empty(observations);
        Assert.True(stop.IsCancellationRequested);
        Assert.Equal("public-stream-disconnect", progress.Reason);
        Assert.Equal("unknown", progress.Quality);
        progress.Quality = "ready";
        Assert.Equal("unknown", progress.Quality);
        Assert.False(Directory.Exists(fixture.RunDirectory));
    }

    private sealed class ClosingTransport : IBinanceWebSocketTransport
    {
        public WebSocketState State => WebSocketState.Open;
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BinanceWebSocketReceiveResult> ReceiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BinanceWebSocketReceiveResult(WebSocketMessageType.Close, Array.Empty<byte>(), true));
        public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public void Abort() { }
        public void Dispose() { }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] RequiredDrills =
            ["disconnect", "gap", "database-failure", "restart", "graceful-stop"];
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"signal-preflight-{Guid.NewGuid():N}");
        public string RunDirectory => Path.Combine(_directory, "run");
        public string InputPath => Path.Combine(RunDirectory, "native-bars.sqlite");
        public string JournalPath => Path.Combine(RunDirectory, "signal-journal.sqlite");
        public string EnvelopePath => Path.Combine(_directory, "envelope.json");
        public JsonObject Envelope { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset history = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 3600 * 3600).AddHours(-2);
            PublicSignalTimeframeConfiguration Timeframe(string timeframe) => new(timeframe,
                PublicSignalDataSource.BinanceUsdMNativeKlines, "source-v1", history, "history-v1",
                1, "seed-v1", 1, "warmup-v1", "freshness-v1", TimeSpan.FromHours(2), TimeSpan.FromHours(2));
            PublicSignalIndicatorConfiguration indicators = new(
                priceContextVersion: PriceContextFeatureCalculator.BranchRecoveryAlgorithmVersion,
                featureQualityVersion: "feature-quality-v1", setupFamilyVersion: "lane-a-v1",
                pivotLeftBars: 1, pivotRightBars: 1, atrPeriodBars: 1, atrMode: PriceContextAtrMode.Sma,
                atrValueUnit: PublicSignalValueUnit.Price, channelCenterKind: PriceContextChannelCenterKind.Ema,
                channelCenterPeriodBars: 1, hlc3SmoothingKind: PriceContextMovingAverageKind.Ema,
                channelCenterValueUnit: PublicSignalValueUnit.Price, channelWidthKind: PriceContextChannelWidthKind.Atr,
                channelWidthPeriodBars: 1, channelWidthMultiplier: 1m, channelWidthValueUnit: PublicSignalValueUnit.Price,
                channelWidthMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless, levelBufferMultiplier: 1m,
                levelBufferMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless, htfFastPeriodBars: 1,
                htfSlowPeriodBars: 1, htfMovingAverageKind: PriceContextMovingAverageKind.Ema,
                htfConfirmationBars: 1, htfMovingAverageValueUnit: PublicSignalValueUnit.Price,
                boundaryMinimumChannelWidth: 1m, boundaryMinimumChannelWidthUnit: PublicSignalValueUnit.Price);
            PublicSignalProfileConfiguration profile = new(1, "preflight-fixture-v1", Timeframe("15m"),
                Timeframe("1h"), indicators, new PublicSignalUniverseConfiguration("fixture-universe-v1", 1, 1));
            SignalScenarioPolicySet policies = new(1,
                new SignalScenarioExpiryPolicy(1, "fixture-expiry", TimeSpan.FromHours(1)),
                new SignalScenarioRearmPolicy(1, "fixture-rearm", TimeSpan.Zero, true),
                new SignalScenarioInvalidationPolicy(1, "fixture-invalidation", true, true));
            SignalOnlyConfiguration config = new(["BTCUSDT"], InputPath, JournalPath, profile, policies);
            Envelope = JsonSerializer.SerializeToNode(new
            {
                formatVersion = 1,
                mode = "signal-only-bounded-public",
                runId = "fixture-run",
                configuration = JsonNode.Parse(config.ToJson()),
                runDirectory = RunDirectory,
                startNotBeforeUtc = now.AddMinutes(-1),
                startNotAfterUtc = now.AddMinutes(5),
                maxDurationSeconds = 4200,
                maxStoreBytes = 64L * 1024 * 1024,
                maxWorkingSetBytes = 512L * 1024 * 1024,
                maxLedgerReceipts = 50000,
                maxRestPagesPerTimeframe = 1,
                maxSetupRestRows = 481,
                maxContextRestRows = 121,
                maxHistoryHours = 120,
                maxReconnectAttempts = 1,
                maxReconnectDowntimeSeconds = 120,
                maxFrameBytes = 65536,
                maxMessageBytes = 262144,
                maxRestResponseBytes = 4194304,
                requestTimeoutSeconds = 30,
                firstSetupCloseDeadlineSeconds = 1200,
                healthCheckIntervalSeconds = 5,
                gracefulStopSeconds = 10,
                requiredSetupCloses = 3,
                requiredContextCloses = 1,
                outcomeHorizonSeconds = 900,
                universePolicy = "exchange-info-at-run-start",
                outputMode = "local-journal-and-console",
                stopOnGap = true,
                stopOnConflict = true,
                stopOnPersistenceFailure = true,
                requireRestartParity = true,
                requireFailureDrills = true,
                requiredDrills = RequiredDrills,
                requireNoPrivateOrOrders = true,
                realOutcomesOnly = true,
                allowNoSetup = true,
                allowUnknownReady = false
            })!.AsObject();
        }

        public void WriteEnvelope() => File.WriteAllText(EnvelopePath, Envelope.ToJsonString());
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
