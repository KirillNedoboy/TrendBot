using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LinearPerpetualRiskMathCalculatorTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void LongAndShortVectorsUseTheSameVersionedFormula()
    {
        LinearPerpetualRiskMathCalculator calculator = new();

        RiskMathResult longResult = calculator.Calculate(CreateInput(Direction.Long));
        RiskMathResult shortResult = calculator.Calculate(CreateInput(Direction.Short));

        Assert.Equal(new RiskMathVersion("linear-perpetual-risk-v1"), calculator.Version);
        Assert.Equal(calculator.Version, longResult.Version);
        Assert.Equal(calculator.Version, shortResult.Version);
        Assert.Equal(20m, longResult.RiskBudget.Amount);
        Assert.Equal(5.3m, longResult.LossPerUnit.Amount);
        Assert.Equal(20m, shortResult.RiskBudget.Amount);
        Assert.Equal(5.3m, shortResult.LossPerUnit.Amount);
        Assert.Equal(20m / 5.3m, longResult.RawQuantity.Value);
        Assert.Equal(20m / 5.3m, shortResult.RawQuantity.Value);
        Assert.Equal(100m * (20m / 5.3m), longResult.NotionalExposure.Amount);
        Assert.Equal(longResult.RawQuantity, shortResult.RawQuantity);
    }

    [Fact]
    public void CalculationPreservesExactDecimalValuesAndStrictJsonRoundTrip()
    {
        LinearPerpetualRiskMathCalculator calculator = new();
        RiskMathInput input = new(Instrument, Direction.Long,
            new Money(1234.5678901234567890123456789m, "USDT"),
            0.01234567890123456789012345678m, new Price(101.2345678901234567890123456789m),
            new Price(99.8765432109876543210987654321m),
            new MoneyPerQuantityUnit(0.0012345678901234567890123456789m, "USDT"),
            new MoneyPerQuantityUnit(0.0009876543210987654321098765432m, "USDT"));

        RiskMathResult result = calculator.Calculate(input);
        string json = CoreJsonSerializer.Serialize(result);
        RiskMathResult restored = CoreJsonSerializer.Deserialize<RiskMathResult>(json);

        Assert.Equal(result, restored);
        Assert.Contains("linear-perpetual-risk-v1", json, StringComparison.Ordinal);
        Assert.Contains("0.0123456789012345678901234568", json, StringComparison.Ordinal);
    }

    [Fact]
    public void OverflowAndZeroUnderflowAreRejectedWithoutRounding()
    {
        LinearPerpetualRiskMathCalculator calculator = new();

        RiskMathInput overflowingBudget = new(Instrument, Direction.Long,
            new Money(decimal.MaxValue, "USDT"), 1m, new Price(decimal.MaxValue),
            new Price(1m), new MoneyPerQuantityUnit(2m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        Assert.Throws<OverflowException>(() => calculator.Calculate(overflowingBudget));

        RiskMathInput underflowingQuantity = new(Instrument, Direction.Long,
            new Money(0.0000000000000000000000000001m, "USDT"), 0.0000000000000000000000000001m,
            new Price(100m), new Price(99m), new MoneyPerQuantityUnit(1m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        Assert.Throws<ArithmeticException>(() => calculator.Calculate(underflowingQuantity));
    }

    [Fact]
    public void CalculatorUsesTheCompleteInputAndDoesNotAcceptMismatchedCurrenciesOrStops()
    {
        LinearPerpetualRiskMathCalculator calculator = new();

        Assert.Throws<ArgumentException>(() => new RiskMathInput(Instrument, Direction.Long,
            new Money(1000m, "USDT"), 0.01m, new Price(100m), new Price(90m),
            new MoneyPerQuantityUnit(1m, "USD"), new MoneyPerQuantityUnit(0m, "USDT")));
        Assert.Throws<ArgumentException>(() => new RiskMathInput(Instrument, Direction.Long,
            new Money(1000m, "USDT"), 0.01m, new Price(100m), new Price(110m),
            new MoneyPerQuantityUnit(1m, "USDT"), new MoneyPerQuantityUnit(0m, "USDT")));

        RiskMathInput input = CreateInput(Direction.Long);
        RiskMathResult result = calculator.Calculate(input);
        Assert.Equal(input, result.Input);
        Assert.Equal(input.Equity.Currency, result.NotionalExposure.Currency);
    }

    private static RiskMathInput CreateInput(Direction direction) =>
        new(Instrument, direction, new Money(1000m, "USDT"), 0.02m,
            new Price(100m), new Price(direction == Direction.Long ? 95m : 105m),
            new MoneyPerQuantityUnit(0.1m, "USDT"),
            new MoneyPerQuantityUnit(0.2m, "USDT"));
}
