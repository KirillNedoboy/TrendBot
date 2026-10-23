namespace TradingBot.Core;

/// <summary>
/// Calculates deterministic linear-perpetual position sizing from the shared
/// risk-math contract. The result is raw and unquantized; venue constraints are
/// applied by a later caller when that metadata is available.
/// </summary>
public sealed class LinearPerpetualRiskMathCalculator : IRiskMathCalculator
{
    /// <summary>Stable identifier for this formula and its decimal semantics.</summary>
    public static RiskMathVersion CurrentVersion { get; } =
        new("linear-perpetual-risk-v1");

    /// <inheritdoc />
    public RiskMathVersion Version => CurrentVersion;

    /// <inheritdoc />
    public RiskMathResult Calculate(RiskMathInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        decimal riskBudget = checked(input.Equity.Amount * input.RiskFraction);
        if (riskBudget <= 0m)
        {
            throw new ArithmeticException("Risk budget must remain positive at decimal precision.");
        }

        decimal priceDistance = checked(Math.Abs(
            input.EntryPrice.Value - input.ProtectiveStopPrice.Value));
        decimal lossPerUnit = checked(priceDistance
            + input.FeePerUnit.Amount
            + input.EstimatedSlippagePerUnit.Amount);
        if (lossPerUnit <= 0m)
        {
            throw new ArithmeticException("Loss per unit must remain positive at decimal precision.");
        }

        decimal rawQuantity = checked(riskBudget / lossPerUnit);
        if (rawQuantity <= 0m)
        {
            throw new ArithmeticException("Raw quantity must remain positive at decimal precision.");
        }

        decimal notionalExposure = checked(input.EntryPrice.Value * rawQuantity);
        if (notionalExposure <= 0m)
        {
            throw new ArithmeticException(
                "Notional exposure must remain positive at decimal precision.");
        }

        return new RiskMathResult(CurrentVersion, input,
            new Money(riskBudget, input.Equity.Currency),
            new MoneyPerQuantityUnit(lossPerUnit, input.Equity.Currency),
            new Quantity(rawQuantity),
            new Money(notionalExposure, input.Equity.Currency));
    }
}
