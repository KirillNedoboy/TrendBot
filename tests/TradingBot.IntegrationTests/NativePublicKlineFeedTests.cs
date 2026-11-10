using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class NativePublicKlineFeedTests
{
    private static readonly DateTimeOffset HistoryStart =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WarmupAt = HistoryStart.AddHours(2).AddMinutes(59);
    private static readonly DateTimeOffset CoClose = HistoryStart.AddHours(3);
    private static readonly DateTimeOffset PrimaryOpen = CoClose.AddMinutes(-15);
    private static readonly DateTimeOffset ContextOpen = CoClose.AddHours(-1);
    private static readonly string[] NativeTimeframes = ["15m", "1h"];

    [Fact]
    public async Task WarmupLoadsNativeTimeframesFromDeclaredStartsAndSkipsOpenBars()
    {
        using FeedFixture fixture = await CreateFixtureAsync();

        Assert.Equal(new UtcTimestamp(HistoryStart), fixture.Feed.SetupHistoryStart);
        Assert.Equal(new UtcTimestamp(HistoryStart), fixture.Feed.ContextHistoryStart);
        Assert.Equal(11, fixture.Feed.SetupHistory.Count);
        Assert.Equal(2, fixture.Feed.ContextHistory.Count);
        Assert.Equal(NativeTimeframes, fixture.Handler.Requests
            .Select(request => Query(request, "interval")).ToArray());
        Assert.Equal(HistoryStart.ToUnixTimeMilliseconds(), long.Parse(
            Query(fixture.Handler.Requests[0], "startTime"), CultureInfo.InvariantCulture));
        Assert.All(fixture.Feed.SetupHistory, bar => Assert.True(bar.IsClosed));
    }

    [Fact]
    public async Task OpenPrimaryKlineDoesNotTriggerButItsAcceptedCloseDoes()
    {
        using FeedFixture fixture = await CreateFixtureAsync();

        NativePublicKlineFeedUpdate open = fixture.Feed.Accept(CreateEnvelope(
            CreateKline(PrimaryOpen, "15m", false), CoClose.AddMilliseconds(10)));
        NativePublicKlineFeedUpdate closed = fixture.Feed.Accept(CreateEnvelope(
            CreateKline(PrimaryOpen, "15m", true), CoClose.AddMilliseconds(20)));

        Assert.Equal(BinanceKlineIngestionStatus.Invalid, open.Status);
        Assert.Null(open.Evaluation);
        Assert.Equal(BinanceKlineIngestionStatus.Accepted, closed.Status);
        Assert.NotNull(closed.Evaluation);
        Assert.True(closed.Evaluation!.IsReady);
        Assert.Same(closed.Evaluation, fixture.Feed.LastEvaluation);
    }

    [Fact]
    public async Task CoClosingContextIsUsedOnlyWhenItArrivedBeforeThePrimaryClose()
    {
        using FeedFixture htfFirst = await CreateFixtureAsync();
        BinanceKline context = CreateKline(ContextOpen, "1h", true);
        BinanceKline primary = CreateKline(PrimaryOpen, "15m", true);
        string contextId = BinanceKlineIdentity.Create("BTCUSDT", "1h",
            context.OpenTimeMilliseconds).EventId;

        Assert.Null(htfFirst.Feed.Accept(CreateEnvelope(context, CoClose.AddMilliseconds(10)))
            .Evaluation);
        NativePublicKlineEvaluation htfFirstDecision = Assert.IsType<NativePublicKlineEvaluation>(
            htfFirst.Feed.Accept(CreateEnvelope(primary, CoClose.AddMilliseconds(20))).Evaluation);
        Assert.Equal(contextId, htfFirstDecision.HtfCutEventId);

        using FeedFixture primaryFirst = await CreateFixtureAsync();
        NativePublicKlineEvaluation primaryFirstDecision = Assert.IsType<NativePublicKlineEvaluation>(
            primaryFirst.Feed.Accept(CreateEnvelope(primary, CoClose.AddMilliseconds(10))).Evaluation);
        Assert.NotEqual(contextId, primaryFirstDecision.HtfCutEventId);
        Assert.DoesNotContain(primaryFirstDecision.Input.HtfBars,
            bar => StringComparer.Ordinal.Equals(bar.Bar.EventId, contextId));

        NativePublicKlineEvaluation committed = primaryFirst.Feed.LastEvaluation!;
        NativePublicKlineFeedUpdate lateContext = primaryFirst.Feed.Accept(
            CreateEnvelope(context, CoClose.AddMilliseconds(20)));
        Assert.Null(lateContext.Evaluation);
        Assert.Same(committed, primaryFirst.Feed.LastEvaluation);
    }

    [Fact]
    public async Task FutureClosedContextIsExcludedWhenItArrivesBeforePrimaryInput()
    {
        using FeedFixture fixture = await CreateFixtureAsync();
        BinanceKline futureContext = CreateKline(CoClose, "1h", true);
        string futureContextId = BinanceKlineIdentity.Create("BTCUSDT", "1h",
            futureContext.OpenTimeMilliseconds).EventId;

        Assert.Null(fixture.Feed.Accept(CreateEnvelope(futureContext,
            CoClose.AddMilliseconds(10))).Evaluation);
        NativePublicKlineEvaluation evaluation = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(CreateKline(PrimaryOpen, "15m", true),
                CoClose.AddMilliseconds(20))).Evaluation);

        Assert.DoesNotContain(evaluation.Input.HtfBars,
            bar => StringComparer.Ordinal.Equals(bar.Bar.EventId, futureContextId));
        Assert.NotEqual(futureContextId, evaluation.HtfCutEventId);
    }

    [Fact]
    public async Task DuplicateClosedPrimaryReceiptDoesNotTriggerAgain()
    {
        using FeedFixture fixture = await CreateFixtureAsync();
        BinanceStreamEnvelope envelope = CreateEnvelope(
            CreateKline(PrimaryOpen, "15m", true), CoClose.AddMilliseconds(10));
        NativePublicKlineEvaluation first = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(envelope).Evaluation);

        NativePublicKlineFeedUpdate duplicate = fixture.Feed.Accept(envelope);

        Assert.Equal(BinanceKlineIngestionStatus.Duplicate, duplicate.Status);
        Assert.Null(duplicate.Evaluation);
        Assert.Same(first, fixture.Feed.LastEvaluation);
    }

    [Fact]
    public async Task ReorderedLatePrimaryInputIsFutureOnlyAndDoesNotRewriteCommittedEvaluation()
    {
        using FeedFixture fixture = await CreateFixtureAsync(missingSetupIndex: 9);
        BinanceKline primary = CreateKline(PrimaryOpen, "15m", true);
        NativePublicKlineEvaluation first = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(primary, CoClose.AddMilliseconds(10))).Evaluation);
        BinanceKline late = CreateKline(HistoryStart.AddHours(2).AddMinutes(15), "15m", true);

        NativePublicKlineFeedUpdate lateUpdate = fixture.Feed.Accept(
            CreateEnvelope(late, CoClose.AddMinutes(1)));

        Assert.Equal("late-primary-input", lateUpdate.DiagnosticCode);
        Assert.Null(lateUpdate.Evaluation);
        Assert.Same(first, fixture.Feed.LastEvaluation);

        BinanceKline next = CreateKline(CoClose, "15m", true);
        NativePublicKlineEvaluation future = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(next, CoClose.AddMinutes(15).AddSeconds(1)))
                .Evaluation);
        Assert.Contains(future.Input.SetupBars,
            bar => bar.Bar.OpenTime.Value
                == DateTimeOffset.FromUnixTimeMilliseconds(late.OpenTimeMilliseconds));
    }

    [Fact]
    public async Task PivotRightWindowIsNotConfirmedUntilTheFollowingClosedBar()
    {
        using FeedFixture fixture = await CreateFixtureAsync();
        BinanceKline candidate = CreateKline(PrimaryOpen, "15m", true, low: 80m);
        NativePublicKlineEvaluation beforeRight = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(candidate, CoClose.AddMilliseconds(10))).Evaluation);
        BinanceKline rightWindow = CreateKline(CoClose, "15m", true, low: 90m);

        NativePublicKlineEvaluation afterRight = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(rightWindow,
                CoClose.AddMinutes(15).AddMilliseconds(10))).Evaluation);
        PriceContextPivotEvidence pivot = Assert.Single(afterRight.LaneA.PriceContext
            .ConfirmedPivots.Pivots, evidence => evidence.PivotBar.Bar.OpenTime.Value
                == DateTimeOffset.FromUnixTimeMilliseconds(candidate.OpenTimeMilliseconds));

        Assert.DoesNotContain(beforeRight.LaneA.PriceContext.ConfirmedPivots.Pivots,
            evidence => evidence.PivotBar.Bar.OpenTime.Value
                == DateTimeOffset.FromUnixTimeMilliseconds(candidate.OpenTimeMilliseconds));
        Assert.Equal(CoClose.AddMinutes(15).AddMilliseconds(10), pivot.ConfirmationTime.Value);
    }

    [Fact]
    public async Task WarmupShortfallBlocksReadinessWithoutFillingHistory()
    {
        using FeedFixture fixture = await CreateFixtureAsync(setupSeedBars: 20,
            setupWarmupBars: 20, missingSetupIndex: 9);

        NativePublicKlineEvaluation evaluation = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(CreateKline(PrimaryOpen, "15m", true),
                CoClose.AddMilliseconds(10))).Evaluation);

        Assert.False(evaluation.IsReady);
        Assert.Contains("seed-history-incomplete:setup", evaluation.ReadinessReasons);
        Assert.DoesNotContain(evaluation.Input.SetupBars,
            bar => bar.Bar.OpenTime.Value == HistoryStart.AddHours(2).AddMinutes(15));
    }

    [Fact]
    public async Task StaleContextAndNativeIntervalGapsBlockReadiness()
    {
        using FeedFixture stale = await CreateFixtureAsync(maximumAge: TimeSpan.FromMinutes(30));
        NativePublicKlineEvaluation staleDecision = Assert.IsType<NativePublicKlineEvaluation>(
            stale.Feed.Accept(CreateEnvelope(CreateKline(PrimaryOpen, "15m", true),
                CoClose.AddMilliseconds(10))).Evaluation);
        Assert.False(staleDecision.IsReady);
        Assert.Contains("market-data-stale:context", staleDecision.ReadinessReasons);

        using FeedFixture gap = await CreateFixtureAsync(missingSetupIndex: 9);
        NativePublicKlineEvaluation gapDecision = Assert.IsType<NativePublicKlineEvaluation>(
            gap.Feed.Accept(CreateEnvelope(CreateKline(PrimaryOpen, "15m", true),
                CoClose.AddMilliseconds(10))).Evaluation);
        Assert.False(gapDecision.IsReady);
        Assert.Contains("history-gap:setup", gapDecision.ReadinessReasons);
    }

    [Fact]
    public async Task AvailabilityPrefixAppendMatchesIndependentPrefixReplays()
    {
        BinanceKline[] events =
        [
            CreateKline(PrimaryOpen, "15m", true),
            CreateKline(ContextOpen, "1h", true),
            CreateKline(CoClose, "15m", true)
        ];
        DateTimeOffset[] availability =
        [CoClose.AddMilliseconds(10), CoClose.AddMilliseconds(20),
            CoClose.AddMinutes(15).AddMilliseconds(10)];
        using FeedFixture continuous = await CreateFixtureAsync();
        NativePublicKlineEvaluation?[] continuousPrefixResults =
            new NativePublicKlineEvaluation?[events.Length];
        NativePublicKlineEvaluation? continuousLatest = null;
        for (int index = 0; index < events.Length; index++)
        {
            NativePublicKlineEvaluation? evaluation = continuous.Feed.Accept(
                CreateEnvelope(events[index], availability[index])).Evaluation;
            continuousLatest = evaluation ?? continuousLatest;
            continuousPrefixResults[index] = continuousLatest;
        }

        for (int prefixEnd = 0; prefixEnd < events.Length; prefixEnd++)
        {
            using FeedFixture prefix = await CreateFixtureAsync();
            NativePublicKlineEvaluation? prefixEvaluation = null;
            for (int index = 0; index <= prefixEnd; index++)
            {
                prefixEvaluation = prefix.Feed.Accept(
                    CreateEnvelope(events[index], availability[index])).Evaluation
                    ?? prefixEvaluation;
            }

            Assert.Equal(continuousPrefixResults[prefixEnd], prefixEvaluation);
        }
    }

    [Fact]
    public async Task DurableRecoveryContinuesReplayWithoutRefetchOrRetrigger()
    {
        BinanceStreamEnvelope primaryEnvelope = CreateEnvelope(
            CreateKline(PrimaryOpen, "15m", true), CoClose.AddMilliseconds(10));
        BinanceStreamEnvelope nextEnvelope = CreateEnvelope(
            CreateKline(CoClose, "15m", true), CoClose.AddMinutes(15).AddMilliseconds(10));
        NativePublicKlineEvaluation expectedNext;
        using (TemporaryLedger database = new())
        using (FeedFixture uninterrupted = await CreateFixtureAsync(ledgerPath: database.Path))
        {
            Assert.NotNull(uninterrupted.Feed.Accept(primaryEnvelope).Evaluation);
            Assert.Equal(BinanceKlineIngestionStatus.Duplicate,
                uninterrupted.Feed.Accept(primaryEnvelope).Status);
            expectedNext = Assert.IsType<NativePublicKlineEvaluation>(
                uninterrupted.Feed.Accept(nextEnvelope).Evaluation);
        }

        using TemporaryLedger recoveredDatabase = new();
        using (FeedFixture beforeRestart = await CreateFixtureAsync(
                   ledgerPath: recoveredDatabase.Path))
        {
            Assert.NotNull(beforeRestart.Feed.Accept(primaryEnvelope).Evaluation);
        }

        using FeedFixture recovered = await CreateFixtureAsync(
            ledgerPath: recoveredDatabase.Path, rejectRestRequests: true);
        Assert.Empty(recovered.Handler.Requests);
        Assert.Null(recovered.Feed.LastEvaluation);
        NativePublicKlineFeedUpdate duplicate = recovered.Feed.Accept(primaryEnvelope);
        Assert.Equal(BinanceKlineIngestionStatus.Duplicate, duplicate.Status);
        Assert.Null(duplicate.Evaluation);

        NativePublicKlineEvaluation actualNext = Assert.IsType<NativePublicKlineEvaluation>(
            recovered.Feed.Accept(nextEnvelope).Evaluation);

        Assert.Equal(expectedNext, actualNext);
        Assert.Equal(expectedNext.IntakeSequence, actualNext.IntakeSequence);
        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(recoveredDatabase.Path);
        NativeBarInputLedgerSnapshot snapshot = ledger.Read(
            "binance-usdm-native-kline:BTCUSDT");
        Assert.Equal(actualNext.IntakeSequence, snapshot.Checkpoint.LastIntakeSequence);
        Assert.Equal(actualNext.TriggerRevision.CoreBar.EventId,
            snapshot.Checkpoint.LastTriggerEventId);
    }

    [Fact]
    public async Task CorrectionIsDurableAndDoesNotRewriteCommittedEvaluation()
    {
        using TemporaryLedger database = new();
        using (FeedFixture fixture = await CreateFixtureAsync(ledgerPath: database.Path))
        {
            BinanceStreamEnvelope primaryEnvelope = CreateEnvelope(
                CreateKline(PrimaryOpen, "15m", true), CoClose.AddMilliseconds(10));
            NativePublicKlineEvaluation first = Assert.IsType<NativePublicKlineEvaluation>(
                fixture.Feed.Accept(primaryEnvelope).Evaluation);
            BinanceStreamEnvelope correction = CreateEnvelope(
                CreateKline(PrimaryOpen, "15m", true, low: 79m), CoClose.AddMinutes(1));

            NativePublicKlineFeedUpdate update = fixture.Feed.Accept(correction);

            Assert.Equal(BinanceKlineIngestionStatus.RevisionConflict, update.Status);
            Assert.Equal("revision-conflict", update.DiagnosticCode);
            Assert.Same(first, fixture.Feed.LastEvaluation);
        }

        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(database.Path);
        NativeBarInputLedgerSnapshot snapshot = ledger.Read(
            "binance-usdm-native-kline:BTCUSDT");
        Assert.Contains(snapshot.Receipts, receipt =>
            receipt.Status == NativeBarInputReceiptStatus.Correction
            && receipt.DiagnosticCode == "revision-conflict");
        Assert.Equal(BinanceKlineIdentity.Create("BTCUSDT", "15m",
                PrimaryOpen.ToUnixTimeMilliseconds()).EventId,
            snapshot.Checkpoint.LastTriggerEventId);

        using FeedFixture reopened = await CreateFixtureAsync(
            ledgerPath: database.Path, rejectRestRequests: true);
        Assert.Empty(reopened.Handler.Requests);
        Assert.Contains("native-bar-correction", reopened.Feed.StreamDiagnostics);
        Assert.Null(reopened.Feed.LastEvaluation);
    }

    [Fact]
    public async Task FailedDurableAppendIsVisibleAndDoesNotAdvanceCheckpoint()
    {
        using TemporaryLedger database = new();
        using FeedFixture fixture = await CreateFixtureAsync(ledgerPath: database.Path);
        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(database.Path);
        NativeBarInputLedgerSnapshot before = ledger.Read(
            "binance-usdm-native-kline:BTCUSDT");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        using (SqliteCommand command = connection.CreateCommand())
        {
            connection.Open();
            command.CommandText = """
                CREATE TRIGGER fail_checkpoint_update
                BEFORE UPDATE ON native_bar_input_checkpoints
                BEGIN
                    SELECT RAISE(ABORT, 'injected checkpoint update failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        BinanceStreamEnvelope input = CreateEnvelope(
            CreateKline(PrimaryOpen, "15m", true), CoClose.AddMilliseconds(10));
        Assert.Throws<SqliteException>(() => fixture.Feed.Accept(input));
        InvalidOperationException stopped = Assert.Throws<InvalidOperationException>(
            () => fixture.Feed.Accept(input));
        Assert.IsType<SqliteException>(stopped.InnerException);

        NativeBarInputLedgerSnapshot after = ledger.Read(
            "binance-usdm-native-kline:BTCUSDT");
        Assert.Equal(before.Checkpoint, after.Checkpoint);
        Assert.DoesNotContain(after.Receipts, receipt =>
            receipt.EventId == BinanceKlineIdentity.Create("BTCUSDT", "15m",
                PrimaryOpen.ToUnixTimeMilliseconds()).EventId);
    }

    [Fact]
    public async Task BackfillFillsOnlyFutureHistoryAndNeverReevaluatesCommittedInput()
    {
        using TemporaryLedger database = new();
        using FeedFixture fixture = await CreateFixtureAsync(missingSetupIndex: 9,
            ledgerPath: database.Path);
        NativePublicKlineEvaluation committed = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(CreateKline(PrimaryOpen, "15m", true),
                CoClose.AddMilliseconds(10))).Evaluation);
        BinanceKline missing = CreateKline(HistoryStart.AddMinutes(15 * 9), "15m", true);
        fixture.Handler.Add(missing);

        await fixture.Feed.BackfillAsync(TestContext.Current.CancellationToken);

        Assert.Same(committed, fixture.Feed.LastEvaluation);
        Assert.Contains(fixture.Feed.SetupHistory, bar =>
            bar.Bar.OpenTime.Value == HistoryStart.AddMinutes(15 * 9));
        NativePublicKlineEvaluation future = Assert.IsType<NativePublicKlineEvaluation>(
            fixture.Feed.Accept(CreateEnvelope(CreateKline(CoClose, "15m", true),
                CoClose.AddMinutes(15).AddMilliseconds(10))).Evaluation);
        Assert.Contains(future.Input.SetupBars, bar =>
            bar.Bar.OpenTime.Value == HistoryStart.AddMinutes(15 * 9));

        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(database.Path);
        NativeBarInputLedgerSnapshot snapshot = ledger.Read(
            "binance-usdm-native-kline:BTCUSDT");
        Assert.Contains(snapshot.Receipts, receipt =>
            receipt.Status == NativeBarInputReceiptStatus.Accepted
            && receipt.EventId == BinanceKlineIdentity.Create("BTCUSDT", "15m",
                missing.OpenTimeMilliseconds).EventId);
    }

    private static async Task<FeedFixture> CreateFixtureAsync(int setupSeedBars = 1,
        int setupWarmupBars = 1, int? missingSetupIndex = null,
        TimeSpan? maximumAge = null, string? ledgerPath = null,
        bool rejectRestRequests = false)
    {
        FixedTimeProvider timeProvider = new(WarmupAt);
        BinanceKline[] bars = CreateRestBars(missingSetupIndex);
        KlineHandler handler = new(bars, rejectRestRequests);
        HttpClient httpClient = new(handler);
        BinancePublicRestClient rest = new(httpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")), timeProvider);
        PublicSignalProfileConfiguration profile = CreateProfile(setupSeedBars,
            setupWarmupBars, maximumAge ?? TimeSpan.FromHours(3));
        SqliteNativeBarInputLedger? ledger = ledgerPath is null
            ? null : SqliteNativeBarInputLedger.Open(ledgerPath);
        NativePublicKlineFeed feed = ledger is null
            ? await NativePublicKlineFeed.WarmupAsync(rest, "BTCUSDT", profile, timeProvider)
            : await NativePublicKlineFeed.WarmupAsync(rest, "BTCUSDT", profile, ledger,
                timeProvider);
        return new FeedFixture(feed, handler, httpClient, ledger);
    }

    private static PublicSignalProfileConfiguration CreateProfile(int setupSeedBars,
        int setupWarmupBars, TimeSpan maximumAge)
    {
        PublicSignalTimeframeConfiguration Timeframe(string timeframe, int seedBars,
            int warmupBars) => new(timeframe, PublicSignalDataSource.BinanceUsdMNativeKlines,
            "binance-usdm-native-kline-v1", HistoryStart, "history-v1", seedBars,
            "seed-v1", warmupBars, "warmup-v1", "freshness-v1", maximumAge,
            maximumAge);

        PublicSignalIndicatorConfiguration indicators = new(
            priceContextVersion: PriceContextFeatureCalculator.CurrentAlgorithmVersion,
            featureQualityVersion: FeatureQualityEvaluation.CurrentAlgorithmVersion,
            setupFamilyVersion: "lane-a-v1", pivotLeftBars: 1, pivotRightBars: 1,
            atrPeriodBars: 1, atrMode: PriceContextAtrMode.Sma,
            atrValueUnit: PublicSignalValueUnit.Price,
            channelCenterKind: PriceContextChannelCenterKind.Ema,
            channelCenterPeriodBars: 1,
            hlc3SmoothingKind: PriceContextMovingAverageKind.Ema,
            channelCenterValueUnit: PublicSignalValueUnit.Price,
            channelWidthKind: PriceContextChannelWidthKind.Atr,
            channelWidthPeriodBars: 1, channelWidthMultiplier: 1m,
            channelWidthValueUnit: PublicSignalValueUnit.Price,
            channelWidthMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless,
            levelBufferMultiplier: 1m,
            levelBufferMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless,
            htfFastPeriodBars: 1, htfSlowPeriodBars: 1,
            htfMovingAverageKind: PriceContextMovingAverageKind.Ema,
            htfConfirmationBars: 1,
            htfMovingAverageValueUnit: PublicSignalValueUnit.Price);
        return new PublicSignalProfileConfiguration(1, "test-profile-v1",
            Timeframe("15m", setupSeedBars, setupWarmupBars),
            Timeframe("1h", 1, 1), indicators,
            new PublicSignalUniverseConfiguration("test-universe-v1", 1, 1));
    }

    private static BinanceKline[] CreateRestBars(int? missingSetupIndex)
    {
        List<BinanceKline> bars = [];
        for (int index = 0; index <= 12; index++)
        {
            if (index != missingSetupIndex)
            {
                bars.Add(CreateKline(HistoryStart.AddMinutes(15 * index), "15m",
                    HistoryStart.AddMinutes(15 * index + 15) <= WarmupAt,
                    low: index == 11 ? 80m : 90m + index));
            }
        }

        for (int index = 0; index <= 2; index++)
        {
            bars.Add(CreateKline(HistoryStart.AddHours(index), "1h",
                HistoryStart.AddHours(index + 1) <= WarmupAt));
        }

        return [.. bars];
    }

    private static BinanceKline CreateKline(DateTimeOffset openTime, string timeframe,
        bool isClosed, decimal? low = null)
    {
        long duration = timeframe == "15m" ? TimeSpan.FromMinutes(15).Ticks
            : TimeSpan.FromHours(1).Ticks;
        long openMilliseconds = openTime.ToUnixTimeMilliseconds();
        decimal open = 100m + (decimal)(openTime - HistoryStart).TotalMinutes / 100m;
        decimal close = open + 0.5m;
        decimal minimum = low ?? open - 1m;
        return new BinanceKline(openMilliseconds, open, Math.Max(open, close) + 1m,
            Math.Min(minimum, Math.Min(open, close)), close, 10m,
            checked(openMilliseconds + duration / TimeSpan.TicksPerMillisecond - 1),
            1000m, 10, 5m, 500m, "0", timeframe, isClosed);
    }

    private static BinanceStreamEnvelope CreateEnvelope(BinanceKline kline,
        DateTimeOffset availability)
    {
        long eventTime = kline.CloseTimeMilliseconds + 1_001;
        DateTimeOffset received = availability.AddMilliseconds(-1);
        BinanceKlineEvent payload = new("BTCUSDT", eventTime, kline,
            new BinanceSubscription("BTCUSDT", BinanceWebSocketEventKind.Kline,
                kline.Timeframe).StreamName);
        return new BinanceStreamEnvelope("fixture-session", 0, payload,
            new UtcTimestamp(received), new UtcTimestamp(availability),
            new UtcTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(eventTime)), "{}");
    }

    private static string Query(Uri uri, string name) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .Where(pair => Uri.UnescapeDataString(pair[0]) == name)
        .Select(pair => Uri.UnescapeDataString(pair[1]))
        .Single();

    private sealed class FeedFixture(NativePublicKlineFeed feed, KlineHandler handler,
        HttpClient httpClient, SqliteNativeBarInputLedger? ledger) : IDisposable
    {
        public NativePublicKlineFeed Feed { get; } = feed;
        public KlineHandler Handler { get; } = handler;

        public void Dispose()
        {
            ledger?.Dispose();
            httpClient.Dispose();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class KlineHandler(IReadOnlyList<BinanceKline> bars,
        bool rejectRequests) : HttpMessageHandler
    {
        private readonly List<BinanceKline> _bars = [.. bars];

        public List<Uri> Requests { get; } = [];

        public void Add(BinanceKline kline) => _bars.Add(kline);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            Requests.Add(uri);
            if (rejectRequests)
            {
                throw new InvalidOperationException("Unexpected REST history request.");
            }

            string interval = Query(uri, "interval");
            long start = long.Parse(Query(uri, "startTime"), CultureInfo.InvariantCulture);
            long end = long.Parse(Query(uri, "endTime"), CultureInfo.InvariantCulture);
            int limit = int.Parse(Query(uri, "limit"), CultureInfo.InvariantCulture);
            object?[][] rows = _bars.Where(bar => bar.Timeframe == interval
                    && bar.OpenTimeMilliseconds >= start && bar.OpenTimeMilliseconds <= end)
                .OrderBy(static bar => bar.OpenTimeMilliseconds)
                .Take(limit)
                .Select(static bar => new object?[]
                {
                    bar.OpenTimeMilliseconds,
                    bar.Open.ToString(CultureInfo.InvariantCulture),
                    bar.High.ToString(CultureInfo.InvariantCulture),
                    bar.Low.ToString(CultureInfo.InvariantCulture),
                    bar.Close.ToString(CultureInfo.InvariantCulture),
                    bar.Volume.ToString(CultureInfo.InvariantCulture),
                    bar.CloseTimeMilliseconds,
                    bar.QuoteAssetVolume.ToString(CultureInfo.InvariantCulture),
                    bar.TradeCount,
                    bar.TakerBuyBaseVolume.ToString(CultureInfo.InvariantCulture),
                    bar.TakerBuyQuoteVolume.ToString(CultureInfo.InvariantCulture),
                    bar.Ignore
                }).ToArray();
            string json = JsonSerializer.Serialize(rows);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TemporaryLedger : IDisposable
    {
        public TemporaryLedger() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-feed-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }
}
