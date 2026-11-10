using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;

namespace TradingBot.T008Capture;

internal static class T008CaptureRunner
{
    private const string Symbol = "BTCUSDT";
    private const string StreamId = "binance-usdm-native-kline:BTCUSDT";
    private const long FifteenMinuteMilliseconds = 15 * 60 * 1000;
    private const long OneHourMilliseconds = 60 * 60 * 1000;
    private const int MaximumPageCount = 1;
    private const int MaximumFifteenMinuteRows = 97;
    private const int MaximumOneHourRows = 25;
    private const int MaximumWebSocketMinutes = 70;
    private const int MaximumReconnectDowntimeSeconds = 120;
    private const long MaximumStoreBytes = 64L * 1024 * 1024;
    private const int MaximumLedgerReceipts = 50_000;
    private const int MaximumArtifactBytes = 4 * 1024 * 1024;
    private const int MaximumFrameBytes = 64 * 1024;
    private const int MaximumMessageBytes = 256 * 1024;
    private const int DeduplicationCapacity = 512;
    private static readonly TimeSpan FirstCloseDeadline = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan MaximumReconnectDowntime = TimeSpan.FromSeconds(
        MaximumReconnectDowntimeSeconds);

    private sealed class CaptureState(string runId, DateTimeOffset runStartedAt,
        DateTimeOffset restHistoryStart, DateTimeOffset restHistoryEnd)
    {
        public string RunId { get; } = runId;
        public DateTimeOffset RunStartedAt { get; } = runStartedAt;
        public DateTimeOffset RestHistoryStart { get; } = restHistoryStart;
        public DateTimeOffset RestHistoryEnd { get; } = restHistoryEnd;
        public string LedgerPath { get; set; } = string.Empty;
        public bool LedgerInitialized { get; set; }
        public bool Succeeded { get; set; }
        public string? Failure { get; set; }
        public CaptureConfiguration Configuration { get; set; } = null!;
        public string ConfigurationSha256 { get; set; } = string.Empty;
        public string? SnapshotPath { get; set; }
        public string? SnapshotSha256 { get; set; }
        public PointInTimeUniverseEvidence? PointInTimeUniverse { get; set; }
        public DateTimeOffset? WebSocketStartedAt { get; set; }
        public DateTimeOffset? WebSocketEndedAt { get; set; }
        public DateTimeOffset? ReconnectStartedAt { get; set; }
        public DateTimeOffset? ReconnectFirstEventAt { get; set; }
        public DateTimeOffset? InitialPrimaryCloseAt { get; set; }
        public long? InitialPrimaryCloseBoundary { get; set; }
        public HashSet<long> InitialClosedHourBoundaries { get; } = [];
        public bool PostReconnectFifteenMinuteClose { get; set; }
        public bool PostReconnectOneHourClose { get; set; }
        public int? RestFifteenMinutePages { get; set; }
        public int? RestOneHourPages { get; set; }
        public int ReceiptCount { get; set; }
        public int OpenWebSocketUpdateCount { get; set; }
        public int DuplicateReceiptCount { get; set; }
        public List<StreamDiagnosticEvidence> WebSocketDiagnostics { get; } = [];
        public RestartReplayEvidence RestartReplay { get; set; } = new(false, false,
            0, 0, 0, 0, null, null, null, null, []);
    }

    private sealed record ReplayStepEvidence(
        string EventId,
        DateTimeOffset EventTime,
        DateTimeOffset ReceiveTime,
        DateTimeOffset AvailabilityTime,
        DateTimeOffset? ExchangeTime,
        long StableSequence);

    private sealed record ReplayEvidence(
        string HistorySha256,
        string ReplaySha256,
        string[] EventIds,
        int EventCount);

