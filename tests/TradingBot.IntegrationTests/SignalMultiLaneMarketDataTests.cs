using System.Net;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Exchange.Binance;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class SignalMultiLaneMarketDataTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly SignalMultiLanePolicy Policy = new(
        SignalMultiLanePolicy.CurrentAlgorithmVersion, TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(10),
        new LaneCFeatureConfiguration("lane-c-formulas-v1", TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), 1, 50));

    [Fact]
    public void PublicObservationsBecomeUsableOnlyAfterCoverageAndRecoverFailClosed()
    {
        MutableClock clock = new(Start);
        using HttpClient http = new();
        BinancePublicRestClient rest = new(http);
        using SignalMultiLaneMarketData source = new("BTCUSDT", Policy, rest, clock);
        SignalMultiLaneAssessment empty = SignalMultiLaneResearchEvaluator.Evaluate(
            source.Capture(Direction.Long, Utc(Start)));
        Assert.Equal(DecisionEvidenceStatus.Unknown, empty.LaneB);
        Assert.Equal(DecisionEvidenceStatus.Unknown, empty.LaneC);

        source.AcceptMarket(Mark(Start, 100m, 1));
        source.AcceptOpenInterest(new BinanceOpenInterest("BTCUSDT", 100m, Millis(Start)));
        source.AcceptMarket(Trade(Start, 1));
        source.AcceptRecoveredBook(Book(Start, 100, 1));
        clock.Now = Start.AddSeconds(1);
        source.AcceptRecoveredBook(Book(clock.Now, 101, 2));
        clock.Now = Start.AddSeconds(61);
        source.AcceptMarket(Mark(clock.Now, 101m, 2));
        source.AcceptOpenInterest(new BinanceOpenInterest("BTCUSDT", 110m, Millis(clock.Now)));
        source.AcceptRecoveredBook(Book(clock.Now, 102, 3));
        source.AcceptMarket(Trade(clock.Now, 2));

        SignalMultiLaneInput input = source.Capture(Direction.Long, Utc(clock.Now));
        SignalMultiLaneAssessment ready = SignalMultiLaneResearchEvaluator.Evaluate(input);
        Assert.Equal(DecisionEvidenceStatus.Confirm, ready.LaneB);
        Assert.Equal(DecisionEvidenceStatus.Confirm, ready.LaneC);
        Assert.True(ready.IsAllowed);
        Assert.Equal(input, CoreJsonSerializer.Deserialize<SignalMultiLaneInput>(
            CoreJsonSerializer.Serialize(input)));

        source.OnDepthDiagnostic(Diagnostic(BinanceStreamDiagnosticKind.Disconnect, clock.Now));
        SignalMultiLaneAssessment disconnected = SignalMultiLaneResearchEvaluator.Evaluate(
            source.Capture(Direction.Long, Utc(clock.Now)));
        Assert.Equal(DecisionEvidenceStatus.Unknown, disconnected.LaneC);
        Assert.False(disconnected.IsAllowed);
        source.OnMarketDiagnostic(Diagnostic(BinanceStreamDiagnosticKind.Gap, clock.Now));
        Assert.Equal(DecisionEvidenceStatus.Unknown, SignalMultiLaneResearchEvaluator.Evaluate(
            source.Capture(Direction.Long, Utc(clock.Now))).LaneB);
    }

    [Fact]
    public void TradeBufferOverflowIsReportedAndRestartsCoverage()
    {
        MutableClock clock = new(Start);
        List<string> observations = [];
        using HttpClient http = new();
        using SignalMultiLaneMarketData source = new("BTCUSDT", Policy,
            new BinancePublicRestClient(http), clock, observations.Add);
        source.AcceptMarket(Trade(Start, 1));
        clock.Now = Start.AddSeconds(60);
        source.AcceptRecoveredBook(Book(clock.Now, 100, 1));
        clock.Now = Start.AddSeconds(61);
        source.AcceptRecoveredBook(Book(clock.Now, 101, 2));
        source.AcceptMarket(Trade(clock.Now, 2));
        Assert.Equal(DecisionEvidenceStatus.Confirm, SignalMultiLaneResearchEvaluator.Evaluate(
            source.Capture(Direction.Long, Utc(clock.Now))).LaneC);

        for (long id = 3; id <= 50; id++)
        {
            source.AcceptMarket(Trade(clock.Now, id));
        }

        Assert.Contains("trade-window-buffer-overflow", observations);
        Assert.Equal(DecisionEvidenceStatus.Unknown, SignalMultiLaneResearchEvaluator.Evaluate(
            source.Capture(Direction.Long, Utc(clock.Now))).LaneC);
    }

    [Fact]
    public void ExchangeEventsAheadOfLocalClockDoNotReplaceVisibleEarlierInputs()
    {
        MutableClock clock = new(Start);
        using HttpClient http = new();
        using SignalMultiLaneMarketData source = new("BTCUSDT", Policy,
            new BinancePublicRestClient(http), clock);
        source.AcceptMarket(Mark(Start.AddSeconds(-1), 100m, 1));
        source.AcceptMarket(Mark(Start.AddSeconds(1), 200m, 2));
        source.AcceptOpenInterest(new BinanceOpenInterest("BTCUSDT", 100m, Millis(Start)));
        source.AcceptRecoveredBook(Book(Start.AddSeconds(-1), 100, 1));
        source.AcceptRecoveredBook(Book(Start, 101, 2));
        source.AcceptRecoveredBook(Book(Start.AddSeconds(1), 102, 3));
        source.AcceptMarket(Trade(Start.AddSeconds(1), 10));

        SignalMultiLaneInput input = source.Capture(Direction.Long, Utc(Start));
        Assert.Equal(100m, input.CurrentMarkPrice?.MarkPrice.Value);
        Assert.NotNull(input.CurrentOpenInterest);
        Assert.NotNull(input.MicrostructureInput);
        Assert.All(input.MicrostructureInput.Records, record =>
            Assert.True(record.EventTime.Value <= Start));
        Assert.DoesNotContain(input.MicrostructureInput.Records, record =>
            record.Payload is Trade trade && trade.EventId.EndsWith(":10", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionalMarketContextHonorsCutsAndLiquidationDisconnectDoesNotResetTrades()
    {
        MutableClock clock = new(Start);
        using HttpClient http = new();
        using SignalMultiLaneMarketData source = new("BTCUSDT", Policy,
            new BinancePublicRestClient(http), clock);
        source.AcceptMarket(Trade(Start, 1));
        clock.Now = Start.AddSeconds(61);
        source.AcceptMarket(Trade(clock.Now, 2));
        BinanceAggregateTradeEvent future = new("BTCUSDT", Millis(clock.Now.AddSeconds(2)),
            3, 500m, 10m, 3, 3, Millis(clock.Now.AddSeconds(-1)), false, "btcusdt@aggTrade");
        source.AcceptMarket(new BinanceStreamEnvelope("trade-session", 3, future,
            Utc(clock.Now), Utc(clock.Now), Utc(clock.Now.AddSeconds(-1)), "{}"));
        BinanceLiquidationEvent liquidation = new("BTCUSDT", Millis(clock.Now.AddSeconds(-1)),
            null, TradeSide.Sell, 2m, 99m, 99m, 2m, 2m, "FILLED",
            Millis(clock.Now.AddSeconds(-1)), "btcusdt@forceOrder");
        source.AcceptLiquidation(new BinanceStreamEnvelope("liquidation-session", 1, liquidation,
            Utc(clock.Now), Utc(clock.Now), Utc(clock.Now.AddSeconds(-1)), "{}"));

        SignalObservedPoc poc = source.CapturePoc(Utc(clock.Now), 0.1m);
        Assert.Equal(100m, poc.Price);
        Assert.Equal(1, poc.TradeCount);
        Assert.Null(source.CaptureLiquidation(Utc(clock.Now.AddMilliseconds(-1))));
        SignalObservedLiquidation observed = Assert.IsType<SignalObservedLiquidation>(
            source.CaptureLiquidation(Utc(clock.Now)));
        Assert.Equal(TradeSide.Sell, observed.Side);
        Assert.Equal(2m, observed.Quantity);
        Assert.Null(source.CaptureLiquidation(Utc(clock.Now.AddMinutes(2))));

        source.OnLiquidationDiagnostic(Diagnostic(BinanceStreamDiagnosticKind.Disconnect, clock.Now));
        Assert.Null(source.CaptureLiquidation(Utc(clock.Now)));
        Assert.Equal(100m, source.CapturePoc(Utc(clock.Now), 0.1m).Price);
    }

    [Fact]
    public async Task TemporaryOpenInterestFailureInvalidatesEvidenceWithoutStoppingTheFeed()
    {
        MutableClock clock = new(Start);
        using HttpClient http = new(new UnavailableHandler());
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using SignalMultiLaneMarketData source = new("BTCUSDT", Policy,
            new BinancePublicRestClient(http), clock,
            kind => { if (kind == "open-interest-http-503") observed.TrySetResult(); });
        source.AcceptMarket(Mark(Start, 100m, 1));
        source.AcceptOpenInterest(new BinanceOpenInterest("BTCUSDT", 100m, Millis(Start)));
        clock.Now = Start.AddSeconds(61);
        source.AcceptMarket(Mark(clock.Now, 101m, 2));
        source.AcceptOpenInterest(new BinanceOpenInterest("BTCUSDT", 110m, Millis(clock.Now)));
        Assert.Equal(DecisionEvidenceStatus.Confirm, SignalMultiLaneResearchEvaluator.Evaluate(
            source.Capture(Direction.Long, Utc(clock.Now))).LaneB);

        using CancellationTokenSource cancellation = new();
        Task pump = source.PumpOpenInterestAsync(cancellation.Token);
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(pump.IsCompleted);
            Assert.Equal(DecisionEvidenceStatus.Unknown, SignalMultiLaneResearchEvaluator.Evaluate(
                source.Capture(Direction.Long, Utc(clock.Now))).LaneB);
        }
        finally
        {
            cancellation.Cancel();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump);
    }

    private static BinanceStreamEnvelope Mark(DateTimeOffset eventTime, decimal price, long sequence)
    {
        BinanceMarkPriceEvent payload = new("BTCUSDT", Millis(eventTime), price, 100m,
            0.0001m, Millis(Start.AddHours(8)), "btcusdt@markPrice@1s");
        DateTimeOffset receive = eventTime > Start ? eventTime : Start;
        return new BinanceStreamEnvelope("mark-session", sequence, payload, Utc(receive),
            Utc(receive), null, "{}");
    }

    private static BinanceStreamEnvelope Trade(DateTimeOffset eventTime, long id)
    {
        BinanceAggregateTradeEvent payload = new("BTCUSDT", Millis(eventTime), id, 100m, 1m,
            id, id, Millis(eventTime), false, "btcusdt@aggTrade");
        DateTimeOffset receive = eventTime > Start ? eventTime : Start;
        return new BinanceStreamEnvelope("trade-session", id, payload, Utc(receive),
            Utc(receive), Utc(eventTime), "{}");
    }

    private static BinanceOrderBookState Book(DateTimeOffset eventTime, long updateId, long sequence) =>
        new("BTCUSDT", BinanceOrderBookStateKind.Ready, updateId, updateId - 1,
            [new BinanceDepthLevel(99m, 4m)], [new BinanceDepthLevel(101m, 1m)], 1,
            sequence, Millis(eventTime), Millis(eventTime), Utc(eventTime), Utc(eventTime),
            Utc(eventTime), Utc(eventTime), Utc(eventTime), Millis(eventTime), Millis(eventTime));

    private static BinanceStreamDiagnostic Diagnostic(BinanceStreamDiagnosticKind kind,
        DateTimeOffset time) => new(kind, "session", "btcusdt@depth", null,
            "fixture disconnect", 1, Utc(time));

    private static long Millis(DateTimeOffset time) => time.ToUnixTimeMilliseconds();
    private static UtcTimestamp Utc(DateTimeOffset time) => new(time);

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"code\":-1,\"msg\":\"unavailable\"}")
            });
    }

    private sealed class MutableClock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
