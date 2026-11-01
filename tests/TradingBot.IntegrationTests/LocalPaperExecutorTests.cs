using System.Collections.ObjectModel;
using TradingBot.App;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LocalPaperExecutorTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly PortfolioId Portfolio = new("portfolio-a");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RiskAndFillEvidenceCommitAtomicallyAndRecoverIdempotently()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture();
        LocalPaperObservation signal = new(BarEnvelope("signal", 1, 100m, 101m, 99m, 100m,
            10_001));
        LocalPaperObservation entry = new(BarEnvelope("entry", 2, 101m, 102m, 100m, 101m,
            10_002));
        LocalPaperObservation exit = new(BarEnvelope("exit", 3, 109m, 111m, 108m, 110m,
            10_003), input, plan);

        LocalPaperExecutionResult first;
        using (LocalPaperStore store = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor executor = new(configuration, store,
                new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator());
            Assert.Equal(LocalPaperExecutionStatus.Recorded, executor.Process(signal).Status);
            Assert.Equal(LocalPaperExecutionStatus.Recorded, executor.Process(entry).Status);
            first = executor.Process(exit);

            Assert.Equal(LocalPaperExecutionStatus.Applied, first.Status);
            Assert.NotNull(first.RiskEvaluation);
            Assert.True(first.RiskEvaluation.IsApproved);
            Assert.NotNull(first.FillSimulation);
            Assert.Equal(SimulatedFillRole.Entry, first.FillSimulation.Fills[0].Role);
            Assert.Equal(SimulatedFillRole.TakeProfit1, first.FillSimulation.Fills[1].Role);
            Assert.Equal(1009m, first.Ledger!.Equity);
            Assert.Empty(first.Ledger.Positions);
            Assert.Equal(3, first.Ledger.Revision);

            LocalPaperExecutionResult duplicate = executor.Process(exit);
            Assert.Equal(LocalPaperExecutionStatus.AlreadyProcessed, duplicate.Status);
            Assert.Equal(first.Ledger.StateJson, duplicate.Ledger!.StateJson);
            Assert.Equal(3, store.ReadEnvelopes("stream-a").Count);
        }

        using (LocalPaperStore reopened = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor executor = new(configuration, reopened,
                new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator());
            LocalPaperExecutionResult recovered = executor.Process(exit);
            Assert.Equal(LocalPaperExecutionStatus.AlreadyProcessed, recovered.Status);
            Assert.Equal(first.Ledger!.StateJson, recovered.Ledger!.StateJson);
            Assert.Equal(["complete", "complete", "complete"],
                reopened.ReadObservations("stream-a").Select(static row => row.Status));
        }
    }

    [Fact]
    public void OpenPositionContinuesOnLaterObservationWithoutNewApplication()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture();
        using (LocalPaperStore store = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor executor = new(configuration, store,
                new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator());
            Assert.Equal(LocalPaperExecutionStatus.Recorded,
                executor.Process(new LocalPaperObservation(BarEnvelope("signal", 1,
                    100m, 101m, 99m, 100m, 10_001))).Status);
            Assert.Equal(LocalPaperExecutionStatus.Recorded,
                executor.Process(new LocalPaperObservation(BarEnvelope("entry", 2,
                    101m, 102m, 100m, 101m, 10_002))).Status);

            LocalPaperExecutionResult opened = executor.Process(new LocalPaperObservation(
                BarEnvelope("open", 3, 103m, 104m, 102m, 103m, 10_003), input, plan));

            Assert.Equal(LocalPaperExecutionStatus.Applied, opened.Status);
            Assert.NotNull(opened.FillSimulation);
            Assert.True(opened.FillSimulation.IsOpen);
            Assert.Single(opened.Ledger!.Positions);
            Assert.True(opened.Ledger.ReservedRisk > 0m);

            LocalPaperExecutionResult closed = executor.Process(new LocalPaperObservation(
                BarEnvelope("target", 4, 109m, 111m, 108m, 110m, 10_004)));

            Assert.Equal(LocalPaperExecutionStatus.Applied, closed.Status);
            Assert.NotNull(closed.FillSimulation);
            Assert.False(closed.FillSimulation.IsOpen);
            Assert.Empty(closed.Ledger!.Positions);
            Assert.Equal(0m, closed.Ledger.ReservedRisk);
            Assert.Equal(1009m, closed.Ledger.Equity);
            Assert.Equal(4, closed.Ledger.Revision);
        }

        using (LocalPaperStore reopened = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor executor = new(configuration, reopened,
                new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator());
            LocalPaperExecutionResult retry = executor.Process(new LocalPaperObservation(
                BarEnvelope("target", 4, 109m, 111m, 108m, 110m, 10_004)));

            Assert.Equal(LocalPaperExecutionStatus.AlreadyProcessed, retry.Status);
            Assert.Empty(retry.Ledger!.Positions);
            Assert.Equal(1009m, retry.Ledger.Equity);
        }
    }

    [Fact]
    public void NewApplicationUsesCurrentLedgerEquityAndRejectsStaleInitialEquity()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture();
        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        LocalPaperExecutor executor = new(configuration, store,
            new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator());

        executor.Process(new LocalPaperObservation(BarEnvelope("signal", 1,
            100m, 101m, 99m, 100m, 10_001)));
        executor.Process(new LocalPaperObservation(BarEnvelope("entry", 2,
            101m, 102m, 100m, 101m, 10_002)));
        LocalPaperExecutionResult firstOpen = executor.Process(new LocalPaperObservation(
            BarEnvelope("open", 3, 103m, 104m, 102m, 103m, 10_003), input, plan));
        Assert.Equal(LocalPaperExecutionStatus.Applied, firstOpen.Status);
        LocalPaperExecutionResult firstClosed = executor.Process(new LocalPaperObservation(
            BarEnvelope("stop", 4, 89m, 90m, 89m, 89m, 10_004)));
        Assert.Equal(LocalPaperExecutionStatus.Applied, firstClosed.Status);
        Assert.Equal(988m, firstClosed.Ledger!.Equity);

        RiskMathInput staleMath = input.RiskMathInput;
        SimulatedRiskPlan stalePlan = new("paper-plan-stale", Instrument, Direction.Long,
            Utc(4), staleMath, [new SimulatedTakeProfit("tp1", new Price(120m), 1m)],
            null, false, null, plan.FillAssumptions, plan.Decision);
        LocalPaperExecutionResult stale = executor.Process(new LocalPaperObservation(
            BarEnvelope("second-entry", 5, 111m, 112m, 110m, 111m, 10_005), input,
            stalePlan));
        Assert.True(stale.IsBlocked);
        Assert.Contains(stale.BlockReasons, static reason =>
            reason.Contains("equity", StringComparison.OrdinalIgnoreCase));

        RiskMathInput currentMath = new(Instrument, Direction.Long,
            new Money(988m, "USDT"), staleMath.RiskFraction, staleMath.EntryPrice,
            staleMath.ProtectiveStopPrice, staleMath.FeePerUnit,
            staleMath.EstimatedSlippagePerUnit);
        RiskEvaluationInput currentInput = new(input.EvaluationId + "-second",
            input.DecisionProjection!, currentMath, input.PortfolioId, input.Cursor,
            input.PortfolioUsage, input.AccumulatedDailyLoss, input.AccumulatedLossPeriodId,
            input.CandidateMargin, input.CandidatePositionSlots, input.AccountHealth,
            input.PortfolioHealth, input.MarketHealth, input.KillSwitch, input.Provenance,
            input.Protection, input.Cooldown);
        SimulatedRiskPlan currentPlan = new("paper-plan-current", Instrument, Direction.Long,
            Utc(4), currentMath, [new SimulatedTakeProfit("tp1", new Price(120m), 1m)],
            null, false, null, plan.FillAssumptions, plan.Decision);

        LocalPaperExecutionResult secondOpen = executor.Process(new LocalPaperObservation(
            BarEnvelope("second-entry", 5, 111m, 112m, 110m, 111m, 10_005), currentInput,
            currentPlan));
        Assert.Equal(LocalPaperExecutionStatus.Applied, secondOpen.Status);
        Assert.Equal(988m, secondOpen.RiskEvaluation!.RiskMathResult!.Input.Equity.Amount);
        Assert.Equal(988m, secondOpen.FillSimulation!.RiskResult!.Input.Equity.Amount);

        LocalPaperExecutionResult secondClosed = executor.Process(new LocalPaperObservation(
            BarEnvelope("second-target", 6, 119m, 121m, 118m, 120m, 10_006)));
        Assert.Equal(LocalPaperExecutionStatus.Applied, secondClosed.Status);
        Assert.Empty(secondClosed.Ledger!.Positions);
        Assert.Equal(988m, secondClosed.RiskEvaluation!.RiskMathResult!.Input.Equity.Amount);
    }

    [Fact]
    public void PartialTakeProfitRemainsReservedUntilTheNextObservationClosesIt()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan sourcePlan) = Fixture();
        SimulatedRiskPlan plan = new(sourcePlan.ResearchPlanId + "-partial", sourcePlan.InstrumentId,
            sourcePlan.Direction, sourcePlan.DecisionTime, sourcePlan.RiskInput,
            [new SimulatedTakeProfit("tp1", new Price(105m), 0.5m),
             new SimulatedTakeProfit("tp2", new Price(110m), 0.5m)],
            sourcePlan.HoldingPeriod, sourcePlan.MoveStopToBreakeven,
            sourcePlan.BreakevenStopPrice, sourcePlan.FillAssumptions, sourcePlan.Decision);

        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        LocalPaperExecutor executor = new(configuration, store,
            new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator());
        executor.Process(new LocalPaperObservation(BarEnvelope("signal", 1,
            100m, 101m, 99m, 100m, 10_001)));
        executor.Process(new LocalPaperObservation(BarEnvelope("entry", 2,
            101m, 102m, 100m, 101m, 10_002)));
        LocalPaperExecutionResult opened = executor.Process(new LocalPaperObservation(
            BarEnvelope("open", 3, 103m, 104m, 102m, 103m, 10_003), input, plan));
        Assert.Equal(LocalPaperExecutionStatus.Applied, opened.Status);
        Assert.True(opened.FillSimulation!.IsOpen);

        LocalPaperExecutionResult halfClosed = executor.Process(new LocalPaperObservation(
            BarEnvelope("tp1", 4, 104m, 106m, 103m, 105m, 10_004)));

        Assert.Equal(LocalPaperExecutionStatus.Applied, halfClosed.Status);
        Assert.True(halfClosed.FillSimulation!.IsOpen);
        Assert.Single(halfClosed.Ledger!.Positions);
        Assert.Equal(0.5m, halfClosed.Ledger.Positions[0].Quantity);
        Assert.True(halfClosed.Ledger.ReservedRisk > 0m);

        LocalPaperExecutionResult fullyClosed = executor.Process(new LocalPaperObservation(
            BarEnvelope("tp2", 5, 109m, 111m, 108m, 110m, 10_005)));

        Assert.Equal(LocalPaperExecutionStatus.Applied, fullyClosed.Status);
        Assert.False(fullyClosed.FillSimulation!.IsOpen);
        Assert.Empty(fullyClosed.Ledger!.Positions);
        Assert.Equal(0m, fullyClosed.Ledger.ReservedRisk);
    }

    [Fact]
    public void ChangedReplayFillEvidenceBlocksAndRetainsOriginalReservation()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture();
        using LocalPaperStore store = LocalPaperStore.Open(database.Path);

        LocalPaperExecutor executor = new(configuration, store,
            new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator());
        executor.Process(new LocalPaperObservation(BarEnvelope("signal", 1,
            100m, 101m, 99m, 100m, 10_001)));
        executor.Process(new LocalPaperObservation(BarEnvelope("entry", 2,
            101m, 102m, 100m, 101m, 10_002)));
        LocalPaperExecutionResult opened = executor.Process(new LocalPaperObservation(
            BarEnvelope("open", 3, 103m, 104m, 102m, 103m, 10_003), input, plan));
        Assert.Equal(LocalPaperExecutionStatus.Applied, opened.Status);

        LocalPaperExecutor changed = new(configuration, store,
            new LinearPerpetualRiskMathCalculator(), new DifferentPriceFillSimulator());
        LocalPaperExecutionResult blocked = changed.Process(new LocalPaperObservation(
            BarEnvelope("target", 4, 109m, 111m, 108m, 110m, 10_004)));

        Assert.True(blocked.IsBlocked);
        Assert.Contains(blocked.BlockReasons, static reason =>
            reason.Contains("fill identity", StringComparison.OrdinalIgnoreCase));
        Assert.True(store.ReadSnapshot("stream-a")!.ReservedRisk > 0m);
        Assert.Equal("blocked", Assert.Single(store.ReadObservations("stream-a"),
            row => row.Sequence == 10_004).Status);
    }

    [Fact]
    public void MissingProtectiveEvidenceBlocksBeforeDurableReservation()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture(includeProtection: false);
        ReplayEnvelope envelope = BarEnvelope("blocked", 3, 100m, 101m, 99m, 100m,
            10_003);

        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        LocalPaperExecutionResult result = new LocalPaperExecutor(configuration, store,
            new LinearPerpetualRiskMathCalculator(), new OhlcvFillSimulator())
            .Process(new LocalPaperObservation(envelope, input, plan));

        Assert.True(result.IsBlocked);
        Assert.NotNull(result.RiskEvaluation);
        Assert.Contains(RiskReasonCodes.ProtectiveEvidenceMissing, result.BlockReasons);
        Assert.Null(store.ReadSnapshot(envelope.StreamId));
    }

    [Fact]
    public void UnsupportedPartialFillLeavesThePreparedReservationFailClosed()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, RiskEvaluationInput input,
            SimulatedRiskPlan plan) = Fixture(fillVersion: "partial-fill-v1");
        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        PartialFillSimulator fillModel = new();
        LocalPaperExecutor executor = new(configuration, store,
            new LinearPerpetualRiskMathCalculator(), fillModel);

        LocalPaperExecutionResult result = executor.Process(new LocalPaperObservation(
            BarEnvelope("partial", 3, 101m, 102m, 100m, 101m, 10_003), input, plan));

        Assert.True(result.IsBlocked);
        Assert.Contains(result.BlockReasons, static reason =>
            reason.Contains("fill", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("quantity", StringComparison.OrdinalIgnoreCase));
        LocalPaperObservationRecord row = Assert.Single(store.ReadObservations("stream-a"));
        Assert.Equal("blocked", row.Status);
        Assert.Equal(0, store.ReadSnapshot("stream-a")!.Revision);
        Assert.True(store.ReadSnapshot("stream-a")!.ReservedRisk > 0m);
    }

    [Fact]
    public void LegacyRowsRemainReadableButBlockExecutorRecovery()
    {
        using TemporaryDatabase database = new();
        using LocalPaperStore store = LocalPaperStore.Open(database.Path);
        Assert.True(store.Append("stream-a", 1, "legacy-event", "legacy-payload",
            "legacy-state"));

        (LocalPaperConfiguration configuration, _, _) = Fixture();
        LocalPaperExecutionResult result = new LocalPaperExecutor(configuration, store,
            new LinearPerpetualRiskMathCalculator())
            .Process(new LocalPaperObservation(BarEnvelope("new-event", 2, 101m, 102m, 100m,
                101m, 10_002)));

        Assert.True(result.IsBlocked);
        Assert.Contains(result.BlockReasons, static reason =>
            reason.Contains("legacy", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("legacy-state", store.ReadCheckpoint("stream-a"));
    }

    [Fact]
    public void ChangedConfigurationOrModelIdentityCannotReplaceACompletedObservation()
    {
        using TemporaryDatabase database = new();
        (LocalPaperConfiguration configuration, _, _) = Fixture();
        ReplayEnvelope envelope = BarEnvelope("identity", 1, 100m, 101m, 99m, 100m, 10_001);
        using (LocalPaperStore store = LocalPaperStore.Open(database.Path))
        {
            LocalPaperExecutor executor = new(configuration, store,
                new LinearPerpetualRiskMathCalculator());
            Assert.Equal(LocalPaperExecutionStatus.Recorded,
                executor.Process(new LocalPaperObservation(envelope)).Status);

            // A new content identity is intentional; the stream remains pinned
            // to the first canonical configuration.
            LocalPaperConfiguration changed = new("changed-config",
                configuration.InitialEquity, configuration.RiskFraction,
                configuration.RiskConfiguration, configuration.FillAssumptions);
            LocalPaperExecutionResult conflict = new LocalPaperExecutor(changed, store,
                new LinearPerpetualRiskMathCalculator())
                .Process(new LocalPaperObservation(envelope));
            Assert.True(conflict.IsBlocked);
            Assert.Contains(conflict.BlockReasons, static reason =>
                reason.Contains("identity", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("configuration", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static (LocalPaperConfiguration Configuration, RiskEvaluationInput Input,
        SimulatedRiskPlan Plan) Fixture(bool includeProtection = true,
        string fillVersion = "ohlcv-fill-v1")
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        RiskMathInput math = new(Instrument, Direction.Long, new Money(1_000m, "USDT"),
            0.01m, new Price(100m), new Price(90m),
            new MoneyPerQuantityUnit(0m, "USDT"), new MoneyPerQuantityUnit(0m, "USDT"));
        RiskProtectionEvidence? protection = includeProtection
            ? new RiskProtectionEvidence(Instrument, Direction.Long,
                projection.Result.Decision.SetupId, "method-v1", "source-v1", "protective-v1",
                RiskEvidenceFreshness.Fresh, cursor, new Price(90m), new Price(110m),
                new Price(120m), "fixture")
            : null;
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
            new FillSimulationVersion(fillVersion));
        RiskEvaluationConfiguration riskConfiguration = new(RiskEvaluationVersion.Current,
            new RiskMathVersion("linear-perpetual-risk-v1"),
            new RiskLimitConfiguration(new Money(10m, "USDT"), new Money(100m, "USDT"),
                new Money(100m, "USDT"), "period-2026-01-01"),
            new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
                new Money(10_000m, "USDT"), 2), "fixture");
        LocalPaperConfiguration configuration = new("paper-config-v1",
            new Money(1_000m, "USDT"), 0.01m, riskConfiguration, assumptions);
        SimulatedRiskPlan plan = new("paper-plan-v1", Instrument, Direction.Long,
            Utc(1), math, [new SimulatedTakeProfit("tp1", new Price(110m), 1m)], null, false,
            null, assumptions, projection.Result.Decision);
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

    private sealed class PartialFillSimulator : IFillSimulator
    {
        public FillSimulationVersion Version { get; } = new("partial-fill-v1");

        public FillSimulationResult Simulate(FillSimulationInput input)
        {
            CausalMarketEvent latest = input.MarketSnapshot[^1];
            return new FillSimulationResult(Version, input,
                [new ResearchFill(input.HypotheticalRequestId + ":partial",
                    new Quantity(input.RequestedQuantity.Value / 2m), input.ReferencePrice!,
                    input.RequestedAt, latest.StableSequence)]);
        }
    }

    private sealed class DifferentPriceFillSimulator : IFillSimulator
    {
        public FillSimulationVersion Version { get; } = new("ohlcv-fill-v1");

        public FillSimulationResult Simulate(FillSimulationInput input)
        {
            CausalMarketEvent latest = input.MarketSnapshot[^1];
            Price changedPrice = new(input.ReferencePrice!.Value + 1m);
            return new FillSimulationResult(Version, input,
                [new ResearchFill(input.HypotheticalRequestId + ":changed",
                    input.RequestedQuantity, changedPrice, input.RequestedAt,
                    latest.StableSequence)]);
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"local-paper-executor-{Guid.NewGuid():N}.sqlite");

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
