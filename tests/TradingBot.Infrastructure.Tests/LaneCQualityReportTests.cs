using System.Text.Json.Nodes;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class LaneCQualityReportTests
{
    private static readonly DateTimeOffset AsOf =
        new(2026, 10, 24, 12, 0, 5, TimeSpan.Zero);

    [Fact]
    public async Task BuildsAndReopensCausalQualityReportAtManifestCheckpoint()
    {
        using TemporaryDirectory fixture = new();
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            Path.Combine(fixture.Path, "capture.db"),
            Path.Combine(fixture.Path, "segments"), "lane-c-quality");
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store, new CaptureDatasetManifestBuildOptions("lane-c-manifest", 8, 8,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 10);
        LaneCFeatureInput input = new(new InstrumentId("BTCUSDT"), [], cursor,
            "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCQualityPolicy policy = new(true, true, TimeSpan.FromSeconds(2));
        CaptureQualitySnapshotBuildOptions options = new(AsOf, 8, 8,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), null);

        LaneCQualityReport report = await LaneCQualityReportBuilder.BuildAsync(store,
            manifest, input, configuration, features, policy, options,
            TestContext.Current.CancellationToken);

        Assert.Equal(manifest.ManifestId, report.ManifestId);
        Assert.Equal(manifest.Checkpoint, report.Checkpoint);
        Assert.Equal(features.InputIdentity, report.Quality.InputIdentity);
        Assert.Equal(LaneCQualityState.InsufficientData, report.Quality.State);
        Assert.Equal(CaptureProviderSilenceStatus.Silent, report.CaptureQuality.ProviderSilenceStatus);
        Assert.Equal(report, await LaneCQualityReportBuilder.BuildAsync(store, manifest,
            input, configuration, features, policy, options,
            TestContext.Current.CancellationToken));

        LaneCQualityReportStore reportStore = new(Path.Combine(fixture.Path, "quality.json"));
        await reportStore.PersistAsync(report, TestContext.Current.CancellationToken);
        LaneCQualityReport reopened = await reportStore.ReadAsync(manifest,
            TestContext.Current.CancellationToken);
        Assert.Equal(report, reopened);

        JsonObject legacySnapshot = JsonNode.Parse(CoreJsonSerializer.Serialize(
            report.CaptureQuality))!.AsObject();
        legacySnapshot.Remove("conflictCount");
        CaptureQualitySnapshot legacy = CoreJsonSerializer.Deserialize<CaptureQualitySnapshot>(
            legacySnapshot.ToJsonString());
        Assert.Equal(0, legacy.ConflictCount);
    }

    [Fact]
    public async Task RejectsFeatureEvidenceFromAnotherCausalInput()
    {
        using TemporaryDirectory fixture = new();
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            Path.Combine(fixture.Path, "capture.db"),
            Path.Combine(fixture.Path, "segments"), "lane-c-quality");
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store, new CaptureDatasetManifestBuildOptions("lane-c-manifest", 8, 8,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 10);
        LaneCFeatureInput input = new(new InstrumentId("BTCUSDT"), [], cursor,
            "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);
        LaneCFeatureInput otherInput = new(input.InstrumentId, [],
            new CausalEventCursor(new UtcTimestamp(AsOf.AddSeconds(1)), 10),
            "other-quality-fixture");

        await Assert.ThrowsAsync<InvalidDataException>(() => LaneCQualityReportBuilder.BuildAsync(
            store, manifest, otherInput, configuration, features,
            new LaneCQualityPolicy(true, true, null),
            new CaptureQualitySnapshotBuildOptions(AsOf, 8, 8, null, null, null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task KeepsHistoricalGapAndConflictSeparateFromCausalUnknownDiagnostics()
    {
        using TemporaryDirectory fixture = new();
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            Path.Combine(fixture.Path, "capture.db"),
            Path.Combine(fixture.Path, "segments"), new CapturePersistenceOptions
            {
                StreamId = "lane-c-quality-history",
                MaxRowsPerSegment = 2,
                MaxBufferedRows = 8
            });
        CaptureEnvelope gap = Diagnostic(0, AsOf.AddSeconds(-2), "BinanceDepthRecoveryGap");
        CaptureEnvelope conflict = Diagnostic(1, AsOf.AddSeconds(-1),
            "BinanceStreamIdentityConflict");
        CaptureEnvelope futureUnknown = Diagnostic(2, AsOf.AddSeconds(1), "future-code");
        await store.AppendAsync(gap, TestContext.Current.CancellationToken);
        await store.AppendAsync(conflict, TestContext.Current.CancellationToken);
        await store.AppendAsync(futureUnknown, TestContext.Current.CancellationToken);
        await store.FlushAsync(TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store, new CaptureDatasetManifestBuildOptions("lane-c-manifest-history", 8, 8,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        CausalEventCursor cursor = new(new UtcTimestamp(AsOf), 2);
        LaneCFeatureInput input = new(new InstrumentId("BTCUSDT"), [gap, conflict, futureUnknown],
            cursor, "quality-fixture");
        LaneCFeatureConfiguration configuration = new("lane-c-formulas-v1",
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            1, 10);
        LaneCFeatureResult features = LaneCFeatureEvaluator.Evaluate(input, configuration);

        LaneCQualityReport report = await LaneCQualityReportBuilder.BuildAsync(store, manifest,
            input, configuration, features, new LaneCQualityPolicy(false, false, null),
            new CaptureQualitySnapshotBuildOptions(AsOf, 8, 8, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, report.CaptureQuality.RecordCount);
        Assert.Equal(1, report.Quality.SignalCounts.GapCount);
        Assert.Equal(1, report.Quality.SignalCounts.ConflictCount);
        Assert.Equal(1, report.CaptureQuality.ConflictCount);
        Assert.Equal(1, report.CaptureQuality.UnknownDiagnosticCodeCount);
        Assert.Empty(report.Quality.UnknownDiagnostics);
        Assert.Equal(LaneCQualityState.SequenceGap, report.Quality.State);
        Assert.Equal(AsOf.AddSeconds(-1), report.CaptureQuality.LastReceiveTimeUtc);
    }

    private static CaptureEnvelope Diagnostic(long sequence, DateTimeOffset eventTime,
        string code) => CaptureEnvelope.FromDiagnostic("lane-c-quality-history", sequence,
        new UtcTimestamp(eventTime), new UtcTimestamp(eventTime), sequence,
        new CaptureDiagnostic(code, "fixture", []),
        new CaptureProvenance("binance-public", "depth", "session", null, null, []));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tradingbot-lane-c-quality-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
