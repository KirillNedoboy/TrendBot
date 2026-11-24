using System.Runtime.InteropServices;
using System.Text.Json;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

internal static class BybitDemoCommand
{
    internal const string CommandName = "bybit-demo";
    private static readonly string[] DefaultProfiles = ["signal-only.flow-25.example.json",
        "signal-only.donchian-flow-30m.example.json", "signal-only.donchian-flow.example.json"];

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (args.Length == 2 && args[1] is "--help" or "help")
        {
            await output.WriteLineAsync("bybit-demo check|run|service-approval-request --config <demo.json> [--flow <json>] [--donchian-30m <json>] [--donchian-1h <json>] [--external-signals] [--telegram] [--confirm-demo-orders] [--service-approval <absolute-path>]");
            await output.WriteLineAsync("check is public-only. run starts three Binance profiles unless --external-signals is supplied. Interactive mode confirms the first entry; explicit service mode blocks entries until a matching operator-managed approval exists. service-approval-request prints a non-authorizing request without network calls. Demo endpoints only; stopping preserves exchange-side protection.");
            return 0;
        }
        bool operatorStopped = false;
        try
        {
            if (args.Length < 4 || args[1] is not ("check" or "run" or "service-approval-request")) throw new ArgumentException("Invalid demo command.", nameof(args));
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            bool confirm = false;
            bool external = false;
            bool telegram = false;
            for (int index = 2; index < args.Length; index++)
            {
                string name = args[index];
                if (name == "--confirm-demo-orders" && !confirm) { confirm = true; continue; }
                if (name == "--external-signals" && !external) { external = true; continue; }
                if (name == "--telegram" && !telegram) { telegram = true; continue; }
                if (name is not ("--config" or "--flow" or "--donchian-30m" or "--donchian-1h" or "--service-approval")
                    || ++index >= args.Length || !values.TryAdd(name, args[index])) throw new ArgumentException("Invalid demo option.", nameof(args));
            }
            if (!values.TryGetValue("--config", out string? configPath)) throw new ArgumentException("The demo configuration is required.", nameof(args));
            if (telegram && external) throw new ArgumentException("Telegram delivery requires supervised public profiles.", nameof(args));
            BybitDemoConfiguration config = BybitDemoConfiguration.Parse(await File.ReadAllTextAsync(configPath, cancellationToken));
            string[] names = ["--flow", "--donchian-30m", "--donchian-1h"];
            string[] paths = names.Select((name, index) => values.GetValueOrDefault(name, DefaultProfiles[index])).ToArray();
            SignalOnlyConfiguration[] profiles = paths.Select(SignalOnlyConfiguration.Load).ToArray();
            HashSet<string> symbols = profiles[0].Symbols.ToHashSet(StringComparer.Ordinal);
            StringComparer pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (symbols.Count != 25 || profiles.Any(profile => !symbols.SetEquals(profile.Symbols))
                || profiles.Where((profile, index) => !pathComparer.Equals(Path.GetFullPath(profile.SignalJournalPath), config.SignalJournalPaths[index])).Any()
                || profiles.Any(profile => pathComparer.Equals(Path.GetFullPath(profile.InputLedgerPath), config.StatePath)))
                throw new InvalidDataException("The demo and public profiles do not share the selected isolated universe and journals.");
            bool serviceMode = values.TryGetValue("--service-approval", out string? approvalPath);
            if (serviceMode && !Path.IsPathFullyQualified(approvalPath!))
                throw new ArgumentException("The service approval path must be absolute.", nameof(args));
            if (args[1] == "check" && serviceMode || args[1] == "service-approval-request" && !serviceMode)
                throw new ArgumentException("Service approval options do not match the selected command.", nameof(args));
            if (args[1] == "check")
            {
                using HttpClient timeHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
                BybitDemoClock checkClock = new();
                await checkClock.RefreshAsync(timeHttp, config.ReceiveWindowMilliseconds, cancellationToken);
                using BybitDemoPublicMarketClient publicMarket = BybitDemoPublicMarketClient.Create(checkClock);
                foreach (string symbol in symbols.Order(StringComparer.Ordinal))
                {
                    BybitDemoInstrument instrument = await publicMarket.GetInstrumentAsync(symbol, cancellationToken);
                    BybitDemoPublicQuote quote = await publicMarket.GetQuoteAsync(symbol, cancellationToken);
                    bool split = true;
                    _ = BybitDemoQuantityPlanner.Create(config.TargetNotionalUsdt, quote.Ask, instrument.QuantityStep,
                        instrument.MinimumQuantity, instrument.MinimumNotional, instrument.MaximumMarketQuantity, split);
                }
                await output.WriteLineAsync("bybit-demo: public validation passed for 25 symbols; no private credentials or orders used.");
                return 0;
            }
            if (args[1] == "run" && (!confirm || Console.IsInputRedirected && !serviceMode))
            {
                await error.WriteLineAsync("bybit-demo: run requires --confirm-demo-orders and either an interactive terminal or explicit --service-approval. No order was sent.");
                return 2;
            }
            string? credentialDirectory = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
            BybitDemoCredentials credentials = BybitDemoCredentials.Load(credentialDirectory,
                credentialDirectory is null ? Environment.GetEnvironmentVariable("TRADINGBOT_BYBIT_DEMO_API_KEY") : null,
                credentialDirectory is null ? Environment.GetEnvironmentVariable("TRADINGBOT_BYBIT_DEMO_API_SECRET") : null);
            if (telegram)
                _ = TelegramCredentials.Load(credentialDirectory,
                    credentialDirectory is null ? Environment.GetEnvironmentVariable("TRADINGBOT_TELEGRAM_BOT_TOKEN") : null,
                    credentialDirectory is null ? Environment.GetEnvironmentVariable("TRADINGBOT_TELEGRAM_CHAT_ID") : null);
            BybitDemoServiceApproval? serviceApproval = serviceMode
                ? BybitDemoServiceApproval.Create(approvalPath!, config, profiles, credentials.Key, external, AppContext.BaseDirectory) : null;
            if (args[1] == "service-approval-request")
            {
                await output.WriteLineAsync(serviceApproval!.PendingRequestJson());
                return 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(config.StatePath)!);
            using FileStream executionLock = new(config.StatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(config.StatePath, config.MaximumPositions);
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? cancellationDispatch = null;
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                operatorStopped = true;
                _ = RequestSignalStopAsync(stop, ref cancellationDispatch);
            };
            Console.CancelKeyPress += cancel;
            using PosixSignalRegistration? termination = OperatingSystem.IsWindows() ? null
                : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    operatorStopped = true;
                    _ = RequestSignalStopAsync(stop, ref cancellationDispatch);
                });
            List<FileStream> profileLocks = [];
            List<Task> signalTasks = [];
            try
            {
                if (!external)
                {
                    foreach (SignalOnlyConfiguration profile in profiles)
                    {
                        string directory = Path.GetDirectoryName(Path.GetFullPath(profile.InputLedgerPath))!;
                        Directory.CreateDirectory(directory);
                        profileLocks.Add(new FileStream(Path.Combine(directory, "research-profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                    }
                    for (int index = 0; index < paths.Length; index++)
                    {
                        string path = paths[index];
                        string deliveryPath = Path.Combine(Path.GetDirectoryName(config.StatePath)!, "telegram", $"profile-{index}.sqlite");
                        if (telegram) Directory.CreateDirectory(Path.GetDirectoryName(deliveryPath)!);
                        string[] signalArgs = telegram
                            ? ["signal-only", "--config", path, "--telegram-state", deliveryPath]
                            : ["signal-only", "--config", path];
                        signalTasks.Add(Task.Run(() => SignalOnlyCommand.RunAsync(signalArgs,
                            TextWriter.Synchronized(output), TextWriter.Synchronized(error), stop.Token), stop.Token));
                    }
                }
                using HttpClient timeHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
                BybitDemoClock clock = new();
                await clock.RefreshAsync(timeHttp, config.ReceiveWindowMilliseconds, stop.Token);
                DateTimeOffset nextCalibration = DateTimeOffset.UtcNow.AddSeconds(30);
                bool clockHealthy = true;
                BybitDemoSession session = new(credentials.Key, credentials.Secret, clock, config.PrivateStreamFreshnessSeconds);
                Task privateTask = session.RunAsync(stop.Token);
                using BybitDemoRestClient rest = new(new BybitDemoThrottledHandler(), credentials.Key, credentials.Secret, clock, config.ReceiveWindowMilliseconds);
                using BybitDemoPublicMarketClient market = BybitDemoPublicMarketClient.Create(clock);
                using HttpClient binance = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
                bool authorized = false;
                bool? lastServiceApproval = null;
                BybitDemoRuntime runtime = new(store, rest, market, new(binance, clock), config, symbols,
                    clock, () => { BybitDemoPrivateHealth health = session.Health(); return health with { Ready = health.Ready && clockHealthy }; },
                    () => session.ValidateEvents(store), ApproveAsync);
                string? lastState = null;
                await output.WriteLineAsync("bybit-demo: started; waiting for Binance profiles, private Demo stream and account reconciliation. No production trading is available.");
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (signalTasks.Any(task => task.IsCompleted) || privateTask.IsCompleted)
                            throw new InvalidOperationException("A supervised data source stopped.");
                        if (DateTimeOffset.UtcNow >= nextCalibration)
                        {
                            try { await clock.RefreshAsync(timeHttp, config.ReceiveWindowMilliseconds, stop.Token); clockHealthy = true; }
                            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException
                                || exception is OperationCanceledException && !stop.IsCancellationRequested)
                            {
                                clockHealthy = false;
                                await error.WriteLineAsync("bybit-demo: clock calibration unavailable; entries blocked, existing positions still monitored.");
                            }
                            nextCalibration = DateTimeOffset.UtcNow.AddSeconds(30);
                        }
                        try
                        {
                            BybitDemoRuntimeResult state = await runtime.StepAsync(stop.Token);
                            string json = JsonSerializer.Serialize(new { mode = "bybit-demo", state = state.State, positions = state.Positions, submitted = state.Submissions });
                            if (json != lastState) { await output.WriteLineAsync(json); lastState = json; }
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or HttpRequestException
                            or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException
                            || exception is OperationCanceledException && !stop.IsCancellationRequested)
                        {
                            await error.WriteLineAsync("bybit-demo: entries blocked; reconciliation/data unavailable (" + exception.GetType().Name + ").");
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                    }
                }
                finally
                {
                    await output.WriteLineAsync("bybit-demo: shutdown waiting for private stream.");
                    try { await Task.WhenAll(privateTask, RequestSignalStopAsync(stop, ref cancellationDispatch)); }
                    catch (OperationCanceledException) { }
                    await output.WriteLineAsync("bybit-demo: private stream stopped.");
                }
                async Task<bool> ApproveAsync(BybitDemoPreparedEntry entry, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested();
                    if (serviceApproval is not null)
                    {
                        bool allowed = serviceApproval.IsAuthorized(clock.GetUtcNow());
                        if (lastServiceApproval != allowed)
                            await output.WriteLineAsync(allowed
                                ? "bybit-demo: matching service approval observed; entries remain subject to account/data preflight."
                                : "bybit-demo: service approval missing or invalid; new entries blocked, owned-position recovery continues.");
                        lastServiceApproval = allowed;
                        return allowed;
                    }
                    if (authorized) return true;
                    await output.WriteLineAsync($"DEMO ONLY: enable ongoing market execution for the selected five strategy/timeframe combinations on 25 pairs, target {config.TargetNotionalUsdt} USDT, maximum {config.MaximumPositions} positions. First candidate: {entry.Reservation.Symbol} {entry.Reservation.Direction}, quantity {entry.Reservation.Quantity}. Existing isolated margin and leverage <= {config.MaximumLeverage} must match. SL/TP and market slippage are not loss guarantees.");
                    await output.WriteLineAsync("Type ENABLE BYBIT DEMO to authorize this session, or anything else to stop without opening an entry:");
                    authorized = await Console.In.ReadLineAsync(token) == "ENABLE BYBIT DEMO";
                    if (!authorized) { operatorStopped = true; stop.Cancel(); }
                    return authorized;
                }
            }
            finally
            {
                await output.WriteLineAsync("bybit-demo: shutdown waiting for public profiles.");
                signalTasks.Add(RequestSignalStopAsync(stop, ref cancellationDispatch));
                try
                {
                    try { await Task.WhenAll(signalTasks); } catch (OperationCanceledException) { }
                }
                finally
                {
                    foreach (FileStream profileLock in profileLocks) profileLock.Dispose();
                    Console.CancelKeyPress -= cancel;
                    await output.WriteLineAsync("bybit-demo: stopped; existing exchange-side protection was not cancelled.");
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (operatorStopped || cancellationToken.IsCancellationRequested) { return 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException
            or InvalidOperationException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException or AggregateException)
        {
            await error.WriteLineAsync("bybit-demo: configuration, lock or data validation failed (" + exception.GetType().Name + "). No implicit order retry.");
            return 2;
        }
    }

    internal static Task RequestSignalStopAsync(CancellationTokenSource stop, ref Task? dispatch)
    {
        // Cancellation callbacks may finish tasks and unregister signal handlers.
        lock (stop) return dispatch ??= stop.CancelAsync();
    }
}

internal sealed class BybitDemoThrottledHandler : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _next;
    public BybitDemoThrottledHandler() : base(new SocketsHttpHandler { AllowAutoRedirect = false }) { }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan delay = _next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            _next = DateTimeOffset.UtcNow.AddMilliseconds(150);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _gate.Dispose();
    }
}
