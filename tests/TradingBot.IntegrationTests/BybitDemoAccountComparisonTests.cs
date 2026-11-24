using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoAccountComparisonTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly BybitDemoReservation Reservation = new(new string('1', 64), new string('a', 64),
        "BTCUSDT", "demo-order", SetupType.FlowAtrRecovery, "15m", Direction.Long, 1m, 100m, 95m);
    private static readonly BybitDemoReservationSnapshot Local = new(Reservation,
        BybitDemoReservationState.Protected, Now, "exchange-order", 1m, 100m, 95m);
    private static readonly BybitDemoAccountPosition Position = new("BTCUSDT", 0, Direction.Long,
        1m, 5m, 100m, 95m, "Normal", false, 10);
    private static readonly BybitDemoOpenOrder Stop = new("stop-order", "", "demo-order", "BTCUSDT",
        "Untriggered", "StopLoss", 0, Direction.Short, 1m, 95m, "LastPrice", true, true);

    [Fact]
    public void MatchesProtectedPositionAndItsExchangeStopOnly()
    {
        Assert.Equal(BybitDemoAccountComparisonResult.NoMismatchObserved, Compare([Local], [Position], [Stop]));
        Assert.Equal(BybitDemoAccountComparisonResult.NoMismatchObserved, Compare([], [], []));
        Assert.Equal(BybitDemoAccountComparisonResult.MissingProtection, Compare([Local], [Position], []));
        Assert.Equal(BybitDemoAccountComparisonResult.PositionMismatch, Compare([Local], [], [Stop]));
    }

    [Fact]
    public void UnknownPositionsOrdersAndPendingEntriesAreNeverTreatedAsACompleteMatch()
    {
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownPosition, Compare([], [Position], []));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([], [], [Stop]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([Local], [Position], [Stop with { ParentClientOrderId = null }]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnresolvedLocalOrder,
            Compare([Local with { State = BybitDemoReservationState.ReconciliationRequired }], [Position], [Stop]));
    }

    [Fact]
    public void PositionProtectionMarginAndIdentityDifferencesAreRejected()
    {
        Assert.Equal(BybitDemoAccountComparisonResult.PositionMismatch, Compare([Local], [Position with { StopLoss = null }], [Stop]));
        Assert.Equal(BybitDemoAccountComparisonResult.PositionMismatch, Compare([Local], [Position with { Quantity = 0.5m }], [Stop]));
        Assert.Equal(BybitDemoAccountComparisonResult.PositionMismatch, Compare([Local], [Position with { PositionIndex = 1 }], [Stop]));
        Assert.Equal(BybitDemoAccountComparisonResult.PositionMismatch, Compare([Local], [Position with { Leverage = null }], [Stop]));
        Assert.Equal(BybitDemoAccountComparisonResult.PositionMismatch, Compare([Local], [Position with { Leverage = 11m }], [Stop]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([Local], [Position], [Stop with { CloseOnTrigger = false }]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([Local], [Position], [Stop with { Side = Direction.Long }]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([Local], [Position], [Stop with { Quantity = 0.5m }]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([Local], [Position], [Stop with { TriggerPrice = 90m }]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([Local], [Position], [Stop with { TriggerPrice = null }]));
        Assert.Equal(BybitDemoAccountComparisonResult.UnknownOrder, Compare([Local], [Position], [Stop with { TriggerBy = "MarkPrice" }]));
    }

    private static BybitDemoAccountComparisonResult Compare(BybitDemoReservationSnapshot[] local,
        BybitDemoAccountPosition[] positions, BybitDemoOpenOrder[] orders) => BybitDemoAccountComparison.Compare(
            local, new BybitDemoPositionSnapshot(Now, Now, positions),
            new BybitDemoOpenOrderSnapshot(Now, Now, orders), 10m);
}
