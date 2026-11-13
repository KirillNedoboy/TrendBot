using System.Text.Json;
using TradingBot.Core;
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

        if ((args.Length != 3 && args.Length != 5)
            || !StringComparer.OrdinalIgnoreCase.Equals(args[0], CommandName)
            || !StringComparer.Ordinal.Equals(args[1], "--config")
            || string.IsNullOrWhiteSpace(args[2])
            || (args.Length == 5 && (!StringComparer.Ordinal.Equals(args[3], "--telegram-state")
                || string.IsNullOrWhiteSpace(args[4]))))
        {
            await error.WriteLineAsync(
                "Usage: TradingBot.App signal-only --config <public-config.json> [--telegram-state <file>]")
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

        string? deliveryPath = null;
        string? botToken = null;
        string? chatId = null;
        if (args.Length == 5)
        {
            botToken = Environment.GetEnvironmentVariable("TRADINGBOT_TELEGRAM_BOT_TOKEN");
            chatId = Environment.GetEnvironmentVariable("TRADINGBOT_TELEGRAM_CHAT_ID");
            if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
            {
                await error.WriteLineAsync(
                    "signal-only: TRADINGBOT_TELEGRAM_BOT_TOKEN and TRADINGBOT_TELEGRAM_CHAT_ID are required")
                    .ConfigureAwait(false);
                return 2;
            }

            try
            {
                if (StringComparer.Ordinal.Equals(args[4], ":memory:"))
                {
                    throw new ArgumentException("The delivery state must be file-backed.");
                }
                deliveryPath = Path.GetFullPath(args[4]);
                StringComparer pathComparer = OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                if (pathComparer.Equals(deliveryPath, configuration.InputLedgerPath)
                    || pathComparer.Equals(deliveryPath, configuration.SignalJournalPath))
                {
                    throw new ArgumentException("Delivery state requires a separate file.");
                }
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException or NotSupportedException)
            {
                await error.WriteLineAsync("signal-only: invalid delivery state path")
                    .ConfigureAwait(false);
                return 2;
            }
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
            await RunConfiguredAsync(configuration, TextWriter.Synchronized(output), runStop,
                deliveryPath, botToken, chatId).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            || consoleStop.IsCancellationRequested)
        {
            await output.WriteLineAsync("signal-only: stopped")
                .ConfigureAwait(false);
            return 0;
        }
        catch (BinanceApiException exception)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new
            {
                kind = "public-api-failure",
                status = (int)exception.StatusCode,
                exchangeCode = exception.Code,
                endpoint = exception.RequestUri?.AbsolutePath
            })).ConfigureAwait(false);
            return 1;
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
        if (args.Length is not (8 or 10)
            || !StringComparer.Ordinal.Equals(args[2], "--journal")
            || !StringComparer.Ordinal.Equals(args[4], "--symbol")
            || !StringComparer.Ordinal.Equals(args[6], "--format")
            || string.IsNullOrWhiteSpace(args[3])
            || string.IsNullOrWhiteSpace(args[5])
            || !IsSupportedRenderFormat(args[7])
            || args.Length == 10 && (args[8] != "--timeframe"
                || PublicSignalTimeframes.ContextForSetup(args[9]) is null))
        {
            await error.WriteLineAsync(
                "Usage: TradingBot.App signal-only render --journal <file> --symbol <symbol> --format <json|console> [--timeframe <5m|15m|30m|1h>]")
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

        string streamId = SignalOnlyStreamId.For(symbol, args.Length == 10 ? args[9] : "15m");
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
        if (args.Length is not (8 or 10)
            || !StringComparer.Ordinal.Equals(args[2], "--journal")
            || !StringComparer.Ordinal.Equals(args[4], "--delivery-state")
            || !StringComparer.Ordinal.Equals(args[6], "--symbol")
            || string.IsNullOrWhiteSpace(args[3])
            || string.IsNullOrWhiteSpace(args[5])
            || string.IsNullOrWhiteSpace(args[7])
            || args.Length == 10 && (args[8] != "--timeframe"
                || PublicSignalTimeframes.ContextForSetup(args[9]) is null))
        {
            await error.WriteLineAsync(
                "Usage: TradingBot.App signal-only telegram-send --journal <file> --delivery-state <file> --symbol <symbol> [--timeframe <5m|15m|30m|1h>]")
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
            string streamId = SignalOnlyStreamId.For(symbol, args.Length == 10 ? args[9] : "15m");
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
        TextWriter output, CancellationTokenSource runStop, string? deliveryPath,
        string? botToken, string? chatId)
    {
        using HttpClient httpClient = new();
        BinancePublicRestClient restClient = new(httpClient);
        using SqliteNativeBarInputLedger inputLedger =
            SqliteNativeBarInputLedger.Open(configuration.InputLedgerPath);
        using SqliteSignalJournalStore signalJournal =
            SqliteSignalJournalStore.Open(configuration.SignalJournalPath);
        if (deliveryPath is not null && !File.Exists(deliveryPath)
            && configuration.StreamIds().Any(streamId =>
                signalJournal.Read(streamId).Outbox.Count != 0))
        {
            await output.WriteLineAsync(
                "signal-only: existing outbox requires its original Telegram delivery state")
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "An existing signal outbox requires its original delivery state.");
        }

        using SqliteSignalOutboxDeliveryStore? deliveryStore = deliveryPath is null
            ? null : SqliteSignalOutboxDeliveryStore.Open(deliveryPath);
        using HttpClient? telegramHttpClient = deliveryStore is null ? null : new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10)
            })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        TelegramOutboxDeliveryService? sender = deliveryStore is null ? null : new(
            signalJournal, deliveryStore, telegramHttpClient!,
            SignalOutboxDeliveryPolicy.Default, botToken!, chatId!);
        List<Task> tasks = configuration.Symbols
            .Select(symbol => RunInstrumentAsync(symbol, configuration, restClient,
                inputLedger, signalJournal, output, runStop))
            .ToList();
        if (sender is not null)
        {
            tasks.Add(RunTelegramAsync(sender, configuration.Symbols.Select(symbol =>
                (IReadOnlyList<string>)configuration.ProfilesForSymbol(symbol)
                    .Select(item => SignalOnlyStreamId.For(symbol, item.Profile.Setup!.Timeframe!))
                    .ToArray()), output, runStop));
        }
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            runStop.Cancel();
            throw;
        }
    }

    private static async Task RunTelegramAsync(TelegramOutboxDeliveryService sender,
        IEnumerable<IReadOnlyList<string>> instrumentStreams, TextWriter output, CancellationTokenSource runStop)
    {
        try
        {
            await SignalOnlyTelegramPump.RunAsync(sender, instrumentStreams, output,
                TimeSpan.FromSeconds(5), runStop.Token).ConfigureAwait(false);
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
            IReadOnlyList<SignalOnlyTimeframeProfile> profiles = configuration.ProfilesForSymbol(symbol);
            List<(NativePublicKlineFeed Feed, SignalOnlyInstrumentConsumer Consumer)> consumers = [];
            using SignalMultiLaneMarketData? multiLane = configuration.MultiLanePolicy is { } policy
                ? new SignalMultiLaneMarketData(symbol, policy, restClient,
                    onFirstObservation: kind => output.WriteLine(JsonSerializer.Serialize(new
                    {
                        kind = "multi-lane-source-observed",
                        symbol,
                        source = kind
                    })))
                : null;
            try
            {
                foreach (SignalOnlyTimeframeProfile setting in profiles)
                {
                    string setupTimeframe = setting.Profile.Setup!.Timeframe!;
                    string streamId = SignalOnlyStreamId.For(symbol, setupTimeframe);
                    NativeBarInputLedgerSnapshot snapshot = inputLedger.Read(streamId);
                    NativePublicKlineFeed feed = snapshot.Receipts.Count == 0
                        ? await NativePublicKlineFeed.WarmupAsync(restClient, symbol,
                            setting.Profile, inputLedger,
                            cancellationToken: runStop.Token).ConfigureAwait(false)
                        : NativePublicKlineFeed.RestoreFromLedger(restClient, symbol,
                            setting.Profile, inputLedger);
                    SignalOnlyInstrumentConsumer consumer = new(symbol, setting.Profile,
                        setting.ScenarioPolicies, feed, inputLedger, signalJournal, multiLane);
                    consumers.Add((feed, consumer));
                    if (snapshot.Receipts.Count != 0)
                    {
                        await consumer.BackfillAsync(runStop.Token).ConfigureAwait(false);
                    }
                    await output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        mode = SignalOnlyConfiguration.SignalOnlyMode,
                        symbol,
                        setupTimeframe,
                        inputSequence = inputLedger.Read(streamId).Checkpoint.LastIntakeSequence
                    })).ConfigureAwait(false);
                }

                using BinanceMarketWebSocketClient marketClient = new();
                marketClient.Diagnostic += diagnostic =>
                {
                    foreach ((NativePublicKlineFeed feed, _) in consumers)
                    {
                        if (diagnostic.StreamName is null
                            || diagnostic.StreamName == BinanceSubscription.Kline(symbol, feed.SetupTimeframe).StreamName
                            || diagnostic.StreamName == BinanceSubscription.Kline(symbol, feed.ContextTimeframe).StreamName)
                        {
                            feed.OnStreamDiagnostic(diagnostic);
                        }
                    }
                    if (diagnostic.Kind != BinanceStreamDiagnosticKind.Duplicate)
                    {
                        output.WriteLine(JsonSerializer.Serialize(new
                        {
                            kind = "public-stream-diagnostic",
                            symbol,
                            code = diagnostic.Kind.ToString()
                        }));
                    }
                };
                Task priceTask = RunSharedPriceAsync(symbol, consumers, marketClient,
                    signalJournal, output, runStop.Token);
                if (multiLane is null)
                {
                    await priceTask.ConfigureAwait(false);
                }
                else
                {
                    await RunMultiLaneAsync(priceTask, multiLane.RunAsync(runStop.Token),
                        runStop).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach ((_, SignalOnlyInstrumentConsumer consumer) in consumers)
                {
                    consumer.Dispose();
                }
            }
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

    internal static async Task RunSharedPriceAsync(string symbol,
        IReadOnlyList<(NativePublicKlineFeed Feed, SignalOnlyInstrumentConsumer Consumer)> consumers,
        BinanceMarketWebSocketClient marketClient, SqliteSignalJournalStore journal,
        TextWriter output, CancellationToken cancellationToken)
    {
        BinanceSubscription[] subscriptions = consumers
            .SelectMany(item => new[] { item.Feed.SetupTimeframe, item.Feed.ContextTimeframe })
            .Distinct(StringComparer.Ordinal)
            .Select(timeframe => BinanceSubscription.Kline(symbol, timeframe))
            .ToArray();
        await foreach (BinanceStreamEnvelope envelope in marketClient.ReadAsync(subscriptions,
            cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Payload is not BinanceKlineEvent kline
                || !StringComparer.Ordinal.Equals(kline.Symbol, symbol))
            {
                throw new InvalidDataException("The public candle stream has an unexpected symbol or event.");
            }
            foreach ((NativePublicKlineFeed feed, SignalOnlyInstrumentConsumer consumer) in consumers)
            {
                if (kline.Kline.Timeframe != feed.SetupTimeframe
                    && kline.Kline.Timeframe != feed.ContextTimeframe)
                {
                    continue;
                }
                await consumer.ProcessStreamEnvelopeAsync(envelope,
                    update => WritePublicCloseAsync(update, symbol, feed.StreamId, journal, output,
                        feed.SetupTimeframe, consumer.CaptureLiveMarketContext(update)),
                    () => output.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        kind = "public-stream-receiving",
                        symbol,
                        setupTimeframe = feed.SetupTimeframe
                    })), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task RunMultiLaneAsync(Task priceTask, Task laneTask,
        CancellationTokenSource runStop)
    {
        try
        {
            Task first = await Task.WhenAny(priceTask, laneTask).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            if (!runStop.IsCancellationRequested)
            {
                throw new IOException("A required public signal feed ended unexpectedly.");
            }
        }
        finally
        {
            runStop.Cancel();
            try
            {
                await Task.WhenAll(priceTask, laneTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runStop.IsCancellationRequested)
            {
            }
        }
    }

    internal static async Task WritePublicCloseAsync(NativePublicKlineFeedUpdate update,
        string symbol, string streamId, SqliteSignalJournalStore journal, TextWriter output,
        string setupTimeframe = "15m", SignalMarketContext? marketContext = null)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            kind = "public-close",
            symbol,
            setupTimeframe,
            status = update.Status.ToString(),
            timeframe = update.Identity?.Timeframe,
            marketContext,
            readiness = update.Evaluation is null ? null
                : update.Evaluation.IsReady ? "ready" : "unknown",
            reasons = update.Evaluation?.ReadinessReasons
        })).ConfigureAwait(false);
        if (update.Status != BinanceKlineIngestionStatus.Accepted
            || update.Identity?.Timeframe != setupTimeframe)
        {
            return;
        }

        SignalJournalSnapshot snapshot = journal.Read(streamId);
        foreach (SignalJournalDiagnostic diagnostic in snapshot.Diagnostics.Where(item =>
            StringComparer.Ordinal.Equals(item.InputEventId, update.Identity.EventId)
            || item.InputEventId.StartsWith($"scenario-confirm:{update.Identity.EventId}:",
                StringComparison.Ordinal)))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                kind = "signal-diagnostic",
                symbol,
                setupTimeframe,
                code = diagnostic.Code,
                reasons = diagnostic.ReasonCodes
            })).ConfigureAwait(false);
        }
        foreach (SignalJournalSignal item in snapshot.Signals.Where(item =>
            StringComparer.Ordinal.Equals(item.Signal.ReferencePrice.EventId, update.Identity.EventId)))
        {
            SignalMultiLaneAssessment? lanes = item.Evidence.MultiLaneInput is { } laneInput
                ? SignalMultiLaneResearchEvaluator.Evaluate(laneInput) : null;
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                kind = "signal",
                symbol,
                timeframe = item.Signal.Identity.SetupTimeframe,
                direction = item.Signal.Identity.Direction.ToString(),
                setup = item.Signal.Identity.Family.ToString(),
                decisionAt = item.Signal.DecisionAt.Value,
                signalId = item.Signal.Identity.SignalId,
                marketContext = item.Evidence.MarketContext,
                research = "RESEARCH",
                statisticalValidation = "UNKNOWN",
                laneB = lanes?.LaneB.ToString(),
                laneC = lanes?.LaneC.ToString()
            })).ConfigureAwait(false);
        }
    }
}
