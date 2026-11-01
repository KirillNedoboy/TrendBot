using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class ExchangeTestExecutorTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public async Task ClaimedExchangeTestDispatchPersistsPrivateReceiptAndIsIdempotent()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("exchange-dispatch", "exchange-client");
        PortfolioId portfolioId = new("exchange-portfolio");
        ExchangeTestRunMetadata metadata = new("run-exchange-1", "exchange-profile-1",
            "binance-usdm-test", Time(0));
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.PortfolioPath);
        Assert.True(journal.Append(intent));
        using SqlitePortfolioOperationalStore portfolio = SqlitePortfolioOperationalStore.Open(
            database.PortfolioPath);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            portfolio.AcknowledgeReconciliation(portfolioId, 0).Status);
        ExecutionDispatchClaimResult claim = portfolio.TryClaimDispatch(portfolioId, intent, 0,
            Utc(1));

        using SqliteExchangeTestStore exchange = SqliteExchangeTestStore.Open(
            database.ExchangePath, metadata);
        RecordingTransport transport = new(ExecutionObservation.Accepted(
            "receipt-exchange", intent.IntentId, "order-exchange"),
            CreateReceipt(metadata.RunId, "event-exchange", intent.IntentId, 1));
        ExchangeTestExecutor executor = new(CreateConfiguration(), portfolio, exchange,
            transport, TimeProvider.System);
        executor.SetSafetyState(ExchangeTestSafetyState.Ready);

        ExchangeTestExecutionResult first = await executor.DispatchAsync(portfolioId, intent,
            claim, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionApplyDisposition.Applied, first.Transition.Disposition);
        Assert.Equal(ExecutionLifecycleState.Accepted, first.Transition.State.State);
        Assert.Equal(1, transport.SubmitCount);
        Assert.Single(exchange.ReadPrivateEvents());

        ExchangeTestExecutionResult duplicate = await executor.DispatchAsync(portfolioId, intent,
            claim, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionApplyDisposition.Blocked, duplicate.Transition.Disposition);
        Assert.Equal(1, transport.SubmitCount);
    }

    [Fact]
    public async Task StaleOrManualSafetyStateBlocksEntryAndMissingPartialProtectionFailsClosed()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("exchange-partial", "exchange-partial-client");
        PortfolioId portfolioId = new("exchange-partial-portfolio");
        ExchangeTestRunMetadata metadata = new("run-exchange-2", "exchange-profile-1",
            "binance-usdm-test", Time(0));
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.PortfolioPath);
        Assert.True(journal.Append(intent));
        using SqlitePortfolioOperationalStore portfolio = SqlitePortfolioOperationalStore.Open(
            database.PortfolioPath);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            portfolio.AcknowledgeReconciliation(portfolioId, 0).Status);
        ExecutionDispatchClaimResult blockedClaim = portfolio.TryClaimDispatch(portfolioId, intent,
            0, Utc(1));
        using SqliteExchangeTestStore exchange = SqliteExchangeTestStore.Open(
            database.ExchangePath, metadata);
        RecordingTransport transport = new(ExecutionObservation.PartialFill(
            "receipt-partial", intent.IntentId, "order-partial", 0.4m),
            CreateReceipt(metadata.RunId, "event-partial", intent.IntentId, 1),
            protectionConfirmed: false);
        ExchangeTestExecutor executor = new(CreateConfiguration(), portfolio, exchange,
            transport, TimeProvider.System);

        executor.SetSafetyState(ExchangeTestSafetyState.StalePrivateStream);
        ExchangeTestExecutionResult stale = await executor.DispatchAsync(portfolioId, intent,
            blockedClaim, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionApplyDisposition.Blocked, stale.Transition.Disposition);
        Assert.Equal(0, transport.SubmitCount);

        executor.SetSafetyState(ExchangeTestSafetyState.UnknownOrManualPosition);
        ExchangeTestExecutionResult manual = await executor.DispatchAsync(portfolioId, intent,
            blockedClaim, TestContext.Current.CancellationToken);
        Assert.Equal(ExecutionApplyDisposition.Blocked, manual.Transition.Disposition);
        Assert.Equal(0, transport.SubmitCount);

        executor.SetSafetyState(ExchangeTestSafetyState.Ready);
        ExchangeTestExecutionResult partial = await executor.DispatchAsync(portfolioId, intent,
            blockedClaim, TestContext.Current.CancellationToken);
        Assert.True(partial.ProtectionBlocked);
        Assert.Equal(ExecutionLifecycleState.Unknown, partial.Transition.State.State);
        Assert.True(partial.Transition.State.RequiresReconciliation);
    }

    [Fact]
    public void AlgoUpdateReceiptForcesReconciliationSafetyState()
    {
        using TemporaryDatabase database = new();
        ExchangeTestRunMetadata metadata = new("run-exchange-3", "exchange-profile-1",
            "binance-usdm-test", Time(0));
        using SqlitePortfolioOperationalStore portfolio = SqlitePortfolioOperationalStore.Open(
            database.PortfolioPath);
        using SqliteExchangeTestStore exchange = SqliteExchangeTestStore.Open(
            database.ExchangePath, metadata);
        ExchangeTestExecutor executor = new(CreateConfiguration(), portfolio, exchange,
            new RecordingTransport(ExecutionObservation.Accepted("unused", "intent-unused",
                "order-unused"), CreateReceipt(metadata.RunId, "unused", "intent-unused", 1)),
            TimeProvider.System);
        executor.SetSafetyState(ExchangeTestSafetyState.Ready);
        ExchangeTestEventReceipt algo = new(metadata.RunId, "algo-event", "intent-algo",
            "ALGO_UPDATE", 1, Time(1), Time(2), Time(3), null, null, "TRIGGERING", true,
            new string('d', 64));

        Assert.Equal(ExchangeTestReceiptWriteDisposition.Recorded,
            executor.RecordPrivateEvent(algo));
        Assert.True(executor.SafetyState.ReconciliationRequired);
        Assert.False(executor.SafetyState.EntryAllowed);
    }

    [Fact]
    public async Task TransportTimeoutIsDurableAndDoesNotAuthorizeBlindRetry()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("exchange-timeout", "exchange-timeout-client");
        PortfolioId portfolioId = new("exchange-timeout-portfolio");
        ExchangeTestRunMetadata metadata = new("run-exchange-4", "exchange-profile-1",
            "binance-usdm-test", Time(0));
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.PortfolioPath);
        Assert.True(journal.Append(intent));
        using SqlitePortfolioOperationalStore portfolio = SqlitePortfolioOperationalStore.Open(
            database.PortfolioPath);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            portfolio.AcknowledgeReconciliation(portfolioId, 0).Status);
        ExecutionDispatchClaimResult claim = portfolio.TryClaimDispatch(portfolioId, intent, 0,
            Utc(1));
        using SqliteExchangeTestStore exchange = SqliteExchangeTestStore.Open(
            database.ExchangePath, metadata);
        RecordingTransport transport = new(ExecutionObservation.Accepted("unused",
            intent.IntentId, "unused-order"), CreateReceipt(metadata.RunId, "timeout-event",
                intent.IntentId, 1), failure: new TimeoutException());
        ExchangeTestExecutor executor = new(CreateConfiguration(), portfolio, exchange,
            transport, TimeProvider.System);
        executor.SetSafetyState(ExchangeTestSafetyState.Ready);

        ExchangeTestExecutionResult result = await executor.DispatchAsync(portfolioId, intent,
            claim, TestContext.Current.CancellationToken);

        Assert.Equal(ExecutionLifecycleState.TimedOut, result.Transition.State.State);
        Assert.True(result.Transition.State.RequiresReconciliation);
        Assert.Single(portfolio.ReadExecutionReceipts(intent.IntentId));
        Assert.Equal(1, transport.SubmitCount);
    }

    private static ExchangeTestConfiguration CreateConfiguration() => new(
        "exchange-profile-1",
        new ExchangeCredentialReferences("environment", "test-api-key-reference",
            "test-api-secret-reference", null, "binance-usdm-test"),
        new ExchangeTestOperationalPolicies("documented-rate-limits", "stream-freshness",
            "reconcile-before-retry", "reduce-only-remainder", "unknown-blocks-entry",
            false, false, false, false));

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) => new(
        intentId, "run-exchange", "setup-exchange", clientOrderId, Instrument, Direction.Long,
        OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static ExchangeTestEventReceipt CreateReceipt(string runId, string identity,
        string intentId, long sequence) => new(runId, identity, intentId, "ORDER_TRADE_UPDATE",
        sequence, Time(1), Time(2), Time(3), "order-exchange", null, "NEW", false,
        new string('c', 64));

    private static DateTimeOffset Time(int seconds) =>
        new(2026, 10, 24, 12, 0, seconds, TimeSpan.Zero);

    private static UtcTimestamp Utc(int minutes) => new(Time(minutes));

    private sealed class RecordingTransport : IExchangeTestTransport
    {
        private readonly ExchangeTestTransportResult _result;
        private readonly Exception? _failure;

        public RecordingTransport(ExecutionObservation observation, ExchangeTestEventReceipt receipt,
            bool protectionConfirmed = true, Exception? failure = null)
        {
            _result = new ExchangeTestTransportResult(observation, [receipt], protectionConfirmed);
            _failure = failure;
        }

        public int SubmitCount { get; private set; }

        public ValueTask<ExchangeTestTransportResult> SubmitAsync(OrderIntent intent,
            ExecutionDispatchAttempt attempt, CancellationToken cancellationToken)
        {
            SubmitCount++;
            if (_failure is not null)
            {
                throw _failure;
            }

            return ValueTask.FromResult(_result);
        }

        public ValueTask<ExecutionRecoveryEvidence> ReconcileAsync(OrderIntent intent,
            PortfolioId portfolioId, ExecutionStateSnapshot state,
            ReconciliationCheckpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"tradingbot-exchange-executor-{Guid.NewGuid():N}");
            PortfolioPath = root + "-portfolio.sqlite";
            ExchangePath = PortfolioPath;
        }

        public string PortfolioPath { get; }
        public string ExchangePath { get; }

        public void Dispose()
        {
            foreach (string path in new[] { PortfolioPath }.Distinct(StringComparer.Ordinal))
            {
                foreach (string candidate in new[] { path, path + "-wal", path + "-shm" })
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
}
