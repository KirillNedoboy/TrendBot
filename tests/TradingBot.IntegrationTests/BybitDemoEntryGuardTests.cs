using TradingBot.App;
using TradingBot.Core;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoEntryGuardTests
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
    public void ValidObservationsPassButStaleOrFutureDataCannotAuthorizeEntry()
    {
        BybitDemoEntryObservation valid = Observation();
        Assert.Equal(BybitDemoEntryBlock.None, Check(valid));
        Assert.Equal(BybitDemoEntryBlock.StaleSignal, Check(valid with { SignalAt = Now.AddSeconds(-61) }));
        Assert.Equal(BybitDemoEntryBlock.StaleSignal, Check(valid with { SignalAt = Now.AddTicks(1) }));
        Assert.Equal(BybitDemoEntryBlock.StaleQuote, Check(valid with { QuoteExchangeAt = Now.AddSeconds(-6) }));
        Assert.Equal(BybitDemoEntryBlock.StaleQuote, Check(valid with { QuoteReceivedAt = Now.AddTicks(1) }));
        Assert.Equal(BybitDemoEntryBlock.PrivateStateUnavailable, Check(valid with { PrivateConfirmedAt = Now.AddSeconds(-31) }));
        Assert.Equal(BybitDemoEntryBlock.PrivateStateUnavailable, Check(valid with { PrivateStreamReady = false }));
        Assert.Equal(BybitDemoEntryBlock.PrivateStateUnavailable, Check(valid with { Reconciled = false }));
        Assert.Equal(BybitDemoEntryBlock.PrivateStateUnavailable, Check(valid with { AccountObservedAt = Now.AddSeconds(-31) }));
        Assert.Equal(BybitDemoEntryBlock.PrivateStateUnavailable, Check(valid with { PositionObservedAt = Now.AddTicks(1) }));
        Assert.Equal(BybitDemoEntryBlock.PrivateStateUnavailable, Check(valid with { PositionReduceOnly = true }));
    }

    [Fact]
    public void UsesExecutableSideForPriceDifferenceAndEnforcesTheInclusiveLimit()
    {
        BybitDemoEntryObservation valid = Observation() with { Bid = 90m, Ask = 110m };
        Assert.Equal(BybitDemoEntryBlock.None, Check(valid, Direction.Long));
        Assert.Equal(BybitDemoEntryBlock.None, Check(valid, Direction.Short));
        Assert.Equal(BybitDemoEntryBlock.PriceDifferenceExceeded, Check(valid with { Ask = 110.01m }, Direction.Long));
        Assert.Equal(BybitDemoEntryBlock.PriceDifferenceExceeded, Check(valid with { Bid = 89.99m }, Direction.Short));
        Assert.Equal(BybitDemoEntryBlock.InvalidInput, Check(valid with { Bid = 111m }));
    }

    [Fact]
    public void RejectsUnverifiedOrUnsafeAccountAndInstrument()
    {
        BybitDemoEntryObservation valid = Observation();
        Assert.Equal(BybitDemoEntryBlock.AccountNotIsolated, Check(valid with { MarginMode = "REGULAR_MARGIN" }));
        Assert.Equal(BybitDemoEntryBlock.LeverageOutsideLimit, Check(valid with { Leverage = 10.01m }));
        Assert.Equal(BybitDemoEntryBlock.LeverageOutsideLimit, Check(valid with { Leverage = 0m }));
        Assert.Equal(BybitDemoEntryBlock.InstrumentUnavailable, Check(valid with { InstrumentTradingLinearUsdtPerpetual = false }));
        Assert.Equal(BybitDemoEntryBlock.InvalidInput, Check(valid with { SignalPrice = 0m }));
    }

    private static BybitDemoEntryObservation Observation() => new(
        Now, Now.AddSeconds(-60), 100m, Now.AddSeconds(-5), Now.AddSeconds(-4), 99m, 101m,
        Now.AddSeconds(-30), true, true, true, "ISOLATED_MARGIN", Now.AddSeconds(-30),
        10m, Now.AddSeconds(-30), false);

    private static BybitDemoEntryBlock Check(BybitDemoEntryObservation observation,
        Direction direction = Direction.Long) => BybitDemoEntryGuard.Check(Configuration, direction, observation);
}
