using System.Net;
using System.Text;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoEntryExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly BybitDemoConfiguration Configuration = BybitDemoConfiguration.Parse("""
        {"formatVersion":1,"mode":"bybit-demo","targetNotionalUsdt":250,"maximumPositions":20,
         "maximumPriceDifferencePercent":10,"maximumSlippagePercent":10,"maximumLeverage":10,
         "marginMode":"isolated","maximumSignalAgeSeconds":60,"maximumQuoteAgeSeconds":5,
         "privateStreamFreshnessSeconds":30,"receiveWindowMilliseconds":5000,
         "statePath":"demo-execution.sqlite",
         "signalJournalPaths":["demo-flow-input.sqlite","demo-donchian-30-input.sqlite","demo-donchian-1h-input.sqlite"]}
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DurableClaimPrecedesEntryAndUnknownOutcomeIsNeverRetried(bool accepted)
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        CaptureHandler handler = new(store, accepted);
        MutableClock clock = new() { Now = Now };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", clock, 5000);
        BybitDemoEntryExecutor executor = new(store, client, clock, Configuration);
        BybitDemoEntryInputs input = Valid();

        Assert.Equal(BybitDemoEntryRequestResult.Rejected,
            await executor.RequestAsync(input with { PrivateStreamGapFree = false }, TestContext.Current.CancellationToken));
        Assert.Empty(store.ReadActive());
        Assert.Equal(0, handler.Count);
        Assert.Equal(accepted ? BybitDemoEntryRequestResult.Acknowledged : BybitDemoEntryRequestResult.OutcomeUnknown,
            await executor.RequestAsync(input, TestContext.Current.CancellationToken));
        Assert.True(handler.ClaimWasDurable);
        Assert.Equal(1, handler.Count);
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(accepted ? BybitDemoReservationState.Acknowledged : BybitDemoReservationState.ReconciliationRequired,
            Assert.Single(reopened.ReadActive()).State);
        Assert.Equal(BybitDemoEntryRequestResult.AlreadyClaimed,
            await executor.RequestAsync(input, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task MismatchedStoreCapacityCannotDispatchAnEntry()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 1);
        CaptureHandler handler = new(store, true);
        MutableClock clock = new() { Now = Now };
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", clock, 5000);
        BybitDemoEntryExecutor executor = new(store, client, clock, Configuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.RequestAsync(Valid(), TestContext.Current.CancellationToken));
        Assert.Empty(store.ReadActive());
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task StaleDataAfterClaimBlocksWithoutSubmitting()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        CaptureHandler handler = new(store, true);
        SteppingClock clock = new();
        using BybitDemoRestClient client = new(handler, "test-key", "test-secret", clock, 5000);
        BybitDemoEntryExecutor executor = new(store, client, clock, Configuration);
        Assert.Equal(BybitDemoEntryRequestResult.ReconciliationRequired,
            await executor.RequestAsync(Valid(), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Count);
        Assert.Equal(BybitDemoReservationState.ReconciliationRequired, Assert.Single(store.ReadActive()).State);
    }

    private static BybitDemoEntryInputs Valid()
    {
        BybitDemoSignalCandidate signal = new(new string('1', 64), new string('a', 64), "BTCUSDT",
            SetupType.FlowAtrRecovery, "15m", Direction.Long, Now, 100m, 95m, 105m, 110m);
        BybitDemoPublicQuote quote = new("BTCUSDT", 101m, 102m, Now.AddSeconds(-1), Now);
        BybitDemoInstrument instrument = new("BTCUSDT", 0.1m, 0.1m, 1_000_000m,
            0.01m, 0.01m, 5m, 100m, 100m, Now);
        return new BybitDemoEntryInputs(signal, quote, instrument, new("ISOLATED_MARGIN", Now),
            new("BTCUSDT", 0m, null, 5m, null, null, false, Now),
            BybitDemoAccountComparisonResult.NoMismatchObserved, true, true, Now, Now);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SteppingClock : TimeProvider
    {
        private int _reads;
        public override DateTimeOffset GetUtcNow() => ++_reads <= 2 ? Now : Now.AddSeconds(6);
    }

    private sealed class CaptureHandler(SqliteBybitDemoReservationStore store, bool accepted) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public bool ClaimWasDurable { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            BybitDemoReservationSnapshot snapshot = Assert.Single(store.ReadActive());
            ClaimWasDurable = snapshot.State == BybitDemoReservationState.DispatchClaimed;
            string json = accepted
                ? $"{{\"retCode\":0,\"result\":{{\"orderId\":\"exchange-order\",\"orderLinkId\":\"{snapshot.Reservation.ClientOrderId}\"}}}}"
                : "{\"retCode\":10006,\"result\":{}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bybit-entry-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(_directory, "state.sqlite");
        public TemporaryDatabase() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
