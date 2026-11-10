using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;
namespace TradingBot.App;

/// <summary>One close-driven evaluation over the immutable causal input cut.</summary>
public sealed record NativePublicKlineEvaluation
{
    public BinanceKlineAcceptedRevision TriggerRevision { get; }

    public long IntakeSequence { get; }

    public PriceContextFeatureInput Input { get; }

    public LaneASignalEvaluationResult LaneA { get; }

    public string? HtfCutEventId { get; }

    public UtcTimestamp? HtfCutTime { get; }

    public ImmutableValueList<string> ReadinessReasons { get; }

    public bool IsReady => ReadinessReasons.Count == 0;

    public NativePublicKlineEvaluation(BinanceKlineAcceptedRevision triggerRevision,
        long intakeSequence, PriceContextFeatureInput input,
        LaneASignalEvaluationResult laneA, string? htfCutEventId,
        UtcTimestamp? htfCutTime, IEnumerable<string> readinessReasons)
    {
        ArgumentNullException.ThrowIfNull(triggerRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(intakeSequence);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(laneA);
        ArgumentNullException.ThrowIfNull(readinessReasons);
        if ((htfCutEventId is null) != (htfCutTime is null))
        {
            throw new ArgumentException("HTF cut identity and time must be present together.");
        }

        TriggerRevision = triggerRevision;
        IntakeSequence = intakeSequence;
        Input = input;
        LaneA = laneA;
        HtfCutEventId = htfCutEventId;
        HtfCutTime = htfCutTime;
        ReadinessReasons = new ImmutableValueList<string>(readinessReasons
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal).OrderBy(static reason => reason,
                StringComparer.Ordinal));
    }
}

/// <summary>Result for one REST or public WebSocket kline intake.</summary>
public sealed record NativePublicKlineFeedUpdate(
    BinanceKlineIngestionStatus Status,
    BinanceKlineIdentity? Identity,
    NativePublicKlineEvaluation? Evaluation,
    string? DiagnosticCode,
    string? DiagnosticMessage);

/// <summary>
/// Composes existing public Binance REST/WebSocket clients with the native
/// timeframe ingestion boundary. A new evaluation is produced only for a newly
/// accepted closed 15m input; later data never mutates a committed evaluation.
/// </summary>
public sealed class NativePublicKlineFeed
{
    private const int RestKlinePageLimit = 1500;
    private const long FifteenMinuteMilliseconds = 15 * 60 * 1000;
    private const long OneHourMilliseconds = 60 * 60 * 1000;
    private static readonly JsonSerializerOptions LedgerJsonOptions = CreateLedgerJsonOptions();

    private sealed record LedgerKlinePayload
    {
        public int FormatVersion { get; }

        public string? Symbol { get; }

        public BinanceKline? Kline { get; }

        public BinanceKlineSourceMetadata? SourceMetadata { get; }

        [JsonConstructor]
        public LedgerKlinePayload(int formatVersion, string? symbol, BinanceKline? kline,
            BinanceKlineSourceMetadata? sourceMetadata)
        {
            FormatVersion = formatVersion;
            Symbol = symbol;
            Kline = kline;
            SourceMetadata = sourceMetadata;
        }
    }

    private readonly string _symbol;
    private readonly string _sourceId;
    private readonly PublicSignalProfileConfiguration _profile;
    private readonly IBinancePublicRestClient _restClient;
    private readonly TimeProvider _timeProvider;
    private readonly SqliteNativeBarInputLedger? _ledger;
    private readonly Dictionary<string, BinanceKlineAcceptedRevision> _acceptedRevisions =
        new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, ScreenerBar> _setupBars = [];
    private readonly SortedDictionary<long, ScreenerBar> _contextBars = [];
    private readonly List<string> _streamDiagnostics = [];
    private readonly HashSet<string> _unresolvedStreamDiagnostics = new(StringComparer.Ordinal);
    private long _nextIntakeSequence;
    private DateTimeOffset? _lastCommittedSetupEventTime;
    private Exception? _ledgerFailure;

    private NativePublicKlineFeed(IBinancePublicRestClient restClient, string symbol,
        PublicSignalProfileConfiguration profile, TimeProvider timeProvider,
        SqliteNativeBarInputLedger? ledger)
    {
        _restClient = restClient;
        _symbol = symbol.ToUpperInvariant();
        _sourceId = $"binance-usdm-native-kline:{_symbol}";
        _profile = profile;
        _timeProvider = timeProvider;
        _ledger = ledger;
    }

    public string Symbol => _symbol;

    public UtcTimestamp? SetupHistoryStart => ToTimestamp(_profile.Setup?.HistoryStartUtc);

