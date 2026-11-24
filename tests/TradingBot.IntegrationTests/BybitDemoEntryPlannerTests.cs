using TradingBot.App;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoEntryPlannerTests
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

    [Fact]
    public void PlansFromBybitExecutableQuoteAndRetainsOriginalSignal()
    {
        BybitDemoEntryInputs inputs = Valid();
        Assert.Equal(BybitDemoPlanningStatus.Ready, BybitDemoEntryPlanner.TryPrepare(Configuration, inputs,
            out BybitDemoPreparedEntry plan, out BybitDemoEntryBlock reason));
        Assert.Equal(BybitDemoEntryBlock.None, reason);
        Assert.Equal(102m, plan.Reservation.ReferenceQuote);
        Assert.Equal(2.45m, plan.Reservation.Quantity);
        Assert.Equal(96.9m, plan.Reservation.InitialStop);
        Assert.Equal(1.22m, plan.Quantities.FirstTargetQuantity);
        Assert.Equal(1.23m, plan.Quantities.RemainderQuantity);
        Assert.Equal(inputs.Signal, plan.Source);
    }

    [Fact]
    public void RejectsStaleOrMismatchedMarketDataAndExistingPosition()
    {
        BybitDemoEntryInputs valid = Valid();
        Assert.Equal(BybitDemoPlanningStatus.InstrumentUnavailable, Plan(valid with
        {
            Instrument = valid.Instrument with { ObservedAt = Now.AddSeconds(-6) }
        }));
        Assert.Equal(BybitDemoPlanningStatus.InstrumentUnavailable, Plan(valid with
        {
            Quote = valid.Quote with { Symbol = "ETHUSDT" }
        }));
        Assert.Equal(BybitDemoPlanningStatus.PositionNotFlat, Plan(valid with
        {
            Position = valid.Position with { Quantity = 0.1m, Direction = Direction.Long }
        }));
    }

    [Fact]
    public void RejectsUnreconciledStreamAndExcessiveExchangeDifference()
    {
        BybitDemoEntryInputs valid = Valid();
        Assert.Equal(BybitDemoPlanningStatus.SafetyRejected,
            BybitDemoEntryPlanner.TryPrepare(Configuration, valid with { PrivateStreamGapFree = false }, out _, out BybitDemoEntryBlock gap));
        Assert.Equal(BybitDemoEntryBlock.PrivateStateUnavailable, gap);
        Assert.Equal(BybitDemoPlanningStatus.SafetyRejected,
            BybitDemoEntryPlanner.TryPrepare(Configuration, valid with { Quote = valid.Quote with { Ask = 111m } }, out _, out BybitDemoEntryBlock price));
        Assert.Equal(BybitDemoEntryBlock.PriceDifferenceExceeded, price);
    }

    private static BybitDemoPlanningStatus Plan(BybitDemoEntryInputs input) =>
        BybitDemoEntryPlanner.TryPrepare(Configuration, input, out _, out _);

    private static BybitDemoEntryInputs Valid()
    {
        BybitDemoSignalCandidate signal = new(new string('1', 64), new string('a', 64), "BTCUSDT",
            SetupType.FlowAtrRecovery, "15m", Direction.Long, Now, 100m, 95m, 105m, 110m);
        BybitDemoPublicQuote quote = new("BTCUSDT", 101m, 102m, Now.AddSeconds(-1), Now);
        BybitDemoInstrument instrument = new("BTCUSDT", 0.1m, 0.1m, 1_000_000m,
            0.01m, 0.01m, 5m, 100m, 100m, Now);
        BybitDemoAccountInfo account = new("ISOLATED_MARGIN", Now);
        BybitDemoPositionInfo position = new("BTCUSDT", 0m, null, 5m, null, null, false, Now);
        return new BybitDemoEntryInputs(signal, quote, instrument, account, position,
            BybitDemoAccountComparisonResult.NoMismatchObserved, true, true, Now, Now);
    }
}
