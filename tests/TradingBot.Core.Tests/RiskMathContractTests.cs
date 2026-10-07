using System.Reflection;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class RiskMathContractTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void RiskMathValuesPreserveExactDecimalValuesAndStructuralEquality()
    {
        RiskMathVersion firstVersion = new("risk-v1");
        RiskMathVersion equalVersion = new("risk-v1");
        MoneyPerQuantityUnit firstCost = new(1.2300m, "USDT");
        MoneyPerQuantityUnit equalCost = new(1.23m, "USDT");

        Assert.Equal(firstVersion, equalVersion);
        Assert.Equal(firstVersion.GetHashCode(), equalVersion.GetHashCode());
        Assert.Equal(equalCost, firstCost);
        Assert.Equal(1.2300m, firstCost.Amount);
        Assert.NotEqual(new RiskMathVersion("risk-v2"), firstVersion);
        Assert.NotEqual(new MoneyPerQuantityUnit(1.23m, "usdt"), firstCost);
    }

    [Fact]
    public void RiskMathInputRequiresPositiveEquityBoundedFractionAndDirectionallyValidStop()
    {
        RiskMathInput validLong = CreateInput(Direction.Long);
        RiskMathInput validShort = CreateInput(Direction.Short);

        Assert.Equal(Direction.Long, validLong.Direction);
        Assert.Equal(Direction.Short, validShort.Direction);
        Assert.Equal(0.01234567890123456789012345678m, validLong.RiskFraction);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RiskMathInput(Instrument, Direction.Long, new Money(0m, "USDT"),
                0.01m, new Price(100m), new Price(90m),
                new MoneyPerQuantityUnit(1m, "USDT"), new MoneyPerQuantityUnit(1m, "USDT")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RiskMathInput(Instrument, Direction.Long, new Money(1000m, "USDT"),
                0m, new Price(100m), new Price(90m),
                new MoneyPerQuantityUnit(1m, "USDT"), new MoneyPerQuantityUnit(1m, "USDT")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RiskMathInput(Instrument, Direction.Long, new Money(1000m, "USDT"),
                1.0000000000000000000000000001m, new Price(100m), new Price(90m),
                new MoneyPerQuantityUnit(1m, "USDT"), new MoneyPerQuantityUnit(1m, "USDT")));
        Assert.Throws<ArgumentException>(() => CreateInput(Direction.Long, stopPrice: 110m));
        Assert.Throws<ArgumentException>(() => CreateInput(Direction.Short, stopPrice: 90m));
    }

    [Fact]
    public void RiskMathInputRequiresOrdinallyMatchingCurrenciesAndNonnegativeCosts()
    {
        Assert.Throws<ArgumentException>(() =>
            new RiskMathInput(Instrument, Direction.Long, new Money(1000m, "USDT"),
                0.01m, new Price(100m), new Price(90m),
                new MoneyPerQuantityUnit(1m, "usdt"), new MoneyPerQuantityUnit(1m, "USDT")));
        Assert.Throws<ArgumentException>(() =>
            new RiskMathInput(Instrument, Direction.Long, new Money(1000m, "USDT"),
                0.01m, new Price(100m), new Price(90m),
                new MoneyPerQuantityUnit(1m, "USDT"), new MoneyPerQuantityUnit(1m, "USD")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MoneyPerQuantityUnit(-0.01m, "USDT"));
        Assert.Throws<ArgumentException>(() => new MoneyPerQuantityUnit(1m, " "));
        Assert.Throws<ArgumentException>(() => new RiskMathVersion(" "));
    }

    [Fact]
    public void RiskMathResultValidatesPositiveOutputsAndMatchingResultCurrenciesWithoutRecomputingThem()
    {
        RiskMathInput input = CreateInput(Direction.Long);
        RiskMathVersion version = new("risk-v1");
        Money riskBudget = new(17.000000000000000000000000001m, "USDT");
        MoneyPerQuantityUnit lossPerUnit = new(12.000000000000000000000000001m, "USDT");
        Quantity rawQuantity = new(0.50000000000000000000000000001m);
        Money notionalExposure = new(321.0000000000000000000000001m, "USDT");

        RiskMathResult result = new(version, input, riskBudget, lossPerUnit,
            rawQuantity, notionalExposure);

        Assert.Equal(version, result.Version);
        Assert.Equal(input, result.Input);
        Assert.Equal(riskBudget, result.RiskBudget);
        Assert.Equal(lossPerUnit, result.LossPerUnit);
        Assert.Equal(rawQuantity, result.RawQuantity);
        Assert.Equal(notionalExposure, result.NotionalExposure);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RiskMathResult(version, input,
            new Money(0m, "USDT"), lossPerUnit, rawQuantity, notionalExposure));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RiskMathResult(version, input,
            riskBudget, new MoneyPerQuantityUnit(0m, "USDT"), rawQuantity, notionalExposure));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RiskMathResult(version, input,
            riskBudget, lossPerUnit, new Quantity(0m), notionalExposure));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RiskMathResult(version, input,
            riskBudget, lossPerUnit, rawQuantity, new Money(0m, "USDT")));
        Assert.Throws<ArgumentException>(() => new RiskMathResult(version, input,
            new Money(17m, "USD"), lossPerUnit, rawQuantity, notionalExposure));
        Assert.Throws<ArgumentException>(() => new RiskMathResult(version, input,
            riskBudget, lossPerUnit, rawQuantity, new Money(321m, "USD")));
    }

    [Fact]
    public void RiskMathResultRetainsAnImmutableFullInputSnapshot()
    {
        RiskMathInput input = CreateInput(Direction.Long);
        RiskMathResult result = CreateResult(input);
        RiskMathInput expected = new(input.InstrumentId, input.Direction, input.Equity,
            input.RiskFraction, input.EntryPrice, input.ProtectiveStopPrice,
            input.FeePerUnit, input.EstimatedSlippagePerUnit);

        Assert.Equal(expected, result.Input);
        Assert.NotSame(input, result.Input);
        Assert.Equal(input.InstrumentId, result.Input.InstrumentId);
        Assert.Equal(input.Equity, result.Input.Equity);
        Assert.Equal(input.FeePerUnit, result.Input.FeePerUnit);
        Assert.All(new[] { typeof(MoneyPerQuantityUnit), typeof(RiskMathInput), typeof(RiskMathResult) },
            static type => Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => Assert.Null(property.SetMethod)));
        Assert.All(new[] { typeof(RiskMathVersion) },
            static type => Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => Assert.Null(property.SetMethod)));
    }

    [Fact]
    public void RiskMathContractsRoundTripStrictJsonAndRejectMissingOrUnknownFields()
    {
        RiskMathResult original = CreateResult(CreateInput(Direction.Long));
        string json = CoreJsonSerializer.Serialize(original);
        RiskMathResult restored = CoreJsonSerializer.Deserialize<RiskMathResult>(json);

        Assert.Equal(original, restored);
        Assert.Contains("0.0123456789012345678901234568", json);
        Assert.Contains("1.230000000000000000000000001", json);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<RiskMathVersion>("{}"));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<MoneyPerQuantityUnit>(
            "{\"amount\":1,\"currency\":\"USDT\",\"unexpected\":true}"));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<RiskMathResult>(
            json.Replace("\"riskBudget\":", "\"missingRiskBudget\":", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<RiskMathInput>(
            CoreJsonSerializer.Serialize(original.Input)
                .Replace("\"protectiveStopPrice\":", "\"missingProtectiveStopPrice\":",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public void RiskMathCalculatorExposesOnlyVersionAndPureCalculationContract()
    {
        Type calculatorType = typeof(IRiskMathCalculator);
        PropertyInfo version = calculatorType.GetProperty(nameof(IRiskMathCalculator.Version))!;
        MethodInfo calculate = calculatorType.GetMethod(nameof(IRiskMathCalculator.Calculate))!;

        Assert.Collection(calculatorType.GetProperties(),
            property => Assert.Equal(nameof(IRiskMathCalculator.Version), property.Name));
        Assert.Equal("Calculate,get_Version", string.Join(",",
            calculatorType.GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal)));
        Assert.Equal(typeof(RiskMathVersion), version.PropertyType);
        Assert.Null(version.SetMethod);
        Assert.Equal(typeof(RiskMathResult), calculate.ReturnType);
        Assert.Single(calculate.GetParameters());
        Assert.Equal(typeof(RiskMathInput), calculate.GetParameters()[0].ParameterType);
    }

    private static RiskMathInput CreateInput(Direction direction, decimal? stopPrice = null)
    {
        decimal stop = stopPrice ?? (direction == Direction.Long ? 90m : 110m);
        return new RiskMathInput(Instrument, direction,
            new Money(1000.0000000000000000000000001m, "USDT"),
            0.01234567890123456789012345678m, new Price(100m), new Price(stop),
            new MoneyPerQuantityUnit(1.230000000000000000000000001m, "USDT"),
            new MoneyPerQuantityUnit(0.45000000000000000000000000001m, "USDT"));
    }

    private static RiskMathResult CreateResult(RiskMathInput input)
    {
        return new RiskMathResult(new RiskMathVersion("risk-v1"), input,
            new Money(17.000000000000000000000000001m, "USDT"),
            new MoneyPerQuantityUnit(12.000000000000000000000000001m, "USDT"),
            new Quantity(0.50000000000000000000000000001m),
            new Money(321.0000000000000000000000001m, "USDT"));
    }
}