    public UtcTimestamp? ContextHistoryStart => ToTimestamp(_profile.Context?.HistoryStartUtc);

    public IReadOnlyList<ScreenerBar> SetupHistory => _setupBars.Values.ToArray();

    public IReadOnlyList<ScreenerBar> ContextHistory => _contextBars.Values.ToArray();

    public NativePublicKlineEvaluation? LastEvaluation { get; private set; }

    public ImmutableValueList<string> StreamDiagnostics => new(_streamDiagnostics);

    /// <summary>Loads the exact declared REST history before live input is read.</summary>
    public static Task<NativePublicKlineFeed> WarmupAsync(
        IBinancePublicRestClient restClient, string symbol,
        PublicSignalProfileConfiguration profile, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        WarmupAsyncCore(restClient, symbol, profile, timeProvider, null, cancellationToken);

    public static Task<NativePublicKlineFeed> WarmupAsync(
        IBinancePublicRestClient restClient, string symbol,
        PublicSignalProfileConfiguration profile, SqliteNativeBarInputLedger ledger,
        TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return WarmupAsyncCore(restClient, symbol, profile, timeProvider,
            ledger, cancellationToken);
    }

    private static async Task<NativePublicKlineFeed> WarmupAsyncCore(
        IBinancePublicRestClient restClient, string symbol,
        PublicSignalProfileConfiguration profile, TimeProvider? timeProvider,
        SqliteNativeBarInputLedger? ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(profile);
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        NativePublicKlineFeed feed = new(restClient, symbol, profile, clock, ledger);
        feed.RestoreLedger();
        DateTimeOffset requestedAt = clock.GetUtcNow();
        await feed.LoadHistoryAsync(profile.Setup, "15m", requestedAt, cancellationToken)
            .ConfigureAwait(false);
        await feed.LoadHistoryAsync(profile.Context, "1h", requestedAt, cancellationToken)
            .ConfigureAwait(false);
        return feed;
    }

    /// <summary>Backfills missing native bars without re-evaluating past inputs.</summary>
    public async Task BackfillAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfLedgerFailed();
        DateTimeOffset requestedAt = _timeProvider.GetUtcNow();
        await LoadHistoryAsync(_profile.Setup, "15m", requestedAt, cancellationToken)
            .ConfigureAwait(false);
        await LoadHistoryAsync(_profile.Context, "1h", requestedAt, cancellationToken)
            .ConfigureAwait(false);
        if (HasCompleteCoverage(_profile.Setup, "15m", requestedAt)
            && HasCompleteCoverage(_profile.Context, "1h", requestedAt))
        {
            _unresolvedStreamDiagnostics.Remove("websocket-gap");
            _unresolvedStreamDiagnostics.Remove("websocket-disconnect");
            _unresolvedStreamDiagnostics.Remove("websocket-reconnect");
            _unresolvedStreamDiagnostics.Remove("websocket-stale");
        }
    }

    private void RestoreLedger()
    {
        if (_ledger is null)
        {
            return;
        }

        NativeBarInputLedgerSnapshot snapshot = _ledger.Read(_sourceId);
        foreach (NativeBarInputLedgerReceipt receipt in snapshot.Receipts)
        {
            LedgerKlinePayload payload = DeserializeLedgerPayload(receipt.PayloadJson);
            if (payload.FormatVersion != 1)
            {
                throw new InvalidDataException("The native kline ledger payload version is unsupported.");
            }

            if (payload.Kline is null || payload.SourceMetadata is null
                || payload.Symbol is null)
            {
                if (receipt.Status is NativeBarInputReceiptStatus.Accepted
                    or NativeBarInputReceiptStatus.Duplicate
                    or NativeBarInputReceiptStatus.Correction)
                {
                    throw new InvalidDataException(
                        "A durable native kline receipt is missing its wire input.");
                }

                if (receipt.Status == NativeBarInputReceiptStatus.Invalid
                    && receipt.DiagnosticCode is not ("bar-not-closed" or "not-kline-event"))
                {
                    AddStreamDiagnostic("native-bar-invalid-input");
                }

                continue;
            }

            BinanceKlineIngestionResult ingestion = Ingest(payload.Symbol, payload.Kline,
                payload.SourceMetadata);
            if (ToLedgerStatus(ingestion.Status) != receipt.Status
                || !StringComparer.Ordinal.Equals(ingestion.Identity.EventId, receipt.EventId)
                || !StringComparer.Ordinal.Equals(ingestion.RevisionHash, receipt.RevisionHash))
            {
                throw new InvalidDataException(
                    "The durable native kline receipt does not match re-evaluated input.");
            }

            if (receipt.Status == NativeBarInputReceiptStatus.Correction)
            {
                AddStreamDiagnostic("native-bar-correction");
            }
            else if (receipt.Status == NativeBarInputReceiptStatus.Invalid
                && receipt.DiagnosticCode is not ("bar-not-closed" or "not-kline-event"))
            {
                AddStreamDiagnostic("native-bar-invalid-input");
            }
        }

        _nextIntakeSequence = snapshot.Checkpoint.LastIntakeSequence;
        _lastCommittedSetupEventTime = snapshot.Checkpoint.LastTriggerEventTime?.Value;
    }

