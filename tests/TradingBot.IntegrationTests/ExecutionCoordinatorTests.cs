using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ExecutionCoordinatorTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void AlreadyClaimedDispatchDoesNotCallAdapterAgain()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-dispatch", "client-dispatch");
        PortfolioId portfolioId = new("portfolio-dispatch");
        ExecutionDispatchClaimResult claim;

        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        using (SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
                   database.Path))
        {
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 0).Status);
            Assert.Equal(ExecutionDispatchClaimStatus.Claimed,
                store.TryClaimDispatch(portfolioId, intent, 0, Time(1)).Status);
            claim = store.TryClaimDispatch(portfolioId, intent, 0, Time(2));

            CountingDispatcher dispatcher = new(ExecutionObservation.Accepted(
                "receipt-accepted", intent.IntentId, "exchange-1"));
            ExecutionCoordinator coordinator = new(store, dispatcher);
            ExecutionTransitionResult duplicate = coordinator.Dispatch(portfolioId, intent, claim);

            Assert.Equal(ExecutionApplyDisposition.Blocked, duplicate.Disposition);
            Assert.Equal(0, dispatcher.CallCount);
            Assert.Equal(ExecutionLifecycleState.DispatchClaimed, duplicate.State.State);
        }
    }

    [Fact]
    public void AdapterTimeoutIsDurableAndRequiresReconciliation()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("intent-timeout", "client-timeout");
        PortfolioId portfolioId = new("portfolio-timeout");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        ExecutionDispatchClaimResult claim = store.TryClaimDispatch(portfolioId, intent, 0,
            Time(1));
        ExecutionCoordinator coordinator = new(store, new TimeoutDispatcher());

        ExecutionTransitionResult result = coordinator.Dispatch(portfolioId, intent, claim);

        Assert.Equal(ExecutionApplyDisposition.Applied, result.Disposition);
        Assert.Equal(ExecutionLifecycleState.TimedOut, result.State.State);
        Assert.True(result.State.RequiresReconciliation);
        Assert.Single(store.ReadExecutionReceipts(intent.IntentId));
    }

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) =>
        new(intentId, "run-1", "setup-1", clientOrderId, Instrument, Direction.Long,
            OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private sealed class CountingDispatcher : IPrivateOrderDispatcher
    {
        private readonly ExecutionObservation _observation;

        public CountingDispatcher(ExecutionObservation observation) => _observation = observation;

        public int CallCount { get; private set; }

        public ExecutionObservation Submit(OrderIntent intent, ExecutionDispatchAttempt attempt)
        {
            CallCount++;
            return _observation;
        }
    }

    private sealed class TimeoutDispatcher : IPrivateOrderDispatcher
    {
        public ExecutionObservation Submit(OrderIntent intent, ExecutionDispatchAttempt attempt) =>
            throw new TimeoutException("fixture timeout");
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-coordinator-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
