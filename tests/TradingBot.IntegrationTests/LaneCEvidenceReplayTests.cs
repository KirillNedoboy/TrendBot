using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LaneCEvidenceReplayTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EvidenceBundleBuildsAndValidatesCausalLinks()
    {
        using TemporaryCapture fixture = new();
        CapturePersistenceOptions persistence = new() { StreamId = "lane-c-evidence" };
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, persistence);
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(store,
            new CaptureDatasetManifestBuildOptions("lane-c-evidence-manifest", 8, 8,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        CausalEventCursor cursor = new(new UtcTimestamp(BaseTime.AddSeconds(1)), 0);
        LaneCFeatureInput input = new(new InstrumentId("BTCUSDT"), [], cursor,
            "fixed-evidence-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 1, 8);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityReport report = await LaneCQualityReportBuilder.BuildAsync(store, manifest,
            input, configuration, features, new LaneCQualityPolicy(true, true, null),
            new CaptureQualitySnapshotBuildOptions(BaseTime.AddSeconds(1), 8, 8,
                null, null, null), TestContext.Current.CancellationToken);
        LaneCCaptureExclusionLedger exclusions = LaneCCaptureExclusionLedgerBuilder.Build(
            manifest, report, [], new CaptureManifestRetentionPolicy(null, null), BaseTime);

        LaneCEvidenceBundle bundle = LaneCEvidenceBundleBuilder.Build(manifest, report,
            exclusions, features, new LaneCFeatureRuleTable("empty-rules-v1", []),
            "fixed-evidence-fixture");
        LaneCEvidenceBundle restored = CoreJsonSerializer.Deserialize<LaneCEvidenceBundle>(
            CoreJsonSerializer.Serialize(bundle));

        Assert.Equal(LaneCShadowStatus.InsufficientData, bundle.Shadow.Status);
        Assert.Contains("feature-not-ready:InsufficientData", bundle.Shadow.Reasons);
        Assert.Equal(DecisionEvidenceStatus.Unknown, bundle.DecisionEvidence.Status);
        Assert.Equal(cursor, bundle.Shadow.Cursor);
        Assert.Equal(bundle, restored);
        Assert.Equal(bundle.BundleIdentity, restored.BundleIdentity);
    }

    [Fact]
    public async Task FixedCaptureReplayPreservesCausalPrefixesRecoveryAndEvidenceIdentities()
    {
        using TemporaryCapture fixture = new();
        CapturePersistenceOptions persistence = new() { StreamId = "lane-c-replay-fixture" };
        CaptureEnvelope[] captured;
        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, persistence))
        {
            LaneCHistoryCapturePipeline pipeline = new(store, persistence.StreamId);
            CaptureEnvelope snapshot = pipeline.CaptureRecoveredSnapshot(ReadyBook(1, 0,
                BaseTime));
            BinanceDepthUpdateEvent firstDepth = Depth(100, 101, 99, BaseTime.AddMilliseconds(1));
            CaptureEnvelope depth = Assert.IsType<CaptureEnvelope>(pipeline.CaptureStreamEvent(
                Stream("session-a", 11, firstDepth, BaseTime.AddMilliseconds(2),
                    BaseTime.AddMilliseconds(3)), 1));
            CaptureEnvelope delayedTrade = Assert.IsType<CaptureEnvelope>(
                pipeline.CaptureStreamEvent(Stream("session-b", 0,
                    Trade(BaseTime.AddMilliseconds(1), 90), BaseTime.AddMilliseconds(3),
                    BaseTime.AddMilliseconds(4)), 1));
            CaptureEnvelope gap = pipeline.CaptureStreamDiagnostic(new BinanceStreamDiagnostic(
                BinanceStreamDiagnosticKind.Gap, "session-a", "btcusdt@depth@100ms",
                "depth:BTCUSDT:103:104", "Synthetic fixed gap", 12,
                new UtcTimestamp(BaseTime.AddMilliseconds(5))));
            CaptureEnvelope recoveredSnapshot = pipeline.CaptureRecoveredSnapshot(
                ReadyBook(2, 1, BaseTime.AddMilliseconds(10)));
            CaptureEnvelope recoveredDepth = Assert.IsType<CaptureEnvelope>(
                pipeline.CaptureStreamEvent(Stream("session-c", 1,
                    Depth(200, 201, 199, BaseTime.AddMilliseconds(11)),
                    BaseTime.AddMilliseconds(12), BaseTime.AddMilliseconds(13)), 2));
            CaptureEnvelope recoveredTrade = Assert.IsType<CaptureEnvelope>(
                pipeline.CaptureStreamEvent(Stream("session-d", 0,
                    Trade(BaseTime.AddMilliseconds(11), 91), BaseTime.AddMilliseconds(13),
                    BaseTime.AddMilliseconds(14)), 2));
            CaptureEnvelope conflict = pipeline.CaptureStreamDiagnostic(
                new BinanceStreamDiagnostic(BinanceStreamDiagnosticKind.IdentityConflict,
                    "session-c", "btcusdt@depth@100ms", "depth:BTCUSDT:201:202",
                    "Synthetic identity conflict", 2,
                    new UtcTimestamp(BaseTime.AddMilliseconds(15))));
            Assert.Equal(new long[] { 0, 1, 2, 3, 4, 5, 6, 7 },
                new[] { snapshot, depth, delayedTrade, gap, recoveredSnapshot, recoveredDepth,
                    recoveredTrade, conflict }.Select(static row => row.StableSequence));
            Assert.Equal(depth.EventTime, delayedTrade.EventTime);
            Assert.True(delayedTrade.AvailabilityTime!.Value
                > depth.AvailabilityTime!.Value);
            await store.FlushAsync(TestContext.Current.CancellationToken);
            captured = (await store.ReadAllAsync(TestContext.Current.CancellationToken)).ToArray();
        }

        await using ParquetCaptureStore reopened = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, persistence);
        CaptureEnvelope[] rows = (await reopened.ReadAllAsync(
            TestContext.Current.CancellationToken)).ToArray();
        Assert.Equal(captured, rows);
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(reopened,
            new CaptureDatasetManifestBuildOptions("lane-c-replay-fixture-manifest", 32, 32,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        LaneCFeatureConfiguration configuration = new("lane-c-fixture-formulas-v1",
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), 1, 32);
        LaneCFeatureRuleTable rules = new("lane-c-fixture-rules-v1", []);

        CausalEventCursor prefixCursor = new(new UtcTimestamp(BaseTime.AddMilliseconds(3)), 2);
        LaneCFeatureInput prefixInput = new(new InstrumentId("BTCUSDT"), rows, prefixCursor,
            "fixed-synthetic-capture");
        LaneCFeatureResult prefixFeatures = LaneCFeatureEvaluator.Evaluate(prefixInput,
            configuration);
        Assert.Equal(prefixCursor, prefixFeatures.Cursor);
        Assert.Null(prefixFeatures.AggressiveBuyVolume);

        CausalEventCursor recoveredCursor = new(new UtcTimestamp(BaseTime.AddMilliseconds(14)), 6);
        LaneCFeatureInput recoveredInput = new(new InstrumentId("BTCUSDT"), rows,
            recoveredCursor, "fixed-synthetic-capture");
        LaneCFeatureResult recoveredFeatures = LaneCFeatureEvaluator.Evaluate(recoveredInput,
            configuration);
        LaneCQualityReport recoveredReport = await LaneCQualityReportBuilder.BuildAsync(reopened,
            manifest, recoveredInput, configuration, recoveredFeatures,
            new LaneCQualityPolicy(true, true, TimeSpan.FromSeconds(2)),
            new CaptureQualitySnapshotBuildOptions(recoveredCursor.AsOf.Value, 32, 32,
                null, null, null), TestContext.Current.CancellationToken);
        Assert.Equal(1, recoveredReport.Quality.SignalCounts.GapCount);
        Assert.Equal(LaneCFeatureState.Ready, recoveredFeatures.State);
        Assert.True(recoveredReport.Quality.State == LaneCQualityState.Ready,
            $"recovered={recoveredFeatures.State}:{string.Join(',', recoveredFeatures.Reasons)}; "
                + $"quality={recoveredReport.Quality.State}:{string.Join(',', recoveredReport.Quality.Reasons)}");
        Assert.DoesNotContain("capture-gap-active", recoveredReport.Quality.Reasons);

        CausalEventCursor conflictCursor = new(new UtcTimestamp(BaseTime.AddMilliseconds(15)), 7);
        LaneCFeatureInput conflictInput = new(new InstrumentId("BTCUSDT"), rows, conflictCursor,
            "fixed-synthetic-capture");
        LaneCFeatureResult conflictFeatures = LaneCFeatureEvaluator.Evaluate(conflictInput,
            configuration);
        LaneCQualityReport conflictReport = await LaneCQualityReportBuilder.BuildAsync(reopened,
            manifest, conflictInput, configuration, conflictFeatures,
            new LaneCQualityPolicy(true, true, TimeSpan.FromSeconds(2)),
            new CaptureQualitySnapshotBuildOptions(conflictCursor.AsOf.Value, 32, 32,
                null, null, null), TestContext.Current.CancellationToken);
        Assert.Equal(1, conflictReport.Quality.SignalCounts.ConflictCount);
        Assert.Equal(LaneCQualityState.Conflict, conflictReport.Quality.State);

        CausalEventCursor staleCursor = new(new UtcTimestamp(BaseTime.AddSeconds(10)), 6);
        LaneCFeatureInput staleInput = new(new InstrumentId("BTCUSDT"), rows[4..5], staleCursor,
            "fixed-synthetic-capture");
        LaneCFeatureResult staleFeatures = LaneCFeatureEvaluator.Evaluate(staleInput,
            configuration);
        LaneCQualityReport staleReport = await LaneCQualityReportBuilder.BuildAsync(reopened,
            manifest, staleInput, configuration, staleFeatures,
            new LaneCQualityPolicy(true, true, TimeSpan.FromSeconds(2)),
            new CaptureQualitySnapshotBuildOptions(staleCursor.AsOf.Value, 32, 32,
                null, null, null), TestContext.Current.CancellationToken);
        Assert.Equal(LaneCQualityState.Stale, staleReport.Quality.State);
        Assert.Equal(LaneCFeatureState.Stale, staleFeatures.State);

        CaptureEnvelope gapRow = rows.Single(static row => row.Diagnostic?.Code ==
            "binance-stream-Gap");
        LaneCCaptureExclusionEntry exclusion = new(manifest.Segments[0].SegmentId,
            gapRow.CaptureSequence, gapRow.CaptureSequence, "BTCUSDT", "binance.usdm",
            gapRow.EventTime?.Value, gapRow.EventTime?.Value, ["recovery-gap"],
            "fixed-synthetic-capture");
        LaneCCaptureExclusionLedger ledger = LaneCCaptureExclusionLedgerBuilder.Build(manifest,
            recoveredReport, [exclusion], new CaptureManifestRetentionPolicy(null, null),
            BaseTime.AddMilliseconds(14));
        LaneCCaptureExclusionLedgerStore ledgerStore = new(fixture.LedgerPath);
        await ledgerStore.PersistAsync(ledger, TestContext.Current.CancellationToken);
        LaneCCaptureExclusionLedger restoredLedger = await ledgerStore.ReadAsync(manifest,
            recoveredReport, TestContext.Current.CancellationToken);
        LaneCQualityReportStore reportStore = new(fixture.ReportPath);
        await reportStore.PersistAsync(recoveredReport, TestContext.Current.CancellationToken);
        LaneCQualityReport restoredReport = await reportStore.ReadAsync(manifest,
            TestContext.Current.CancellationToken);
        CaptureCheckpoint checkpointBeforeBundle = reopened.Checkpoint;
        LaneCEvidenceBundle first = LaneCEvidenceBundleBuilder.Build(manifest, restoredReport,
            restoredLedger, recoveredFeatures, rules, "fixed-synthetic-capture");
        LaneCEvidenceBundle replayed = LaneCEvidenceBundleBuilder.Build(manifest, restoredReport,
            restoredLedger, recoveredFeatures, rules, "fixed-synthetic-capture");
        LaneCEvidenceBundle roundTrip = CoreJsonSerializer.Deserialize<LaneCEvidenceBundle>(
            CoreJsonSerializer.Serialize(first));
        Assert.Equal(first.BundleIdentity, replayed.BundleIdentity);
        Assert.Equal("rules-empty", Assert.Single(first.Shadow.Reasons));
        Assert.Equal(first.Shadow.Reasons, replayed.Shadow.Reasons);
        Assert.Equal(first.DecisionEvidence, replayed.DecisionEvidence);
        Assert.Equal(first, roundTrip);
        Assert.Equal(DecisionEvidenceStatus.Unknown, first.DecisionEvidence.Status);
        Assert.Equal(manifest.Coverage, first.Exclusions.Coverage);
        Assert.Equal(checkpointBeforeBundle, reopened.Checkpoint);
        Assert.Throws<ArgumentException>(() => new LaneCEvidenceBundle(
            LaneCEvidenceBundle.CurrentFormatVersion, "invalid-identity", manifest.ManifestId,
            manifest.Checkpoint, "mismatched-quality-report", restoredLedger.LedgerIdentity,
            recoveredFeatures, restoredReport, restoredLedger, first.Shadow,
            first.DecisionEvidence));
    }

    private static BinanceOrderBookState ReadyBook(long generation, long stableSequence,
        DateTimeOffset time)
    {
        DateTimeOffset eventTime = time.AddMilliseconds(1);
        return new BinanceOrderBookState("BTCUSDT", BinanceOrderBookStateKind.Ready,
            generation * 100, generation * 100 - 1,
            [new BinanceDepthLevel(99m + generation * 100, 2m)],
            [new BinanceDepthLevel(101m + generation * 100, 3m)], generation, stableSequence,
            ToMillis(eventTime), ToMillis(eventTime), new UtcTimestamp(time),
            new UtcTimestamp(time.AddMilliseconds(1)), new UtcTimestamp(time.AddMilliseconds(-1)),
            new UtcTimestamp(time), new UtcTimestamp(time.AddMilliseconds(1)),
            ToMillis(time.AddMilliseconds(-1)), ToMillis(time.AddMilliseconds(-2)));
    }

    private static BinanceDepthUpdateEvent Depth(long first, long last, long previous,
        DateTimeOffset time) => new("BTCUSDT", ToMillis(time), first, last, previous,
        [new BinanceDepthLevel(100m + last, 4m)], [], "btcusdt@depth@100ms");

    private static BinanceAggregateTradeEvent Trade(DateTimeOffset time, long tradeId) => new("BTCUSDT",
        ToMillis(time), tradeId, 100m, 0.5m, tradeId, tradeId, ToMillis(time), false,
        "btcusdt@aggTrade");

    private static BinanceStreamEnvelope Stream(string session, long sequence,
        BinanceWebSocketEvent payload, DateTimeOffset receive, DateTimeOffset available) =>
        new(session, sequence, payload, new UtcTimestamp(receive),
            new UtcTimestamp(available), new UtcTimestamp(BaseTime.AddMilliseconds(1)),
            "{\"fixture\":true}");

    private static long ToMillis(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private sealed class TemporaryCapture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "tradingbot-lane-c-evidence-" + Guid.NewGuid().ToString("N"));

        public TemporaryCapture() => Directory.CreateDirectory(_root);

        public string DatabasePath => Path.Combine(_root, "capture.db");

        public string SegmentDirectory => Path.Combine(_root, "segments");

        public string ReportPath => Path.Combine(_root, "quality-report.json");

        public string LedgerPath => Path.Combine(_root, "exclusions.json");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
