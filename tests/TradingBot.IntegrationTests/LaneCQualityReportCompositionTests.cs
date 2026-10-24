using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LaneCQualityReportCompositionTests
{
    [Fact]
    public async Task AppCompositionBuildsFeaturesAndCausalQualityReport()
    {
        DateTimeOffset asOf = new(2026, 10, 24, 12, 0, 5, TimeSpan.Zero);
        using TemporaryDirectory fixture = new();
        await using ParquetCaptureStore store = ParquetCaptureStore.Open(
            Path.Combine(fixture.Path, "capture.db"),
            Path.Combine(fixture.Path, "segments"), "lane-c-composition");
        CaptureDatasetManifest manifest = await CaptureDatasetManifestBuilder.BuildAsync(
            store, new CaptureDatasetManifestBuildOptions("composition-manifest", 8, 8,
                new CaptureManifestRetentionPolicy(null, null), [], []),
            TestContext.Current.CancellationToken);
        LaneCFeatureInput input = new(new InstrumentId("BTCUSDT"), [],
            new CausalEventCursor(new UtcTimestamp(asOf), 10), "composition-fixture");

        LaneCQualityReport report = await LaneCQualityReportService.BuildAsync(store,
            manifest, input, new LaneCFeatureConfiguration("lane-c-formulas-v1",
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3), 1, 10), new LaneCQualityPolicy(true, true,
                TimeSpan.FromSeconds(2)), new CaptureQualitySnapshotBuildOptions(asOf, 8, 8,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), null),
            TestContext.Current.CancellationToken);

        Assert.Equal(LaneCQualityState.InsufficientData, report.Quality.State);
        Assert.Equal(input.Cursor, report.Quality.Cursor);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "tradingbot-lane-c-quality-composition-" + Guid.NewGuid().ToString("N"));
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
