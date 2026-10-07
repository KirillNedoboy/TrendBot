using System.Reflection;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class FillSimulationContractTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    private static readonly UtcTimestamp SnapshotTime =
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    [Fact]
    public void AssumptionsRequireEveryDescriptionAndRemainImmutable()
    {
        FillSimulationAssumptions assumptions = CreateAssumptions();

        Assert.Equal("unknown: fee schedule is unavailable", assumptions.Fees);
        Assert.Equal("unknown: venue slippage is unavailable", assumptions.Slippage);
        Assert.Equal("unknown: request latency is unavailable", assumptions.Latency);
        Assert.Equal("unknown: queue position is unavailable", assumptions.LiquidityAndQueue);
        Assert.Equal("unknown: partial fill behavior is unavailable", assumptions.PartialFills);
        Assert.Equal("unknown: intrabar order is unavailable", assumptions.IntrabarOrdering);
        Assert.Equal("OHLCV input cannot reconstruct spread or queue state", assumptions.DataLimitations);

        Assert.All(typeof(FillSimulationAssumptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.Null(property.SetMethod));

        Assert.Throws<ArgumentException>(() => new FillSimulationAssumptions(
            " ", assumptions.Slippage, assumptions.Latency, assumptions.LiquidityAndQueue,
            assumptions.PartialFills, assumptions.IntrabarOrdering, assumptions.DataLimitations));
        Assert.Throws<ArgumentException>(() => new FillSimulationAssumptions(
            assumptions.Fees, " ", assumptions.Latency, assumptions.LiquidityAndQueue,
            assumptions.PartialFills, assumptions.IntrabarOrdering, assumptions.DataLimitations));
        Assert.Throws<ArgumentException>(() => new FillSimulationAssumptions(
            assumptions.Fees, assumptions.Slippage, assumptions.Latency, " ",
            assumptions.PartialFills, assumptions.IntrabarOrdering, assumptions.DataLimitations));
    }

    [Fact]
    public void InputSnapshotsOnlyCausalMarketEventsForAResearchRequest()
    {
        List<CausalMarketEvent> source = [CreateCausalEvent(1)];
        FillSimulationInput input = new(
            "research-request-1",
            Instrument,
            Direction.Long,
            new Quantity(0.25m),
            SnapshotTime,
            source,
            CreateAssumptions(),
            new Price(100m));

        source.Clear();

        Assert.Equal("research-request-1", input.HypotheticalRequestId);
        Assert.Equal(Instrument, input.InstrumentId);
        Assert.Equal(Direction.Long, input.Direction);
        Assert.Equal(new Quantity(0.25m), input.RequestedQuantity);
        Assert.Equal(new Price(100m), input.ReferencePrice);
        Assert.Single(input.MarketSnapshot);
        Assert.Equal(1, input.MarketSnapshot[0].StableSequence);
        Assert.All(typeof(FillSimulationInput)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.Null(property.SetMethod));

        Assert.Throws<ArgumentException>(() => new FillSimulationInput(
            "research-request-2", Instrument, Direction.Long, new Quantity(0.25m),
            SnapshotTime, [CreateCausalEvent(2,
                new UtcTimestamp(SnapshotTime.Value.AddMinutes(1)))],
            CreateAssumptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FillSimulationInput(
            "research-request-3", Instrument, Direction.Long, new Quantity(0m),
            SnapshotTime, [CreateCausalEvent(3)], CreateAssumptions()));
    }

    [Fact]
    public void ResultContainsOnlyResearchFillsAndAFullInputSnapshot()
    {
        FillSimulationInput input = CreateInput();
        ResearchFill fill = new(
            "research-fill-1", new Quantity(0.1m), new Price(100.5m), SnapshotTime, 1);

        FillSimulationResult result = new(
            new FillSimulationVersion("fill-v1"), input, [fill]);

        Assert.Equal(input, result.Input);
        Assert.NotSame(input, result.Input);
        Assert.Equal(new FillSimulationVersion("fill-v1"), result.Version);
        Assert.Equal([fill], result.Fills);
        Assert.All(typeof(FillSimulationResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.Null(property.SetMethod));

        Assert.Throws<ArgumentException>(() => new FillSimulationResult(
            result.Version, input, [new ResearchFill(
                "research-fill-2", new Quantity(0.3m), new Price(100m), SnapshotTime, 2)]));
    }

    [Fact]
    public void FillContractsRoundTripStrictJsonAndRejectUnknownOrMissingValues()
    {
        FillSimulationInput input = CreateInput();
        FillSimulationResult original = new(
            new FillSimulationVersion("fill-v1"),
            input,
            [new ResearchFill("research-fill-1", new Quantity(0.1m),
                new Price(100.5m), SnapshotTime, 1)]);

        string json = CoreJsonSerializer.Serialize(original);
        FillSimulationResult restored = CoreJsonSerializer.Deserialize<FillSimulationResult>(json);

        Assert.Equal(original, restored);
        Assert.Contains("\"fees\":\"unknown: fee schedule is unavailable\"", json);
        Assert.Contains("\"marketSnapshot\":[", json);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<FillSimulationVersion>("{}"));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<FillSimulationAssumptions>(
            json.Replace("\"dataLimitations\":", "\"missingDataLimitations\":",
                StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<FillSimulationResult>(
            json.Replace("\"fills\":", "\"unexpectedFills\":", StringComparison.Ordinal)));
    }

    [Fact]
    public void FillSimulatorSurfaceContainsNoAuthorizationOrRiskTypes()
    {
        Type simulatorType = typeof(IFillSimulator);

        Assert.Collection(simulatorType.GetProperties(),
            property => Assert.Equal(nameof(IFillSimulator.Version), property.Name));
        Assert.Equal("Simulate,get_Version", string.Join(",",
            simulatorType.GetMethods().Select(method => method.Name)
                .Order(StringComparer.Ordinal)));
        MethodInfo simulate = simulatorType.GetMethod(nameof(IFillSimulator.Simulate))!;
        Assert.Equal(typeof(FillSimulationResult), simulate.ReturnType);
        Assert.Single(simulate.GetParameters());
        Assert.Equal(typeof(FillSimulationInput), simulate.GetParameters()[0].ParameterType);

        Type[] apiTypes =
        [typeof(IFillSimulator), typeof(FillSimulationInput), typeof(FillSimulationResult),
            typeof(FillSimulationAssumptions), typeof(FillSimulationVersion), typeof(ResearchFill)];
        string[] forbiddenNames = ["OrderIntent", "RiskDecision", "IRiskMathCalculator",
            "RiskMathInput", "RiskMathResult", "Authorize", "Execute", "Executor"];

        foreach (Type apiType in apiTypes)
        {
            string surface = string.Join("|", apiType.GetMembers()
                .Select(member => member.Name)
                .Concat(apiType.GetProperties().Select(property => property.PropertyType.FullName ?? ""))
                .Concat(apiType.GetMethods().SelectMany(method =>
                    new[] { method.ReturnType.FullName ?? "" }
                        .Concat(method.GetParameters().Select(parameter =>
                            parameter.ParameterType.FullName ?? "")))));

            Assert.DoesNotContain(forbiddenNames, name => surface.Contains(name,
                StringComparison.Ordinal));
        }
    }

    private static FillSimulationInput CreateInput()
    {
        return new FillSimulationInput(
            "research-request-1",
            Instrument,
            Direction.Long,
            new Quantity(0.25m),
            SnapshotTime,
            [CreateCausalEvent(1)],
            CreateAssumptions(),
            new Price(100m));
    }

    private static FillSimulationAssumptions CreateAssumptions()
    {
        return new FillSimulationAssumptions(
            "unknown: fee schedule is unavailable",
            "unknown: venue slippage is unavailable",
            "unknown: request latency is unavailable",
            "unknown: queue position is unavailable",
            "unknown: partial fill behavior is unavailable",
            "unknown: intrabar order is unavailable",
            "OHLCV input cannot reconstruct spread or queue state");
    }

    private static CausalMarketEvent CreateCausalEvent(long sequence,
        UtcTimestamp? eventTime = null)
    {
        UtcTimestamp timestamp = eventTime
            ?? new UtcTimestamp(SnapshotTime.Value.AddMinutes(-1));
        return new CausalMarketEvent(
            new Trade($"trade-{sequence}", Instrument, timestamp,
                new Price(100m), new Quantity(0.5m), TradeSide.Buy),
            timestamp,
            timestamp,
            sequence);
    }
}
