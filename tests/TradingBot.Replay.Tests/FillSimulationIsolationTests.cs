using System.Reflection;
using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class FillSimulationIsolationTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ReplaceableFillModelChangesObservationsWithoutChangingRiskResult()
    {
        LinearPerpetualRiskMathCalculator calculator = new();
        RiskMathInput input = CreateInput();
        SimulatedRiskPlan plan = Plan(input);
        RecordingFillSimulator fillModel = new(1m);
        ReplayEnvelope[] events =
        [
            BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
            BarEnvelope("entry", 1, 101m, 102m, 100m, 101m, 2),
            BarEnvelope("future", 2, 102m, 103m, 101m, 102m, 3)
        ];

        RiskMathResult expectedRisk = calculator.Calculate(input);
        SimulatedRiskSimulationResult result = new SimulatedRiskSimulator(calculator,
            fillModel).Simulate(plan, events);

        Assert.Equal(expectedRisk, result.RiskResult);
        Assert.Equal(new Price(102m), Assert.Single(result.Fills).Price);
        Assert.Single(fillModel.Inputs);
        Assert.Equal(["signal", "entry"], fillModel.Inputs[0].MarketSnapshot
            .Select(static item => item.Payload.EventId));
        Assert.DoesNotContain(fillModel.Inputs[0].MarketSnapshot,
            static item => item.Payload.EventId == "future");
    }

    [Fact]
    public void InvalidFutureFillObservationIsRejectedByTheHarness()
    {
        RiskMathInput input = CreateInput();
        SimulatedRiskPlan plan = Plan(input);

        Assert.Throws<InvalidOperationException>(() => new SimulatedRiskSimulator(
            new LinearPerpetualRiskMathCalculator(), new InvalidFillSimulator())
            .Simulate(plan,
                [BarEnvelope("signal", 0, 100m, 101m, 99m, 100m, 1),
                    BarEnvelope("entry", 1, 101m, 102m, 100m, 101m, 2)]));
    }

    [Fact]
    public void FillModelSurfaceContainsNoRiskOrAuthorizationDependency()
    {
        Type type = typeof(OhlcvFillSimulator);
        string surface = string.Join("|", type.GetMembers(BindingFlags.Public
            | BindingFlags.Instance | BindingFlags.Static)
            .Select(static member => member.Name)
            .Concat(type.GetProperties().Select(static property =>
                property.PropertyType.FullName ?? string.Empty)));

        foreach (string forbidden in new[]
        {
            "OrderIntent", "RiskDecision", "IRiskMathCalculator", "RiskMathInput",
            "RiskMathResult", "Authorize", "Execute", "Executor"
        })
        {
            Assert.DoesNotContain(forbidden, surface, StringComparison.Ordinal);
        }
    }

    private static SimulatedRiskPlan Plan(RiskMathInput input) => new(
        "fill-isolation-plan", Instrument, Direction.Long, Utc(0), input, [], null, false,
        null, new SimulatedFillAssumptions(0m, 0m,
            new FillSimulationAssumptions("fees", "slippage", "next bar", "unknown queue",
                "none", "stop before target", "OHLCV limitations"),
            new FillSimulationVersion("test-fill-v1")),
        new Decision("fill-isolation-decision", "setup-1", DecisionOutcome.Allow,
            ["fixture"]));

    private static RiskMathInput CreateInput() => new(Instrument, Direction.Long,
        new Money(1000m, "USDT"), 0.02m, new Price(100m), new Price(95m),
        new MoneyPerQuantityUnit(0.1m, "USDT"),
        new MoneyPerQuantityUnit(0.2m, "USDT"));

    private static ReplayEnvelope BarEnvelope(string id, int minutes, decimal open,
        decimal high, decimal low, decimal close, long sequence) => new("stream-a",
        new CausalMarketEvent(new Bar(id, Instrument, Utc(minutes), Utc(minutes),
            Utc(minutes + 1), new Price(open), new Price(high), new Price(low),
            new Price(close), new Quantity(1m)), Utc(minutes + 1), Utc(minutes + 1), sequence));

    private static UtcTimestamp Utc(int minutes) => new(BaseTime.AddMinutes(minutes));

    private sealed class RecordingFillSimulator : IFillSimulator
    {
        private readonly decimal _priceAdjustment;

        public RecordingFillSimulator(decimal priceAdjustment)
        {
            _priceAdjustment = priceAdjustment;
        }

        public FillSimulationVersion Version { get; } = new("recording-fill-v1");

        public List<FillSimulationInput> Inputs { get; } = [];

        public FillSimulationResult Simulate(FillSimulationInput input)
        {
            Inputs.Add(input);
            Price reference = input.ReferencePrice
                ?? throw new InvalidOperationException("A reference price is required.");
            CausalMarketEvent latest = input.MarketSnapshot[^1];
            return new FillSimulationResult(Version, input,
                [new ResearchFill(input.HypotheticalRequestId + ":fill",
                    input.RequestedQuantity, new Price(reference.Value + _priceAdjustment),
                    input.RequestedAt, latest.StableSequence)]);
        }
    }

    private sealed class InvalidFillSimulator : IFillSimulator
    {
        public FillSimulationVersion Version { get; } = new("invalid-fill-v1");

        public FillSimulationResult Simulate(FillSimulationInput input) =>
            new(Version, input,
                [new ResearchFill(input.HypotheticalRequestId + ":future",
                    input.RequestedQuantity, input.ReferencePrice!, Utc(99), 9999)]);
    }
}
