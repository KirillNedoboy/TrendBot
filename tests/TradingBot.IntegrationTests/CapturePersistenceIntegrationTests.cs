using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class CapturePersistenceIntegrationTests
{
    [Fact]
    public async Task PublicCaptureSurvivesCloseAndReopenWithoutExecutionState()
    {
        using TemporaryCapture fixture = new();
        DateTimeOffset time = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        CaptureProvenance provenance = new("binance.usdm", "btcusdt@aggTrade", "session-1",
            "aggTrade:BTCUSDT:1", "{\"e\":\"aggTrade\"}", ["public"]);
        Trade trade = new("trade-1", new InstrumentId("BTCUSDT"), new UtcTimestamp(time),
            new Price(100m), new Quantity(1m), TradeSide.Buy);
        CaptureEnvelope expected = CaptureEnvelope.FromMarketEvent("public", 0, trade,
            new UtcTimestamp(time.AddMilliseconds(1)),
            new UtcTimestamp(time.AddMilliseconds(2)), 9, provenance);

        await using (ParquetCaptureStore store = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, "public"))
        {
            await store.AppendAsync(expected, TestContext.Current.CancellationToken);
            await store.FlushAsync(TestContext.Current.CancellationToken);
        }

        await using ParquetCaptureStore reopened = ParquetCaptureStore.Open(
            fixture.DatabasePath, fixture.SegmentDirectory, "public");
        Assert.Equal(expected, Assert.Single(await reopened.ReadAllAsync(
            TestContext.Current.CancellationToken)));
        Assert.Equal(1, reopened.Checkpoint.NextCaptureSequence);
        Assert.Equal(9, Assert.Single(reopened.Segments).LastStableSequence);
    }

    private sealed class TemporaryCapture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "tradingbot-integration-capture-" + Guid.NewGuid().ToString("N"));

        public TemporaryCapture()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "capture.db");
            SegmentDirectory = Path.Combine(_root, "segments");
        }

        public string DatabasePath { get; }

        public string SegmentDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                foreach (string file in Directory.GetFiles(_root, "*",
                    SearchOption.AllDirectories))
                {
                    File.Delete(file);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