    private static LedgerKlinePayload DeserializeLedgerPayload(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LedgerKlinePayload>(payloadJson,
                LedgerJsonOptions) ?? throw new JsonException("The native kline ledger payload is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The native kline ledger payload is invalid.", exception);
        }
    }

    /// <summary>Accepts one public WebSocket envelope without timer-driven work.</summary>
    public NativePublicKlineFeedUpdate Accept(BinanceStreamEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ThrowIfLedgerFailed();
        long sequence = ReserveIntakeSequence();
        if (envelope.Payload is not BinanceKlineEvent klineEvent)
        {
            NativePublicKlineFeedUpdate invalid = Invalid(null, "not-kline-event",
                "Only public kline events are accepted.");
            RecordReceipt(sequence, null, null, null, null, invalid.DiagnosticCode,
                invalid.DiagnosticMessage, null);
            return invalid;
        }

        BinanceKline kline = klineEvent.Kline;
        BinanceKlineSourceMetadata source = new(BinanceKlineSourceKind.WebSocket,
            $"{klineEvent.StreamName}:{envelope.SessionId}",
            klineEvent.EventTimeMilliseconds, envelope.ReceiveTime,
            envelope.AvailabilityTime, sequence);
        string expectedStream = kline.Timeframe switch
        {
            "15m" => BinanceSubscription.Kline(_symbol, "15m").StreamName,
            "1h" => BinanceSubscription.Kline(_symbol, "1h").StreamName,
            _ => string.Empty
        };
        if (!StringComparer.Ordinal.Equals(klineEvent.Symbol, _symbol)
            || string.IsNullOrEmpty(expectedStream)
            || !StringComparer.Ordinal.Equals(klineEvent.StreamName, expectedStream))
        {
            NativePublicKlineFeedUpdate invalid = Invalid(null, "unexpected-kline-stream",
                "The envelope does not match this symbol's native 15m/1h subscriptions.");
            RecordReceipt(sequence, null, null, null, null,
                invalid.DiagnosticCode, invalid.DiagnosticMessage, null);
            AddStreamDiagnostic("native-bar-invalid-input");
            return invalid;
        }

        BinanceKlineIngestionResult ingestion = Ingest(klineEvent.Symbol, kline, source);
        NativePublicKlineEvaluation? evaluation = null;
        string? diagnosticCode = ingestion.Diagnostic?.Code;
        string? diagnosticMessage = ingestion.Diagnostic?.Message;
        if (ingestion.Status == BinanceKlineIngestionStatus.Accepted
            && ingestion.AcceptedRevision is { } revision
            && StringComparer.Ordinal.Equals(kline.Timeframe, "15m"))
        {
            if (_lastCommittedSetupEventTime is { } committed
                && revision.CoreBar.EventTime.Value <= committed)
            {
                diagnosticCode = "late-primary-input";
                diagnosticMessage = "The accepted 15m input is older than an already committed evaluation.";
            }
            else if (SetupHistoryStart is null || ContextHistoryStart is null)
            {
                diagnosticCode = "history-start-unknown";
                diagnosticMessage = "Both native timeframe history starts must be declared before evaluation.";
            }
            else
            {
                evaluation = Evaluate(revision, sequence);
            }
        }

        RecordReceipt(sequence, ingestion, klineEvent.Symbol, kline, source,
            diagnosticCode, diagnosticMessage, evaluation?.TriggerRevision.CoreBar.EventTime);
        RecordIngestionDiagnostic(ingestion);
        if (evaluation is not null)
        {
            LastEvaluation = evaluation;
            _lastCommittedSetupEventTime = evaluation.TriggerRevision.CoreBar.EventTime.Value;
        }

        return new NativePublicKlineFeedUpdate(ingestion.Status, ingestion.Identity,
            evaluation, diagnosticCode, diagnosticMessage);
    }

    /// <summary>
    /// Reads both native public streams through the existing reconnecting Binance
    /// client and yields every intake result. Open klines are rejected by the
    /// same timeframe-aware ingestion boundary as direct callers.
    /// </summary>
    public async IAsyncEnumerable<NativePublicKlineFeedUpdate> ReadAsync(
        BinanceMarketWebSocketClient marketClient,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(marketClient);
        marketClient.Diagnostic += OnStreamDiagnostic;
        try
        {
            BinanceSubscription[] subscriptions =
            [
                BinanceSubscription.Kline(_symbol, "15m"),
                BinanceSubscription.Kline(_symbol, "1h")
            ];
            await foreach (BinanceStreamEnvelope envelope in marketClient
                .ReadAsync(subscriptions, cancellationToken).ConfigureAwait(false))
            {
                yield return Accept(envelope);
            }
        }
        finally
        {
            marketClient.Diagnostic -= OnStreamDiagnostic;
        }
    }

    private async Task LoadHistoryAsync(PublicSignalTimeframeConfiguration? timeframe,
        string expectedTimeframe, DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        if (timeframe?.HistoryStartUtc is not { } historyStart
            || timeframe.Timeframe is not { } declaredTimeframe
            || !StringComparer.Ordinal.Equals(declaredTimeframe, expectedTimeframe)
            || timeframe.Source != PublicSignalDataSource.BinanceUsdMNativeKlines)
        {
            return;
        }

        ThrowIfLedgerFailed();
        long interval = IntervalMilliseconds(expectedTimeframe);
        long startMilliseconds = historyStart.ToUnixTimeMilliseconds();
        if (startMilliseconds < 0 || startMilliseconds % interval != 0)
        {
            AddStreamDiagnostic($"history-start-off-utc-grid:{Role(expectedTimeframe)}");
            return;
        }

        long latestClosedOpen = LatestClosedOpen(requestedAt, interval);
        long? missingOpen = FindFirstMissingOpen(HistoryFor(expectedTimeframe).Keys,
            startMilliseconds, latestClosedOpen, interval);
        if (missingOpen is null)
        {
            return;
        }

        DateTimeOffset pageStart = DateTimeOffset.FromUnixTimeMilliseconds(missingOpen.Value);
        while (pageStart <= requestedAt)
        {
            IReadOnlyList<BinanceKline> page = await _restClient.GetKlinesAsync(_symbol,
                expectedTimeframe, RestKlinePageLimit, pageStart, requestedAt,
                cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            DateTimeOffset receivedAt = _timeProvider.GetUtcNow();
            foreach (BinanceKline kline in page.OrderBy(static item =>
                         item.OpenTimeMilliseconds))
            {
                if (!kline.IsClosed
                    || kline.CloseTimeMilliseconds > requestedAt.ToUnixTimeMilliseconds())
                {
                    continue;
                }

                long sequence = ReserveIntakeSequence();
                UtcTimestamp observedAt = new(receivedAt);
                BinanceKlineSourceMetadata source = new(BinanceKlineSourceKind.Rest,
                    $"binance-usdm-rest:{_symbol}:{expectedTimeframe}", null,
                    observedAt, observedAt, sequence);
                BinanceKlineIngestionResult ingestion = Ingest(_symbol, kline, source);
                RecordReceipt(sequence, ingestion, _symbol, kline, source,
                    ingestion.Diagnostic?.Code, ingestion.Diagnostic?.Message, null);
                RecordIngestionDiagnostic(ingestion);
            }

            long lastOpen = page.Max(static item => item.OpenTimeMilliseconds);
            long nextStart = checked(lastOpen + interval);
            if (nextStart <= pageStart.ToUnixTimeMilliseconds())
            {
                break;
            }
            pageStart = DateTimeOffset.FromUnixTimeMilliseconds(nextStart);
        }
    }

    private SortedDictionary<long, ScreenerBar> HistoryFor(string timeframe) =>
        timeframe switch
        {
            "15m" => _setupBars,
            "1h" => _contextBars,
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null)
        };

    private bool HasCompleteCoverage(PublicSignalTimeframeConfiguration? timeframe,
        string expectedTimeframe, DateTimeOffset requestedAt)
    {
        if (timeframe?.HistoryStartUtc is not { } historyStart
            || !StringComparer.Ordinal.Equals(timeframe.Timeframe, expectedTimeframe)
            || timeframe.Source != PublicSignalDataSource.BinanceUsdMNativeKlines)
        {
            return false;
        }

        long interval = IntervalMilliseconds(expectedTimeframe);
        long startMilliseconds = historyStart.ToUnixTimeMilliseconds();
        return startMilliseconds >= 0 && startMilliseconds % interval == 0
            && FindFirstMissingOpen(HistoryFor(expectedTimeframe).Keys, startMilliseconds,
                LatestClosedOpen(requestedAt, interval), interval) is null;
    }

    private static long LatestClosedOpen(DateTimeOffset requestedAt, long interval)
    {
        long inclusiveCloseCutoff = requestedAt.ToUnixTimeMilliseconds() - (interval - 1);
        return inclusiveCloseCutoff < 0
            ? -1 : inclusiveCloseCutoff / interval * interval;
    }

    private static long? FindFirstMissingOpen(IEnumerable<long> openTimes,
        long startMilliseconds, long latestClosedOpen, long interval)
    {
        if (latestClosedOpen < startMilliseconds)
        {
            return null;
        }

        long expectedOpen = startMilliseconds;
        foreach (long openTime in openTimes)
        {
            if (openTime < expectedOpen)
            {
                continue;
            }

            if (openTime > latestClosedOpen)
            {
                break;
            }

            if (openTime != expectedOpen)
            {
                return expectedOpen;
            }

            expectedOpen = checked(expectedOpen + interval);
            if (expectedOpen > latestClosedOpen)
            {
                return null;
            }
        }

        return expectedOpen <= latestClosedOpen ? expectedOpen : null;
    }

    private BinanceKlineIngestionResult Ingest(string symbol, BinanceKline kline,
        BinanceKlineSourceMetadata source)
    {
        BinanceKlineIdentity identity = BinanceKlineIdentity.Create(symbol, kline.Timeframe,
            kline.OpenTimeMilliseconds);
        _acceptedRevisions.TryGetValue(identity.EventId, out BinanceKlineAcceptedRevision? prior);
        BinanceKlineIngestionResult result = BinanceKlineIngestionBoundary.Evaluate(symbol,
            kline, source, prior);
        if (result.Status == BinanceKlineIngestionStatus.Accepted
            && result.AcceptedRevision is { } accepted && result.CoreBar is { } coreBar)
        {
            _acceptedRevisions[identity.EventId] = accepted;
            ScreenerBar bar = new(_sourceId, coreBar, kline.QuoteAssetVolume,
                source.ReceiveTime ?? source.AvailabilityTime!, source.AvailabilityTime
                    ?? source.ReceiveTime!, source.StableSequence ?? 0);
            SortedDictionary<long, ScreenerBar> history = kline.Timeframe switch
            {
                "15m" => _setupBars,
                "1h" => _contextBars,
                _ => throw new InvalidOperationException("Unsupported native kline timeframe.")
            };
            history.Add(kline.OpenTimeMilliseconds, bar);
        }
        else if (result.Status == BinanceKlineIngestionStatus.Duplicate
            && result.AcceptedRevision is { } duplicate)
        {
            _acceptedRevisions[identity.EventId] = duplicate;
        }

        return result;
    }

    private NativePublicKlineEvaluation Evaluate(BinanceKlineAcceptedRevision trigger,
        long sequence)
    {
        PublicSignalTimeframeConfiguration? setup = _profile.Setup;
        PublicSignalTimeframeConfiguration? context = _profile.Context;
        PublicSignalIndicatorConfiguration? indicators = _profile.Indicators;
        UtcTimestamp setupStart = SetupHistoryStart
            ?? throw new InvalidOperationException("Setup history start was not validated.");
        UtcTimestamp contextStart = ContextHistoryStart
            ?? throw new InvalidOperationException("Context history start was not validated.");
        UtcTimestamp triggerEventTime = trigger.CoreBar.EventTime;
        UtcTimestamp triggerAvailability = trigger.SourceMetadata
            .Where(static item => item.AvailabilityTime is not null)
            .Select(static item => item.AvailabilityTime!)
            .DefaultIfEmpty(new UtcTimestamp(_timeProvider.GetUtcNow()))
            .MaxBy(static item => item.Value)!;
        UtcTimestamp asOf = triggerAvailability.Value >= triggerEventTime.Value
            ? triggerAvailability : triggerEventTime;
        CausalEventCursor cursor = new(asOf, sequence);
        ScreenerBar[] setupBars = EligibleBars(_setupBars.Values, triggerEventTime,
            triggerAvailability, sequence);
        ScreenerBar[] contextBars = EligibleBars(_contextBars.Values, triggerEventTime,
            triggerAvailability, sequence);
        PriceContextFeatureInput input = new(_sourceId,
            new InstrumentId(_symbol), "15m", setupStart, setupBars, "1h", contextStart,
            contextBars, Array.Empty<PriceVolumeObservation>());
        PriceContextFeatureConfiguration priceConfiguration = CreatePriceConfiguration(
            setup, context, indicators);
        FeatureQualityPolicy qualityPolicy = new(null,
            ConservativeTtl(setup), ConservativeTtl(context), null);
        LaneASignalEvaluationResult laneA = LaneASignalEvaluator.Evaluate(input, cursor,
            new LaneASignalEvaluationConfiguration(priceConfiguration, qualityPolicy, null));
        List<string> reasons = BuildReadinessReasons(setup, context, indicators,
            trigger.CoreBar, asOf, setupBars, contextBars,
            _unresolvedStreamDiagnostics);
        ScreenerBar? htfCut = contextBars.Length == 0 ? null : contextBars[^1];
        return new NativePublicKlineEvaluation(trigger, sequence, input, laneA,
            htfCut?.Bar.EventId, htfCut?.Bar.CloseTime, reasons);
    }

    private List<string> BuildReadinessReasons(
        PublicSignalTimeframeConfiguration? setup,
        PublicSignalTimeframeConfiguration? context,
        PublicSignalIndicatorConfiguration? indicators,
        Bar trigger,
        UtcTimestamp freshnessCut,
        ScreenerBar[] setupBars,
        ScreenerBar[] contextBars,
        IEnumerable<string> streamDiagnostics)
    {
        List<string> reasons = [.. _profile.UnknownReasons];
        AddSourceReason(setup, "setup", "15m", reasons);
        AddSourceReason(context, "context", "1h", reasons);
        AddUnitReasons(indicators, reasons);
        AddMinimumCountReason(setupBars.Length, setup?.SeedHistoryBars,
            "seed-history-incomplete:setup", reasons);
        AddMinimumCountReason(setupBars.Length, setup?.WarmupBars,
            "warmup-incomplete:setup", reasons);
        AddMinimumCountReason(contextBars.Length, context?.SeedHistoryBars,
            "seed-history-incomplete:context", reasons);
        AddMinimumCountReason(contextBars.Length, context?.WarmupBars,
            "warmup-incomplete:context", reasons);
        CheckSetupCoverage(setup, trigger, setupBars, reasons);
        CheckLeadingContextCoverage(context, contextBars, reasons);
        ScreenerBar? latestSetup = setupBars.Length == 0 ? null : setupBars[^1];
        CheckFreshness(latestSetup, setup, "setup", freshnessCut, reasons);
        ScreenerBar? latestContext = contextBars.Length == 0 ? null : contextBars[^1];
        if (latestContext is null)
        {
            reasons.Add("context-closed-bar-missing");
        }
        else
        {
            CheckFreshness(latestContext, context, "context", freshnessCut, reasons);
        }

        foreach (string diagnostic in streamDiagnostics)
        {
            reasons.Add(diagnostic);
        }

        return reasons;
    }

    private static ScreenerBar[] EligibleBars(IEnumerable<ScreenerBar> bars,
        UtcTimestamp triggerEventTime, UtcTimestamp triggerAvailability, long sequence) =>
        bars.Where(bar => bar.EventTime.Value <= triggerEventTime.Value
                && bar.Bar.CloseTime.Value <= triggerEventTime.Value
                && bar.AvailabilityTime.Value <= triggerAvailability.Value
                && bar.StableSequence <= sequence)
            .ToArray();

    private static PriceContextFeatureConfiguration CreatePriceConfiguration(
        PublicSignalTimeframeConfiguration? setup,
        PublicSignalTimeframeConfiguration? context,
        PublicSignalIndicatorConfiguration? indicators) => new(
        setup?.Timeframe, context?.Timeframe,
        indicators?.PriceContextVersion is { } version
            ? new PriceContextFeatureVersion(version) : null,
        indicators?.PivotLeftBars, indicators?.PivotRightBars, indicators?.AtrPeriodBars,
        indicators?.AtrMode, indicators?.ChannelCenterKind,
        indicators?.ChannelCenterPeriodBars, indicators?.Hlc3SmoothingKind,
        indicators?.ChannelWidthKind, indicators?.ChannelWidthPeriodBars,
        indicators?.ChannelWidthMultiplier, indicators?.LevelBufferMultiplier,
        indicators?.HtfFastPeriodBars, indicators?.HtfSlowPeriodBars,
        indicators?.HtfMovingAverageKind, indicators?.HtfConfirmationBars,
        null, null, null);

    private static TimeSpan? ConservativeTtl(PublicSignalTimeframeConfiguration? timeframe)
    {
        if (timeframe?.MaximumMarketDataAge is not { } marketAge
            || timeframe.MaximumAvailabilityAge is not { } availabilityAge)
        {
            return null;
        }

        return marketAge <= availabilityAge ? marketAge : availabilityAge;
    }


    private static void AddSourceReason(PublicSignalTimeframeConfiguration? timeframe,
        string role, string expectedTimeframe, List<string> reasons)
    {
        if (timeframe?.Timeframe is not { } actual
            || !StringComparer.Ordinal.Equals(actual, expectedTimeframe)
            || timeframe.Source != PublicSignalDataSource.BinanceUsdMNativeKlines)
        {
            reasons.Add($"public-native-source-unsupported:{role}");
        }
    }

    private static void AddUnitReasons(PublicSignalIndicatorConfiguration? indicators,
        List<string> reasons)
    {
        if (indicators is null)
        {
            return;
        }

        if (!StringComparer.Ordinal.Equals(indicators.PriceContextVersion,
                PriceContextFeatureCalculator.CurrentAlgorithmVersion))
        {
            reasons.Add("price-context-version-unsupported");
        }

        if (!StringComparer.Ordinal.Equals(indicators.FeatureQualityVersion,
                FeatureQualityEvaluation.CurrentAlgorithmVersion))
        {
            reasons.Add("feature-quality-version-unsupported");
        }

        if (indicators.AtrValueUnit != PublicSignalValueUnit.Price
            || indicators.ChannelCenterValueUnit != PublicSignalValueUnit.Price
            || indicators.ChannelWidthValueUnit != PublicSignalValueUnit.Price
            || indicators.HtfMovingAverageValueUnit != PublicSignalValueUnit.Price)
        {
            reasons.Add("price-context-value-unit-unsupported");
        }

        if (indicators.ChannelWidthMultiplierUnit != PublicSignalMultiplierUnit.Dimensionless
            || indicators.LevelBufferMultiplierUnit != PublicSignalMultiplierUnit.Dimensionless)
        {
            reasons.Add("price-context-multiplier-unit-unsupported");
        }
    }

    private static void AddMinimumCountReason(int actual, int? minimum, string reason,
        List<string> reasons)
    {
        if (minimum is not { } required || actual < required)
        {
            reasons.Add(reason);
        }
    }

    private static void CheckSetupCoverage(PublicSignalTimeframeConfiguration? setup,
        Bar trigger, ScreenerBar[] bars, List<string> reasons)
    {
        if (setup?.HistoryStartUtc is not { } start)
        {
            reasons.Add("setup-history-start-unknown");
            return;
        }

        long startMilliseconds = start.ToUnixTimeMilliseconds();
        long triggerOpen = trigger.OpenTime.Value.ToUnixTimeMilliseconds();
        if (startMilliseconds < 0 || startMilliseconds % FifteenMinuteMilliseconds != 0
            || triggerOpen < startMilliseconds
            || (triggerOpen - startMilliseconds) % FifteenMinuteMilliseconds != 0)
        {
            reasons.Add("setup-history-grid-invalid");
            return;
        }

        long expectedCount = (triggerOpen - startMilliseconds)
            / FifteenMinuteMilliseconds + 1;
        int actualCount = 0;
        foreach (ScreenerBar bar in bars)
        {
            long openMilliseconds = bar.Bar.OpenTime.Value.ToUnixTimeMilliseconds();
            if (openMilliseconds >= startMilliseconds && openMilliseconds <= triggerOpen)
            {
                actualCount++;
            }
        }

        if (actualCount != expectedCount)
        {
            reasons.Add("history-gap:setup");
        }
    }

    private static void CheckLeadingContextCoverage(
        PublicSignalTimeframeConfiguration? context, ScreenerBar[] bars,
        List<string> reasons)
    {
        if (context?.HistoryStartUtc is not { } start)
        {
            reasons.Add("context-history-start-unknown");
            return;
        }

        long startMilliseconds = start.ToUnixTimeMilliseconds();
        if (startMilliseconds < 0 || startMilliseconds % OneHourMilliseconds != 0
            || (bars.Length > 0
                && bars[0].Bar.OpenTime.Value.ToUnixTimeMilliseconds() != startMilliseconds))
        {
            reasons.Add("history-gap:context");
        }

        for (int index = 1; index < bars.Length; index++)
        {
            long gap = bars[index].Bar.OpenTime.Value.ToUnixTimeMilliseconds()
                - bars[index - 1].Bar.OpenTime.Value.ToUnixTimeMilliseconds();
            if (gap > OneHourMilliseconds)
            {
                reasons.Add("history-gap:context");
                break;
            }
        }
    }

    private static void CheckFreshness(ScreenerBar? latest,
        PublicSignalTimeframeConfiguration? timeframe, string role,
        UtcTimestamp asOf, List<string> reasons)
    {
        if (latest is null)
        {
            return;
        }

        CheckAge(asOf.Value - latest.Bar.CloseTime.Value,
            timeframe?.MaximumMarketDataAge, $"market-data-stale:{role}", reasons);
        CheckAge(asOf.Value - latest.AvailabilityTime.Value,
            timeframe?.MaximumAvailabilityAge, $"availability-stale:{role}", reasons);
    }

    private static void CheckAge(TimeSpan age, TimeSpan? maximum, string reason,
        List<string> reasons)
    {
        if (age < TimeSpan.Zero)
        {
            reasons.Add($"age-invalid:{reason}");
        }
        else if (maximum is not { } limit || age > limit)
        {
            reasons.Add(reason);
        }
    }

    private void RecordReceipt(long sequence, BinanceKlineIngestionResult? ingestion,
        string? symbol, BinanceKline? kline, BinanceKlineSourceMetadata? source,
        string? diagnosticCode, string? diagnosticMessage, UtcTimestamp? triggerEventTime)
    {
        if (_ledger is null)
        {
            _nextIntakeSequence = sequence;
            return;
        }

        try
        {
            string payloadJson = JsonSerializer.Serialize(new LedgerKlinePayload(1, symbol,
                kline, source), LedgerJsonOptions);
            string? triggerEventId = triggerEventTime is null
                ? null : ingestion?.Identity.EventId;
            NativeBarInputLedgerReceipt receipt = new(sequence,
                ingestion?.Identity.EventId, ingestion?.RevisionHash,
                ingestion is null ? NativeBarInputReceiptStatus.Invalid
                    : ToLedgerStatus(ingestion.Status),
                payloadJson, diagnosticCode, diagnosticMessage, triggerEventId,
                triggerEventTime);
            _ledger.Append(_sourceId, receipt);
            _nextIntakeSequence = sequence;
        }
        catch (Exception exception)
        {
            _ledgerFailure = exception;
            throw;
        }
    }

    private void RecordIngestionDiagnostic(BinanceKlineIngestionResult ingestion)
    {
        if (ingestion.Status == BinanceKlineIngestionStatus.RevisionConflict)
        {
            AddStreamDiagnostic("native-bar-correction");
        }
        else if (ingestion.Status == BinanceKlineIngestionStatus.Invalid
            && ingestion.Diagnostic?.Code is not ("bar-not-closed" or "not-kline-event"))
        {
            AddStreamDiagnostic("native-bar-invalid-input");
        }
    }

    private void AddStreamDiagnostic(string diagnostic)
    {
        _streamDiagnostics.Add(diagnostic);
        _unresolvedStreamDiagnostics.Add(diagnostic);
    }

    private void ThrowIfLedgerFailed()
    {
        if (_ledgerFailure is { } failure)
        {
            throw new InvalidOperationException(
                "The native public feed cannot continue after a durable ledger failure.",
                failure);
        }
    }

    private static NativeBarInputReceiptStatus ToLedgerStatus(
        BinanceKlineIngestionStatus status) => status switch
        {
            BinanceKlineIngestionStatus.Accepted => NativeBarInputReceiptStatus.Accepted,
            BinanceKlineIngestionStatus.Duplicate => NativeBarInputReceiptStatus.Duplicate,
            BinanceKlineIngestionStatus.RevisionConflict => NativeBarInputReceiptStatus.Correction,
            BinanceKlineIngestionStatus.Invalid => NativeBarInputReceiptStatus.Invalid,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

    private static JsonSerializerOptions CreateLedgerJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private void OnStreamDiagnostic(BinanceStreamDiagnostic diagnostic)
    {
        if (diagnostic.Kind is BinanceStreamDiagnosticKind.IdentityConflict
            or BinanceStreamDiagnosticKind.Late
            or BinanceStreamDiagnosticKind.Stale
            or BinanceStreamDiagnosticKind.Disconnect
            or BinanceStreamDiagnosticKind.Gap
            or BinanceStreamDiagnosticKind.ParseError
            or BinanceStreamDiagnosticKind.FrameTooLarge
            or BinanceStreamDiagnosticKind.Reconnect
            or BinanceStreamDiagnosticKind.BufferOverflow)
        {
            AddStreamDiagnostic($"websocket-{diagnostic.Kind.ToString().ToLowerInvariant()}");
        }
    }

    private static NativePublicKlineFeedUpdate Invalid(BinanceKlineIdentity? identity,
        string code, string message) => new(BinanceKlineIngestionStatus.Invalid,
        identity, null, code, message);

    private static long IntervalMilliseconds(string timeframe) => timeframe switch
    {
        "15m" => FifteenMinuteMilliseconds,
        "1h" => OneHourMilliseconds,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null)
    };

    private long ReserveIntakeSequence() => checked(_nextIntakeSequence + 1);

    private static string Role(string timeframe) => timeframe switch
    {
        "15m" => "setup",
        "1h" => "context",
        _ => "unknown"
    };

    private static UtcTimestamp? ToTimestamp(DateTimeOffset? value) => value is { } time
        ? new UtcTimestamp(time)
        : null;
}
