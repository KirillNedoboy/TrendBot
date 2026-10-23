using System.Reflection;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class SimulatedRiskTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void LongSimulationEntersOnTheNextBarAndAppliesPartialTpAndNextBarBreakeven()
    {
        FixedRiskCalculator calculator = new(Direction.Long, 1m, 100m, 95m);
        SimulatedRiskPlan plan = Plan(Direction.Long, calculator,
            [new SimulatedTakeProfit("tp1", new Price(105m), 0.5m)],
            moveStopToBreakeven: true, holdingPeriod: null,
            breakevenStopPrice: new Price(102m), slippagePerUnit: 0.5m);
        ReplayEnvelope[] events =
        [
            BarEnvelope("signal", 0, 100m, 106m, 94m, 101m, 1),
            BarEnvelope("entry", 1, 102m, 103m, 101m, 102m, 2),
            BarEnvelope("tp", 2, 103m, 106m, 102m, 105m, 3),
            BarEnvelope("breakeven", 3, 105m, 106m, 99m, 100m, 4)
        ];

        SimulatedRiskSimulationResult result = new SimulatedRiskSimulator(calculator)
            .Simulate(plan, events);

        Assert.Equal(1, calculator.Calls);
        Assert.Equal([SimulatedFillRole.Entry, SimulatedFillRole.TakeProfit1,
            SimulatedFillRole.BreakevenStop], result.Fills.Select(static fill => fill.Role));
        Assert.Equal(new Quantity(1m), result.Fills[0].Quantity);
        Assert.Equal(new Price(102.5m), result.Fills[0].Price);
        Assert.Equal(new Quantity(0.5m), result.Fills[1].Quantity);
        Assert.Equal(new Price(104.5m), result.Fills[1].Price);
        Assert.Equal(new Price(101.5m), result.Fills[2].Price);
        Assert.Equal(SimulatedRiskExitReason.BreakevenStop, result.ExitReason);
        Assert.False(result.IsOpen);

        ReplayRunResult replay = HistoricalReplayRunner.Run("stream-a", events,
            static context => context.Event.EventId);
        BacktestReport report = BacktestReport.Create("simulation-run", replay, result);
        Assert.Equal("decision-1", Assert.Single(report.Decisions).DecisionId);
        Assert.Contains("fixture-fill-v1", report.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains("decision-1", report.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void StopGapUsesTheLessFavorableOpenAndChargesEveryExecution()
    {
        FixedRiskCalculator calculator = new(Direction.Short, 2m, 100m, 105m);
        SimulatedRiskPlan plan = Plan(Direction.Short, calculator, [],
            moveStopToBreakeven: false, holdingPeriod: null,
            feePerUnit: 0.1m, slippagePerUnit: 0.5m);
        ReplayEnvelope[] events =
        [
            BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
            BarEnvelope("entry", 1, 98m, 99m, 97m, 98m, 2),
            BarEnvelope("stop-gap", 2, 106m, 107m, 104m, 105m, 3)
        ];

        SimulatedRiskSimulationResult result = new SimulatedRiskSimulator(calculator)
            .Simulate(plan, events);

        Assert.Equal([SimulatedFillRole.Entry, SimulatedFillRole.StopLoss],
            result.Fills.Select(static fill => fill.Role));
        Assert.Equal(new Price(97.5m), result.Fills[0].Price);
        Assert.Equal(new Price(106.5m), result.Fills[1].Price);
        Assert.Equal(0.4m, result.TotalFees);
        Assert.Equal(2m, result.TotalSlippage);
        Assert.Equal(SimulatedRiskExitReason.StopLoss, result.ExitReason);
    }

    [Fact]
    public void SameBarStopAndTargetChoosesStopAndRecordsAmbiguity()
    {
        FixedRiskCalculator calculator = new(Direction.Long, 1m, 100m, 95m);
        SimulatedRiskPlan plan = Plan(Direction.Long, calculator,
            [new SimulatedTakeProfit("tp1", new Price(105m), 1m)],
            moveStopToBreakeven: false, holdingPeriod: null);
        ReplayEnvelope[] events =
        [
            BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
            BarEnvelope("entry", 1, 100m, 101m, 99m, 100m, 2),
            BarEnvelope("stop-and-target", 2, 100m, 106m, 94m, 100m, 3)
        ];

        SimulatedRiskSimulationResult result = new SimulatedRiskSimulator(calculator)
            .Simulate(plan, events);

        Assert.Equal(SimulatedFillRole.StopLoss, result.Fills[^1].Role);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SimulatedRiskExitReason.StopLoss, result.ExitReason);
    }

    [Fact]
    public void TimeExitUsesTheFirstAvailableCloseAndLeavesRemainderOpenAtEndOfData()
    {
        FixedRiskCalculator calculator = new(Direction.Long, 1m, 100m, 90m);
        SimulatedRiskPlan plan = Plan(Direction.Long, calculator, [],
            moveStopToBreakeven: false, holdingPeriod: TimeSpan.FromMinutes(3));
        ReplayEnvelope[] events =
        [
            BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
            BarEnvelope("entry", 1, 100m, 101m, 99m, 100m, 2),
            BarEnvelope("before-exit", 2, 100m, 101m, 99m, 100m, 3),
            BarEnvelope("time-exit", 3, 101m, 102m, 100m, 101m, 4)
        ];

        SimulatedRiskSimulationResult result = new SimulatedRiskSimulator(calculator)
            .Simulate(plan, events);

        Assert.Equal(SimulatedRiskExitReason.TimeExit, result.ExitReason);
        Assert.Equal(new Price(101m), Assert.Single(result.Fills.Skip(1)).Price);
        Assert.False(result.IsOpen);
    }

    [Fact]
    public void UnknownMandatoryPlanValuesAndOverflowFailClosed()
    {
        FixedRiskCalculator calculator = new(Direction.Long, 1m, 100m, 90m);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedRiskPlan(
            "plan", Instrument, Direction.Long, Utc(0), calculator.Input,
            [new SimulatedTakeProfit("tp", new Price(105m), 1.1m)], null, false, null,
            Assumptions(0m, 0m)));
        Assert.Throws<ArgumentException>(() => Plan(Direction.Long, calculator, [],
            moveStopToBreakeven: true, holdingPeriod: null));

        ReplayEnvelope[] minimumEvents =
        [
            BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
            BarEnvelope("entry", 1, 100m, 101m, 99m, 100m, 2)
        ];
        SimulatedRiskPlan missingVersion = new(
            "plan-missing-version", Instrument, Direction.Long, Utc(0), calculator.Input,
            [], null, false, null,
            new SimulatedFillAssumptions(0m, 0m,
                new FillSimulationAssumptions("fees", "slippage", "latency",
                    "queue", "partial", "intrabar", "limitations")),
            new Decision("decision-missing-version", "setup-1", DecisionOutcome.Allow,
                ["fixture"]));
        Assert.Throws<InvalidOperationException>(() => new SimulatedRiskSimulator(calculator)
            .Simulate(missingVersion, minimumEvents));

        SimulatedRiskPlan missingDecision = new(
            "plan-missing-decision", Instrument, Direction.Long, Utc(0), calculator.Input,
            [], null, false, null, Assumptions(0m, 0m));
        Assert.Throws<InvalidOperationException>(() => new SimulatedRiskSimulator(calculator)
            .Simulate(missingDecision, minimumEvents));

        SimulatedRiskPlan plan = Plan(Direction.Long, calculator, [], false, null,
            slippagePerUnit: 1m);
        ReplayEnvelope[] events =
        [
            BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
            BarEnvelope("entry", 1, decimal.MaxValue, decimal.MaxValue,
                decimal.MaxValue - 1m, decimal.MaxValue, 2)
        ];
        Assert.Throws<OverflowException>(() => new SimulatedRiskSimulator(calculator)
            .Simulate(plan, events));
    }

    [Fact]
    public void SimulationSurfaceContainsOnlyResearchObservations()
    {
        Type[] publicTypes =
        [typeof(SimulatedRiskPlan), typeof(SimulatedRiskSimulationResult),
            typeof(SimulatedFill), typeof(SimulatedFillAssumptions),
            typeof(SimulatedRiskSimulator)];
        string[] forbidden = ["OrderIntent", "RiskDecision", "Authorize", "Execute", "Executor"];

        foreach (Type type in publicTypes)
        {
            string surface = string.Join("|", type.GetMembers(BindingFlags.Public
                | BindingFlags.Instance | BindingFlags.Static)
                .Select(member => member.Name)
                .Concat(type.GetProperties().Select(property => property.PropertyType.FullName
                    ?? string.Empty)));
            Assert.DoesNotContain(forbidden, name => surface.Contains(name,
                StringComparison.Ordinal));
        }
    }

    private static SimulatedRiskPlan Plan(Direction direction, FixedRiskCalculator calculator,
        IEnumerable<SimulatedTakeProfit> takeProfits, bool moveStopToBreakeven,
        TimeSpan? holdingPeriod, Price? breakevenStopPrice = null,
        decimal feePerUnit = 0m, decimal slippagePerUnit = 0m) =>
        new("plan-1", Instrument, direction, Utc(0), calculator.Input, takeProfits,
            holdingPeriod, moveStopToBreakeven, breakevenStopPrice,
            Assumptions(feePerUnit, slippagePerUnit),
            new Decision("decision-1", "setup-1", DecisionOutcome.Allow, ["fixture"]));

    private static SimulatedFillAssumptions Assumptions(decimal feePerUnit,
        decimal slippagePerUnit) => new(feePerUnit, slippagePerUnit,
        new FillSimulationAssumptions("fees", "slippage", "next bar", "unknown queue",
            "partial by configured fraction", "stop before target", "OHLCV limitations"),
        new FillSimulationVersion("fixture-fill-v1"));

    private static ReplayEnvelope BarEnvelope(string id, int minutes, decimal open,
        decimal high, decimal low, decimal close, long sequence) => new("stream-a",
        new CausalMarketEvent(new Bar(id, Instrument, Utc(minutes),
            Utc(minutes), Utc(minutes + 1), new Price(open), new Price(high),
            new Price(low), new Price(close), new Quantity(1m)), Utc(minutes + 1),
            Utc(minutes + 1), sequence));

    private static UtcTimestamp Utc(int minutes) => new(BaseTime.AddMinutes(minutes));

    private sealed class FixedRiskCalculator : IRiskMathCalculator
    {
        public FixedRiskCalculator(Direction direction, decimal quantity, decimal entry,
            decimal stop)
        {
            Input = new RiskMathInput(Instrument, direction, new Money(1000m, "USDT"),
                0.01m, new Price(entry), new Price(stop),
                new MoneyPerQuantityUnit(0m, "USDT"),
                new MoneyPerQuantityUnit(0m, "USDT"));
            Result = new RiskMathResult(new RiskMathVersion("test-risk-v1"), Input,
                new Money(10m, "USDT"), new MoneyPerQuantityUnit(10m, "USDT"),
                new Quantity(quantity), new Money(entry * quantity, "USDT"));
        }

        public RiskMathVersion Version => Result.Version;

        public RiskMathInput Input { get; }

        public RiskMathResult Result { get; }

        public int Calls { get; private set; }

        public RiskMathResult Calculate(RiskMathInput input)
        {
            Calls++;
            Assert.Equal(Input, input);
            return Result;
        }
    }
}
