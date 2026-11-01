using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LocalPaperDeterminismRecoveryTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly PortfolioId Portfolio = new("portfolio-a");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RepeatedAndRestartedTracesHaveIdenticalEvidence()
    {
        using TemporaryDatabase uninterrupted = new("uninterrupted");
        using TemporaryDatabase restarted = new("restarted");
        using TemporaryDatabase repeated = new("repeated");

        string uninterruptedJson = RunTrace(uninterrupted.Path, restartBeforeClose: false,
            repeatCompleted: false);
        string restartedJson = RunTrace(restarted.Path, restartBeforeClose: true,
            repeatCompleted: false);
        string repeatedJson = RunTrace(repeated.Path, restartBeforeClose: false,
            repeatCompleted: true);

        Assert.Equal(uninterruptedJson, restartedJson);
        Assert.Equal(uninterruptedJson, repeatedJson);
    }

    [Fact]
    public void PartialTakeProfitEvidenceRetainsOpenPositionThenCloses()
    {
        using TemporaryDatabase database = new("partial");
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan sourcePlan) = Fixture();
        SimulatedRiskPlan plan = new(sourcePlan.ResearchPlanId + "-partial",
            sourcePlan.InstrumentId, sourcePlan.Direction, sourcePlan.DecisionTime,
            sourcePlan.RiskInput,
            [new SimulatedTakeProfit("tp1", new Price(105m), 0.5m),
             new SimulatedTakeProfit("tp2", new Price(110m), 0.5m)],
            sourcePlan.HoldingPeriod, sourcePlan.MoveStopToBreakeven,
            sourcePlan.BreakevenStopPrice, sourcePlan.FillAssumptions, sourcePlan.Decision);

        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        LocalPaperExecutor executor = new(configuration, store,
            new LinearPerpetualRiskMathCalculator());
        RecordSignalAndEntry(executor);
        Assert.Equal(LocalPaperExecutionStatus.Applied, executor.Process(
            new LocalPaperObservation(BarEnvelope("open", 3, 103m, 104m, 102m, 103m,
                10_003), input, plan)).Status);
        Assert.Equal(LocalPaperExecutionStatus.Applied, executor.Process(
            new LocalPaperObservation(BarEnvelope("tp1", 4, 104m, 106m, 103m, 105m,
                10_004))).Status);

        LocalPaperEvidence openEvidence = LocalPaperEvidence.Capture(store, "stream-a",
            "source-v1");
        Assert.Single(openEvidence.Ledger!.Positions);
        Assert.Equal(0.5m, openEvidence.Ledger.Positions[0].Quantity);
        Assert.Contains("open-virtual-position", openEvidence.Unknowns);
        Assert.Equal(2, openEvidence.Fills.Count);

        Assert.Equal(LocalPaperExecutionStatus.Applied, executor.Process(
            new LocalPaperObservation(BarEnvelope("tp2", 5, 109m, 111m, 108m, 110m,
                10_005))).Status);
        LocalPaperEvidence closedEvidence = LocalPaperEvidence.Capture(store, "stream-a",
            "source-v1");
        Assert.Empty(closedEvidence.Ledger!.Positions);
        Assert.DoesNotContain("open-virtual-position", closedEvidence.Unknowns);
    }

    [Fact]
    public void InterruptionBeforeIntentLeavesNoDurableIntent()
    {
        VerifyInterruption(LocalPaperFaultPoint.BeforeIntent, expectedPending: false,
            expectedComplete: false);
    }

    [Fact]
    public void InterruptionAfterIntentRecoversExactlyOnce()
    {
        VerifyInterruption(LocalPaperFaultPoint.AfterIntent, expectedPending: true,
            expectedComplete: false);
    }

    [Fact]
    public void InterruptionBeforeCommitRecoversExactlyOnce()
    {
        VerifyInterruption(LocalPaperFaultPoint.BeforeCommit, expectedPending: true,
            expectedComplete: false);
    }

    [Fact]
    public void InterruptionAfterCommitReturnsIdempotentResult()
    {
        VerifyInterruption(LocalPaperFaultPoint.AfterCommitBeforeResponse,
            expectedPending: false, expectedComplete: true);
    }

    [Fact]
    public void CorruptCheckpointProducesBlockedEvidenceWithoutOptimisticLedger()
    {
        using TemporaryDatabase database = new("corrupt");
        using (LocalPaperStore store = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor executor = new(Fixture().Configuration, store,
                new LinearPerpetualRiskMathCalculator());
            Assert.Equal(LocalPaperExecutionStatus.Recorded,
                executor.Process(new LocalPaperObservation(BarEnvelope("row", 1, 100m,
                    101m, 99m, 100m, 1))).Status);
        }

        using (SqliteConnection connection = new($"Data Source={database.Path}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                UPDATE paper_checkpoints_v2 SET state_json = '{"corrupt":true}'
                WHERE stream_id = 'stream-a';
                """;
            command.ExecuteNonQuery();
        }

        using LocalPaperStore corruptStore = LocalPaperStore.Open(database.Path);
        LocalPaperEvidence evidence = LocalPaperEvidence.Capture(corruptStore, "stream-a",
            "source-v1");
        Assert.True(evidence.IsBlocked);
        Assert.Null(evidence.Ledger);
        Assert.Contains("local-paper-store-corrupt", evidence.Blockers);
        Assert.False(evidence.HasExecutionAuthority);
    }

    [Fact]
    public void ConfigurationAndModelChangesRemainFailClosedDuringRecovery()
    {
        using TemporaryDatabase database = new("identity");
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture();
        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        LocalPaperExecutor executor = new(configuration, store,
            new LinearPerpetualRiskMathCalculator());
        RecordSignalAndEntry(executor);
        Assert.Equal(LocalPaperExecutionStatus.Applied, executor.Process(
            new LocalPaperObservation(BarEnvelope("open", 3, 103m, 104m, 102m, 103m,
                10_003), input, plan)).Status);

        LocalPaperConfiguration changedConfiguration = new("changed-config",
            configuration.InitialEquity, configuration.RiskFraction,
            configuration.RiskConfiguration, configuration.FillAssumptions);
        LocalPaperExecutionResult changed = new LocalPaperExecutor(changedConfiguration,
            store, new LinearPerpetualRiskMathCalculator(), new ChangedModelSimulator())
            .Process(new LocalPaperObservation(BarEnvelope("target", 4, 109m, 111m,
                108m, 110m, 10_004)));

        Assert.True(changed.IsBlocked);
        Assert.Contains(changed.BlockReasons, static reason =>
            reason.Contains("model", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("configuration", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("fill", StringComparison.OrdinalIgnoreCase));
        LocalPaperEvidence evidence = LocalPaperEvidence.Capture(store, "stream-a",
            "source-v1");
        Assert.Contains("open-virtual-position", evidence.Unknowns);
        Assert.False(evidence.HasExecutionAuthority);
    }

    private static void VerifyInterruption(LocalPaperFaultPoint point, bool expectedPending,
        bool expectedComplete)
    {
        using TemporaryDatabase database = new(point.ToString());
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture();
        using (LocalPaperStore store = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor clean = new(configuration, store,
                new LinearPerpetualRiskMathCalculator());
            RecordSignalAndEntry(clean);
            bool injected = false;
            LocalPaperExecutor interrupted = new(configuration, store,
                new LinearPerpetualRiskMathCalculator(),
                fault =>
                {
                    if (!injected && fault == point)
                    {
                        injected = true;
                        throw new LocalPaperInterruptionException(point);
                    }
                });

            Assert.Throws<LocalPaperInterruptionException>(() => interrupted.Process(
                new LocalPaperObservation(BarEnvelope("open", 3, 103m, 104m, 102m, 103m,
                    10_003), input, plan)));
            LocalPaperObservationRecord? row = store.ReadObservations("stream-a")
                .SingleOrDefault(item => item.Sequence == 10_003);
            Assert.Equal(expectedPending, row?.Status == "pending");
            Assert.Equal(expectedComplete, row?.Status == "complete");
        }

        using (LocalPaperStore reopened = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor recovered = new(configuration, reopened,
                new LinearPerpetualRiskMathCalculator());
            LocalPaperExecutionResult applied = recovered.Process(new LocalPaperObservation(
                BarEnvelope("open", 3, 103m, 104m, 102m, 103m, 10_003), input, plan));
            LocalPaperExecutionStatus expectedStatus = expectedComplete
                ? LocalPaperExecutionStatus.AlreadyProcessed
                : LocalPaperExecutionStatus.Applied;
            Assert.True(applied.Status == expectedStatus,
                $"{applied.Status}: {string.Join(" | ", applied.BlockReasons)}");
            Assert.Equal(LocalPaperExecutionStatus.Applied, recovered.Process(
                new LocalPaperObservation(BarEnvelope("target", 4, 109m, 111m, 108m,
                    110m, 10_004))).Status);
            LocalPaperEvidence evidence = LocalPaperEvidence.Capture(reopened, "stream-a",
                "source-v1");
            Assert.Empty(evidence.Ledger!.Positions);
            Assert.True(evidence.Fills.Count == 2,
                string.Join(",", evidence.Fills.Select(static fill => fill.FillId)));
            Assert.Equal(4, reopened.ReadObservations("stream-a")
                .Count(static item => item.Status == "complete"));
        }
    }

    private static string RunTrace(string path, bool restartBeforeClose,
        bool repeatCompleted)
    {
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture();
        using (LocalPaperStore store = LocalPaperStore.Open(path))
        {
            LocalPaperExecutor executor = new(configuration, store,
                new LinearPerpetualRiskMathCalculator());
            RecordSignalAndEntry(executor);
            Assert.Equal(LocalPaperExecutionStatus.Applied, executor.Process(
                new LocalPaperObservation(BarEnvelope("open", 3, 103m, 104m, 102m, 103m,
                    10_003), input, plan)).Status);
            if (!restartBeforeClose)
            {
                Assert.Equal(LocalPaperExecutionStatus.Applied, executor.Process(
                    new LocalPaperObservation(BarEnvelope("target", 4, 109m, 111m, 108m,
                        110m, 10_004))).Status);
            }

            if (repeatCompleted)
            {
                _ = executor.Process(new LocalPaperObservation(BarEnvelope("signal", 1,
                    100m, 101m, 99m, 100m, 10_001)));
                _ = executor.Process(new LocalPaperObservation(BarEnvelope("entry", 2,
                    101m, 102m, 100m, 101m, 10_002)));
                _ = executor.Process(new LocalPaperObservation(BarEnvelope("open", 3,
                    103m, 104m, 102m, 103m, 10_003), input, plan));
            }
        }

        if (restartBeforeClose)
        {
            using LocalPaperStore store = LocalPaperStore.Open(path);
            LocalPaperExecutor executor = new(configuration, store,
                new LinearPerpetualRiskMathCalculator());
            Assert.Equal(LocalPaperExecutionStatus.Applied, executor.Process(
                new LocalPaperObservation(BarEnvelope("target", 4, 109m, 111m, 108m,
                    110m, 10_004))).Status);
        }

        using LocalPaperStore evidenceStore = LocalPaperStore.Open(path);
        return LocalPaperEvidence.Capture(evidenceStore, "stream-a", "source-v1")
            .CanonicalJson;
    }

    private static void RecordSignalAndEntry(LocalPaperExecutor executor)
    {
        Assert.Equal(LocalPaperExecutionStatus.Recorded,
            executor.Process(new LocalPaperObservation(BarEnvelope("signal", 1,
                100m, 101m, 99m, 100m, 10_001))).Status);
        Assert.Equal(LocalPaperExecutionStatus.Recorded,
            executor.Process(new LocalPaperObservation(BarEnvelope("entry", 2,
                101m, 102m, 100m, 101m, 10_002))).Status);
    }

    private static (LocalPaperConfiguration Configuration, RiskEvaluationInput Input,
        SimulatedRiskPlan Plan) Fixture()
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        RiskMathInput math = new(Instrument, Direction.Long, new Money(1_000m, "USDT"),
            0.01m, new Price(100m), new Price(90m),
            new MoneyPerQuantityUnit(0m, "USDT"), new MoneyPerQuantityUnit(0m, "USDT"));
        RiskProtectionEvidence protection = new(Instrument, Direction.Long,
            projection.Result.Decision.SetupId, "method-v1", "source-v1", "protective-v1",
            RiskEvidenceFreshness.Fresh, cursor, new Price(90m), new Price(110m),
            new Price(120m), "fixture");
        RiskEvaluationInput input = new("local-paper-risk", projection, math, Portfolio, cursor,
            new RiskPortfolioUsage(Portfolio, new Money(0m, "USDT"), new Money(0m, "USDT"),
                new Money(0m, "USDT"), 0, "usage-source", "usage-v1", cursor, "fixture"),
            new Money(0m, "USDT"), "period-2026-01-01", new Money(10m, "USDT"), 1,
            RiskHealthEvidence.Healthy("account-source", "account-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("portfolio-source", "portfolio-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("market-source", "market-v1", cursor, "fixture"),
            RiskKillSwitchEvidence.Clear("kill-source", "kill-v1", cursor, "fixture"),
            "fixture", protection,
            RiskCooldownState.Clear(Instrument, "cooldown-source", "cooldown-v1", cursor,
                "fixture"));
        SimulatedFillAssumptions assumptions = new(0m, 0m,
            new FillSimulationAssumptions("fees", "slippage", "next bar", "unknown queue",
                "unsupported partial fills", "stop before target", "synthetic OHLCV"),
            new FillSimulationVersion("ohlcv-fill-v1"));
        RiskEvaluationConfiguration riskConfiguration = new(RiskEvaluationVersion.Current,
            new RiskMathVersion("linear-perpetual-risk-v1"),
            new RiskLimitConfiguration(new Money(10m, "USDT"), new Money(100m, "USDT"),
                new Money(100m, "USDT"), "period-2026-01-01"),
            new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
                new Money(10_000m, "USDT"), 2), "fixture");
        LocalPaperConfiguration configuration = new("paper-config-v1",
            new Money(1_000m, "USDT"), 0.01m, riskConfiguration, assumptions);
        SimulatedRiskPlan plan = new("paper-plan-v1", Instrument, Direction.Long,
            Utc(1), math, [new SimulatedTakeProfit("tp1", new Price(110m), 1m)], null,
            false, null, assumptions, projection.Result.Decision);
        return (configuration, input, plan);
    }

    private static ReplayEnvelope BarEnvelope(string id, int offset, decimal open,
        decimal high, decimal low, decimal close, long sequence)
    {
        UtcTimestamp eventTime = Utc(offset);
        Bar bar = new(id, Instrument, eventTime, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(1)), new Price(open), new Price(high),
            new Price(low), new Price(close), new Quantity(1m));
        return new ReplayEnvelope("stream-a", new CausalMarketEvent(bar, eventTime, eventTime,
            sequence));
    }

    private static UtcTimestamp Utc(int offset) => new(BaseTime.AddMinutes(offset));

    private sealed class ChangedModelSimulator : IFillSimulator
    {
        public FillSimulationVersion Version { get; } = new("changed-fill-model-v1");

        public FillSimulationResult Simulate(FillSimulationInput input) =>
            throw new InvalidOperationException("The changed model must be rejected before simulation.");
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase(string suffix) => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"local-paper-recovery-{suffix}-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string path in new[] { Path, Path + "-wal", Path + "-shm" })
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
