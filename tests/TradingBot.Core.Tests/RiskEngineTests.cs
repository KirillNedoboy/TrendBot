using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class RiskEngineTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly PortfolioId Portfolio = new("portfolio-a");

    [Fact]
    public void LimitExceededBlocksTheWholeSharedMathResultWithoutReducingQuantity()
    {
        RiskEvaluationInput input = CreateInput(maxPerTradeRisk: new Money(9m, "USDT"));

        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(input, new Money(9m, "USDT")),
            new LinearPerpetualRiskMathCalculator());

        Assert.Equal(RiskDecisionOutcome.Blocked, result.RiskDecision.Outcome);
        Assert.Equal(1m, result.RiskMathResult!.RawQuantity.Value);
        Assert.Equal(10m, result.RiskMathResult.RiskBudget.Amount);
        Assert.False(result.LimitsPassed);
        Assert.Contains(RiskReasonCodes.PerTradeRiskExceeded, result.Reasons);
    }

    [Fact]
    public void LimitBoundaryIsPermittedWhileMissingProtectionStillPreventsApproval()
    {
        RiskEvaluationInput input = CreateInput(maxPerTradeRisk: new Money(10m, "USDT"));

        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(input), new LinearPerpetualRiskMathCalculator());

        Assert.True(result.LimitsPassed);
        Assert.Equal(RiskDecisionOutcome.Blocked, result.RiskDecision.Outcome);
        Assert.False(result.IsApproved);
        Assert.Contains(RiskReasonCodes.ProtectiveEvidenceMissing, result.Reasons);
    }

    [Fact]
    public void NonAllowDecisionCannotReachIndependentRiskApproval()
    {
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: true);
        RiskEvaluationInput input = CreateInput(projection: projection);

        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(input), new LinearPerpetualRiskMathCalculator());

        Assert.Equal(RiskDecisionOutcome.Blocked, result.RiskDecision.Outcome);
        Assert.Contains(RiskReasonCodes.DecisionOutcomeNotAllow, result.Reasons);
        Assert.False(result.IsApproved);
    }

    [Fact]
    public void DailyLossUsesCallerPeriodAndCandidateRiskWithoutLocalPnlCalculation()
    {
        RiskEvaluationInput input = CreateInput(accumulatedDailyLoss: new Money(1m, "USDT"));

        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, CreateConfiguration(input, dailyLoss: new Money(10m, "USDT")),
            new LinearPerpetualRiskMathCalculator());

        Assert.False(result.LimitsPassed);
        Assert.Contains(RiskReasonCodes.DailyRiskExceeded, result.Reasons);
        Assert.Equal(10m, result.RiskMathResult!.RiskBudget.Amount);
    }

    [Theory]
    [InlineData("per-trade")]
    [InlineData("daily")]
    [InlineData("portfolio")]
    [InlineData("notional")]
    [InlineData("margin")]
    [InlineData("slots")]
    public void EveryKnownCapacityLimitBlocksWithoutReducingTheCandidate(string limit)
    {
        RiskEvaluationInput input = limit switch
        {
            "daily" => CreateInput(accumulatedDailyLoss: new Money(1m, "USDT")),
            "portfolio" => CreateInput(),
            "notional" => CreateInput(),
            "margin" => CreateInput(candidateMargin: new Money(1m, "USDT")),
            "slots" => CreateInput(candidatePositionSlots: 1),
            _ => CreateInput()
        };
        RiskEvaluationConfiguration configuration = limit switch
        {
            "daily" => CreateConfiguration(input, dailyLoss: new Money(10m, "USDT")),
            "portfolio" => CreateConfiguration(input,
                portfolioLimits: new PortfolioLimits(Portfolio, new Money(9m, "USDT"),
                    new Money(10_000m, "USDT"), 2)),
            "notional" => CreateConfiguration(input,
                portfolioLimits: new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
                    new Money(99m, "USDT"), 2)),
            "margin" => CreateConfiguration(input, maxMargin: new Money(0m, "USDT")),
            "slots" => CreateConfiguration(input,
                portfolioLimits: new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
                    new Money(10_000m, "USDT"), 0)),
            _ => CreateConfiguration(input, maxPerTradeRisk: new Money(9m, "USDT"))
        };

        RiskEvaluationResult result = RiskEvaluationEngine.Evaluate(
            input, configuration, new LinearPerpetualRiskMathCalculator());

        Assert.False(result.LimitsPassed);
        Assert.Equal(1m, result.RiskMathResult!.RawQuantity.Value);
        Assert.NotEqual(RiskDecisionOutcome.Approved, result.RiskDecision.Outcome);
    }

    [Fact]
    public void CalculatorVersionInputAndExceptionFailuresRemainBlocked()
    {
        RiskEvaluationInput input = CreateInput();
        RiskEvaluationConfiguration configuration = CreateConfiguration(input);

        RiskEvaluationResult wrongVersion = RiskEvaluationEngine.Evaluate(input, configuration,
            new WrongVersionCalculator());
        Assert.Contains(RiskReasonCodes.RiskMathVersionMismatch, wrongVersion.Reasons);

        RiskEvaluationResult wrongInput = RiskEvaluationEngine.Evaluate(input, configuration,
            new WrongInputCalculator());
        Assert.Contains(RiskReasonCodes.RiskMathInputMismatch, wrongInput.Reasons);

        RiskEvaluationResult failure = RiskEvaluationEngine.Evaluate(input, configuration,
            new ThrowingCalculator());
        Assert.Contains(RiskReasonCodes.RiskMathCalculationFailed, failure.Reasons);
    }

    private static RiskEvaluationInput CreateInput(
        Money? maxPerTradeRisk = null,
        DecisionProjectionSnapshot? projection = null,
        Money? accumulatedDailyLoss = null,
        Money? candidateMargin = null,
        int? candidatePositionSlots = null,
        Direction direction = Direction.Long)
    {
        projection ??= DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow, blocked: false);
        CausalEventCursor cursor = projection.CausalCursor;
        decimal stop = direction == Direction.Long ? 90m : 110m;
        RiskMathInput mathInput = new(Instrument, direction,
            new Money(1_000m, "USDT"), 0.01m, new Price(100m), new Price(stop),
            new MoneyPerQuantityUnit(0m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        PortfolioLimits portfolioLimits = new(Portfolio,
            new Money(100m, "USDT"), new Money(10_000m, "USDT"), 2);
        return new RiskEvaluationInput(
            "risk-evaluation-a", projection, mathInput, Portfolio, cursor,
            new RiskPortfolioUsage(Portfolio, new Money(0m, "USDT"),
                new Money(0m, "USDT"), new Money(0m, "USDT"), 0, "usage-source",
                "usage-v1", cursor, "fixture"),
            accumulatedDailyLoss ?? new Money(0m, "USDT"), "period-2026-01-01",
            candidateMargin ?? new Money(10m, "USDT"), candidatePositionSlots ?? 1,
            RiskHealthEvidence.Healthy("account-source", "account-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("portfolio-source", "portfolio-v1", cursor, "fixture"),
            RiskHealthEvidence.Healthy("market-source", "market-v1", cursor, "fixture"),
            RiskKillSwitchEvidence.Clear("kill-source", "kill-v1", cursor, "fixture"),
            "fixture", protection: null, cooldown: null);
    }

    private static RiskEvaluationConfiguration CreateConfiguration(RiskEvaluationInput input,
        Money? maxPerTradeRisk = null, Money? dailyLoss = null, Money? maxMargin = null,
        PortfolioLimits? portfolioLimits = null) => new(
        RiskEvaluationVersion.Current,
        new RiskMathVersion("linear-perpetual-risk-v1"),
        new RiskLimitConfiguration(maxPerTradeRisk ?? new Money(10m, "USDT"),
            dailyLoss ?? new Money(100m, "USDT"), maxMargin ?? new Money(100m, "USDT"),
            "period-2026-01-01"),
        portfolioLimits ?? new PortfolioLimits(Portfolio, new Money(100m, "USDT"),
            new Money(10_000m, "USDT"), 2), "fixture");

    private sealed class WrongVersionCalculator : IRiskMathCalculator
    {
        public RiskMathVersion Version { get; } = new("wrong-risk-v1");

        public RiskMathResult Calculate(RiskMathInput input) => throw new InvalidOperationException();
    }

    private sealed class WrongInputCalculator : IRiskMathCalculator
    {
        public RiskMathVersion Version { get; } = new("linear-perpetual-risk-v1");

        public RiskMathResult Calculate(RiskMathInput input)
        {
            RiskMathInput altered = new(input.InstrumentId, input.Direction, input.Equity,
                input.RiskFraction, input.EntryPrice,
                new Price(input.Direction == Direction.Long ? 89m : 111m), input.FeePerUnit,
                input.EstimatedSlippagePerUnit);
            return new RiskMathResult(Version, altered, new Money(10m, "USDT"),
                new MoneyPerQuantityUnit(10m, "USDT"), new Quantity(1m),
                new Money(100m, "USDT"));
        }
    }

    private sealed class ThrowingCalculator : IRiskMathCalculator
    {
        public RiskMathVersion Version { get; } = new("linear-perpetual-risk-v1");

        public RiskMathResult Calculate(RiskMathInput input) => throw new InvalidOperationException(
            "fixture failure");
    }
}
