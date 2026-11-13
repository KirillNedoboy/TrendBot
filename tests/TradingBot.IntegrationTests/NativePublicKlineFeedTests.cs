using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task BoundedWarmupUsesOnePageAndTimeframeSpecificRawRowLimits()
    {
        FixedTimeProvider timeProvider = new(WarmupAt);
        KlineHandler handler = new(CreateRestBars(null), false);
        using HttpClient httpClient = new(handler);
        BinancePublicRestClient restClient = new(httpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")), timeProvider);
        using TemporaryLedger database = new();
        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(database.Path);
        PublicSignalProfileConfiguration profile = CreateProfile(1, 1,
            TimeSpan.FromHours(3));

        NativePublicKlineFeed feed = await NativePublicKlineFeed.WarmupBoundedAsync(
            restClient, "BTCUSDT", profile, ledger,
            new NativePublicKlineRestHistoryBudget(1, 12),
            new NativePublicKlineRestHistoryBudget(1, 3),
            timeProvider, TestContext.Current.CancellationToken);

        Assert.Equal(1, feed.SetupRestPagesRequested);
        Assert.Equal(1, feed.ContextRestPagesRequested);
        Assert.Equal(12, int.Parse(Query(handler.Requests[0], "limit"),
            CultureInfo.InvariantCulture));
        Assert.Equal(3, int.Parse(Query(handler.Requests[1], "limit"),
            CultureInfo.InvariantCulture));
        Assert.Equal(11, feed.SetupHistory.Count);
        Assert.Equal(2, feed.ContextHistory.Count);
    }

    [Fact]
    public async Task RestoreFromLedgerRecoversHistoryWithoutRequestingRest()
    {
        using TemporaryLedger database = new();
        string[] expectedSetupIds;
        string[] expectedContextIds;
        long[] expectedSetupSequences;
        long[] expectedContextSequences;
        using (FeedFixture original = await CreateFixtureAsync(ledgerPath: database.Path))
        {
            expectedSetupIds = original.Feed.SetupHistory
                .Select(static bar => bar.Bar.EventId).ToArray();
            expectedContextIds = original.Feed.ContextHistory
                .Select(static bar => bar.Bar.EventId).ToArray();
            expectedSetupSequences = original.Feed.SetupHistory
                .Select(static bar => bar.StableSequence).ToArray();
            expectedContextSequences = original.Feed.ContextHistory
                .Select(static bar => bar.StableSequence).ToArray();
        }

        FixedTimeProvider timeProvider = new(WarmupAt);
        KlineHandler handler = new(CreateRestBars(null), true);
        using HttpClient httpClient = new(handler);
        BinancePublicRestClient restClient = new(httpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")), timeProvider);
        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(database.Path);

        NativePublicKlineFeed recovered = NativePublicKlineFeed.RestoreFromLedger(
            restClient, "BTCUSDT", CreateProfile(1, 1, TimeSpan.FromHours(3)),
            ledger, timeProvider);

        Assert.Empty(handler.Requests);
        Assert.Equal(expectedSetupIds,
            recovered.SetupHistory.Select(static bar => bar.Bar.EventId));
        Assert.Equal(expectedContextIds,
            recovered.ContextHistory.Select(static bar => bar.Bar.EventId));
        Assert.Equal(expectedSetupSequences,
            recovered.SetupHistory.Select(static bar => bar.StableSequence));
        Assert.Equal(expectedContextSequences,
            recovered.ContextHistory.Select(static bar => bar.StableSequence));
    }


    [Fact]
    public async Task BoundedWarmupStopsBeforeRequestingPastItsPageBudget()
    {
        FixedTimeProvider timeProvider = new(WarmupAt);
        KlineHandler handler = new(CreateRestBars(null), false, maxRowsPerResponse: 5);
        using HttpClient httpClient = new(handler);
        BinancePublicRestClient restClient = new(httpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")), timeProvider);
        using TemporaryLedger database = new();
        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(database.Path);
        PublicSignalProfileConfiguration profile = CreateProfile(1, 1,
            TimeSpan.FromHours(3));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NativePublicKlineFeed.WarmupBoundedAsync(restClient, "BTCUSDT",
                profile, ledger, new NativePublicKlineRestHistoryBudget(1, 12),
                new NativePublicKlineRestHistoryBudget(1, 3), timeProvider,
                TestContext.Current.CancellationToken));

        Assert.Contains("page budget", exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal("15m", Query(handler.Requests[0], "interval"));
        Assert.Equal("12", Query(handler.Requests[0], "limit"));
        Assert.Equal(5, ledger.Read("binance-usdm-native-kline:BTCUSDT").Receipts.Count);
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

    [Theory]
    [InlineData("price-context-v3")]
    [InlineData("price-context-v4")]
    public async Task VersionedRecoveryProfileProducesACommittedDecision(string version)
    {
        using TemporaryLedger inputDatabase = new();
        using TemporaryLedger journalDatabase = new();
        PublicSignalProfileConfiguration profile = CreateProfile(1, 1, TimeSpan.FromHours(3),
            priceContextVersion: version);
        using FeedFixture fixture = await CreateFixtureAsync(ledgerPath: inputDatabase.Path,
            priceContextVersion: version);
        using SqliteNativeBarInputLedger ledger = SqliteNativeBarInputLedger.Open(inputDatabase.Path);
        using SqliteSignalJournalStore journal = SqliteSignalJournalStore.Open(journalDatabase.Path);
        using SignalOnlyInstrumentConsumer consumer = new("BTCUSDT", profile,
            CreateScenarioPolicies(), fixture.Feed, ledger, journal);
        BinanceKline trigger = CreateKline(PrimaryOpen, "15m", true);
        NativePublicKlineFeedUpdate update = await consumer.ProcessAsync(
            CreateEnvelope(trigger, CoClose.AddMilliseconds(20)), TestContext.Current.CancellationToken);

        NativePublicKlineEvaluation evaluation = Assert.IsType<NativePublicKlineEvaluation>(update.Evaluation);
        Assert.True(evaluation.IsReady);
        Assert.NotEqual(BoundaryRejectionSetupState.UnknownConfiguration, evaluation.LaneA.BoundaryRejection.Long.State);
        string eventId = BinanceKlineIdentity.Create("BTCUSDT", "15m",
            trigger.OpenTimeMilliseconds).EventId;
        AssertDurableOutcome(evaluation, journal.Read("binance-usdm-native-kline:BTCUSDT"), eventId);
    }

    [Fact]
    public async Task SignalOnlyConsumerPersistsClosedInputAndReopensWithoutDuplicates()
    {
        using TemporaryLedger inputDatabase = new();
        using TemporaryLedger journalDatabase = new();
        PublicSignalProfileConfiguration profile = CreateProfile(1, 1,
            TimeSpan.FromHours(3));
        SignalScenarioPolicySet policies = CreateScenarioPolicies();
        string streamId = "binance-usdm-native-kline:BTCUSDT";
        BinanceKline trigger = CreateKline(PrimaryOpen, "15m", true);
        BinanceStreamEnvelope envelope = CreateEnvelope(trigger, CoClose.AddMilliseconds(20));
        string eventId = BinanceKlineIdentity.Create("BTCUSDT", "15m",
            trigger.OpenTimeMilliseconds).EventId;
        string committedJson;

        using (FeedFixture fixture = await CreateFixtureAsync(
            ledgerPath: inputDatabase.Path))
        using (SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(inputDatabase.Path))
        using (SqliteSignalJournalStore journal =
            SqliteSignalJournalStore.Open(journalDatabase.Path))
        {
            using SignalOnlyInstrumentConsumer consumer = new("BTCUSDT", profile,
                policies, fixture.Feed, ledger, journal);
            NativePublicKlineFeedUpdate update = await consumer.ProcessAsync(envelope,
                TestContext.Current.CancellationToken);
            NativePublicKlineEvaluation evaluation =
                Assert.IsType<NativePublicKlineEvaluation>(update.Evaluation);
            Assert.Equal(BinanceKlineIngestionStatus.Accepted, update.Status);
            Assert.True(evaluation.IsReady);
            Assert.Equal(1m, evaluation.LaneA.BoundaryRejection.Configuration.MinimumChannelWidth);

            SignalJournalSnapshot committed = journal.Read(streamId);
            Assert.False(evaluation.LaneA.SetupObservations.IsComplete);
            Assert.Empty(committed.Signals);
            Assert.Contains(committed.Diagnostics, diagnostic =>
                diagnostic.InputEventId == eventId
                && diagnostic.Code == "signal-evaluation-unknown");
            AssertDurableOutcome(evaluation, committed, eventId);
            committedJson = CoreJsonSerializer.Serialize(committed);
        }

        KlineHandler rejectingHandler = new(CreateRestBars(null), true);
        using HttpClient recoveryHttpClient = new(rejectingHandler);
        BinancePublicRestClient recoveryRestClient = new(recoveryHttpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")),
            new FixedTimeProvider(WarmupAt));
        using SqliteNativeBarInputLedger reopenedLedger =
            SqliteNativeBarInputLedger.Open(inputDatabase.Path);
        using SqliteSignalJournalStore reopenedJournal =
            SqliteSignalJournalStore.Open(journalDatabase.Path);
        NativePublicKlineFeed recoveredFeed = NativePublicKlineFeed.RestoreFromLedger(
            recoveryRestClient, "BTCUSDT", profile, reopenedLedger,
            new FixedTimeProvider(WarmupAt));
        using SignalOnlyInstrumentConsumer recovered = new("BTCUSDT", profile,
            policies, recoveredFeed, reopenedLedger, reopenedJournal);

        NativePublicKlineFeedUpdate duplicate = await recovered.ProcessAsync(envelope,
            TestContext.Current.CancellationToken);

        Assert.Equal(BinanceKlineIngestionStatus.Duplicate, duplicate.Status);
        Assert.Equal(committedJson,
            CoreJsonSerializer.Serialize(reopenedJournal.Read(streamId)));
        Assert.Empty(rejectingHandler.Requests);
    }

    [Fact]
    public async Task SignalOnlyConsumerPersistsConfirmedSignalAndOutbox()
    {
        using TemporaryLedger inputDatabase = new();
        using TemporaryLedger journalDatabase = new();
        DateTimeOffset contextHistoryStart = HistoryStart.AddHours(-2);
        BinanceKline[] restBars = CreateRestBars(null, contextHistoryStart);
        DateTimeOffset pivotOpen = HistoryStart.AddMinutes(15 * 8);
        int pivotIndex = Array.FindIndex(restBars, bar => bar.Timeframe == "15m"
            && bar.OpenTimeMilliseconds == pivotOpen.ToUnixTimeMilliseconds());
        Assert.NotEqual(-1, pivotIndex);
        restBars[pivotIndex] = CreateKline(pivotOpen, "15m", true, low: 60m);
        DateTimeOffset contextPivotOpen = contextHistoryStart.AddHours(1);
        int contextPivotIndex = Array.FindIndex(restBars, bar => bar.Timeframe == "1h"
            && bar.OpenTimeMilliseconds == contextPivotOpen.ToUnixTimeMilliseconds());
        Assert.NotEqual(-1, contextPivotIndex);
        restBars[contextPivotIndex] = CreateKline(contextPivotOpen, "1h", true, low: 60m);

        KlineHandler handler = new(restBars, false);
        using HttpClient httpClient = new(handler);
        FixedTimeProvider timeProvider = new(WarmupAt);
        BinancePublicRestClient rest = new(httpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")), timeProvider);
        using SqliteNativeBarInputLedger ledger =
            SqliteNativeBarInputLedger.Open(inputDatabase.Path);
        using SqliteSignalJournalStore journal =
            SqliteSignalJournalStore.Open(journalDatabase.Path);
        PublicSignalProfileConfiguration profile = CreateProfile(1, 1,
            TimeSpan.FromHours(3), htfSlowPeriodBars: 2,
            contextHistoryStart: contextHistoryStart, channelCenterPeriodBars: 2);
        NativePublicKlineFeed feed = await NativePublicKlineFeed.WarmupAsync(
            rest, "BTCUSDT", profile, ledger, timeProvider,
            TestContext.Current.CancellationToken);
        using SignalOnlyInstrumentConsumer consumer = new("BTCUSDT", profile,
            CreateScenarioPolicies(), feed, ledger, journal);
        BinanceKline context = CreateKline(ContextOpen, "1h", true);
        NativePublicKlineFeedUpdate contextUpdate = await consumer.ProcessAsync(
            CreateEnvelope(context, CoClose.AddMilliseconds(10)),
            TestContext.Current.CancellationToken);
        Assert.Null(contextUpdate.Evaluation);
        BinanceKline trigger = CreateKline(PrimaryOpen, "15m", true,
            low: 100m, openOverride: 110m, highOverride: 112m,
            closeOverride: 111m);
        string triggerEventId = BinanceKlineIdentity.Create("BTCUSDT", "15m",
            trigger.OpenTimeMilliseconds).EventId;
        BinanceStreamEnvelope envelope = CreateEnvelope(trigger,
            CoClose.AddMilliseconds(20));

        NativePublicKlineEvaluation evaluation = Assert.IsType<NativePublicKlineEvaluation>(
            (await consumer.ProcessAsync(envelope, TestContext.Current.CancellationToken))
                .Evaluation);

        Assert.True(evaluation.IsReady);
        Assert.Equal(LaneASetupObservationStatus.Confirmed,
            evaluation.LaneA.SetupObservations.CenterRetestLongStatus);
        SignalJournalSnapshot snapshot = journal.Read(
            "binance-usdm-native-kline:BTCUSDT");
        SignalJournalSignal persisted = Assert.Single(snapshot.Signals);
        Assert.Equal(SetupType.CenterRetest, persisted.Signal.Identity.Family);
        Assert.Equal(Direction.Long, persisted.Signal.Identity.Direction);
        Assert.Equal(BinanceKlineIdentity.Create("BTCUSDT", "15m",
            pivotOpen.ToUnixTimeMilliseconds()).EventId,
            persisted.Signal.Identity.SetupEventId);
        Assert.Equal(triggerEventId, persisted.Signal.ReferencePrice.EventId);
        Assert.Equal(SignalEvidenceState.Complete, persisted.Evidence.State);
        string contextEventId = BinanceKlineIdentity.Create("BTCUSDT", "1h",
            context.OpenTimeMilliseconds).EventId;
        NativeBarInputLedgerReceipt contextReceipt = Assert.Single(
            ledger.Read("binance-usdm-native-kline:BTCUSDT").Receipts,
            receipt => receipt.EventId == contextEventId
                && receipt.Status == NativeBarInputReceiptStatus.Accepted);
        SignalInputReference contextReference = Assert.Single(
            persisted.Evidence.Provenance.InputReferences,
            reference => reference.Role == SignalInputRole.HigherTimeframe
                && reference.EventId == contextEventId);
        Assert.Equal(contextReceipt.RevisionHash, contextReference.PayloadHash);
        Assert.Contains(snapshot.ScenarioEvents, scenarioEvent =>
            scenarioEvent.Kind == SignalScenarioEventKind.AlertCreated
            && scenarioEvent.SignalId == persisted.Signal.Identity.SignalId);
        Assert.Single(snapshot.Outbox);
    }

    [Fact]
    public async Task SignalOnlyConsumerReplaysInputCommittedBeforeProcessCrash()
    {
        using TemporaryLedger inputDatabase = new();
        using TemporaryLedger journalDatabase = new();
        PublicSignalProfileConfiguration profile = CreateProfile(1, 1,
            TimeSpan.FromHours(3));
        SignalScenarioPolicySet policies = CreateScenarioPolicies();
        string streamId = "binance-usdm-native-kline:BTCUSDT";
        BinanceKline trigger = CreateKline(PrimaryOpen, "15m", true);
        BinanceStreamEnvelope envelope = CreateEnvelope(trigger, CoClose.AddMilliseconds(20));
        string eventId = BinanceKlineIdentity.Create("BTCUSDT", "15m",
            trigger.OpenTimeMilliseconds).EventId;

        using (FeedFixture fixture = await CreateFixtureAsync(
            ledgerPath: inputDatabase.Path))
        using (SqliteSignalJournalStore journal =
            SqliteSignalJournalStore.Open(journalDatabase.Path))
        {
            NativePublicKlineFeedUpdate accepted = fixture.Feed.Accept(envelope);
            Assert.Equal(BinanceKlineIngestionStatus.Accepted, accepted.Status);
            Assert.Null(journal.Read(streamId).Checkpoint);
        }

        KlineHandler rejectingHandler = new(CreateRestBars(null), true);
        using HttpClient recoveryHttpClient = new(rejectingHandler);
        BinancePublicRestClient recoveryRestClient = new(recoveryHttpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")),
            new FixedTimeProvider(WarmupAt));
        using SqliteNativeBarInputLedger reopenedLedger =
            SqliteNativeBarInputLedger.Open(inputDatabase.Path);
        using SqliteSignalJournalStore reopenedJournal =
            SqliteSignalJournalStore.Open(journalDatabase.Path);
        NativePublicKlineFeed recoveredFeed = NativePublicKlineFeed.RestoreFromLedger(
            recoveryRestClient, "BTCUSDT", profile, reopenedLedger,
            new FixedTimeProvider(WarmupAt));
        using SignalOnlyInstrumentConsumer recovered = new("BTCUSDT", profile,
            policies, recoveredFeed, reopenedLedger, reopenedJournal);

        SignalJournalSnapshot recoveredSnapshot = reopenedJournal.Read(streamId);
        NativeBarInputLedgerReceipt triggerReceipt = Assert.Single(
            reopenedLedger.Read(streamId).Receipts, receipt =>
                receipt.EventId == eventId
                && receipt.Status == NativeBarInputReceiptStatus.Accepted
                && receipt.TriggerEventId == eventId);
        NativePublicKlineEvaluation replayed = Assert.IsType<NativePublicKlineEvaluation>(
            recoveredFeed.ReplayDurableReceipt(triggerReceipt).Update.Evaluation);
        AssertDurableOutcome(replayed, recoveredSnapshot, eventId);
        Assert.NotNull(recoveredSnapshot.Checkpoint);
        Assert.Empty(rejectingHandler.Requests);
    }

    [Fact]
    public async Task SignalOnlyConfigurationRejectsPrivateTransportBeforeOpeningStores()
    {
        using TemporaryLedger inputDatabase = new();
        using TemporaryLedger journalDatabase = new();
        SignalOnlyConfiguration configuration = new(["btcusdt"],
            inputDatabase.Path, journalDatabase.Path,
            CreateProfile(1, 1, TimeSpan.FromHours(3)), CreateScenarioPolicies());
        JsonObject root = JsonNode.Parse(configuration.ToJson())!.AsObject();
        root["privateTransport"] = "enabled";
        string configPath = Path.Combine(Path.GetTempPath(),
            $"tradingbot-signal-only-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, root.ToJsonString(),
            TestContext.Current.CancellationToken);

        try
        {
            using StringWriter output = new();
            using StringWriter error = new();
            int result = await SignalOnlyCommand.RunAsync(
                ["signal-only", "--config", configPath], output, error,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, result);
            Assert.Contains("invalid public configuration", error.ToString(),
                StringComparison.Ordinal);
            Assert.False(File.Exists(inputDatabase.Path));
            Assert.False(File.Exists(journalDatabase.Path));
            Assert.Throws<JsonException>(() =>
                SignalOnlyConfiguration.Parse(root.ToJsonString()));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void SignalOnlyCompositionHasNoPrivateOrExecutionDependencies()
    {
        string[] forbiddenNames =
        [
            "Private", "Signer", "Executor", "Risk", "Authorization",
            "OrderIntent", "Position", "Portfolio"
        ];
        Type[] roots = [typeof(SignalOnlyCommand), typeof(SignalOnlyInstrumentConsumer),
            typeof(SignalOnlyBoundedPreflight), typeof(SignalOnlyBoundedRun)];
        Type[] implementationTypes = roots.Concat(roots.SelectMany(type =>
            type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))).ToArray();
        Type[] dependencyTypes = implementationTypes.SelectMany(DeclaredTypes)
            .SelectMany(ExpandType).ToArray();

        Assert.DoesNotContain(dependencyTypes, type => forbiddenNames.Any(name =>
            type.Name.Contains(name, StringComparison.Ordinal)));
    }

    private static IEnumerable<Type> DeclaredTypes(Type type)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return type.GetFields(flags).Select(static field => field.FieldType)
            .Concat(type.GetProperties(flags).Select(static property => property.PropertyType))
            .Concat(type.GetConstructors(flags).SelectMany(static constructor =>
                constructor.GetParameters().Select(static parameter => parameter.ParameterType)))
            .Concat(type.GetMethods(flags).SelectMany(static method =>
                new[] { method.ReturnType }.Concat(method.GetParameters()
                    .Select(static parameter => parameter.ParameterType))));
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (Type element in ExpandType(elementType))
            {
                yield return element;
            }
        }

        yield return type;
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                foreach (Type nested in ExpandType(argument))
                {
                    yield return nested;
                }
            }
        }
    }

    private static void AssertDurableOutcome(NativePublicKlineEvaluation evaluation,
        SignalJournalSnapshot snapshot, string sourceEventId)
    {
        Assert.NotNull(snapshot.Checkpoint);
        LaneASetupObservationProjection projection = evaluation.LaneA.SetupObservations;
        List<string> confirmedInputs = [];
        Add(projection.BoundaryLongStatus, SetupType.BoundaryRejection, Direction.Long);
        Add(projection.BoundaryShortStatus, SetupType.BoundaryRejection, Direction.Short);
        Add(projection.CenterRetestLongStatus, SetupType.CenterRetest, Direction.Long);
        Add(projection.CenterRetestShortStatus, SetupType.CenterRetest, Direction.Short);
        if (confirmedInputs.Count == 0)
        {
            string expectedCode = projection.IsComplete
                ? "no-confirmed-setup" : "signal-evaluation-unknown";
            Assert.Contains(snapshot.Diagnostics, diagnostic =>
                diagnostic.InputEventId == sourceEventId && diagnostic.Code == expectedCode);
            return;
        }

        Assert.NotNull(snapshot.ScenarioSnapshot);
        foreach (string inputEventId in confirmedInputs)
        {
            Assert.True(snapshot.ScenarioSnapshot.ProcessedInputs.Any(input =>
                    input.EventId == inputEventId)
                || snapshot.Diagnostics.Any(diagnostic =>
                    diagnostic.InputEventId == inputEventId));
        }

        void Add(LaneASetupObservationStatus status, SetupType family,
            Direction direction)
        {
            if (status == LaneASetupObservationStatus.Confirmed)
            {
                confirmedInputs.Add(
                    $"scenario-confirm:{sourceEventId}:{family}:{direction}");
            }
        }
    }

    private static SignalScenarioPolicySet CreateScenarioPolicies() => new(
        SignalScenarioPolicySet.CurrentFormatVersion,
        new SignalScenarioExpiryPolicy(SignalScenarioExpiryPolicy.CurrentFormatVersion,
            "signal-only-expiry-v1", TimeSpan.FromHours(24)),
        new SignalScenarioRearmPolicy(SignalScenarioRearmPolicy.CurrentFormatVersion,
            "signal-only-rearm-v1", TimeSpan.Zero, true),
        new SignalScenarioInvalidationPolicy(
            SignalScenarioInvalidationPolicy.CurrentFormatVersion,
            "signal-only-invalidation-v1", true, true));


    private static async Task<FeedFixture> CreateFixtureAsync(int setupSeedBars = 1,
        int setupWarmupBars = 1, int? missingSetupIndex = null,
        TimeSpan? maximumAge = null, string? ledgerPath = null,
        bool rejectRestRequests = false,
        string priceContextVersion = PriceContextFeatureCalculator.CurrentAlgorithmVersion)
    {
        FixedTimeProvider timeProvider = new(WarmupAt);
        BinanceKline[] bars = CreateRestBars(missingSetupIndex);
        KlineHandler handler = new(bars, rejectRestRequests);
        HttpClient httpClient = new(handler);
        BinancePublicRestClient rest = new(httpClient,
            new BinanceRestOptions(new Uri("https://fixture.test")), timeProvider);
        PublicSignalProfileConfiguration profile = CreateProfile(setupSeedBars,
            setupWarmupBars, maximumAge ?? TimeSpan.FromHours(3), priceContextVersion: priceContextVersion);
        SqliteNativeBarInputLedger? ledger = ledgerPath is null
            ? null : SqliteNativeBarInputLedger.Open(ledgerPath);
        NativePublicKlineFeed feed = ledger is null
            ? await NativePublicKlineFeed.WarmupAsync(rest, "BTCUSDT", profile, timeProvider)
            : await NativePublicKlineFeed.WarmupAsync(rest, "BTCUSDT", profile, ledger,
                timeProvider);
        return new FeedFixture(feed, handler, httpClient, ledger);
    }

    private static PublicSignalProfileConfiguration CreateProfile(int setupSeedBars,
        int setupWarmupBars, TimeSpan maximumAge, int htfSlowPeriodBars = 1,
        DateTimeOffset? contextHistoryStart = null, int channelCenterPeriodBars = 1,
        string priceContextVersion = PriceContextFeatureCalculator.CurrentAlgorithmVersion)
    {
        PublicSignalTimeframeConfiguration Timeframe(string timeframe, int seedBars,
            int warmupBars, DateTimeOffset? historyStart = null) => new(timeframe,
            PublicSignalDataSource.BinanceUsdMNativeKlines,
            "binance-usdm-native-kline-v1", historyStart ?? HistoryStart, "history-v1",
            seedBars, "seed-v1", warmupBars, "warmup-v1", "freshness-v1",
            maximumAge, maximumAge);

        PublicSignalIndicatorConfiguration indicators = new(
            priceContextVersion: priceContextVersion,
            featureQualityVersion: FeatureQualityEvaluation.CurrentAlgorithmVersion,
            setupFamilyVersion: "lane-a-v1", pivotLeftBars: 1, pivotRightBars: 1,
            atrPeriodBars: 1, atrMode: PriceContextAtrMode.Sma,
            atrValueUnit: PublicSignalValueUnit.Price,
            channelCenterKind: PriceContextChannelCenterKind.Ema,
            channelCenterPeriodBars: channelCenterPeriodBars,
            hlc3SmoothingKind: PriceContextMovingAverageKind.Ema,
            channelCenterValueUnit: PublicSignalValueUnit.Price,
            channelWidthKind: PriceContextChannelWidthKind.Atr,
            channelWidthPeriodBars: 1, channelWidthMultiplier: 1m,
            channelWidthValueUnit: PublicSignalValueUnit.Price,
            channelWidthMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless,
            levelBufferMultiplier: 1m,
            levelBufferMultiplierUnit: PublicSignalMultiplierUnit.Dimensionless,
            htfFastPeriodBars: 1, htfSlowPeriodBars: htfSlowPeriodBars,
            htfMovingAverageKind: PriceContextMovingAverageKind.Ema,
            htfConfirmationBars: 1,
            htfMovingAverageValueUnit: PublicSignalValueUnit.Price,
            boundaryMinimumChannelWidth: 1m,
            boundaryMinimumChannelWidthUnit: PublicSignalValueUnit.Price);
        return new PublicSignalProfileConfiguration(1, "test-profile-v1",
            Timeframe("15m", setupSeedBars, setupWarmupBars),
            Timeframe("1h", 1, 1, contextHistoryStart), indicators,
            new PublicSignalUniverseConfiguration("test-universe-v1", 1, 1));
    }

    private static BinanceKline[] CreateRestBars(int? missingSetupIndex,
        DateTimeOffset? contextHistoryStart = null)
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

        DateTimeOffset contextStart = contextHistoryStart ?? HistoryStart;
        for (int index = 0; index <= 4; index++)
        {
            DateTimeOffset openTime = contextStart.AddHours(index);
            bars.Add(CreateKline(openTime, "1h",
                openTime.AddHours(1) <= WarmupAt));
        }

        return [.. bars];
    }

    private static BinanceKline CreateKline(DateTimeOffset openTime, string timeframe,
        bool isClosed, decimal? low = null, decimal? openOverride = null,
        decimal? highOverride = null, decimal? closeOverride = null)
    {
        long duration = timeframe == "15m" ? TimeSpan.FromMinutes(15).Ticks
            : TimeSpan.FromHours(1).Ticks;
        long openMilliseconds = openTime.ToUnixTimeMilliseconds();
        decimal open = openOverride
            ?? 100m + (decimal)(openTime - HistoryStart).TotalMinutes / 100m;
        decimal close = closeOverride ?? open + 0.5m;
        decimal high = highOverride ?? Math.Max(open, close) + 1m;
        decimal minimum = low ?? open - 1m;
        return new BinanceKline(openMilliseconds, open, high,
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
        bool rejectRequests, int maxRowsPerResponse = int.MaxValue) : HttpMessageHandler
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
                .Take(Math.Min(limit, maxRowsPerResponse))
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
