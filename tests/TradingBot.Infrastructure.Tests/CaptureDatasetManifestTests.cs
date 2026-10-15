using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class CaptureDatasetManifestTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BuildsAllowlistedManifestWithoutPayloadOrAbsoluteSegmentPaths()
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope[] rows = CreateRows();
        CapturePersistenceOptions persistence = new()
        {
            StreamId = "manifest-stream",
            MaxRowsPerSegment = 1,
            MaxBufferedRows = 4
        };

        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, persistence);
        foreach (CaptureEnvelope row in rows)
        {
            store.Append(row);
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);
        CaptureDatasetManifestBuildOptions options = new(
            "manifest-1", 4, 4,
            new CaptureManifestRetentionPolicy(null, null),
            [new CaptureManifestGap("sequence gap", "capture:3", "fixture:gap")],
            [new CaptureManifestExclusion("out of scope", "symbol:ETHUSDT", "fixture:scope")]);

        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store, options, TestContext.Current.CancellationToken);
        CaptureDatasetManifestStore manifestStore = new(fixture.ManifestPath);
        await manifestStore.PersistAsync(manifest, TestContext.Current.CancellationToken);
        string json = await File.ReadAllTextAsync(fixture.ManifestPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(CaptureDatasetManifest.CurrentFormatVersion, manifest.FormatVersion);
        Assert.Equal("manifest-1", manifest.ManifestId);
        Assert.Equal("manifest-stream", manifest.StreamId);
        Assert.Equal(store.Checkpoint.NextCaptureSequence,
            manifest.Checkpoint.NextCaptureSequence);
        Assert.Equal(3, manifest.TotalRecordCount);
        Assert.Equal(3, manifest.Segments.Count);
        Assert.Single(manifest.Provenance.Instruments);
        Assert.Contains("BTCUSDT", manifest.Provenance.Instruments);
        Assert.Contains("binance.usdm", manifest.Provenance.Sources);
        Assert.Contains("session-1", manifest.Provenance.Sessions);
        Assert.NotNull(manifest.ObservedBounds.EarliestEventTime);
        Assert.NotNull(manifest.ObservedBounds.LatestAvailabilityTime);
        Assert.Equal(CaptureManifestCoverage.Unknown, manifest.Coverage);
        Assert.Single(manifest.Gaps);
        Assert.Single(manifest.Exclusions);
        Assert.DoesNotContain("raw-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.SegmentDirectory, json, StringComparison.Ordinal);
        Assert.DoesNotContain(rows[0].Provenance.RawPayload!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(manifest.Segments, segment => json.Contains(
            segment.SegmentId + ".parquet", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://user:secret@example.test/feed")]
    [InlineData("https://example.test/feed?listenKey=private-value")]
    [InlineData("C:\\capture\\absolute\\source")]
    public async Task RejectsPrivateOrAbsoluteSourceMetadata(string source)
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope row = CreateRows(source)[0];
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, row.StreamId);
        await store.AppendAsync(row, TestContext.Current.CancellationToken);
        await store.FlushAsync(TestContext.Current.CancellationToken);

        CaptureDatasetManifestBuildOptions options = new(
            "manifest-unsafe", 2, 2,
            new CaptureManifestRetentionPolicy(null, null), [], []);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CaptureDatasetManifestBuilder.BuildAsync(
                store, options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PersistsAndReopensOnlyAtTheFixedDurableCheckpoint()
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope first = CreateRows()[0];
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, first.StreamId);
        await store.AppendAsync(first, TestContext.Current.CancellationToken);
        await store.FlushAsync(TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store,
            new CaptureDatasetManifestBuildOptions(
                "manifest-checkpoint", 2, 2,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        CaptureDatasetManifestStore manifestStore = new(fixture.ManifestPath);
        await manifestStore.PersistAsync(manifest, TestContext.Current.CancellationToken);

        CaptureDatasetManifest reopened = await manifestStore.ReadAsync(
            store, TestContext.Current.CancellationToken);
        Assert.Equal(manifest, reopened);

        await store.AppendAsync(CreateRows()[1], TestContext.Current.CancellationToken);
        await store.FlushAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => manifestStore.ReadAsync(
            store, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownRetentionPolicyProducesNoCandidatesAndNeverDeletes()
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope[] rows = CreateRows();
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory,
            new CapturePersistenceOptions
            {
                StreamId = rows[0].StreamId,
                MaxRowsPerSegment = 1,
                MaxBufferedRows = 4
            });
        foreach (CaptureEnvelope row in rows)
        {
            store.Append(row);
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store,
            new CaptureDatasetManifestBuildOptions(
                "manifest-retention", 4, 4,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        string[] filesBefore = Directory.GetFiles(fixture.SegmentDirectory);
        CaptureCheckpoint checkpointBefore = store.Checkpoint;

        CaptureRetentionDryRunResult result = CaptureRetentionPlanner.DryRun(
            manifest, new CaptureManifestRetentionPolicy(null, null),
            new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(CaptureRetentionStatus.Unknown, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Equal(checkpointBefore, store.Checkpoint);
        Assert.Equal(filesBefore, Directory.GetFiles(fixture.SegmentDirectory));
    }

    [Fact]
    public async Task ByteRetentionUsesDurableAgeThenCaptureSequenceTieBreak()
    {
        using TemporaryCaptureStore fixture = new();
        CaptureEnvelope[] rows = CreateRows();
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory,
            new CapturePersistenceOptions
            {
                StreamId = rows[0].StreamId,
                MaxRowsPerSegment = 1,
                MaxBufferedRows = 4
            });
        foreach (CaptureEnvelope row in rows)
        {
            store.Append(row);
        }

        await store.FlushAsync(TestContext.Current.CancellationToken);
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store,
            new CaptureDatasetManifestBuildOptions(
                "manifest-byte-retention", 4, 4,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        long maxTotalBytes = manifest.TotalByteLength - manifest.Segments[0].ByteLength;

        CaptureRetentionDryRunResult result = CaptureRetentionPlanner.DryRun(
            manifest, new CaptureManifestRetentionPolicy(null, maxTotalBytes),
            DateTimeOffset.UtcNow);

        Assert.Equal(CaptureRetentionStatus.Candidates, result.Status);
        Assert.NotEmpty(result.Candidates);
        Assert.Equal(manifest.Segments[0].SegmentId, result.Candidates[0].SegmentId);
        Assert.Contains("max-total-bytes", result.Candidates[0].Reasons);
        Assert.DoesNotContain(result.Candidates, candidate =>
            candidate.Reasons.Contains("max-age"));
    }

    private static CaptureEnvelope[] CreateRows(string? source = null)
    {
        source ??= "binance.usdm";
        CaptureProvenance depthProvenance = new(source, "btcusdt@depth",
            "session-1", "depth:BTCUSDT:1:2", "raw-secret", ["public"]);
        BookDelta depth = new("depth-1", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(BaseTime), [new BookLevel(new Price(100m), new Quantity(2m))], []);
        CaptureEnvelope first = CaptureEnvelope.FromMarketEvent("manifest-stream", 0, depth,
            new UtcTimestamp(BaseTime.AddMilliseconds(1)),
            new UtcTimestamp(BaseTime.AddMilliseconds(2)), 10, depthProvenance,
            new UtcTimestamp(BaseTime.AddMilliseconds(-1)));

        CaptureProvenance tradeProvenance = new(source, "btcusdt@aggTrade",
            "session-1", "aggTrade:BTCUSDT:2", "raw-secret-2", []);
        Trade trade = new("trade-1", new InstrumentId("BTCUSDT"),
            new UtcTimestamp(BaseTime.AddSeconds(1)), new Price(101m), new Quantity(1m),
            TradeSide.Sell);
        CaptureEnvelope second = CaptureEnvelope.FromMarketEvent("manifest-stream", 1, trade,
            new UtcTimestamp(BaseTime.AddSeconds(1)),
            new UtcTimestamp(BaseTime.AddSeconds(1).AddMilliseconds(1)), 11,
            tradeProvenance);

        CaptureDiagnostic diagnostic = new("gap", "depth gap", ["u=3"]);
        CaptureEnvelope third = CaptureEnvelope.FromDiagnostic("manifest-stream", 2,
            new UtcTimestamp(BaseTime.AddSeconds(2)),
            new UtcTimestamp(BaseTime.AddSeconds(2)), 12, diagnostic,
            new CaptureProvenance(source, "btcusdt@depth", "session-1", null,
                "raw-secret-3", ["gap"]));
        return [first, second, third];
    }

    private sealed class TemporaryCaptureStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "tradingbot-manifest-" + Guid.NewGuid().ToString("N"));

        public TemporaryCaptureStore()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "capture.db");
            SegmentDirectory = Path.Combine(_root, "segments");
            ManifestPath = Path.Combine(_root, "manifest.json");
        }

        public string DatabasePath { get; }

        public string SegmentDirectory { get; }

        public string ManifestPath { get; }

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
