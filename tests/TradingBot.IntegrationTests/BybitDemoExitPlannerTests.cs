using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoExitPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
    private static readonly BybitDemoInstrument Instrument = new("BTCUSDT", 0.1m, 0.1m, 1_000_000m,
        0.01m, 0.01m, 5m, 100m, 100m, Now);

    [Fact]
    public void FlowUsesActualPartialFillToRebasePricesAndSplitQuantity()
    {
        BybitDemoExitPlan plan = BybitDemoExitPlanner.Create(Signal(SetupType.FlowAtrRecovery, "15m", 110m),
            Instrument, 1.23m, 102m);
        Assert.Equal(96.9m, plan.StopLoss);
        Assert.Equal(107.1m, plan.FirstTarget);
        Assert.Equal(112.2m, plan.SecondTarget);
        Assert.Equal(0.61m, plan.FirstQuantity);
        Assert.Equal(0.62m, plan.SecondQuantity);
        Assert.Equal(102m, plan.StopAfterFirstTarget);
    }

    [Fact]
    public void DonchianHasOneTargetAndKeepsSeparateExitSemantics()
    {
        BybitDemoExitPlan plan = BybitDemoExitPlanner.Create(Signal(SetupType.DonchianFlowBreakout, "30m", null),
            Instrument, 1m, 102m);
        Assert.Equal(1m, plan.FirstQuantity);
        Assert.Equal(0m, plan.SecondQuantity);
        Assert.Null(plan.SecondTarget);
        Assert.Null(plan.StopAfterFirstTarget);
    }

    [Fact]
    public void TooSmallOrUnalignedPartialFillCannotBePretendedToHaveTwoTargets()
    {
        BybitDemoSignalCandidate signal = Signal(SetupType.FlowAtrRecovery, "15m", 110m);
        Assert.Throws<ArgumentException>(() => BybitDemoExitPlanner.Create(signal, Instrument, 0.01m, 102m));
        Assert.Throws<ArgumentException>(() => BybitDemoExitPlanner.Create(signal, Instrument, 0.015m, 102m));
        Assert.Throws<ArgumentException>(() => BybitDemoExitPlanner.Create(signal, Instrument with { Symbol = "ETHUSDT" }, 1m, 102m));
    }

    [Fact]
    public void ProtectedFillMustMatchRebasedStopBeforeItsExitPlanIsPersisted()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bybit-exit-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            string path = System.IO.Path.Combine(directory, "state.sqlite");
            SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(path, 20);
            BybitDemoSignalCandidate signal = Signal(SetupType.FlowAtrRecovery, "15m", 110m);
            BybitDemoReservation reservation = new(signal.SignalId, signal.RecordHash, signal.Symbol, "demo-1",
                signal.Family, signal.Timeframe, signal.Direction, 1.23m, 100m, 95m);
            Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(reservation));
            Assert.True(store.TryClaimDispatch(signal.SignalId, Now));
            store.RecordAcknowledgement(signal.SignalId, reservation.ClientOrderId, "exchange-1");
            Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(new BybitDemoEntryFill(
                signal.SignalId, "fill-1", reservation.ClientOrderId, "exchange-1", signal.Symbol,
                signal.Direction, 1.23m, 102m, 0m, 1, Now, Now)));
            store.ConfirmProtected(signal.SignalId, "exchange-1", 1.23m, 102m, 95m, Now, true);
            Assert.Throws<InvalidOperationException>(() => BybitDemoExitPlanner.PersistProtected(store, signal, Instrument));
            Assert.Null(store.ReadExitPlan(signal.SignalId));
            Assert.Equal(BybitDemoStopClaimResult.Claimed, store.TryClaimStopChange(
                new BybitDemoStopChange("stop-1", signal.SignalId, 1.23m, 95m, 96.9m, Now)));
            store.ConfirmStopChange("stop-1", new BybitDemoStopProof(signal.Symbol, Direction.Long,
                1.23m, 102m, 96.9m, "stop-order", reservation.ClientOrderId, Direction.Short, 1.23m,
                96.9m, 0, "Untriggered", "LastPrice", true, true, Now, Now));
            Assert.Throws<InvalidOperationException>(() => BybitDemoExitPlanner.PersistProtected(
                store, signal with { RecordHash = new string('b', 64) }, Instrument));
            BybitDemoExitPlan plan = BybitDemoExitPlanner.PersistProtected(store, signal, Instrument);
            SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(path, 20);
            BybitDemoStoredExitPlan saved = Assert.IsType<BybitDemoStoredExitPlan>(reopened.ReadExitPlan(signal.SignalId));
            Assert.Equal(plan.FirstQuantity, saved.FirstQuantity);
            Assert.Equal(plan.SecondQuantity, saved.SecondQuantity);
            Assert.Equal(plan.StopAfterFirstTarget, saved.StopAfterFirstTarget);
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    private static BybitDemoSignalCandidate Signal(SetupType family, string timeframe, decimal? secondTarget) =>
        new(new string('1', 64), new string('a', 64), "BTCUSDT", family, timeframe, Direction.Long,
            Now, 100m, 95m, 105m, secondTarget);
}
