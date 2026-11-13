using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>A separately bounded, public-only signal run and its restart evidence.</summary>
public static class SignalOnlyBoundedRun
{
    private const int MaximumEnvelopeBytes = 4 * 1024 * 1024;
    private const long MinimumStorageBytes = 32L * 1024 * 1024;
    private const long AuxiliaryReserveBytes = 8L * 1024 * 1024;
    private const string OutcomePolicyVersion = "decision-clock-15m-observed-only-v1";
    private const string OutcomeCancellationPolicy = "scenario-cancel-availability";
    private const string OutcomeTargetPolicy = "no-declared-target";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ResumeFiles = new(StringComparer.Ordinal)
    {
        "run.lock", "run-declaration.json", "run.jsonl", "pit-start.json", "pit-resume.json",
        "native-bars.sqlite", "native-bars.sqlite-journal", "signal-journal.sqlite",
        "signal-journal.sqlite-journal", "replay-journal.sqlite", "replay-journal.sqlite-journal"
    };

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (args.Length is not (6 or 7) || args[0] != "signal-only" || args[1] != "bounded-run"
            || args[2] != "--envelope" || args[4] != "--sha256"
            || args[5].Length != 64 || !args[5].All(Uri.IsHexDigit)
            || args.Length == 7 && args[6] is not ("--resume" or "--pause-after-first-close"))
        {
            await error.WriteLineAsync("Usage: signal-only bounded-run --envelope <file> --sha256 <hash> [--resume|--pause-after-first-close]").ConfigureAwait(false);
            return 2;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo file = new(args[3]);
            if (file.Length > MaximumEnvelopeBytes)
                throw new InvalidDataException("The envelope exceeds its size bound.");
            byte[] bytes = await File.ReadAllBytesAsync(args[3], cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaximumEnvelopeBytes)
                throw new InvalidDataException("The envelope exceeds its size bound.");
            string hash = Hash(bytes);
            if (!StringComparer.OrdinalIgnoreCase.Equals(hash, args[5]))
            {
                await output.WriteLineAsync("{\"status\":\"blocked\",\"reason\":\"envelope-hash-mismatch\"}").ConfigureAwait(false);
                return 1;
            }

            SignalOnlyBoundedPreflight.BoundedSignalEnvelope envelope = SignalOnlyBoundedPreflight.Parse(bytes);
            bool resume = args.Length == 7 && args[6] == "--resume";
            bool pause = args.Length == 7 && args[6] == "--pause-after-first-close";
            string root = Path.GetFullPath(envelope.Wire.RunDirectory ?? throw new InvalidDataException());
            if (resume) ValidateResumeDirectory(root);
            RunMarker? marker = resume ? ReadMarker(root) : null;
            DateTimeOffset startedAt = marker?.StartedAtUtc ?? DateTimeOffset.UtcNow;
            string[] reasons = SignalOnlyBoundedPreflight.Validate(envelope, startedAt, allowExistingRun: resume);
            if (reasons.Length != 0)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(new { status = "blocked", reasons })).ConfigureAwait(false);
                return 1;
            }

            SignalOnlyConfiguration configuration = envelope.Configuration!;
            SignalOnlyBoundedPreflight.EnvelopeWire wire = envelope.Wire;
            if (configuration.Symbols.Count != 1 || wire.MaxStoreBytes is not { } storeBudget
                || storeBudget < MinimumStorageBytes || wire.OutcomeHorizonSeconds != 900
                || 2 * (storeBudget / 8 + 2 * (storeBudget / 16))
                    + 4L * wire.MaxRestResponseBytes!.Value + storeBudget / 16
                    + AuxiliaryReserveBytes > storeBudget
                || (long)wire.MaxSetupRestRows!.Value + wire.MaxContextRestRows!.Value
                    + wire.RequiredSetupCloses!.Value + wire.RequiredContextCloses!.Value > wire.MaxLedgerReceipts)
            {
                await output.WriteLineAsync("{\"status\":\"blocked\",\"reason\":\"unsupported-run-budget\"}").ConfigureAwait(false);
                return 1;
            }

            string[] assemblies = AssemblyHashes();
            if (resume && (marker is null || marker.FormatVersion != 1 || marker.RunId != wire.RunId
                || marker.EnvelopeSha256 != hash || marker.AssemblyHashes is null
                || !marker.AssemblyHashes.SequenceEqual(assemblies, StringComparer.Ordinal)
                || marker.OutcomePolicyVersion != OutcomePolicyVersion
                || marker.OutcomeCancellationPolicy != OutcomeCancellationPolicy
                || marker.OutcomeTargetPolicy != OutcomeTargetPolicy
                || marker.OutcomeHorizonSeconds != wire.OutcomeHorizonSeconds
                || marker.MaximumStoreBytes != storeBudget || marker.InputBudget != storeBudget / 8
                || marker.JournalBudget != storeBudget / 16 || marker.LogBudget != storeBudget / 16
                || marker.StartedAtUtc.Offset != TimeSpan.Zero || marker.StartedAtUtc > DateTimeOffset.UtcNow))
            {
                await output.WriteLineAsync("{\"status\":\"blocked\",\"reason\":\"restart-identity-mismatch\"}").ConfigureAwait(false);
                return 1;
            }

            DateTimeOffset deadline = startedAt.AddSeconds(wire.MaxDurationSeconds!.Value);
            if (DateTimeOffset.UtcNow >= deadline)
            {
                await output.WriteLineAsync("{\"status\":\"blocked\",\"reason\":\"run-deadline-expired\"}").ConfigureAwait(false);
                return 1;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await ExecuteAsync(envelope, hash, assemblies, root, startedAt, deadline,
                resume, pause, output, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await output.WriteLineAsync("{\"status\":\"cancelled\"}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or NotSupportedException
            or OverflowException or HttpRequestException)
        {
            await error.WriteLineAsync($"signal-only bounded-run: stopped ({exception.GetType().Name})").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> ExecuteAsync(SignalOnlyBoundedPreflight.BoundedSignalEnvelope envelope,
        string hash, string[] assemblies, string root, DateTimeOffset startedAt, DateTimeOffset deadline,
        bool resume, bool pause, TextWriter output, CancellationToken cancellationToken)
    {
        SignalOnlyBoundedPreflight.EnvelopeWire wire = envelope.Wire;
        SignalOnlyConfiguration configuration = envelope.Configuration!;
        if (!resume)
        {
            if (Directory.Exists(root))
                throw new IOException("The run directory already exists.");
            Directory.CreateDirectory(root);
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("The run directory is not empty.");
        }

        string lockPath = Path.Combine(root, "run.lock");
        using FileStream runLock = new(lockPath, resume ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        long maximumStore = wire.MaxStoreBytes!.Value;
        long inputBudget = maximumStore / 8;
        long journalBudget = maximumStore / 16;
        long logBudget = maximumStore / 16;
        if (!resume)
        {
            RunMarker marker = new(1, wire.RunId!, hash, startedAt, assemblies, pause,
                OutcomePolicyVersion, OutcomeCancellationPolicy, OutcomeTargetPolicy,
                wire.OutcomeHorizonSeconds!.Value, maximumStore, inputBudget, journalBudget, logBudget);
            WriteNew(Path.Combine(root, "run-declaration.json"), JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions));
        }

        using RunLog log = new(Path.Combine(root, "run.jsonl"), resume, logBudget, output);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenSource monitorStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            throw new InvalidOperationException("The run deadline elapsed before startup.");
        stop.CancelAfter(remaining);
        RunProgress progress = new(startedAt, wire, configuration.Profile, stop);
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; progress.Stop("cancelled"); };
        Console.CancelKeyPress += handler;
        Task? monitor = null;
        bool awaitingOutcome = false;
        try
        {
            log.Write(new
            {
                kind = "start",
                wire.RunId,
                envelopeSha256 = hash,
                startedAtUtc = startedAt,
                segmentStartedAtUtc = DateTimeOffset.UtcNow,
                resumed = resume,
                inputBudget,
                journalBudget,
                maximumStore,
                assemblies
            });
            monitor = MonitorAsync(root, log, progress, monitorStop.Token);
            using HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false });
            BinancePublicRestClient rest = new(http, new BinanceRestOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(wire.RequestTimeoutSeconds!.Value),
                MaxResponseBytes = wire.MaxRestResponseBytes!.Value
            });
            BinanceExchangeInfo exchangeInfo = await rest.GetExchangeInfoAsync(stop.Token).ConfigureAwait(false);
            MetadataCandidateFilterResult filtered = MetadataCandidateFilter.Evaluate(exchangeInfo.ToCoreEligibilityMetadata());
            string symbol = configuration.Symbols[0];
            if (!filtered.Candidates.Any(item => StringComparer.Ordinal.Equals(item.InstrumentId.Value, symbol)))
                throw new InvalidOperationException("The declared symbol is not eligible in current public metadata.");
            byte[] pit = JsonSerializer.SerializeToUtf8Bytes(new
            {
                receiveTime = exchangeInfo.ReceiveTime?.Value,
                availabilityTime = exchangeInfo.AvailabilityTime?.Value,
                exchangeInfo
            }, JsonOptions);
            if (pit.LongLength > wire.MaxRestResponseBytes.Value * 2L)
                throw new InvalidDataException("The exchange metadata snapshot exceeds its budget.");
            string pitName = resume ? "pit-resume.json" : "pit-start.json";
            WriteNew(Path.Combine(root, pitName), pit);
            log.Write(new { kind = "public-metadata", symbol, sha256 = Hash(pit), eligible = true });

            int sqliteTimeout = Math.Min(wire.RequestTimeoutSeconds.Value, wire.GracefulStopSeconds!.Value);
            using SqliteNativeBarInputLedger ledger = SqliteNativeBarInputLedger.OpenBounded(
                configuration.InputLedgerPath, inputBudget, sqliteTimeout);
            using SqliteSignalJournalStore journal = SqliteSignalJournalStore.OpenBounded(
                configuration.SignalJournalPath, journalBudget, sqliteTimeout);
            string stream = $"binance-usdm-native-kline:{symbol}";
            NativeBarInputLedgerSnapshot existing = ledger.Read(stream);
            if (resume && existing.Receipts.Count == 0 || !resume && existing.Receipts.Count != 0)
                throw new InvalidDataException("The run input state does not match its restart mode.");
            NativePublicKlineFeed feed = resume
                ? NativePublicKlineFeed.RestoreFromLedger(rest, symbol, configuration.Profile, ledger)
                : await NativePublicKlineFeed.WarmupBoundedAsync(rest, symbol, configuration.Profile, ledger,
                    new NativePublicKlineRestHistoryBudget(wire.MaxRestPagesPerTimeframe!.Value, wire.MaxSetupRestRows!.Value),
                    new NativePublicKlineRestHistoryBudget(wire.MaxRestPagesPerTimeframe.Value, wire.MaxContextRestRows!.Value),
                    cancellationToken: stop.Token).ConfigureAwait(false);
            existing = ledger.Read(stream);
            progress.ReceiptCount = existing.Receipts.Count;
            CountPriorCloses(feed, existing, progress);
            IReadOnlyList<ScreenerBar> setupHistory = feed.SetupHistory;
            IReadOnlyList<ScreenerBar> contextHistory = feed.ContextHistory;
            if (!IsContiguous(setupHistory, TimeSpan.FromMinutes(15))
                || !IsContiguous(contextHistory, TimeSpan.FromHours(1))
                || setupHistory[0].Bar.OpenTime.Value != configuration.Profile.Setup!.HistoryStartUtc
                || contextHistory[0].Bar.OpenTime.Value != configuration.Profile.Context!.HistoryStartUtc)
                throw new InvalidDataException("The declared public history contains a gap.");
            long lastSetupOpen = setupHistory[^1].Bar.OpenTime.Value.ToUnixTimeMilliseconds();
            long lastContextOpen = contextHistory[^1].Bar.OpenTime.Value.ToUnixTimeMilliseconds();
            progress.LastSetupCloseTicks = setupHistory[^1].Bar.CloseTime.Value.UtcTicks;
            progress.LastContextCloseTicks = contextHistory[^1].Bar.CloseTime.Value.UtcTicks;
            progress.LastSetupAvailabilityTicks = setupHistory[^1].AvailabilityTime.Value.UtcTicks;
            progress.LastContextAvailabilityTicks = contextHistory[^1].AvailabilityTime.Value.UtcTicks;
            using SignalOnlyInstrumentConsumer consumer = new(symbol, configuration.Profile,
                configuration.ScenarioPolicies, feed, ledger, journal);
            using BinanceMarketWebSocketClient market = new(new ClientWebSocketTransportFactory(),
                new BinanceWebSocketOptions
                {
                    MaxFrameBytes = wire.MaxFrameBytes!.Value,
                    MaxMessageBytes = wire.MaxMessageBytes!.Value,
                    MaxReconnectAttempts = wire.MaxReconnectAttempts!.Value,
                    InitialReconnectDelay = TimeSpan.FromSeconds(1),
                    MaxReconnectDelay = TimeSpan.FromSeconds(wire.MaxReconnectDowntimeSeconds!.Value),
                    ReceiveTimeout = TimeSpan.FromSeconds(wire.RequestTimeoutSeconds.Value)
                }, TimeProvider.System);
            AttachFailClosedDiagnostics(market, progress.Stop);
            log.Write(new
            {
                kind = "warmup",
                setup = setupHistory.Count,
                context = contextHistory.Count,
                setupPages = feed.SetupRestPagesRequested,
                contextPages = feed.ContextRestPagesRequested,
                receiptCount = progress.ReceiptCount
            });
            bool pausePending = false;
            DateTimeOffset? completionCutoff = null;

            await foreach (BinanceStreamEnvelope input in feed.ReadEnvelopesAsync(market, stop.Token).ConfigureAwait(false))
            {
                if (input.Payload is not BinanceKlineEvent item || !item.Kline.IsClosed)
                    continue;
                if (item.Symbol != symbol || item.Kline.Timeframe is not ("15m" or "1h"))
                {
                    progress.Stop("unexpected-public-stream");
                    break;
                }
                DateTimeOffset end = DateTimeOffset.FromUnixTimeMilliseconds(checked(item.Kline.CloseTimeMilliseconds + 1));
                if (end <= startedAt)
                {
                    progress.Stop("pre-run-close");
                    break;
                }
                TimeSpan delay = end - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.FromSeconds(wire.RequestTimeoutSeconds.Value))
                {
                    progress.Stop("future-close-outside-bound");
                    break;
                }
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                long interval = item.Kline.Timeframe == "15m" ? 900_000 : 3_600_000;
                long previous = item.Kline.Timeframe == "15m" ? lastSetupOpen : lastContextOpen;
                if (item.Kline.OpenTimeMilliseconds > previous + interval)
                {
                    progress.Stop("public-history-gap");
                    break;
                }
                if (progress.ReceiptCount >= wire.MaxLedgerReceipts!.Value)
                {
                    progress.Stop("ledger-receipt-budget");
                    break;
                }
                stop.Token.ThrowIfCancellationRequested();
                NativePublicKlineFeedUpdate update = await consumer.ProcessAsync(input, stop.Token).ConfigureAwait(false);
                progress.ReceiptCount++;
                stop.Token.ThrowIfCancellationRequested();
                if (update.Status == BinanceKlineIngestionStatus.RevisionConflict)
                {
                    progress.Stop("public-revision-conflict");
                    break;
                }
                if (update.Status != BinanceKlineIngestionStatus.Accepted)
                    continue;
                if (item.Kline.OpenTimeMilliseconds != previous + interval)
                {
                    progress.Stop("late-public-close");
                    break;
                }
                if (item.Kline.Timeframe == "15m")
                {
                    lastSetupOpen = Math.Max(lastSetupOpen, item.Kline.OpenTimeMilliseconds);
                    progress.SetupCloses++;
                    progress.LastSetupCloseTicks = end.UtcTicks;
                    progress.LastSetupAvailabilityTicks = input.AvailabilityTime.Value.UtcTicks;
                    SignalJournalSnapshot current = journal.Read(stream);
                    bool degraded = current.Diagnostics.Any(diagnostic =>
                        SignalOnlyInstrumentConsumer.SourceEventId(diagnostic.InputEventId) == update.Identity!.EventId
                        && diagnostic.Code != "no-confirmed-setup");
                    progress.Quality = update.Evaluation is { IsReady: true } evaluation
                        && evaluation.LaneA.SetupObservations.IsComplete && !degraded ? "ready" : "unknown";
                    log.Write(new
                    {
                        kind = "committed-decision",
                        symbol,
                        quality = progress.Quality,
                        journal = JsonSerializer.Deserialize<JsonElement>(SignalOnlyJournalRenderer.RenderJson(stream, current))
                    });
                    if (pause && progress.SetupCloses >= 1)
                        pausePending = true;
                }
                else
                {
                    lastContextOpen = Math.Max(lastContextOpen, item.Kline.OpenTimeMilliseconds);
                    progress.ContextCloses++;
                    progress.LastContextCloseTicks = end.UtcTicks;
                    progress.LastContextAvailabilityTicks = input.AvailabilityTime.Value.UtcTicks;
                }
                DateTimeOffset processedAt = DateTimeOffset.UtcNow;
                bool completeCoverage = HasCompleteCoverage(startedAt, processedAt,
                    progress.SetupCloses, progress.ContextCloses);
                if (pausePending && completeCoverage)
                {
                    progress.SetSuccess("planned-restart");
                    break;
                }
                if (completeCoverage && progress.SetupCloses >= wire.RequiredSetupCloses!.Value
                    && progress.ContextCloses >= wire.RequiredContextCloses!.Value)
                {
                    SignalJournalSnapshot current = journal.Read(stream);
                    awaitingOutcome = !OutcomeHorizonsObserved(current, feed.SetupHistory,
                        processedAt, wire.OutcomeHorizonSeconds!.Value);
                    if (!awaitingOutcome)
                    {
                        completionCutoff = processedAt;
                        progress.SetSuccess("observations-complete");
                        break;
                    }
                }
            }
            DateTimeOffset stoppingAt = completionCutoff ?? DateTimeOffset.UtcNow;
            monitorStop.Cancel();
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) when (monitorStop.IsCancellationRequested) { }
            monitor = null;
            if (progress.Reason is null)
                progress.SetSuccess(cancellationToken.IsCancellationRequested ? "cancelled" : "public-stream-ended");
            stop.Token.ThrowIfCancellationRequested();
            if (progress.Reason == "planned-restart")
            {
                log.Write(new
                {
                    kind = "stop",
                    atUtc = DateTimeOffset.UtcNow,
                    reason = progress.Reason,
                    progress.SetupCloses,
                    progress.ContextCloses
                });
                return 0;
            }
            if (progress.Reason != "observations-complete")
            {
                log.Write(new { kind = "stop", reason = progress.Reason, progress.SetupCloses, progress.ContextCloses });
                return 1;
            }
            long expectedSetup = ExpectedCloses(startedAt, stoppingAt, 900);
            long expectedContext = ExpectedCloses(startedAt, stoppingAt, 3600);
            if (progress.SetupCloses != expectedSetup || progress.ContextCloses != expectedContext)
            {
                progress.Stop("prospective-coverage-incomplete");
                log.Write(new
                {
                    kind = "stop",
                    reason = "prospective-coverage-incomplete",
                    quality = progress.Quality,
                    setupExpected = expectedSetup,
                    setupObserved = progress.SetupCloses,
                    contextExpected = expectedContext,
                    contextObserved = progress.ContextCloses
                });
                return 1;
            }

            SignalJournalSnapshot live = journal.Read(stream);
            string liveJson = SerializeJournalForReplay(live);
            string replayPath = Path.Combine(root, "replay-journal.sqlite");
            using SqliteSignalJournalStore replayJournal = SqliteSignalJournalStore.OpenBounded(replayPath, journalBudget, sqliteTimeout);
            NativePublicKlineFeed replayFeed = NativePublicKlineFeed.RestoreFromLedger(rest, symbol, configuration.Profile, ledger);
            using (SignalOnlyInstrumentConsumer replay = new(symbol, configuration.Profile,
                configuration.ScenarioPolicies, replayFeed, ledger, replayJournal))
            {
                string replayJson = SerializeJournalForReplay(replayJournal.Read(stream));
                if (!StringComparer.Ordinal.Equals(liveJson, replayJson))
                    throw new InvalidDataException("The causal replay differs from the live journal.");
            }
            DateTimeOffset observedThrough = DateTimeOffset.UtcNow;
            int outcomes = ObserveOutcomes(root, feed.SetupHistory, live, observedThrough,
                wire.OutcomeHorizonSeconds!.Value, maximumStore / 16);
            using Process completedProcess = Process.GetCurrentProcess();
            if (stop.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline
                || DateTimeOffset.UtcNow - stoppingAt > TimeSpan.FromSeconds(wire.GracefulStopSeconds!.Value)
                || TotalBytes(root) > maximumStore || completedProcess.WorkingSet64 > wire.MaxWorkingSetBytes)
                throw new InvalidOperationException("The run exceeded its declared completion bounds.");
            log.Write(new
            {
                kind = "completed",
                atUtc = DateTimeOffset.UtcNow,
                stoppingAtUtc = stoppingAt,
                progress.SetupCloses,
                progress.ContextCloses,
                setupExpected = expectedSetup,
                contextExpected = expectedContext,
                signals = live.Signals.Count,
                diagnostics = live.Diagnostics.Count,
                outcomes,
                replaySha256 = Hash(Encoding.UTF8.GetBytes(liveJson)),
                inputSha256 = HashFile(configuration.InputLedgerPath),
                journalSha256 = HashFile(configuration.SignalJournalPath),
                quality = progress.Quality,
                publication = "not-observed",
                composition = "public-only"
            });
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            progress.Quality = "unknown";
            string reason = progress.Reason ?? (cancellationToken.IsCancellationRequested
                ? "cancelled" : awaitingOutcome ? "outcome-horizon-not-observed" : "deadline-or-cancellation");
            log.Write(new { kind = "stop", reason, quality = progress.Quality });
            return 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            progress.Stop("runtime-failure");
            log.Write(new { kind = "stop", reason = progress.Reason, quality = progress.Quality, failure = exception.GetType().Name });
            return 1;
        }
        finally
        {
            stop.Cancel();
            try
            {
                if (monitor is not null)
                {
                    try { await monitor.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }
    }

    internal static string SerializeJournalForReplay(SignalJournalSnapshot snapshot) =>
        CoreJsonSerializer.Serialize(snapshot);

    internal static bool HasCompleteCoverage(DateTimeOffset startedAt, DateTimeOffset observedAt,
        long setupCloses, long contextCloses) =>
        setupCloses == ExpectedCloses(startedAt, observedAt, 900)
        && contextCloses == ExpectedCloses(startedAt, observedAt, 3600);

    internal static bool OutcomeHorizonsObserved(SignalJournalSnapshot snapshot,
        IReadOnlyList<ScreenerBar> history, DateTimeOffset now, int horizonSeconds)
    {
        if (snapshot.Signals.Count == 0)
            return true;
        DateTimeOffset? observedThrough = null;
        for (int index = history.Count - 1; index >= 0; index--)
        {
            ScreenerBar bar = history[index];
            if (bar.Bar.CloseTime.Value <= now && bar.EventTime.Value <= now && bar.AvailabilityTime.Value <= now)
            {
                observedThrough = bar.Bar.CloseTime.Value;
                break;
            }
        }
        foreach (SignalJournalSignal signal in snapshot.Signals)
        {
            DateTimeOffset horizon = signal.Signal.DecisionAt.Value.AddSeconds(horizonSeconds);
            if (horizon > now || observedThrough is null || horizon > observedThrough.Value)
                return false;
        }
        return true;
    }

    internal static SignalOutcomeBar[] CreateOutcomeBars(IReadOnlyList<ScreenerBar> history)
    {
        SignalOutcomeBar[] bars = new SignalOutcomeBar[history.Count];
        for (int index = 0; index < history.Count; index++)
        {
            ScreenerBar item = history[index];
            DateTimeOffset availableAt = item.AvailabilityTime.Value;
            if (availableAt < item.Bar.CloseTime.Value) availableAt = item.Bar.CloseTime.Value;
            if (availableAt < item.EventTime.Value) availableAt = item.EventTime.Value;
            bars[index] = new SignalOutcomeBar(
                item.Bar.OpenTime.Value, item.Bar.CloseTime.Value, availableAt,
                item.Bar.Open.Value, item.Bar.High.Value, item.Bar.Low.Value, item.Bar.Close.Value);
        }
        return bars;
    }

    internal static void AttachFailClosedDiagnostics(BinanceMarketWebSocketClient market, Action<string> stop)
    {
        market.Diagnostic += diagnostic =>
        {
            if (diagnostic.Kind is BinanceStreamDiagnosticKind.Disconnect or BinanceStreamDiagnosticKind.Gap
                or BinanceStreamDiagnosticKind.IdentityConflict or BinanceStreamDiagnosticKind.Stale
                or BinanceStreamDiagnosticKind.Late or BinanceStreamDiagnosticKind.ParseError
                or BinanceStreamDiagnosticKind.FrameTooLarge or BinanceStreamDiagnosticKind.BufferOverflow)
                stop("public-stream-" + diagnostic.Kind.ToString().ToLowerInvariant());
        };
    }

    private static async Task MonitorAsync(string root, RunLog log, RunProgress progress, CancellationToken token)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(progress.Wire.HealthCheckIntervalSeconds!.Value));
        try
        {
            do
            {
                using Process process = Process.GetCurrentProcess();
                long memory = process.WorkingSet64;
                long storage = TotalBytes(root);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (memory > progress.Wire.MaxWorkingSetBytes) progress.Stop("working-set-budget");
                else if (storage > progress.Wire.MaxStoreBytes) progress.Stop("storage-budget");
                else if (progress.SetupCloses == 0 && now - progress.StartedAt > TimeSpan.FromSeconds(progress.Wire.FirstSetupCloseDeadlineSeconds!.Value))
                    progress.Stop("first-setup-close-deadline");
                else if (IsStale(progress.LastSetupCloseTicks, progress.Profile.Setup!.MaximumMarketDataAge, now)
                    || IsStale(progress.LastSetupAvailabilityTicks, progress.Profile.Setup.MaximumAvailabilityAge, now))
                    progress.Stop("setup-freshness");
                else if (IsStale(progress.LastContextCloseTicks, progress.Profile.Context!.MaximumMarketDataAge, now)
                    || IsStale(progress.LastContextAvailabilityTicks, progress.Profile.Context.MaximumAvailabilityAge, now))
                    progress.Stop("context-freshness");
                long expectedSetup = ExpectedCloses(progress.StartedAt, now, 900);
                long expectedContext = ExpectedCloses(progress.StartedAt, now, 3600);
                string quality = progress.SetupCloses == expectedSetup && progress.ContextCloses == expectedContext
                    ? progress.Quality : "unknown";
                log.Write(new
                {
                    kind = "health",
                    atUtc = now,
                    progress.SetupCloses,
                    progress.ContextCloses,
                    setupExpected = expectedSetup,
                    contextExpected = expectedContext,
                    progress.ReceiptCount,
                    setupLastCloseUtc = Timestamp(progress.LastSetupCloseTicks),
                    contextLastCloseUtc = Timestamp(progress.LastContextCloseTicks),
                    setupLastAvailabilityUtc = Timestamp(progress.LastSetupAvailabilityTicks),
                    contextLastAvailabilityUtc = Timestamp(progress.LastContextAvailabilityTicks),
                    quality,
                    workingSetBytes = memory,
                    storeBytes = storage,
                    progress.Reason,
                    delivery = "local-only",
                    publication = "not-observed"
                });
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            progress.Stop("health-persistence-failure");
            throw;
        }
    }

    private static bool IsStale(long ticks, TimeSpan? maximumAge, DateTimeOffset now) =>
        ticks != 0 && (maximumAge is null || now.UtcTicks < ticks || now.UtcTicks - ticks > maximumAge.Value.Ticks);

    private static DateTimeOffset? Timestamp(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);

    private static long ExpectedCloses(DateTimeOffset start, DateTimeOffset end, long intervalSeconds) =>
        Math.Max(0, end.ToUnixTimeSeconds() / intervalSeconds - start.ToUnixTimeSeconds() / intervalSeconds);

    private static void CountPriorCloses(NativePublicKlineFeed feed, NativeBarInputLedgerSnapshot snapshot, RunProgress progress)
    {
        foreach (NativeBarInputLedgerReceipt receipt in snapshot.Receipts)
        {
            if (receipt.Status != NativeBarInputReceiptStatus.Accepted) continue;
            NativePublicKlineFeedReplay replay = feed.ReplayDurableReceipt(receipt);
            if (replay.SourceKind != BinanceKlineSourceKind.WebSocket || replay.Kline is not { IsClosed: true } kline) continue;
            if (kline.Timeframe == "15m") progress.SetupCloses++;
            else if (kline.Timeframe == "1h") progress.ContextCloses++;
        }
    }

    private static bool IsContiguous(IReadOnlyList<ScreenerBar> bars, TimeSpan interval)
    {
        if (bars.Count == 0) return false;
        for (int index = 1; index < bars.Count; index++)
            if (bars[index].Bar.OpenTime.Value - bars[index - 1].Bar.OpenTime.Value != interval) return false;
        return true;
    }

    private static int ObserveOutcomes(string root, IReadOnlyList<ScreenerBar> history,
        SignalJournalSnapshot snapshot, DateTimeOffset now, int horizonSeconds, long maximumOutcomeBytes)
    {
        int count = 0;
        string directory = Path.Combine(root, "outcomes");
        long usedBytes = Directory.Exists(directory) ? TotalBytes(directory) : 0;
        SignalOutcomeBar[] bars = CreateOutcomeBars(history);
        foreach (SignalJournalSignal item in snapshot.Signals)
        {
            SignalScenarioEpisode episode = snapshot.ScenarioSnapshot?.Episodes
                .FirstOrDefault(value => value.Identity.SignalId == item.Signal.Identity.SignalId)
                ?? throw new InvalidDataException("The observed signal has no scenario episode.");
            SignalScenarioEvent? canceled = snapshot.ScenarioEvents
                .Where(value => value.SignalId == item.Signal.Identity.SignalId && value.Kind == SignalScenarioEventKind.Canceled)
                .OrderBy(value => value.AvailabilityTime.Value).FirstOrDefault();
            DateTimeOffset? invalidatedAt = canceled is null ? null
                : canceled.EventTime.Value > canceled.AvailabilityTime.Value
                    ? canceled.EventTime.Value : canceled.AvailabilityTime.Value;
            SignalOutcomePolicy policy = new(OutcomePolicyVersion,
                item.Signal.DecisionAt.Value.AddSeconds(horizonSeconds), episode.ExpiresAt.Value,
                invalidatedAt, null, null);
            SignalObservedOutcome outcome = SignalOutcomeObserver.Observe(item.Signal, item.Evidence,
                SignalObservationClock.Decision, null, null, policy, bars, now);
            long bytes = 65L + Encoding.UTF8.GetByteCount(outcome.CanonicalJson);
            if (bytes > maximumOutcomeBytes - usedBytes)
                throw new IOException("The outcome journal exceeds its budget.");
            if (SignalOutcomeJournal.Append(directory, outcome)) usedBytes += bytes;
            count++;
        }
        return count;
    }

    private static void ValidateResumeDirectory(string root)
    {
        for (DirectoryInfo? parent = new(root); parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The run directory is not isolated.");
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The run directory contains a reparse point.");
            string name = Path.GetFileName(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (name != "outcomes")
                    throw new IOException("The run directory contains an unexpected directory.");
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                {
                    if ((File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new IOException("The outcome directory is not isolated.");
                }
            }
            else if (!ResumeFiles.Contains(name))
                throw new IOException("The run directory contains an unexpected file.");
        }
    }

    private static RunMarker ReadMarker(string root)
    {
        string path = Path.Combine(root, "run-declaration.json");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The run declaration is not a regular file.");
        return JsonSerializer.Deserialize<RunMarker>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("The run declaration is empty.");
    }

    private static string[] AssemblyHashes() => new[]
    {
        typeof(SignalOnlyBoundedRun).Assembly, typeof(PriceContextFeatureCalculator).Assembly,
        typeof(SqliteNativeBarInputLedger).Assembly, typeof(BinancePublicRestClient).Assembly,
        typeof(SignalOutcomeObserver).Assembly
    }.Select(static assembly => assembly.GetName().Name + ":" + HashFile(assembly.Location)).ToArray();

    private static long TotalBytes(string root)
    {
        long total = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { total = checked(total + new FileInfo(path).Length); }
            catch (FileNotFoundException) { }
        }
        return total;
    }
    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void WriteNew(string path, byte[] bytes)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private sealed record RunMarker(int FormatVersion, string RunId, string EnvelopeSha256,
        DateTimeOffset StartedAtUtc, string[] AssemblyHashes, bool PauseAfterFirstClose,
        string OutcomePolicyVersion, string OutcomeCancellationPolicy, string OutcomeTargetPolicy,
        int OutcomeHorizonSeconds, long MaximumStoreBytes, long InputBudget, long JournalBudget, long LogBudget);

    internal sealed class RunProgress(DateTimeOffset startedAt, SignalOnlyBoundedPreflight.EnvelopeWire wire,
        PublicSignalProfileConfiguration profile, CancellationTokenSource stop)
    {
        private long _setupCloses, _contextCloses, _receiptCount, _lastSetupCloseTicks, _lastContextCloseTicks;
        private long _lastSetupAvailabilityTicks, _lastContextAvailabilityTicks;
        private readonly object _gate = new();
        private string _quality = "unknown";
        private string? _reason;

        public DateTimeOffset StartedAt { get; } = startedAt;
        public SignalOnlyBoundedPreflight.EnvelopeWire Wire { get; } = wire;
        public PublicSignalProfileConfiguration Profile { get; } = profile;
        public long SetupCloses { get => Interlocked.Read(ref _setupCloses); set => Interlocked.Exchange(ref _setupCloses, value); }
        public long ContextCloses { get => Interlocked.Read(ref _contextCloses); set => Interlocked.Exchange(ref _contextCloses, value); }
        public long ReceiptCount { get => Interlocked.Read(ref _receiptCount); set => Interlocked.Exchange(ref _receiptCount, value); }
        public long LastSetupCloseTicks { get => Interlocked.Read(ref _lastSetupCloseTicks); set => Interlocked.Exchange(ref _lastSetupCloseTicks, value); }
        public long LastContextCloseTicks { get => Interlocked.Read(ref _lastContextCloseTicks); set => Interlocked.Exchange(ref _lastContextCloseTicks, value); }
        public long LastSetupAvailabilityTicks { get => Interlocked.Read(ref _lastSetupAvailabilityTicks); set => Interlocked.Exchange(ref _lastSetupAvailabilityTicks, value); }
        public long LastContextAvailabilityTicks { get => Interlocked.Read(ref _lastContextAvailabilityTicks); set => Interlocked.Exchange(ref _lastContextAvailabilityTicks, value); }
        public string Quality
        {
            get { lock (_gate) return _quality; }
            set { lock (_gate) _quality = _reason is null && !stop.IsCancellationRequested ? value : "unknown"; }
        }
        public string? Reason { get { lock (_gate) return _reason; } }
        public void SetSuccess(string reason)
        {
            lock (_gate) _reason ??= reason;
        }
        public void Stop(string reason)
        {
            lock (_gate)
            {
                _reason = reason;
                _quality = "unknown";
            }
            stop.Cancel();
        }
    }

    private sealed class RunLog : IDisposable
    {
        private readonly StreamWriter _file;
        private readonly TextWriter _output;
        private readonly long _maximumBytes;
        private readonly object _gate = new();
        private long _length;

        public RunLog(string path, bool resume, long maximumBytes, TextWriter output)
        {
            FileStream stream = new(path, resume ? FileMode.Append : FileMode.CreateNew,
                FileAccess.Write, FileShare.Read);
            _length = stream.Length;
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _output = output;
            _maximumBytes = maximumBytes;
        }

        public void Write<T>(T value)
        {
            string line = JsonSerializer.Serialize(value, JsonOptions);
            long length = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            lock (_gate)
            {
                if (_length + length > _maximumBytes)
                    throw new IOException("The run log exceeds its budget.");
                _file.WriteLine(line);
                _output.WriteLine(line);
                _length += length;
            }
        }
        public void Dispose() => _file.Dispose();
    }
}
