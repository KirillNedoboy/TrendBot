using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ExchangeTestAtomicPersistenceTests
{
    [Fact]
    public void ExchangeReceiptLifecycleAndCheckpointCommitTogetherAndSurviveRestart()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("atomic-portfolio");
        OrderIntent intent = CreateIntent("atomic-intent", "atomic-client");
        ExchangeTestRunMetadata metadata = Metadata("atomic-run");
        ExchangeTestEventReceipt acceptedReceipt = Receipt(metadata.RunId, intent.IntentId,
            "atomic-accepted", 1);
        ExchangeTestEventReceipt fillReceipt = Receipt(metadata.RunId, intent.IntentId,
            "atomic-fill", 2);
        Fill fill = new("binance-trade:42", intent.IntentId, new InstrumentId("BTCUSDT"),
            new Quantity(0.5m), new Price(100m), new UtcTimestamp(Time(1)),
            new UtcTimestamp(Time(2)), new UtcTimestamp(Time(3)), 1);
        ReconciliationCheckpoint checkpoint = new(portfolioId, 1, [], [], [], []);

        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        using (SqlitePortfolioOperationalStore portfolio = SqlitePortfolioOperationalStore.Open(
                   database.Path))
        {
            Assert.Equal(ReconciliationStatus.Acknowledged,
                portfolio.AcknowledgeReconciliation(portfolioId, 0).Status);
            ExecutionDispatchClaimResult claim = portfolio.TryClaimDispatch(portfolioId, intent,
                0, new UtcTimestamp(Time(1)));
            using SqliteExchangeTestStore exchange = SqliteExchangeTestStore.Open(database.Path,
                metadata);

            ExecutionTransitionResult accepted = portfolio.ApplyExchangeTestObservation(exchange,
                portfolioId, ExecutionObservation.Accepted("atomic-accepted-observation",
                    intent.IntentId, "42"), 0, [acceptedReceipt]);
            ExecutionTransitionResult transition = portfolio.ApplyExchangeTestObservation(exchange,
                portfolioId, ExecutionObservation.PartialFill("atomic-fill-observation",
                    intent.IntentId, "42", 0.5m), 0, [fillReceipt], fill: fill,
                checkpoint: checkpoint);

            Assert.Equal(ExecutionApplyDisposition.Applied, accepted.Disposition);
            Assert.Equal(ExecutionApplyDisposition.Applied, transition.Disposition);
            Assert.Equal(ExecutionLifecycleState.PartiallyFilled, transition.State.State);
            Assert.Equal(2, exchange.ReadPrivateEvents().Count);
            Assert.Equal(2, portfolio.ReadExecutionReceipts(intent.IntentId).Count);
            Assert.Single(portfolio.ReadFills(intent.IntentId));
            Assert.Equal(1, portfolio.ReadCheckpoint(portfolioId).Revision);
            Assert.Equal(ExecutionLifecycleState.PartiallyFilled,
                portfolio.ReadExecutionState(intent.IntentId)!.State);
            Assert.Equal(ExecutionDispatchClaimStatus.Claimed, claim.Status);
        }

        using (SqlitePortfolioOperationalStore reopenedPortfolio =
                   SqlitePortfolioOperationalStore.Open(database.Path))
        using (SqliteExchangeTestStore reopenedExchange = SqliteExchangeTestStore.Open(
                   database.Path, metadata))
        {
            Assert.Equal(2, reopenedExchange.ReadPrivateEvents().Count);
            Assert.Equal(2, reopenedPortfolio.ReadExecutionReceipts(intent.IntentId).Count);
            Assert.Single(reopenedPortfolio.ReadFills(intent.IntentId));
            Assert.Equal(1, reopenedPortfolio.ReadCheckpoint(portfolioId).Revision);
        }
    }

    [Fact]
    public void AtomicExchangeObservationRollsBackReceiptAndLifecycleOnProjectionFailure()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("rollback-portfolio");
        OrderIntent intent = CreateIntent("rollback-intent", "rollback-client");
        ExchangeTestRunMetadata metadata = Metadata("rollback-run");
        ExchangeTestEventReceipt receipt = Receipt(metadata.RunId, intent.IntentId,
            "rollback-event", 1);

        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        using SqlitePortfolioOperationalStore portfolio = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            portfolio.AcknowledgeReconciliation(portfolioId, 0).Status);
        _ = portfolio.TryClaimDispatch(portfolioId, intent, 0, new UtcTimestamp(Time(1)));
        using SqliteExchangeTestStore exchange = SqliteExchangeTestStore.Open(database.Path,
            metadata);

        ReconciliationCheckpoint invalidCheckpoint = new(portfolioId, 4, [], [], [], []);
        Assert.Throws<InvalidOperationException>(() => portfolio.ApplyExchangeTestObservation(
            exchange, portfolioId, ExecutionObservation.Accepted("rollback-observation",
                intent.IntentId, "43"), 0, [receipt], checkpoint: invalidCheckpoint));

        Assert.Empty(exchange.ReadPrivateEvents());
        Assert.Empty(portfolio.ReadExecutionReceipts(intent.IntentId));
        Assert.Equal(ExecutionLifecycleState.DispatchClaimed,
            portfolio.ReadExecutionState(intent.IntentId)!.State);
        Assert.Equal(0, portfolio.ReadCheckpoint(portfolioId).Revision);
    }

    private static ExchangeTestRunMetadata Metadata(string runId) => new(runId,
        "atomic-profile", "binance-usdm-test", Time(0));

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) => new(
        intentId, "atomic-run", "atomic-setup", clientOrderId, new InstrumentId("BTCUSDT"),
        Direction.Long, OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static ExchangeTestEventReceipt Receipt(string runId, string intentId,
        string identity, long sequence) => new(runId, identity, intentId, "ORDER_TRADE_UPDATE",
        sequence, Time(1), Time(2), Time(3), "42", null, "NEW", false, new string('a', 64));

    private static DateTimeOffset Time(int seconds) =>
        new(2026, 10, 24, 12, 0, seconds, TimeSpan.Zero);

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-exchange-atomic-{Guid.NewGuid():N}.sqlite");
        }

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, Path + "-wal", Path + "-shm" })
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
