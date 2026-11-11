using System.Text.Json;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Public-only composition root for durable per-instrument signal capture.</summary>
public static class SignalOnlyCommand
{
    public const string CommandName = "signal-only";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null,
        TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length != 3
            || !StringComparer.OrdinalIgnoreCase.Equals(args[0], CommandName)
            || !StringComparer.Ordinal.Equals(args[1], "--config")
            || string.IsNullOrWhiteSpace(args[2]))
        {
            await error.WriteLineAsync(
                "Usage: TradingBot.App signal-only --config <public-config.json>")
                .ConfigureAwait(false);
            return 2;
        }

        SignalOnlyConfiguration configuration;
        try
        {
            configuration = SignalOnlyConfiguration.Load(args[2]);
        }
        catch (Exception exception) when (exception is IOException or JsonException
            or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync("signal-only: invalid public configuration")
                .ConfigureAwait(false);
            return 2;
        }

        using CancellationTokenSource consoleStop = new();
        using CancellationTokenSource runStop =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                consoleStop.Token);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            consoleStop.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await RunConfiguredAsync(configuration, output, runStop)
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            || consoleStop.IsCancellationRequested)
        {
            await output.WriteLineAsync("signal-only: stopped")
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(
                $"signal-only: stopped ({exception.GetType().Name})")
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RunConfiguredAsync(SignalOnlyConfiguration configuration,
        TextWriter output, CancellationTokenSource runStop)
    {
        using HttpClient httpClient = new();
        BinancePublicRestClient restClient = new(httpClient);
        using SqliteNativeBarInputLedger inputLedger =
            SqliteNativeBarInputLedger.Open(configuration.InputLedgerPath);
        using SqliteSignalJournalStore signalJournal =
            SqliteSignalJournalStore.Open(configuration.SignalJournalPath);
        Task[] instrumentTasks = configuration.Symbols
            .Select(symbol => RunInstrumentAsync(symbol, configuration, restClient,
                inputLedger, signalJournal, output, runStop))
            .ToArray();
        try
        {
            await Task.WhenAll(instrumentTasks).ConfigureAwait(false);
        }
        catch
        {
            runStop.Cancel();
            throw;
        }
    }

    private static async Task RunInstrumentAsync(string symbol,
        SignalOnlyConfiguration configuration, BinancePublicRestClient restClient,
        SqliteNativeBarInputLedger inputLedger,
        SqliteSignalJournalStore signalJournal, TextWriter output,
        CancellationTokenSource runStop)
    {
        try
        {
            string streamId = $"binance-usdm-native-kline:{symbol}";
            NativeBarInputLedgerSnapshot snapshot = inputLedger.Read(streamId);
            NativePublicKlineFeed feed = snapshot.Receipts.Count == 0
                ? await NativePublicKlineFeed.WarmupAsync(restClient, symbol,
                    configuration.Profile, inputLedger,
                    cancellationToken: runStop.Token).ConfigureAwait(false)
                : NativePublicKlineFeed.RestoreFromLedger(restClient, symbol,
                    configuration.Profile, inputLedger);
            using SignalOnlyInstrumentConsumer consumer = new(symbol,
                configuration.Profile, configuration.ScenarioPolicies, feed,
                inputLedger, signalJournal);
            using BinanceMarketWebSocketClient marketClient = new();
            long intakeSequence = inputLedger.Read(streamId)
                .Checkpoint.LastIntakeSequence;
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                mode = SignalOnlyConfiguration.SignalOnlyMode,
                symbol,
                inputSequence = intakeSequence
            })).ConfigureAwait(false);
            await consumer.RunAsync(marketClient, runStop.Token).ConfigureAwait(false);
            if (!runStop.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "The public market stream ended without cancellation.");
            }
        }
        catch
        {
            runStop.Cancel();
            throw;
        }
    }
}
