using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class CaptureQualitySnapshotTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BuildsDeterministicQualitySnapshotFromDurableEvidence()
    {
        using TemporaryCaptureStore fixture = new();
        await using ParquetCaptureStore store = await CreateEvidenceStoreAsync(
            fixture, TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CreateManifestAsync(
            store, TestContext.Current.CancellationToken);

        CaptureQualitySnapshot snapshot = await CaptureQualitySnapshotBuilder.BuildAsync(
            store, manifest, CreateOptions(BaseTime.AddSeconds(10),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        Assert.Equal(CaptureQualitySnapshot.CurrentFormatVersion, snapshot.FormatVersion);
        Assert.Equal(manifest.ManifestId, snapshot.ManifestId);
        Assert.Equal(manifest.Checkpoint, snapshot.Checkpoint);
        Assert.Equal(7, snapshot.RecordCount);
        Assert.Equal(2, snapshot.GapCount);
        Assert.Equal(1, snapshot.DuplicateCount);
        Assert.Equal(1, snapshot.MissingFieldCount);
        Assert.Equal(1, snapshot.ClockSkewCount);
        Assert.Equal(1, snapshot.StaleCount);
        Assert.Equal(1, snapshot.UnknownDiagnosticCodeCount);
        Assert.Equal(7, snapshot.Latency.SampleCount);
        Assert.Equal(TimeSpan.FromSeconds(-1), snapshot.Latency.Minimum);
        Assert.Equal(BaseTime.AddSeconds(6), snapshot.LastReceiveTimeUtc);
        Assert.Equal(CaptureFreshnessStatus.Fresh, snapshot.FreshnessStatus);
        Assert.Equal(CaptureProviderSilenceStatus.Active, snapshot.ProviderSilenceStatus);
        Assert.Equal(CaptureClockSkewStatus.Signaled, snapshot.ClockSkewStatus);
        Assert.Equal(snapshot, await CaptureQualitySnapshotBuilder.BuildAsync(
            store, manifest, CreateOptions(BaseTime.AddSeconds(10),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingThresholdsStayUnknownAndSilenceCoversEmptyOrNoNewCases()
    {
        using TemporaryCaptureStore emptyFixture = new();
        await using ParquetCaptureStore emptyStore = ParquetCaptureStore.Open(
            emptyFixture.DatabasePath, emptyFixture.SegmentDirectory, "quality");
        CaptureDatasetManifest emptyManifest = await CreateManifestAsync(
            emptyStore, TestContext.Current.CancellationToken);
        CaptureQualitySnapshot emptySnapshot =
            await CaptureQualitySnapshotBuilder.BuildAsync(
                emptyStore, emptyManifest,
                CreateOptions(BaseTime, null, null, null),
                TestContext.Current.CancellationToken);

        Assert.Equal(CaptureFreshnessStatus.Unknown, emptySnapshot.FreshnessStatus);
        Assert.Equal(CaptureProviderSilenceStatus.Unknown,
            emptySnapshot.ProviderSilenceStatus);
        Assert.Equal(CaptureClockSkewStatus.Unknown, emptySnapshot.ClockSkewStatus);

        CaptureQualitySnapshot emptySilentSnapshot =
            await CaptureQualitySnapshotBuilder.BuildAsync(
                emptyStore, emptyManifest,
                CreateOptions(BaseTime, TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
                TestContext.Current.CancellationToken);
        Assert.Equal(CaptureProviderSilenceStatus.Silent,
            emptySilentSnapshot.ProviderSilenceStatus);

        using TemporaryCaptureStore staleFixture = new();
        await using ParquetCaptureStore staleStore = await CreateStaleStoreAsync(
            staleFixture, TestContext.Current.CancellationToken);
        CaptureDatasetManifest staleManifest = await CreateManifestAsync(
            staleStore, TestContext.Current.CancellationToken);
        CaptureQualitySnapshot staleSnapshot =
            await CaptureQualitySnapshotBuilder.BuildAsync(
                staleStore, staleManifest,
                CreateOptions(BaseTime, TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
                TestContext.Current.CancellationToken);

        Assert.Equal(CaptureFreshnessStatus.Stale, staleSnapshot.FreshnessStatus);
        Assert.Equal(CaptureProviderSilenceStatus.Silent,
            staleSnapshot.ProviderSilenceStatus);
    }

    [Fact]
    public async Task PersistsAndRebuildsSnapshotAtManifestCheckpoint()
    {
        using TemporaryCaptureStore fixture = new();
        await using ParquetCaptureStore store = await CreateEvidenceStoreAsync(
            fixture, TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CreateManifestAsync(
            store, TestContext.Current.CancellationToken);
        CaptureQualitySnapshot expected = await CaptureQualitySnapshotBuilder.BuildAsync(
            store, manifest, CreateOptions(BaseTime.AddSeconds(10),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);
        CaptureQualitySnapshotStore snapshotStore = new(fixture.SnapshotPath);
        await snapshotStore.PersistAsync(expected, TestContext.Current.CancellationToken);

        CaptureQualitySnapshot reopened = await snapshotStore.ReadAsync(
            manifest, TestContext.Current.CancellationToken);
        Assert.Equal(expected, reopened);

        await using ParquetCaptureStore restarted = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, "quality");
        CaptureQualitySnapshot rebuilt = await CaptureQualitySnapshotBuilder.BuildAsync(
            restarted, manifest, CreateOptions(BaseTime.AddSeconds(10),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, rebuilt);
    }

    [Fact]
    public async Task PublishesCountersOnceAndGaugesThroughBclMetrics()
    {
        using TemporaryCaptureStore fixture = new();
        await using ParquetCaptureStore store = await CreateEvidenceStoreAsync(
            fixture, TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CreateManifestAsync(
            store, TestContext.Current.CancellationToken);
        CaptureQualitySnapshot snapshot = await CaptureQualitySnapshotBuilder.BuildAsync(
            store, manifest, CreateOptions(BaseTime.AddSeconds(10),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        List<(string Name, long Value)> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == CaptureQualityMetricsPublisher.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        using CaptureQualityMetricsPublisher publisher = new();
        publisher.Publish(snapshot);
        publisher.Publish(snapshot);
        listener.RecordObservableInstruments();

        Assert.Equal(1, measurements.Count(item => item.Name == "capture.records"));
        Assert.Equal(7, measurements.Single(item => item.Name == "capture.records").Value);
        Assert.Contains(measurements, item => item.Name == "capture.gaps"
            && item.Value == snapshot.GapCount);
        Assert.Contains(measurements, item => item.Name == "capture.last_receive_unix_ms"
            && item.Value == snapshot.LastReceiveTimeUtc!.Value.ToUnixTimeMilliseconds());
    }

    private static CaptureQualitySnapshotBuildOptions CreateOptions(DateTimeOffset asOfUtc,
        TimeSpan? freshnessThreshold = null, TimeSpan? providerSilenceThreshold = null,
        TimeSpan? clockSkewThreshold = null) => new(asOfUtc, 16, 16,
        freshnessThreshold, providerSilenceThreshold, clockSkewThreshold);

    private static async Task<CaptureDatasetManifest> CreateManifestAsync(
        ParquetCaptureStore store, CancellationToken cancellationToken) =>
        await CaptureDatasetManifestBuilder.BuildAsync(store,
            new CaptureDatasetManifestBuildOptions("quality-manifest", 16, 16,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            cancellationToken);

    private static async Task<ParquetCaptureStore> CreateEvidenceStoreAsync(
        TemporaryCaptureStore fixture, CancellationToken cancellationToken)
    {
        ParquetCaptureStore store = ParquetCaptureStore.Open(fixture.DatabasePath,
            fixture.SegmentDirectory, new CapturePersistenceOptions
            {
                StreamId = "quality",
                MaxRowsPerSegment = 1,
                MaxBufferedRows = 16
            });
        CaptureEnvelope[] rows =
        [
            MarketRow(0, BaseTime.AddSeconds(2), BaseTime.AddSeconds(1),
                ["gap"]),
            DiagnosticRow(1, "gap", BaseTime.AddSeconds(1), ["gap"]),
            DiagnosticRow(2, "duplicate", BaseTime.AddSeconds(2), ["duplicate"]),
            DiagnosticRow(3, "missing-field", BaseTime.AddSeconds(3), ["missing-field"]),
            DiagnosticRow(4, "clock-skew", BaseTime.AddSeconds(4), ["clock-skew"]),
            DiagnosticRow(5, "stale", BaseTime.AddSeconds(5), ["stale"]),
            DiagnosticRow(6, "mystery", BaseTime.AddSeconds(6), ["mystery"])
        ];
        foreach (CaptureEnvelope row in rows)
        {
            await store.AppendAsync(row, cancellationToken);
        }

        await store.FlushAsync(cancellationToken);
        return store;
    }

    private static async Task<ParquetCaptureStore> CreateStaleStoreAsync(
        TemporaryCaptureStore fixture, CancellationToken cancellationToken)
    {
        ParquetCaptureStore store = ParquetCaptureStore.Open(fixture.DatabasePath,
            fixture.SegmentDirectory, "quality");
        await store.AppendAsync(MarketRow(0, BaseTime.AddSeconds(-10),
            BaseTime.AddSeconds(-10)), cancellationToken);
        await store.FlushAsync(cancellationToken);
        return store;
    }

    private static CaptureEnvelope MarketRow(long captureSequence,
        DateTimeOffset eventTime, DateTimeOffset receiveTime,
        IEnumerable<string>? flags = null) =>
        CaptureEnvelope.FromMarketEvent("quality", captureSequence,
            new Trade("trade-" + captureSequence, new InstrumentId("BTCUSDT"),
                new UtcTimestamp(eventTime), new Price(100m + captureSequence),
                new Quantity(1m), TradeSide.Buy), new UtcTimestamp(receiveTime),
            new UtcTimestamp(receiveTime.AddMilliseconds(1)), captureSequence + 10,
            new CaptureProvenance("synthetic", "quality", "session-1",
                "event-" + captureSequence, "raw-secret", flags ?? []));

    private static CaptureEnvelope DiagnosticRow(long captureSequence, string code,
        DateTimeOffset receiveTime, IEnumerable<string> flags) =>
        CaptureEnvelope.FromDiagnostic("quality", captureSequence,
            new UtcTimestamp(receiveTime), new UtcTimestamp(receiveTime),
            captureSequence + 10, new CaptureDiagnostic(code, code, []),
            new CaptureProvenance("synthetic", "quality", "session-1",
                "diagnostic-" + captureSequence, "raw-secret", flags));

    private sealed class TemporaryCaptureStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "tradingbot-quality-" + Guid.NewGuid().ToString("N"));

        public TemporaryCaptureStore()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "capture.db");
            SegmentDirectory = Path.Combine(_root, "segments");
            SnapshotPath = Path.Combine(_root, "quality.json");
        }

        public string DatabasePath { get; }

        public string SegmentDirectory { get; }

        public string SnapshotPath { get; }

        public void Dispose()
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Directory.Delete(_root, recursive: true);
        }
    }
}
