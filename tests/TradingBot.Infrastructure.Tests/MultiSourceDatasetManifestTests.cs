using System.Text.Json;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class MultiSourceDatasetManifestTests
{
    [Fact]
    public void MultiSourceManifestContractIsAvailableAtRuntime()
    {
        Type? manifestType = Type.GetType(
            "TradingBot.Core.MultiSourceDatasetManifest, TradingBot.Core");

        Assert.NotNull(manifestType);
    }

    [Fact]
    public async Task ManifestRoundTripsAndIdentityDoesNotDependOnInputOrder()
    {
        using TemporaryDirectory directory = new();
        MultiSourceDatasetSource depth = Source("depth", MultiSourceLaneKind.L2, 10);
        MultiSourceDatasetSource funding = Source("funding", MultiSourceLaneKind.Funding, 20);
        MultiSourceReplayOrderEntry[] order =
        [
            Entry("depth", "depth-1", 0, 1),
            Entry("funding", "funding-1", 0, 2)
        ];

        MultiSourceDatasetManifest first = MultiSourceDatasetManifest.Create(
            "multi-lane-fixture", [depth, funding], order);
        MultiSourceDatasetManifest permuted = MultiSourceDatasetManifest.Create(
            "multi-lane-fixture", [funding, depth], order.Reverse());
        MultiSourceDatasetManifestStore store = new(Path.Combine(directory.Path,
            "manifest.json"));

        await store.PersistAsync(first, TestContext.Current.CancellationToken);
        MultiSourceDatasetManifest reopened = await store.ReadAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Identity, permuted.Identity);
        Assert.Equal(first, reopened);
        Assert.Equal("depth", reopened.Ordering[0].SourceId);
        Assert.Equal(0, reopened.Ordering[0].SourceSequence);
        Assert.Equal(1, reopened.Ordering[0].GlobalStableSequence);
    }

    [Fact]
    public void ManifestRejectsConflictingGlobalSequenceAndDanglingSourceReference()
    {
        MultiSourceDatasetSource depth = Source("depth", MultiSourceLaneKind.L2, 10);
        MultiSourceDatasetSource funding = Source("funding", MultiSourceLaneKind.Funding, 20);

        Assert.Throws<ArgumentException>(() => MultiSourceDatasetManifest.Create(
            "multi-lane-fixture", [depth, funding],
            [Entry("depth", "depth-1", 0, 1), Entry("funding", "funding-1", 0, 1)]));
        Assert.Throws<ArgumentException>(() => MultiSourceDatasetManifest.Create(
            "multi-lane-fixture", [depth], [Entry("funding", "funding-1", 0, 1)]));
    }

    [Fact]
    public void UnknownCoverageAndQualityRemainExplicit()
    {
        MultiSourceDatasetSource unknown = new("liquidations",
            MultiSourceLaneKind.Liquidations, "source-manifest-3", null,
            [],
            MultiSourceCoverage.Unknown, MultiSourceQuality.Unknown, [], 0,
            null, null, null, null, null, null);

        MultiSourceDatasetManifest manifest = MultiSourceDatasetManifest.Create(
            "multi-lane-unknown", [unknown], []);

        Assert.Equal(MultiSourceCoverage.Unknown, manifest.Sources[0].Coverage);
        Assert.Equal(MultiSourceQuality.Unknown, manifest.Sources[0].Quality);
        Assert.Null(manifest.Sources[0].FirstSourceSequence);
        Assert.Null(manifest.Sources[0].QualityEvidenceIdentity);
    }

    [Fact]
    public void ManifestIdentityChangesWhenARecordedSequenceChanges()
    {
        MultiSourceDatasetSource depth = Source("depth", MultiSourceLaneKind.L2, 10);
        MultiSourceDatasetManifest first = MultiSourceDatasetManifest.Create(
            "multi-lane-fixture", [depth], [Entry("depth", "depth-1", 0, 1)]);
        MultiSourceDatasetManifest changed = MultiSourceDatasetManifest.Create(
            "multi-lane-fixture", [depth], [Entry("depth", "depth-1", 0, 2)]);

        Assert.NotEqual(first.Identity, changed.Identity);
    }

    [Fact]
    public async Task ReopenRejectsManifestWhoseReferencedRecordWasChanged()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "manifest.json");
        MultiSourceDatasetManifest manifest = MultiSourceDatasetManifest.Create(
            "multi-lane-fixture", [Source("depth", MultiSourceLaneKind.L2, 10)],
            [Entry("depth", "depth-1", 0, 1)]);
        MultiSourceDatasetManifestStore store = new(path);
        await store.PersistAsync(manifest, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path,
            (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))
                .Replace("depth-1", "depth-2", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<JsonException>(() =>
            store.ReadAsync(TestContext.Current.CancellationToken));
    }

    private static MultiSourceDatasetSource Source(string sourceId,
        MultiSourceLaneKind kind, long sequence) => new(sourceId, kind,
        $"source-manifest-{sequence}", $"quality-{sequence}", [],
        MultiSourceCoverage.Complete,
        MultiSourceQuality.Ready, [], 1, new UtcTimestamp(Utc(1)),
        new UtcTimestamp(Utc(1)), new UtcTimestamp(Utc(1)),
        new UtcTimestamp(Utc(1)), 0, 0);

    private static MultiSourceReplayOrderEntry Entry(string sourceId, string eventId,
        long sourceSequence, long globalSequence) => new(sourceId, eventId, sourceSequence,
        globalSequence, new UtcTimestamp(Utc(1)), new UtcTimestamp(Utc(1)),
        new UtcTimestamp(Utc(1)), new UtcTimestamp(Utc(1)));

    private static DateTimeOffset Utc(int second) =>
        new(2026, 10, 24, 12, 0, second, TimeSpan.Zero);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"multi-source-manifest-{Guid.NewGuid():N}");
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