    public static async Task<int> RunAsync()
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset historyEnd = FloorToUtcHour(startedAt);
        DateTimeOffset historyStart = historyEnd.AddHours(-24);
        string runId = $"{startedAt.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
        string runDirectory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts",
            "P15-M03-T008", runId);
        Directory.CreateDirectory(runDirectory);

        CaptureState state = new(runId, startedAt, historyStart, historyEnd)
        {
            LedgerPath = Path.Combine(runDirectory, "native-bars.sqlite")
        };
        state.Configuration = CreateConfiguration(historyStart, historyEnd);
        state.ConfigurationSha256 = CaptureJson.Sha256(
            CaptureJson.SerializeCanonical(state.Configuration));
        Console.WriteLine($"T008 run: {runId}");
        Console.WriteLine($"Artifacts: {Path.GetFullPath(runDirectory)}");

        using CancellationTokenSource userCancellation = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            userCancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            await ExecuteAsync(state, runDirectory, userCancellation.Token)
                .ConfigureAwait(false);
            state.Succeeded = true;
        }
        catch (Exception exception)
        {
            state.Succeeded = false;
            state.Failure ??= Describe(exception);
            Console.Error.WriteLine($"T008 capture failed: {state.Failure}");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        DateTimeOffset endedAt = DateTimeOffset.UtcNow;
        try
        {
            PersistCoverageReport(state, runDirectory, endedAt);
        }
        catch (Exception exception)
        {
            state.Succeeded = false;
            state.Failure ??= Describe(exception);
            Console.Error.WriteLine($"T008 report persistence failed: {state.Failure}");
        }

        Console.WriteLine($"Result: {(state.Succeeded ? "PASS" : "BLOCKED")}");
        Console.WriteLine($"Coverage report: {Path.Combine(runDirectory, "coverage-report.json")}");
        return state.Succeeded ? 0 : 1;
    }

    private static async Task ExecuteAsync(CaptureState state, string runDirectory,
        CancellationToken cancellationToken)
    {
        using HttpClient restHttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        BinancePublicRestClient restClient = new(restHttpClient,
            new BinanceRestOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(30),
                MaxResponseBytes = MaximumArtifactBytes
            }, TimeProvider.System);

        BinanceExchangeInfo exchangeInfo = await restClient.GetExchangeInfoAsync(
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ExchangeEligibilityMetadata> metadata =
            exchangeInfo.ToCoreEligibilityMetadata();
        MetadataCandidateFilterResult filter = MetadataCandidateFilter.Evaluate(metadata);
        ExchangeEligibilityMetadata? candidate = filter.Candidates.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.InstrumentId.Value, Symbol));
        MetadataCandidateExclusion? exclusion = filter.Exclusions.FirstOrDefault(item =>
            StringComparer.Ordinal.Equals(item.Metadata.InstrumentId.Value, Symbol));
        byte[] snapshotBytes = CaptureJson.SerializeCanonical(exchangeInfo);
        state.SnapshotSha256 = CaptureJson.Sha256(snapshotBytes);
        state.PointInTimeUniverse = new PointInTimeUniverseEvidence(Symbol, 1,
            "GET https://fapi.binance.com/fapi/v1/exchangeInfo + MetadataCandidateFilter.Evaluate",
            candidate is not null, exchangeInfo.ServerTimeMilliseconds,
            exchangeInfo.ReceiveTime, exchangeInfo.AvailabilityTime, state.SnapshotSha256,
            candidate, new ImmutableValueList<string>(exclusion is null
                ? Array.Empty<string>() : exclusion.Reasons));

        ExchangeInfoArtifact artifact = new(exchangeInfo, state.SnapshotSha256,
            exchangeInfo.ReceiveTime, exchangeInfo.AvailabilityTime, candidate, exclusion);
        byte[] artifactBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, CaptureJson.Options);
        if (artifactBytes.Length > MaximumArtifactBytes)
        {
            throw new InvalidDataException("The exchangeInfo evidence exceeded its 4 MiB cap.");
        }

        state.SnapshotPath = "pit-exchange-info.json";
        WriteNewFile(Path.Combine(runDirectory, state.SnapshotPath), artifactBytes);
        EnsureStoreCaps(runDirectory, 0);
        if (candidate is null)
        {
            string reasons = exclusion is null ? "no BTCUSDT metadata row" :
                string.Join(",", exclusion.Reasons);
            throw new InvalidDataException($"The predeclared BTCUSDT instrument is ineligible: {reasons}.");
        }

        PublicSignalProfileConfiguration profile = CreateDataOnlyProfile(
            state.RestHistoryStart);
        using SqliteNativeBarInputLedger ledger = SqliteNativeBarInputLedger.Open(
            state.LedgerPath);
        state.LedgerInitialized = true;

        NativePublicKlineFeed feed = await NativePublicKlineFeed.WarmupBoundedAsync(
            restClient, Symbol, profile, ledger,
            new NativePublicKlineRestHistoryBudget(MaximumPageCount,
                MaximumFifteenMinuteRows),
            new NativePublicKlineRestHistoryBudget(MaximumPageCount,
                MaximumOneHourRows), new WarmupCutoffTimeProvider(state.RestHistoryEnd),
            cancellationToken).ConfigureAwait(false);
        state.RestFifteenMinutePages = feed.SetupRestPagesRequested;
        state.RestOneHourPages = feed.ContextRestPagesRequested;
        NativeBarInputLedgerSnapshot initialSnapshot = ledger.Read(StreamId);
        state.ReceiptCount = initialSnapshot.Receipts.Count;
        EnsureStoreCaps(runDirectory, state.ReceiptCount);
        if (state.RestFifteenMinutePages != MaximumPageCount
            || state.RestOneHourPages != MaximumPageCount)
        {
            throw new InvalidDataException("The initial REST warmup did not use exactly one page per timeframe.");
        }

        Exception? webSocketFailure = null;
        using BinanceMarketWebSocketClient marketClient = CreateMarketClient();
        try
        {
            await CaptureProspectiveAsync(state, feed, ledger, marketClient,
                runDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            webSocketFailure = exception;
            state.Failure ??= Describe(exception);
        }

        state.WebSocketEndedAt ??= DateTimeOffset.UtcNow;
        RestartReplayEvidence restartEvidence;
        try
        {
            NativeBarInputLedgerSnapshot beforeRestartSnapshot = ledger.Read(StreamId);
            state.ReceiptCount = beforeRestartSnapshot.Receipts.Count;
            EnsureStoreCaps(runDirectory, state.ReceiptCount);
            ReplayEvidence before = BuildReplayEvidence(feed, beforeRestartSnapshot);

            ledger.Dispose();
            using SqliteNativeBarInputLedger reopenedLedger = SqliteNativeBarInputLedger.Open(
                state.LedgerPath);
            NativePublicKlineFeed recovered = NativePublicKlineFeed.RestoreFromLedger(
                restClient, Symbol, profile, reopenedLedger, TimeProvider.System);
            NativeBarInputLedgerSnapshot afterRestartSnapshot = reopenedLedger.Read(StreamId);
            ReplayEvidence after = BuildReplayEvidence(recovered, afterRestartSnapshot);
            bool historyMatched = StringComparer.Ordinal.Equals(before.HistorySha256,
                    after.HistorySha256)
                && StringComparer.Ordinal.Equals(before.ReplaySha256, after.ReplaySha256)
                && before.EventIds.SequenceEqual(after.EventIds, StringComparer.Ordinal)
                && beforeRestartSnapshot.Receipts.Count == afterRestartSnapshot.Receipts.Count;
            int initialRestPageRequests = (state.RestFifteenMinutePages ?? 0)
                + (state.RestOneHourPages ?? 0);
            restartEvidence = new RestartReplayEvidence(true, historyMatched,
                initialRestPageRequests, 0, afterRestartSnapshot.Receipts.Count,
                after.EventCount, before.HistorySha256, after.HistorySha256,
                before.ReplaySha256, after.ReplaySha256, after.EventIds);
            state.RestartReplay = restartEvidence;
            state.ReceiptCount = afterRestartSnapshot.Receipts.Count;
            state.OpenWebSocketUpdateCount = CountOpenWebSocketUpdates(
                afterRestartSnapshot.Receipts);
            state.DuplicateReceiptCount = afterRestartSnapshot.Receipts.Count(receipt =>
                receipt.Status == NativeBarInputReceiptStatus.Duplicate);
            EnsureStoreCaps(runDirectory, state.ReceiptCount);
            if (!historyMatched)
            {
                throw new InvalidDataException("Durable history or replay changed after reopening the ledger.");
            }
        }
        catch (Exception exception)
        {
            int initialRestPageRequests = (state.RestFifteenMinutePages ?? 0)
                + (state.RestOneHourPages ?? 0);
            state.RestartReplay = restartEvidence = new RestartReplayEvidence(false, false,
                initialRestPageRequests, 0, 0, 0, null, null, null, null, []);
            state.Failure ??= Describe(exception);
        }

        if (webSocketFailure is not null)
        {
            throw new InvalidOperationException("The bounded WebSocket capture did not meet its health criteria.",
                webSocketFailure);
        }

        if (!restartEvidence.RestoreCompleted || !restartEvidence.HistoryMatched)
        {
            throw new InvalidDataException("Durable native input was not reproduced after restart.");
        }

        ValidateCaptureCoverage(state, ledger.Read(StreamId));
    }

    private static async Task CaptureProspectiveAsync(CaptureState state,
        NativePublicKlineFeed feed, SqliteNativeBarInputLedger ledger,
        BinanceMarketWebSocketClient marketClient, string runDirectory,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource captureCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        captureCancellation.CancelAfter(TimeSpan.FromMinutes(MaximumWebSocketMinutes));
        BinanceSubscription[] subscriptions =
        [
            BinanceSubscription.Kline(Symbol, "15m"),
            BinanceSubscription.Kline(Symbol, "1h")
        ];
        state.WebSocketStartedAt = DateTimeOffset.UtcNow;
        await ReadSessionAsync(state, feed, marketClient, subscriptions,
            runDirectory, captureCancellation, false, ShouldStopInitialSession)
            .ConfigureAwait(false);
        if (state.InitialPrimaryCloseAt is null)
        {
            throw new InvalidDataException("No accepted closed 15m WebSocket input arrived within 20 minutes.");
        }

        state.ReconnectStartedAt = DateTimeOffset.UtcNow;
        await ReadSessionAsync(state, feed, marketClient, subscriptions,
            runDirectory, captureCancellation, true, static current =>
                current.PostReconnectFifteenMinuteClose
                && current.PostReconnectOneHourClose).ConfigureAwait(false);
        state.WebSocketEndedAt = DateTimeOffset.UtcNow;
        if (state.ReconnectFirstEventAt is null)
        {
            throw new InvalidDataException("No event arrived after the controlled reconnect.");
        }

        if (state.WebSocketEndedAt.Value - state.WebSocketStartedAt.Value
            > TimeSpan.FromMinutes(MaximumWebSocketMinutes))
        {
            throw new InvalidDataException("The prospective WebSocket run exceeded 70 minutes.");
        }

        NativeBarInputLedgerSnapshot snapshot = ledger.Read(StreamId);
        state.ReceiptCount = snapshot.Receipts.Count;
        state.OpenWebSocketUpdateCount = CountOpenWebSocketUpdates(snapshot.Receipts);
        state.DuplicateReceiptCount = snapshot.Receipts.Count(receipt =>
            receipt.Status == NativeBarInputReceiptStatus.Duplicate);
        EnsureStoreCaps(runDirectory, state.ReceiptCount);
    }

    private static async Task ReadSessionAsync(CaptureState state,
        NativePublicKlineFeed feed, BinanceMarketWebSocketClient marketClient,
        IReadOnlyList<BinanceSubscription> subscriptions, string runDirectory,
        CancellationTokenSource sessionCancellation, bool isReconnect,
        Func<CaptureState, bool> shouldStop)
    {
        string? healthFailure = null;
        void OnDiagnostic(BinanceStreamDiagnostic diagnostic)
        {
            state.WebSocketDiagnostics.Add(new StreamDiagnosticEvidence(
                diagnostic.Kind.ToString(), diagnostic.SessionId, diagnostic.StreamName,
                diagnostic.EventId, diagnostic.Message, diagnostic.StableSequence,
                diagnostic.ObservedAt));
            if (diagnostic.Kind == BinanceStreamDiagnosticKind.Duplicate)
            {
                return;
            }

            healthFailure ??= $"Unexpected WebSocket diagnostic {diagnostic.Kind}: {diagnostic.Message}";
            sessionCancellation.Cancel();
        }

        marketClient.Diagnostic += OnDiagnostic;
        try
        {
            await foreach (BinanceStreamEnvelope envelope in marketClient
                .ReadAsync(subscriptions, sessionCancellation.Token).ConfigureAwait(false))
            {
                NativePublicKlineFeedUpdate update = feed.Accept(envelope);
                state.ReceiptCount = checked(state.ReceiptCount + 1);
                RecordClosedInput(state, envelope, update, isReconnect);
                if (isReconnect && state.ReconnectFirstEventAt is null)
                {
                    state.ReconnectFirstEventAt = envelope.ReceiveTime.Value;
                    TimeSpan downtime = state.ReconnectFirstEventAt.Value
                        - state.ReconnectStartedAt!.Value;
                    if (downtime < TimeSpan.Zero || downtime > MaximumReconnectDowntime)
                    {
                        healthFailure = "The first event after reconnect exceeded the 120-second bound.";
                        sessionCancellation.Cancel();
                    }
                }

                if (!isReconnect && envelope.ReceiveTime.Value
                    - state.WebSocketStartedAt!.Value > FirstCloseDeadline
                    && (state.InitialPrimaryCloseAt is null
                        || state.InitialPrimaryCloseAt.Value - state.WebSocketStartedAt.Value
                        > FirstCloseDeadline))
                {
                    healthFailure = "The initial accepted closed 15m input missed its 20-minute deadline.";
                    sessionCancellation.Cancel();
                }

                if (update.DiagnosticCode is not null
                    && !StringComparer.Ordinal.Equals(update.DiagnosticCode, "bar-not-closed"))
                {
                    healthFailure ??= $"Unexpected native input diagnostic {update.DiagnosticCode}.";
                    sessionCancellation.Cancel();
                }

                EnsureStoreCaps(runDirectory, state.ReceiptCount);
                if (healthFailure is not null)
                {
                    throw new InvalidDataException(healthFailure);
                }

                if (DateTimeOffset.UtcNow - state.WebSocketStartedAt!.Value
                    > TimeSpan.FromMinutes(MaximumWebSocketMinutes))
                {
                    throw new InvalidDataException("The prospective WebSocket run exceeded 70 minutes.");
                }

                if (shouldStop(state))
                {
                    break;
                }
            }

            if (healthFailure is not null)
            {
                throw new InvalidDataException(healthFailure);
            }
        }
        finally
        {
            marketClient.Diagnostic -= OnDiagnostic;
        }
    }

    private static void RecordClosedInput(CaptureState state,
        BinanceStreamEnvelope envelope, NativePublicKlineFeedUpdate update,
        bool isReconnect)
    {
        if (envelope.Payload is not BinanceKlineEvent klineEvent
            || !klineEvent.Kline.IsClosed
            || update.Status is not (BinanceKlineIngestionStatus.Accepted
                or BinanceKlineIngestionStatus.Duplicate))
        {
            return;
        }

        long boundary = checked(klineEvent.Kline.OpenTimeMilliseconds
            + IntervalMilliseconds(klineEvent.Kline.Timeframe));
        if (StringComparer.Ordinal.Equals(klineEvent.Kline.Timeframe, "1h"))
        {
            state.InitialClosedHourBoundaries.Add(boundary);
        }

        if (!isReconnect && state.InitialPrimaryCloseAt is null
            && update.Status == BinanceKlineIngestionStatus.Accepted
            && StringComparer.Ordinal.Equals(klineEvent.Kline.Timeframe, "15m"))
        {
            state.InitialPrimaryCloseAt = envelope.ReceiveTime.Value;
            state.InitialPrimaryCloseBoundary = boundary;
        }

        if (isReconnect)
        {
            if (StringComparer.Ordinal.Equals(klineEvent.Kline.Timeframe, "15m"))
            {
                state.PostReconnectFifteenMinuteClose = true;
            }
            else if (StringComparer.Ordinal.Equals(klineEvent.Kline.Timeframe, "1h"))
            {
                state.PostReconnectOneHourClose = true;
            }
        }
    }

    private static bool ShouldStopInitialSession(CaptureState state)
    {
        if (state.InitialPrimaryCloseBoundary is not { } boundary)
        {
            return false;
        }

        return boundary % OneHourMilliseconds != 0
            || state.InitialClosedHourBoundaries.Contains(boundary);
    }

    private static BinanceMarketWebSocketClient CreateMarketClient() =>
        new(new ClientWebSocketTransportFactory(), new BinanceWebSocketOptions
        {
            AutoReconnect = false,
            MaxReconnectAttempts = 0,
            ReceiveTimeout = TimeSpan.FromSeconds(30),
            StaleAfter = TimeSpan.FromMinutes(2),
            MaxFrameBytes = MaximumFrameBytes,
            MaxMessageBytes = MaximumMessageBytes,
            DeduplicationCapacity = DeduplicationCapacity
        }, TimeProvider.System);

    private static void ValidateCaptureCoverage(CaptureState state,
        NativeBarInputLedgerSnapshot snapshot)
    {
        if (state.WebSocketStartedAt is null || state.WebSocketEndedAt is null
            || state.ReconnectStartedAt is null || state.ReconnectFirstEventAt is null)
        {
            throw new InvalidDataException("The bounded prospective session or reconnect timestamps are incomplete.");
        }

        TimeSpan reconnectDowntime = state.ReconnectFirstEventAt.Value
            - state.ReconnectStartedAt.Value;
        if (reconnectDowntime < TimeSpan.Zero || reconnectDowntime > MaximumReconnectDowntime)
        {
            throw new InvalidDataException("Reconnect downtime exceeded 120 seconds.");
        }

        if (state.WebSocketEndedAt.Value - state.WebSocketStartedAt.Value
            > TimeSpan.FromMinutes(MaximumWebSocketMinutes))
        {
            throw new InvalidDataException("The prospective WebSocket run exceeded 70 minutes.");
        }

        if (state.InitialPrimaryCloseAt is null
            || !state.PostReconnectFifteenMinuteClose
            || !state.PostReconnectOneHourClose)
        {
            throw new InvalidDataException("Initial/reconnected closed-bar acceptance criteria are incomplete.");
        }

        CoverageIntervalEvidence[] coverage = BuildCoverage(state, snapshot);
        CoverageIntervalEvidence? gap = coverage.FirstOrDefault(item =>
            item.GapOpenTimeMilliseconds.Length != 0);
        if (gap is not null)
        {
            throw new InvalidDataException($"Coverage has {gap.GapOpenTimeMilliseconds.Length} "
                + $"uncovered {gap.Timeframe} interval(s) for {gap.AvailabilityModel}.");
        }

        if (state.RestFifteenMinutePages != MaximumPageCount
            || state.RestOneHourPages != MaximumPageCount)
        {
            throw new InvalidDataException("REST history did not remain within one page per timeframe.");
        }
    }

    private static CoverageIntervalEvidence[] BuildCoverage(CaptureState state,
        NativeBarInputLedgerSnapshot? snapshot)
    {
        IReadOnlyList<NativeBarInputLedgerReceipt> receipts = snapshot?.Receipts
            ?? Array.Empty<NativeBarInputLedgerReceipt>();
        return
        [
            BuildIntervalCoverage("15m", "modeled-historical-rest-local-response-observation",
                FifteenMinuteMilliseconds, HistoricalExpectedOpens(state.RestHistoryStart,
                    state.RestHistoryEnd, FifteenMinuteMilliseconds), receipts,
                BinanceKlineSourceKind.Rest),
            BuildIntervalCoverage("1h", "modeled-historical-rest-local-response-observation",
                OneHourMilliseconds, HistoricalExpectedOpens(state.RestHistoryStart,
                    state.RestHistoryEnd, OneHourMilliseconds), receipts,
                BinanceKlineSourceKind.Rest),
            BuildIntervalCoverage("15m", "recorded-prospective-websocket-availability",
                FifteenMinuteMilliseconds, ProspectiveExpectedOpens(state.WebSocketStartedAt,
                    state.WebSocketEndedAt, FifteenMinuteMilliseconds), receipts,
                BinanceKlineSourceKind.WebSocket),
            BuildIntervalCoverage("1h", "recorded-prospective-websocket-availability",
                OneHourMilliseconds, ProspectiveExpectedOpens(state.WebSocketStartedAt,
                    state.WebSocketEndedAt, OneHourMilliseconds), receipts,
                BinanceKlineSourceKind.WebSocket)
        ];
    }

    private static CoverageIntervalEvidence BuildIntervalCoverage(string timeframe,
        string availabilityModel, long intervalMilliseconds, long[] expected,
        IReadOnlyList<NativeBarInputLedgerReceipt> receipts,
        BinanceKlineSourceKind sourceKind)
    {
        HashSet<long> expectedSet = [.. expected];
        HashSet<long> covered = [];
        foreach (NativeBarInputLedgerReceipt receipt in receipts)
        {
            if (receipt.Status is not (NativeBarInputReceiptStatus.Accepted
                or NativeBarInputReceiptStatus.Duplicate))
            {
                continue;
            }

            NativeLedgerPayload payload = DeserializePayload(receipt.PayloadJson);
            if (payload.Kline is { IsClosed: true } kline
                && StringComparer.Ordinal.Equals(kline.Timeframe, timeframe)
                && payload.SourceMetadata?.Kind == sourceKind
                && expectedSet.Contains(kline.OpenTimeMilliseconds))
            {
                covered.Add(kline.OpenTimeMilliseconds);
            }
        }

        long[] coveredOpens = expected.Where(covered.Contains).ToArray();
        long[] gaps = expected.Where(open => !covered.Contains(open)).ToArray();
        return new CoverageIntervalEvidence(timeframe, availabilityModel,
            intervalMilliseconds, expected, coveredOpens, gaps);
    }

    private static long[] HistoricalExpectedOpens(DateTimeOffset start,
        DateTimeOffset end, long intervalMilliseconds)
    {
        List<long> result = [];
        for (long open = start.ToUnixTimeMilliseconds();
             open < end.ToUnixTimeMilliseconds(); open = checked(open + intervalMilliseconds))
        {
            result.Add(open);
        }

        return result.ToArray();
    }

    private static long[] ProspectiveExpectedOpens(DateTimeOffset? start,
        DateTimeOffset? end, long intervalMilliseconds)
    {
        if (start is null || end is null || end.Value <= start.Value)
        {
            return [];
        }

        long startMilliseconds = start.Value.ToUnixTimeMilliseconds();
        long nextBoundary = checked((startMilliseconds / intervalMilliseconds + 1)
            * intervalMilliseconds);
        long endMilliseconds = end.Value.ToUnixTimeMilliseconds();
        List<long> result = [];
        for (long boundary = nextBoundary; boundary <= endMilliseconds;
             boundary = checked(boundary + intervalMilliseconds))
        {
            result.Add(checked(boundary - intervalMilliseconds));
        }

        return result.ToArray();
    }

    private static int CountOpenWebSocketUpdates(
        IReadOnlyList<NativeBarInputLedgerReceipt> receipts) => receipts.Count(receipt =>
    {
        if (receipt.Status != NativeBarInputReceiptStatus.Invalid
            || !StringComparer.Ordinal.Equals(receipt.DiagnosticCode, "bar-not-closed"))
        {
            return false;
        }

        NativeLedgerPayload payload = DeserializePayload(receipt.PayloadJson);
        return payload.SourceMetadata?.Kind == BinanceKlineSourceKind.WebSocket;
    });

    private static ReplayEvidence BuildReplayEvidence(NativePublicKlineFeed feed,
        NativeBarInputLedgerSnapshot snapshot)
    {
        Dictionary<string, NativeBarInputLedgerReceipt> accepted = snapshot.Receipts
            .Where(static receipt => receipt.Status == NativeBarInputReceiptStatus.Accepted
                && receipt.EventId is not null)
            .GroupBy(static receipt => receipt.EventId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(),
                StringComparer.Ordinal);
        ScreenerBar[] history = [.. feed.SetupHistory, .. feed.ContextHistory];
        ReplayEnvelope[] envelopes = history.Select(bar =>
        {
            if (!accepted.TryGetValue(bar.Bar.EventId, out NativeBarInputLedgerReceipt? receipt))
            {
                throw new InvalidDataException($"Durable accepted event {bar.Bar.EventId} has no ledger receipt.");
            }

            NativeLedgerPayload payload = DeserializePayload(receipt.PayloadJson);
            BinanceKlineSourceMetadata source = payload.SourceMetadata
                ?? throw new InvalidDataException("An accepted native input has no source metadata.");
            if (source.StableSequence != bar.StableSequence)
            {
                throw new InvalidDataException("Restored bar sequence differs from its durable source metadata.");
            }

            UtcTimestamp? exchangeTime = source.ExchangeEventTimeMilliseconds is { } milliseconds
                ? new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds))
                : null;
            CausalMarketEvent causal = new(bar.Bar, bar.ReceiveTime, bar.AvailabilityTime,
                bar.StableSequence, exchangeTime);
            return new ReplayEnvelope(StreamId, causal);
        }).ToArray();

        ReplayRunResult replay = new ReplayRunner().Run(StreamId, envelopes,
            static _ => { });
        if (!replay.Succeeded || replay.ProcessedCount != envelopes.Length)
        {
            throw new InvalidDataException("Restored native bars did not replay as a complete causal stream.");
        }

        ReplayStepEvidence[] historySteps = envelopes.Select(static envelope =>
        {
            CausalMarketEvent causal = envelope.CausalEvent;
            return new ReplayStepEvidence(causal.Payload.EventId,
                causal.Payload.EventTime.Value, causal.ReceiveTime.Value,
                causal.AvailabilityTime.Value, causal.ExchangeTime?.Value,
                causal.StableSequence);
        }).OrderBy(static step => step.StableSequence).ToArray();
        ReplayStepEvidence[] replaySteps = replay.Steps.Select(step =>
            new ReplayStepEvidence(step.Event.EventId, step.Event.EventTime.Value,
                step.Context.ReceiveTime.Value,
                step.Context.Envelope.CausalEvent.AvailabilityTime.Value,
                step.Context.ExchangeTime?.Value, step.Context.StableSequence))
            .ToArray();
        string historyHash = CaptureJson.Sha256(
            CaptureJson.SerializeCanonical(historySteps));
        string replayHash = CaptureJson.Sha256(
            CaptureJson.SerializeCanonical(replaySteps));
        return new ReplayEvidence(historyHash, replayHash,
            replaySteps.Select(static step => step.EventId).ToArray(),
            replaySteps.Length);
    }

    private static void PersistCoverageReport(CaptureState state,
        string runDirectory, DateTimeOffset endedAt)
    {
        NativeBarInputLedgerSnapshot? snapshot = null;
        if (state.LedgerInitialized && File.Exists(state.LedgerPath))
        {
            try
            {
                using SqliteNativeBarInputLedger ledger = SqliteNativeBarInputLedger.Open(
                    state.LedgerPath);
                snapshot = ledger.Read(StreamId);
            }
            catch (Exception exception)
            {
                state.Succeeded = false;
                state.Failure ??= $"Final ledger read failed: {Describe(exception)}";
            }
        }

        CoverageIntervalEvidence[] coverage = BuildCoverage(state, snapshot);
        NativeBarInputLedgerReceipt[] receipts = snapshot?.Receipts.ToArray() ?? [];
        state.ReceiptCount = receipts.Length;
        state.OpenWebSocketUpdateCount = CountOpenWebSocketUpdates(receipts);
        state.DuplicateReceiptCount = receipts.Count(receipt =>
            receipt.Status == NativeBarInputReceiptStatus.Duplicate);
        string? sourceHash = state.SnapshotSha256 is null || snapshot is null
            ? null : ComputeSourceHash(state, receipts);
        string runHash = ComputeRunHash(state, endedAt, sourceHash);
        CoverageReport report = BuildReport(state, endedAt, coverage, receipts,
            sourceHash, runHash);
        string reportHash = CaptureJson.Sha256(CaptureJson.SerializeCanonical(report));
        CoverageReportEnvelope envelope = new(reportHash, report);
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(envelope,
            CaptureJson.Options);
        if (reportBytes.Length > MaximumArtifactBytes)
        {
            state.Succeeded = false;
            state.Failure ??= "The coverage report exceeded its 4 MiB cap.";
            throw new InvalidDataException(state.Failure);
        }

        long existingBytes = DirectoryBytes(runDirectory);
        if (checked(existingBytes + reportBytes.Length) > MaximumStoreBytes)
        {
            state.Succeeded = false;
            state.Failure ??= "The run store would exceed its 64 MiB cap after writing coverage evidence.";
            report = BuildReport(state, endedAt, coverage, receipts, sourceHash,
                ComputeRunHash(state, endedAt, sourceHash));
            reportHash = CaptureJson.Sha256(CaptureJson.SerializeCanonical(report));
            envelope = new CoverageReportEnvelope(reportHash, report);
            reportBytes = JsonSerializer.SerializeToUtf8Bytes(envelope,
                CaptureJson.Options);
            if (reportBytes.Length > MaximumArtifactBytes
                || checked(existingBytes + reportBytes.Length) > MaximumStoreBytes)
            {
                throw new InvalidDataException("The failure report cannot be stored within the predeclared caps.");
            }
        }

        WriteNewFile(Path.Combine(runDirectory, "coverage-report.json"), reportBytes);
        if (DirectoryBytes(runDirectory) > MaximumStoreBytes)
        {
            throw new InvalidDataException("The final T008 store exceeded its 64 MiB cap.");
        }

        VerifyReopenedReport(Path.Combine(runDirectory, "coverage-report.json"),
            state.RunId, reportHash);
        Console.WriteLine($"Source SHA-256: {sourceHash ?? "unavailable"}");
        Console.WriteLine($"Configuration SHA-256: {state.ConfigurationSha256}");
        Console.WriteLine($"Run SHA-256: {report.RunSha256}");
    }

    private static CoverageReport BuildReport(CaptureState state,
        DateTimeOffset endedAt, CoverageIntervalEvidence[] coverage,
        NativeBarInputLedgerReceipt[] receipts, string? sourceHash, string runHash)
    {
        LedgerReceiptEvidence[] corrections = receipts
            .Where(static receipt => receipt.Status == NativeBarInputReceiptStatus.Correction)
            .Select(ToReceiptEvidence).ToArray();
        LedgerReceiptEvidence[] unknown = receipts.Where(static receipt =>
                (receipt.Status == NativeBarInputReceiptStatus.Invalid
                    && !StringComparer.Ordinal.Equals(receipt.DiagnosticCode, "bar-not-closed"))
                || (receipt.DiagnosticCode is not null
                    && !StringComparer.Ordinal.Equals(receipt.DiagnosticCode, "bar-not-closed")
                    && receipt.Status != NativeBarInputReceiptStatus.Correction))
            .Select(ToReceiptEvidence).ToArray();
        return new CoverageReport("1", state.RunId, "P15-M03-T008",
            state.Succeeded, state.Failure, state.RunStartedAt, endedAt,
            state.RestHistoryStart, state.RestHistoryEnd, state.WebSocketStartedAt,
            state.WebSocketEndedAt, state.ReconnectStartedAt,
            state.ReconnectFirstEventAt, state.Configuration,
            state.ConfigurationSha256, sourceHash, runHash, state.SnapshotPath,
            state.PointInTimeUniverse, coverage, state.RestFifteenMinutePages,
            state.RestOneHourPages, receipts.Length, state.OpenWebSocketUpdateCount,
            state.DuplicateReceiptCount, corrections, unknown,
            state.WebSocketDiagnostics.ToArray(), state.RestartReplay, false,
            ["BTCUSDT only; not full-market coverage.",
                "REST rows are historical observations with local response availability, not historical publication times.",
                "Prospective availability includes only recorded WebSocket receipts in this run.",
                "No synthetic gap filling, trading signal authorization, paper, exchange-test, limited-live, or production gate is asserted."]);
    }

    private static LedgerReceiptEvidence ToReceiptEvidence(
        NativeBarInputLedgerReceipt receipt) => new(receipt.IntakeSequence,
        receipt.EventId, receipt.RevisionHash, receipt.Status.ToString(),
        receipt.DiagnosticCode, receipt.DiagnosticMessage);

    private static string ComputeSourceHash(CaptureState state,
        IReadOnlyList<NativeBarInputLedgerReceipt> receipts)
    {
        object sourceMaterial = new
        {
            exchangeInfoSnapshotSha256 = state.SnapshotSha256,
            pointInTimeReceiveTime = state.PointInTimeUniverse?.ReceiveTime,
            pointInTimeAvailabilityTime = state.PointInTimeUniverse?.AvailabilityTime,
            receipts = receipts.Select(receipt => new
            {
                receipt.IntakeSequence,
                receipt.EventId,
                receipt.RevisionHash,
                status = receipt.Status.ToString(),
                payloadSha256 = CaptureJson.Sha256(Encoding.UTF8.GetBytes(receipt.PayloadJson))
            }).ToArray()
        };
        return CaptureJson.Sha256(CaptureJson.SerializeCanonical(sourceMaterial));
    }

    private static string ComputeRunHash(CaptureState state, DateTimeOffset endedAt,
        string? sourceHash)
    {
        object runMaterial = new
        {
            state.RunId,
            state.ConfigurationSha256,
            sourceSha256 = sourceHash,
            startedAt = state.RunStartedAt,
            endedAt,
            succeeded = state.Succeeded,
            state.Failure,
            state.RestartReplay.BeforeRestartReplaySha256,
            state.RestartReplay.AfterRestartReplaySha256
        };
        return CaptureJson.Sha256(CaptureJson.SerializeCanonical(runMaterial));
    }

    private static CaptureConfiguration CreateConfiguration(DateTimeOffset historyStart,
        DateTimeOffset historyEnd) => new("P15-M03-T008", Symbol, 1,
        "fixed ex-ante BTCUSDT; Binance exchangeInfo metadata plus MetadataCandidateFilter",
        "t008-capture-only-v1", "15m", "1h", "not configured; no strategy parameters",
        historyStart.ToUnixTimeMilliseconds(), historyEnd.ToUnixTimeMilliseconds(), 96, 24,
        MaximumPageCount, MaximumFifteenMinuteRows, MaximumOneHourRows,
        MaximumWebSocketMinutes, 1, MaximumReconnectDowntimeSeconds,
        MaximumStoreBytes, MaximumLedgerReceipts, MaximumArtifactBytes,
        MaximumFrameBytes, MaximumMessageBytes, DeduplicationCapacity,
        "modeled-historical-rest-local-response-observation",
        "recorded-prospective-websocket-availability",
        ["BTCUSDT metadata is eligible in the point-in-time exchangeInfo snapshot.",
            "REST and WebSocket use existing Binance clients and the existing feed/ledger.",
            "No HTTP, WebSocket, or application retry beyond the one controlled reconnect.",
            "Abort on HTTP/SQLite errors, any resource cap, unexpected disconnect/stale/parse/late/unknown/correction.",
            "Abort if initial accepted closed 15m input is absent after 20 minutes.",
            "Abort if first event after reconnect takes more than 120 seconds.",
            "Abort if post-reconnect closed 15m or 1h input is absent after 70 minutes.",
            "Any missing expected interval remains a gap and blocks T008 DONE."]);

    private static PublicSignalProfileConfiguration CreateDataOnlyProfile(
        DateTimeOffset historyStart)
    {
        PublicSignalTimeframeConfiguration setup = new("15m",
            PublicSignalDataSource.BinanceUsdMNativeKlines,
            "binance-usdm-native-kline-v1", historyStart, "t008-bounded-history-v1");
        PublicSignalTimeframeConfiguration context = new("1h",
            PublicSignalDataSource.BinanceUsdMNativeKlines,
            "binance-usdm-native-kline-v1", historyStart, "t008-bounded-history-v1");
        return new PublicSignalProfileConfiguration(1, "t008-capture-only-v1",
            setup, context);
    }

    private static NativeLedgerPayload DeserializePayload(string payloadJson) =>
        JsonSerializer.Deserialize<NativeLedgerPayload>(payloadJson, CaptureJson.Options)
        ?? throw new InvalidDataException("A native ledger receipt payload is null.");

    private static void EnsureStoreCaps(string directory, int receiptCount)
    {
        if (receiptCount > MaximumLedgerReceipts)
        {
            throw new InvalidDataException("The native input ledger exceeded 50,000 receipts.");
        }

        if (DirectoryBytes(directory) > MaximumStoreBytes)
        {
            throw new InvalidDataException("The T008 run store exceeded 64 MiB.");
        }
    }

    private static long DirectoryBytes(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(static path => new FileInfo(path).Length);

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void VerifyReopenedReport(string path, string runId,
        string expectedReportHash)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        string persistedHash = root.GetProperty("reportSha256").GetString()
            ?? throw new InvalidDataException("The coverage report has no report SHA-256.");
        JsonElement report = root.GetProperty("report");
        string actualHash = CaptureJson.Sha256(CaptureJson.Canonicalize(report));
        string persistedRunId = report.GetProperty("runId").GetString() ?? string.Empty;
        if (!StringComparer.Ordinal.Equals(persistedHash, expectedReportHash)
            || !StringComparer.Ordinal.Equals(actualHash, expectedReportHash)
            || !StringComparer.Ordinal.Equals(persistedRunId, runId))
        {
            throw new InvalidDataException("The reopened coverage report failed its SHA-256/run-ID check.");
        }
    }

    private static long IntervalMilliseconds(string timeframe) => timeframe switch
    {
        "15m" => FifteenMinuteMilliseconds,
        "1h" => OneHourMilliseconds,
        _ => throw new InvalidDataException($"Unexpected native timeframe {timeframe}.")
    };

    private static DateTimeOffset FloorToUtcHour(DateTimeOffset value)
    {
        long milliseconds = value.ToUniversalTime().ToUnixTimeMilliseconds();
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds
            - milliseconds % OneHourMilliseconds);
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    private sealed class WarmupCutoffTimeProvider(DateTimeOffset cutoff) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _reads) == 1
            ? cutoff : TimeProvider.System.GetUtcNow();
    }
}
