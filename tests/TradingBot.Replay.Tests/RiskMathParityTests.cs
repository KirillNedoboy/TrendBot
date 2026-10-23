using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class RiskMathParityTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ResearchSimulationUsesTheSameLinearCalculatorResultAsPlanning()
    {
        LinearPerpetualRiskMathCalculator calculator = new();
        RiskMathInput input = CreateInput(Direction.Long);
        SimulatedRiskPlan plan = new(
            "parity-plan", Instrument, Direction.Long, Utc(0), input, [], null, false,
            null, Assumptions(), new Decision("parity-decision", "setup-1",
                DecisionOutcome.Allow, ["fixture"]));

        RiskMathResult planned = calculator.Calculate(input);
        SimulatedRiskSimulationResult simulated = new SimulatedRiskSimulator(calculator)
            .Simulate(plan, [BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
                BarEnvelope("entry", 1, 101m, 102m, 100m, 101m, 2)]);

        Assert.Equal(planned, simulated.RiskResult);
        Assert.Equal(calculator.Version, simulated.RiskResult.Version);
        Assert.Equal(input, simulated.RiskResult.Input);
    }

    [Fact]
    public void ResearchSimulationRejectsAResultWhoseFullInputDiffersFromThePlan()
    {
        RiskMathInput input = CreateInput(Direction.Long);
        SimulatedRiskPlan plan = new(
            "tampered-plan", Instrument, Direction.Long, Utc(0), input, [], null, false,
            null, Assumptions(), new Decision("tampered-decision", "setup-1",
                DecisionOutcome.Allow, ["fixture"]));

        Assert.Throws<InvalidOperationException>(() => new SimulatedRiskSimulator(
            new TamperingCalculator()).Simulate(plan,
                [BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
                    BarEnvelope("entry", 1, 101m, 102m, 100m, 101m, 2)]));
    }

    private static RiskMathInput CreateInput(Direction direction) =>
        new(Instrument, direction, new Money(1000m, "USDT"), 0.02m,
            new Price(100m), new Price(direction == Direction.Long ? 95m : 105m),
            new MoneyPerQuantityUnit(0.1m, "USDT"),
            new MoneyPerQuantityUnit(0.2m, "USDT"));

    private static SimulatedFillAssumptions Assumptions() =>
        new(0m, 0m, new FillSimulationAssumptions("fees", "slippage", "next bar",
            "unknown queue", "none", "stop before target", "OHLCV limitations"),
            new FillSimulationVersion("parity-fill-v1"));

    private static ReplayEnvelope BarEnvelope(string id, int minutes, decimal open,
        decimal high, decimal low, decimal close, long sequence) => new("stream-a",
        new CausalMarketEvent(new Bar(id, Instrument, Utc(minutes), Utc(minutes),
            Utc(minutes + 1), new Price(open), new Price(high), new Price(low),
            new Price(close), new Quantity(1m)), Utc(minutes + 1), Utc(minutes + 1), sequence));

    private static UtcTimestamp Utc(int minutes) => new(BaseTime.AddMinutes(minutes));

    private sealed class TamperingCalculator : IRiskMathCalculator
    {
        public RiskMathVersion Version { get; } = new("tampered-risk-v1");

        public RiskMathResult Calculate(RiskMathInput input)
        {
            RiskMathInput tampered = new(input.InstrumentId, input.Direction, input.Equity,
                input.RiskFraction, input.EntryPrice, input.ProtectiveStopPrice,
                new MoneyPerQuantityUnit(input.FeePerUnit.Amount + 1m, input.Equity.Currency),
                input.EstimatedSlippagePerUnit);
            return new RiskMathResult(Version, tampered, new Money(20m, "USDT"),
                new MoneyPerQuantityUnit(6.3m, "USDT"), new Quantity(3m),
                new Money(300m, "USDT"));
        }
    }
}
