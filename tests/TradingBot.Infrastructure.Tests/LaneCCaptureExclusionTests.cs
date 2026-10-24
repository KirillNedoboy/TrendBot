using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class LaneCCaptureExclusionTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BuildsDeterministicLedgerAndRecordsRetentionDryRunWithoutDeletion()
    {
        using TemporaryDirectory fixture = new();
        (ParquetCaptureStore store, CaptureDatasetManifest manifest,
            LaneCQualityReport report, CaptureEnvelope row) = await CreateReportAsync(fixture);
        await using (store)
        {
            CaptureManifestSegment segment = Assert.Single(manifest.Segments);
            LaneCCaptureExclusionEntry known = new(segment.SegmentId,
                segment.FirstCaptureSequence, segment.LastCaptureSequence, "BTCUSDT",
                "binance-public", row.EventTime.Value, row.EventTime.Value,
                ["provider-gap"], "quality-report");
            LaneCCaptureExclusionEntry unknownBounds = new(null, null, null, "BTCUSDT",
                "binance-public", null, null, ["unbounded-provider-silence"],
                "quality-report");
            CaptureManifestRetentionPolicy policy = new(TimeSpan.FromSeconds(1), null);
            DateTimeOffset asOf = segment.DurableTimeUtc.AddSeconds(1);
            CaptureCheckpoint checkpoint = store.Checkpoint;
            string[] files = Directory.GetFiles(fixture.SegmentDirectory);

            LaneCCaptureExclusionLedger first = LaneCCaptureExclusionLedgerBuilder.Build(
                manifest, report, [unknownBounds, known], policy, asOf);
            LaneCCaptureExclusionLedger reordered = LaneCCaptureExclusionLedgerBuilder.Build(
                manifest, report, [known, unknownBounds], policy, asOf);

            Assert.Equal(first, reordered);
            Assert.Equal(manifest.Coverage, first.Coverage);
            Assert.Equal(CaptureManifestCoverage.Unknown, first.Coverage);
            Assert.Equal(report.ReportIdentity, first.QualityReportIdentity);
            Assert.Equal(manifest.Checkpoint, first.Checkpoint);
            Assert.Equal(2, first.Exclusions.Count);
            Assert.Null(first.Exclusions.Single(item => item.ReasonCodes.Contains(
                "unbounded-provider-silence")).FirstCaptureSequence);
            Assert.Equal(CaptureRetentionStatus.Candidates, first.RetentionDryRun.Status);
            Assert.Contains(first.RetentionDryRun.Candidates,
                item => item.SegmentId == segment.SegmentId
                    && item.Reasons.Contains("max-age"));
            Assert.Equal(checkpoint, store.Checkpoint);
            Assert.Equal(files, Directory.GetFiles(fixture.SegmentDirectory));

            LaneCCaptureExclusionLedgerStore ledgerStore = new(
                Path.Combine(fixture.Path, "exclusions.json"));
            await ledgerStore.PersistAsync(first, TestContext.Current.CancellationToken);
            LaneCCaptureExclusionLedger reopened = await ledgerStore.ReadAsync(manifest,
                report, TestContext.Current.CancellationToken);
            Assert.Equal(first, reopened);
        }
    }

    [Fact]
    public async Task UnknownBoundsAndUnknownRetentionPolicyRemainUnknown()
    {
        using TemporaryDirectory fixture = new();
        (ParquetCaptureStore store, CaptureDatasetManifest manifest,
            LaneCQualityReport report, _) = await CreateReportAsync(fixture);
        await using (store)
        {
            LaneCCaptureExclusionEntry unknown = new(null, null, null, null,
                "binance-public", null, null, ["unbounded-gap"], "quality-report");
            LaneCCaptureExclusionLedger ledger = LaneCCaptureExclusionLedgerBuilder.Build(
                manifest, report, [unknown], new CaptureManifestRetentionPolicy(null, null),
                BaseTime.AddSeconds(5));

            Assert.Equal(CaptureManifestCoverage.Unknown, ledger.Coverage);
            Assert.Equal(CaptureRetentionStatus.Unknown, ledger.RetentionDryRun.Status);
            Assert.Empty(ledger.RetentionDryRun.Candidates);
            Assert.Null(Assert.Single(ledger.Exclusions).FirstCaptureSequence);
            Assert.Null(Assert.Single(ledger.Exclusions).EarliestEventTimeUtc);
        }
    }

    [Fact]
    public async Task RejectsReferencesOutsideManifestAndUnsafeProvenance()
    {
        using TemporaryDirectory fixture = new();
        (ParquetCaptureStore store, CaptureDatasetManifest manifest,
            LaneCQualityReport report, _) = await CreateReportAsync(fixture);
        await using (store)
        {
            Assert.Throws<ArgumentException>(() => new LaneCCaptureExclusionEntry(null,
                null, null, null, "https://user:password@example.com", null, null,
                ["provider-gap"], "quality-report"));

            LaneCCaptureExclusionEntry outside = new("missing-segment", null, null,
                "BTCUSDT", "binance-public", null, null, ["provider-gap"],
                "quality-report");
            Assert.Throws<InvalidDataException>(() =>
                LaneCCaptureExclusionLedgerBuilder.Build(manifest, report, [outside],
                    new CaptureManifestRetentionPolicy(null, null), BaseTime));
        }
    }

    [Fact]
    public async Task ReadValidatesBothManifestAndQualityReportLinks()
    {
        using TemporaryDirectory fixture = new();
        (ParquetCaptureStore store, CaptureDatasetManifest manifest,
            LaneCQualityReport report, CaptureEnvelope _) = await CreateReportAsync(fixture);
        await using (store)
        {
            LaneCCaptureExclusionLedger ledger = LaneCCaptureExclusionLedgerBuilder.Build(
                manifest, report, [], new CaptureManifestRetentionPolicy(null, null), BaseTime);
            LaneCCaptureExclusionLedgerStore ledgerStore = new(
                Path.Combine(fixture.Path, "exclusions.json"));
            await ledgerStore.PersistAsync(ledger, TestContext.Current.CancellationToken);

            LaneCQualityReport otherReport = await LaneCQualityReportBuilder.BuildAsync(store,
                manifest, new LaneCFeatureInput(new InstrumentId("BTCUSDT"), [],
                    new CausalEventCursor(new UtcTimestamp(BaseTime.AddSeconds(5)), 7),
                    "other-quality-fixture"), new LaneCFeatureConfiguration(
                    "lane-c-formulas-v1", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(3), 1, 10), LaneCFeatureEvaluator.Evaluate(
                    new LaneCFeatureInput(new InstrumentId("BTCUSDT"), [],
                        new CausalEventCursor(new UtcTimestamp(BaseTime.AddSeconds(5)), 7),
                        "other-quality-fixture"), new LaneCFeatureConfiguration(
                        "lane-c-formulas-v1", TimeSpan.FromSeconds(10),
                        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), 1, 10)),
                new LaneCQualityPolicy(false, false, null),
                new CaptureQualitySnapshotBuildOptions(BaseTime.AddSeconds(5), 8, 8,
                    null, null, null), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() => ledgerStore.ReadAsync(manifest,
                otherReport, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ReadRejectsExclusionReferencesOutsideManifest()
    {
        using TemporaryDirectory fixture = new();
        (ParquetCaptureStore store, CaptureDatasetManifest manifest,
            LaneCQualityReport report, _) = await CreateReportAsync(fixture);
        await using (store)
        {
            LaneCCaptureExclusionLedger valid = LaneCCaptureExclusionLedgerBuilder.Build(
                manifest, report, [], new CaptureManifestRetentionPolicy(null, null), BaseTime);
            ImmutableValueList<LaneCCaptureExclusionEntry> forgedEntries = new(
                [new LaneCCaptureExclusionEntry("outside-manifest", null, null, null,
                    "binance-public", null, null, ["provider-gap"], "quality-report")]);
            string identity = LaneCCaptureExclusionLedger.ComputeIdentity(
                valid.QualityReportIdentity, valid.ManifestId, valid.Checkpoint,
                valid.Coverage, forgedEntries, valid.RetentionDryRun);
            LaneCCaptureExclusionLedger forged = new(valid.FormatVersion, identity,
                valid.QualityReportIdentity, valid.ManifestId, valid.Checkpoint,
                valid.Coverage, forgedEntries, valid.RetentionDryRun);
            LaneCCaptureExclusionLedgerStore ledgerStore = new(
                Path.Combine(fixture.Path, "forged-exclusions.json"));
            await ledgerStore.PersistAsync(forged, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidDataException>(() => ledgerStore.ReadAsync(manifest,
                report, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SortsEntriesWithMatchingPrimaryKeysByTheirCompleteEvidence()
    {
        using TemporaryDirectory fixture = new();
        (ParquetCaptureStore store, CaptureDatasetManifest manifest,
            LaneCQualityReport report, CaptureEnvelope row) = await CreateReportAsync(fixture);
        await using (store)
        {
            CaptureManifestSegment segment = Assert.Single(manifest.Segments);
            LaneCCaptureExclusionEntry earlier = new(segment.SegmentId,
                segment.FirstCaptureSequence, segment.LastCaptureSequence, "BTCUSDT",
                "binance-public", row.EventTime!.Value, row.EventTime.Value,
                ["provider-gap"], "quality-report");
            LaneCCaptureExclusionEntry later = new(segment.SegmentId,
                segment.FirstCaptureSequence, segment.LastCaptureSequence, "BTCUSDT",
                "binance-public", row.EventTime.Value.AddSeconds(1),
                row.EventTime.Value.AddSeconds(1), ["provider-gap"], "quality-report");
            CaptureManifestRetentionPolicy policy = new(null, null);

            LaneCCaptureExclusionLedger forward = LaneCCaptureExclusionLedgerBuilder.Build(
                manifest, report, [earlier, later], policy, BaseTime);
            LaneCCaptureExclusionLedger reverse = LaneCCaptureExclusionLedgerBuilder.Build(
                manifest, report, [later, earlier], policy, BaseTime);

            Assert.Equal([earlier.EarliestEventTimeUtc, later.EarliestEventTimeUtc],
                forward.Exclusions.Select(static item => item.EarliestEventTimeUtc));
            Assert.Equal(forward.Exclusions.Select(static item => item.EarliestEventTimeUtc),
                reverse.Exclusions.Select(static item => item.EarliestEventTimeUtc));
            Assert.Equal(forward, reverse);
        }
    }

    private static async Task<(ParquetCaptureStore Store, CaptureDatasetManifest Manifest,
        LaneCQualityReport Report, CaptureEnvelope Row)> CreateReportAsync(
        TemporaryDirectory fixture)
    {
        ParquetCaptureStore store = ParquetCaptureStore.Open(
            Path.Combine(fixture.Path, "capture.db"),
            Path.Combine(fixture.Path, "segments"), new CapturePersistenceOptions
            {
                StreamId = "lane-c-exclusions",
                MaxRowsPerSegment = 1,
                MaxBufferedRows = 8
            });
        CaptureEnvelope row = CaptureEnvelope.FromDiagnostic("lane-c-exclusions", 0,
            new UtcTimestamp(BaseTime), new UtcTimestamp(BaseTime), 7,
            new CaptureDiagnostic("BinanceDepthRecoveryGap", "fixture", []),
            new CaptureProvenance("binance-public", "depth", "session", null, null, []));
        await store.AppendAsync(row, TestContext.Current.CancellationToken);
        await store.FlushAsync(TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(store,
            new CaptureDatasetManifestBuildOptions("lane-c-exclusion-manifest", 8, 8,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        CausalEventCursor cursor = new(new UtcTimestamp(BaseTime.AddSeconds(5)), 7);
        LaneCFeatureInput input = new(new InstrumentId("BTCUSDT"), [row], cursor,
            "exclusion-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 8);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityReport report = await LaneCQualityReportBuilder.BuildAsync(store, manifest,
            input, configuration, features, new LaneCQualityPolicy(false, false, null),
            new CaptureQualitySnapshotBuildOptions(BaseTime.AddSeconds(5), 8, 8,
                null, null, null), TestContext.Current.CancellationToken);
        return (store, manifest, report, row);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tradingbot-lane-c-exclusions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string SegmentDirectory => System.IO.Path.Combine(Path, "segments");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
