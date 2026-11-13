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

        if (args.Length >= 2
            && StringComparer.OrdinalIgnoreCase.Equals(args[0], CommandName)
            && StringComparer.OrdinalIgnoreCase.Equals(args[1], "telegram-send"))
        {
            return await DeliverTelegramAsync(args, output, error,
                cancellationToken).ConfigureAwait(false);
        }

        if (args.Length >= 2
            && StringComparer.OrdinalIgnoreCase.Equals(args[0], CommandName)
            && StringComparer.OrdinalIgnoreCase.Equals(args[1], "render"))
        {
            return await RenderJournalAsync(args, output, error).ConfigureAwait(false);
        }

        if (args.Length >= 2
            && StringComparer.Ordinal.Equals(args[0], CommandName)
            && StringComparer.Ordinal.Equals(args[1], "opportunity-report"))
        {
            return SignalOpportunityReportCommand.Run(args, output, error);
        }

        if (args.Length >= 2
            && StringComparer.Ordinal.Equals(args[0], CommandName)
            && StringComparer.Ordinal.Equals(args[1], "preflight"))
        {
            return await SignalOnlyBoundedPreflight.RunAsync(args, output, error,
                cancellationToken).ConfigureAwait(false);
        }

        if (args.Length >= 2
            && StringComparer.Ordinal.Equals(args[0], CommandName)
            && StringComparer.Ordinal.Equals(args[1], "bounded-run"))
        {
            return await SignalOnlyBoundedRun.RunAsync(args, output, error,
                cancellationToken).ConfigureAwait(false);
        }

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
            await output.WriteLineAsync(
                SignalOnlyJournalRenderer.ConfigurationUnavailableDiagnosticJson())
                .ConfigureAwait(false);
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


    private static async Task<int> RenderJournalAsync(string[] args,
        TextWriter output, TextWriter error)
    {
        if (args.Length != 8
            || !StringComparer.Ordinal.Equals(args[2], "--journal")
            || !StringComparer.Ordinal.Equals(args[4], "--symbol")
            || !StringComparer.Ordinal.Equals(args[6], "--format")
            || string.IsNullOrWhiteSpace(args[3])
            || string.IsNullOrWhiteSpace(args[5])
            || !IsSupportedRenderFormat(args[7]))
        {
            await error.WriteLineAsync(
                "Usage: TradingBot.App signal-only render --journal <file> --symbol <symbol> --format <json|console>")
                .ConfigureAwait(false);
            return 2;
        }

        string symbol = args[5].ToUpperInvariant();
        if (symbol.Length > 32 || !symbol.All(char.IsAsciiLetterOrDigit))
        {
            await error.WriteLineAsync("signal-only render: invalid public symbol")
                .ConfigureAwait(false);
            return 2;
        }

        string streamId = $"binance-usdm-native-kline:{symbol}";
        try
        {
            using SqliteSignalJournalStore journal =
                SqliteSignalJournalStore.OpenReadOnly(args[3]);
            SignalJournalSnapshot snapshot = journal.Read(streamId);
            string rendered = StringComparer.OrdinalIgnoreCase.Equals(args[7], "json")
                ? SignalOnlyJournalRenderer.RenderJson(streamId, snapshot)
                : SignalOnlyJournalRenderer.RenderConsole(streamId, snapshot);
            await output.WriteLineAsync(rendered).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(
                $"signal-only render: failed ({exception.GetType().Name})")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> DeliverTelegramAsync(string[] args,
        TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length != 8
            || !StringComparer.Ordinal.Equals(args[2], "--journal")
            || !StringComparer.Ordinal.Equals(args[4], "--delivery-state")
            || !StringComparer.Ordinal.Equals(args[6], "--symbol")
            || string.IsNullOrWhiteSpace(args[3])
            || string.IsNullOrWhiteSpace(args[5])
            || string.IsNullOrWhiteSpace(args[7]))
        {
            await error.WriteLineAsync(
                "Usage: TradingBot.App signal-only telegram-send --journal <file> --delivery-state <file> --symbol <symbol>")
                .ConfigureAwait(false);
            return 2;
        }

        string? botToken = Environment.GetEnvironmentVariable(
            "TRADINGBOT_TELEGRAM_BOT_TOKEN");
        string? chatId = Environment.GetEnvironmentVariable(
            "TRADINGBOT_TELEGRAM_CHAT_ID");
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            await error.WriteLineAsync(
                "signal-only telegram-send: TRADINGBOT_TELEGRAM_BOT_TOKEN and TRADINGBOT_TELEGRAM_CHAT_ID are required")
                .ConfigureAwait(false);
            return 2;
        }

        string symbol = args[7].ToUpperInvariant();
        if (symbol.Length > 32 || !symbol.All(char.IsAsciiLetterOrDigit))
        {
            await error.WriteLineAsync(
                "signal-only telegram-send: invalid public symbol")
                .ConfigureAwait(false);
            return 2;
        }

        try
        {
            string journalPath = Path.GetFullPath(args[3]);
            string deliveryPath = Path.GetFullPath(args[5]);
            StringComparer pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (pathComparer.Equals(journalPath, deliveryPath))
            {
                await error.WriteLineAsync(
                    "signal-only telegram-send: journal and delivery state require separate files")
                    .ConfigureAwait(false);
                return 2;
            }

            using SqliteSignalJournalStore journal =
                SqliteSignalJournalStore.OpenReadOnly(journalPath);
            using SqliteSignalOutboxDeliveryStore deliveryStore =
                SqliteSignalOutboxDeliveryStore.Open(deliveryPath);
            using HttpClient httpClient = new(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            TelegramOutboxDeliveryService sender = new(journal, deliveryStore,
                httpClient, SignalOutboxDeliveryPolicy.Default, botToken, chatId);
            string streamId = $"binance-usdm-native-kline:{symbol}";
            SignalOutboxDeliverySnapshot? result =
                await sender.DispatchNextAsync(streamId, cancellationToken)
                    .ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                channel = "Telegram",
                status = result?.Status.ToString() ?? "Idle",
                outboxId = result?.OutboxId,
                attemptsStarted = result?.AttemptsStarted
            })).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(
                $"signal-only telegram-send: failed ({exception.GetType().Name})")
                .ConfigureAwait(false);
            return 1;
        }
    }


    private static bool IsSupportedRenderFormat(string format) =>
        StringComparer.OrdinalIgnoreCase.Equals(format, "json")
        || StringComparer.OrdinalIgnoreCase.Equals(format, "console");

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
