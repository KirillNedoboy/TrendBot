using System.Runtime.InteropServices;
using System.Text.Json;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Cancellation-aware command entry point for the explicit shadow mode.</summary>
public static class OperationalShadowCommand
{
    public static Task<int> RunAsync(OperationalShadowRuntime runtime,
        TextWriter? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return RunOwnedAsync(runtime, output ?? Console.Out, cancellationToken);
    }

    public static Task<int> RunAsync(TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        OperationalShadowRuntime runtime = CreateDefaultRuntime();
        return RunOwnedAsync(runtime, output ?? Console.Out, cancellationToken);
    }

    private static async Task<int> RunOwnedAsync(OperationalShadowRuntime runtime,
        TextWriter output, CancellationToken cancellationToken)
    {
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using ConsoleCancelRegistration console = new(shutdown);
        using PosixSignalRegistration? sigterm = TryRegisterSigterm(shutdown);
        void WriteCycle(OperationalShadowCycleResult result)
        {
            string json = JsonSerializer.Serialize(new
            {
                mode = "shadow",
                status = result.Gate.CanCreateHypotheticalEntry ? "ready" : "blocked",
                hypotheticalEntry = result.HypotheticalEntryCreated,
                realOrderCount = result.RealOrderCount,
                gateReasons = result.Gate.BlockReasons.Select(static reason => reason.ToString())
            });
            output.WriteLine(json);
            output.Flush();
        }

        void WriteHealth(OperationalShadowHealthSnapshot snapshot)
        {
            string json = JsonSerializer.Serialize(new
            {
                mode = "shadow",
                status = snapshot.Status.ToString().ToLowerInvariant(),
                runId = runtime.RunId,
                healthSequence = snapshot.HealthSequence,
                checkedAt = snapshot.CheckedAt,
                dataStale = snapshot.DataStale,
                sequenceGap = snapshot.SequenceGap,
                reconnecting = snapshot.Reconnecting,
                recoveryComplete = snapshot.RecoveryComplete,
                persistenceHealthy = snapshot.PersistenceHealthy,
                killSwitchKnown = snapshot.KillSwitchKnown,
                killSwitchEnabled = snapshot.KillSwitchEnabled,
                gateOpen = snapshot.CanCreateHypotheticalEntry,
                gateReasons = snapshot.BlockReasons,
                coverageKnown = snapshot.CoverageKnown,
                coverageApplicable = snapshot.CoverageApplicable,
                expectedFeedCount = snapshot.ExpectedFeedCount,
                coveredFeedCount = snapshot.CoveredFeedCount,
                coverageRatio = snapshot.CoverageRatio,
                missingStreamReasons = snapshot.MissingStreamReasons,
                causalCheckpointLink = snapshot.CausalCheckpointLink,
                reconciliationStatus = snapshot.ReconciliationStatus.ToString(),
                feeds = snapshot.Feeds,
                realOrderCount = snapshot.RealOrderCount
            });
            output.WriteLine(json);
            output.Flush();
        }

        runtime.CycleCompleted += WriteCycle;
        runtime.HealthChanged += WriteHealth;
        await WriteJsonAsync(output, new
        {
            mode = "shadow",
            status = "started",
            runId = runtime.RunId,
            realOrderCount = 0L
        }).ConfigureAwait(false);
        WriteHealth(runtime.HealthSnapshot);

        try
        {
            await runtime.RunAsync(shutdown.Token).ConfigureAwait(false);
            await WriteJsonAsync(output, new
            {
                mode = "shadow",
                status = "stopped",
                runId = runtime.RunId,
                realOrderCount = 0L
            }).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            await WriteJsonAsync(output, new
            {
                mode = "shadow",
                status = "stopped",
                runId = runtime.RunId,
                realOrderCount = 0L
            }).ConfigureAwait(false);
            return 0;
        }
        catch (Exception)
        {
            await WriteJsonAsync(output, new
            {
                mode = "shadow",
                status = "faulted",
                reason = "runtime-fault",
                runId = runtime.RunId,
                realOrderCount = 0L
            }).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            runtime.CycleCompleted -= WriteCycle;
            runtime.HealthChanged -= WriteHealth;
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static OperationalShadowRuntime CreateDefaultRuntime()
    {
        string stateDirectory = Environment.GetEnvironmentVariable(
            "TRADINGBOT_SHADOW_STATE_DIRECTORY") ?? Path.Combine(AppContext.BaseDirectory, "state");
        Directory.CreateDirectory(stateDirectory);
        OperationalShadowStore store = OperationalShadowStore.Open(Path.Combine(stateDirectory,
            "operational-shadow.sqlite"));
        long initialStableSequence = store.ReadCheckpoint("shadow")?.StableSequence ?? -1;
        string? symbol = Environment.GetEnvironmentVariable("TRADINGBOT_SHADOW_SYMBOL");
        IOperationalShadowMarketDataSource source = string.IsNullOrWhiteSpace(symbol)
            ? new IdleOperationalShadowMarketDataSource()
            : CreateBinanceSource(symbol, initialStableSequence);
        return new OperationalShadowRuntime(source, store,
            new OperationalShadowRuntimeOptions { StreamId = "shadow" });
    }

    private static BinanceOperationalShadowMarketDataSource CreateBinanceSource(string symbol,
        long initialStableSequence)
    {
        HttpClient httpClient = new();
        BinancePublicRestClient restClient = new(httpClient);
        BinanceMarketWebSocketClient marketClient = new();
        BinanceDepthWebSocketClient depthClient = new();
        IReadOnlyList<BinanceSubscription> subscriptions =
        [
            BinanceSubscription.AggregateTrade(symbol),
            BinanceSubscription.Ticker(symbol),
            BinanceSubscription.MarkPrice(symbol)
        ];
        return new BinanceOperationalShadowMarketDataSource(restClient, marketClient,
            depthClient, symbol, subscriptions, initialStableSequence: initialStableSequence);
    }

    private static PosixSignalRegistration? TryRegisterSigterm(
        CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                context =>
                {
                    context.Cancel = true;
                    shutdown.Cancel();
                });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync(TextWriter output, object value)
    {
        string json = JsonSerializer.Serialize(value);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private sealed class ConsoleCancelRegistration : IDisposable
    {
        private readonly CancellationTokenSource _shutdown;

        public ConsoleCancelRegistration(CancellationTokenSource shutdown)
        {
            _shutdown = shutdown;
            Console.CancelKeyPress += OnCancel;
        }

        public void Dispose() => Console.CancelKeyPress -= OnCancel;

        private void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            _shutdown.Cancel();
        }
    }

    private sealed class IdleOperationalShadowMarketDataSource :
        IOperationalShadowMarketDataSource
    {
        public async IAsyncEnumerable<OperationalShadowMarketObservation> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            yield break;
        }
    }
}

