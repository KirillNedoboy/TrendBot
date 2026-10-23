using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneBShadowTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void LiquidationAndFailedContinuationRetainCausalFieldsAndRoundTrip()
    {
        LaneBLiquidationObservation liquidation = new(
            "liquidation-source", "liq-1", Instrument, Utc(1), Utc(2), Utc(3), Utc(0), 4,
            TradeSide.Sell, new Quantity(2.5m), new Price(101.25m), "fixture-provenance");
        LaneBFailedContinuationObservation failedContinuation = new(
            "behavior-source", "fc-1", Instrument, Utc(2), Utc(3), Utc(4), Utc(1), 5,
            "failed-continuation-v1", LaneBFailedContinuationOutcome.Observed,
            ["setup-1", "bar-2"], "fixture-provenance");

        Assert.Equal(liquidation, CoreJsonSerializer.Deserialize<LaneBLiquidationObservation>(
            CoreJsonSerializer.Serialize(liquidation)));
        Assert.Equal(failedContinuation,
            CoreJsonSerializer.Deserialize<LaneBFailedContinuationObservation>(
                CoreJsonSerializer.Serialize(failedContinuation)));
        Assert.Equal(TradeSide.Sell, liquidation.Side);
        Assert.Equal(2.5m, liquidation.Quantity.Value);
        Assert.Equal(101.25m, liquidation.Price.Value);
        Assert.Equal(["setup-1", "bar-2"], failedContinuation.CausalReferences);
        Assert.Equal(Utc(0), liquidation.ExchangeTime);
    }

    [Fact]
    public void UnknownMethodAndMissingCoverageRemainInsufficientData()
    {
        LaneBInput input = InputWithFunding();
        LaneBInputConfiguration inputConfiguration = new(
            "input-v1", null, "rate", "periodic", false, true, false);
        LaneBRuleTable rules = new("rules-v1", [
            new LaneBRule("rule-1", SetupType.BoundaryRejection, Direction.Long,
                "rule-v1", [
                    new LaneBRuleCondition("funding", LaneBMetricKind.FundingRate,
                        LaneBComparisonOperator.GreaterThan, 0m),
                    new LaneBRuleCondition("failed-continuation",
                        LaneBMetricKind.FailedContinuation, LaneBComparisonOperator.Exists,
                        null)
                ], LaneBShadowStatus.Confirm)
        ]);
        LaneBFailedContinuationObservation unknownMethod = new(
            "behavior", "fc-1", Instrument, Utc(1), Utc(1), Utc(1), null, 2,
            null, LaneBFailedContinuationOutcome.Observed, ["setup-1"], "fixture");

        LaneBShadowResult unknown = new LaneBEvaluator(rules).Evaluate(
            input, Cursor(2, 2), inputConfiguration, [], [unknownMethod],
            SetupType.BoundaryRejection, Direction.Long);
        LaneBShadowResult missingCoverage = new LaneBEvaluator(rules).Evaluate(
            input, Cursor(2, 2), inputConfiguration, [], [],
            SetupType.BoundaryRejection, Direction.Long);

        Assert.Equal(LaneBShadowStatus.InsufficientData, unknown.Status);
        Assert.Contains(unknown.Reasons, reason => reason.Contains("method", StringComparison.Ordinal));
        Assert.Equal(LaneBShadowStatus.InsufficientData, missingCoverage.Status);
        Assert.Contains(missingCoverage.Reasons,
            reason => reason.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenInterestAloneCannotConfirmAndExplicitRuleCanConfirmWithObservedBehavior()
    {
        LaneBInput input = InputWithFunding();
        LaneBInputConfiguration inputConfiguration = new(
            "input-v1", null, "rate", "periodic", false, true, false);
        LaneBRuleTable oiOnlyRules = new("rules-v1", [
            new LaneBRule("oi-only", SetupType.BoundaryRejection, Direction.Long,
                "rule-v1", [
                    new LaneBRuleCondition("oi", LaneBMetricKind.OpenInterestDeltaAbsolute,
                        LaneBComparisonOperator.GreaterThan, 0m)
                ], LaneBShadowStatus.Confirm)
        ]);
        LaneBRuleTable explicitRules = new("rules-v2", [
            new LaneBRule("explicit", SetupType.BoundaryRejection, Direction.Long,
                "rule-v2", [
                    new LaneBRuleCondition("funding", LaneBMetricKind.FundingRate,
                        LaneBComparisonOperator.GreaterThan, 0m),
                    new LaneBRuleCondition("failed-continuation",
                        LaneBMetricKind.FailedContinuation, LaneBComparisonOperator.Equal, 1m)
                ], LaneBShadowStatus.Confirm)
        ]);
        LaneBFailedContinuationObservation observed = new(
            "behavior", "fc-1", Instrument, Utc(1), Utc(1), Utc(1), null, 2,
            "failed-continuation-v1", LaneBFailedContinuationOutcome.Observed,
            ["setup-1"], "fixture");

        LaneBShadowResult oiOnly = new LaneBEvaluator(oiOnlyRules).Evaluate(
            input, Cursor(2, 2), inputConfiguration, [], [observed],
            SetupType.BoundaryRejection, Direction.Long);
        LaneBShadowResult explicitResult = new LaneBEvaluator(explicitRules).Evaluate(
            input, Cursor(2, 2), inputConfiguration, [], [observed],
            SetupType.BoundaryRejection, Direction.Long);

        Assert.Equal(LaneBShadowStatus.InsufficientData, oiOnly.Status);
        Assert.Contains(oiOnly.Reasons, reason => reason.Contains("OI", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LaneBShadowStatus.Confirm, explicitResult.Status);
        Assert.Equal("explicit", explicitResult.RuleId);
        Assert.Contains("fc-1", explicitResult.UsedInputIds);
        LaneBShadowResult restored = CoreJsonSerializer.Deserialize<LaneBShadowResult>(
            CoreJsonSerializer.Serialize(explicitResult));
        Assert.Equal(explicitResult, restored);
    }

    [Fact]
    public void RuleTableAndResultExposeNoExecutionAuthorizationSurface()
    {
        string[] forbidden = ["OrderIntent", "RiskDecision", "Authorization", "Executor"];
        foreach (string typeName in new[] { nameof(LaneBEvaluator), nameof(LaneBShadowResult),
                     nameof(LaneBRuleTable), nameof(LaneBRule) })
        {
            Type type = typeof(LaneBInput).Assembly.GetType("TradingBot.Core." + typeName)!;
            Assert.DoesNotContain(type.GetProperties(), property => forbidden.Any(name =>
                property.PropertyType.Name.Contains(name, StringComparison.Ordinal)));
        }
    }

    private static LaneBInput InputWithFunding() => new(Instrument, [], [
        new LaneBFundingObservation("funding", new FundingUpdate("funding-1", Instrument,
            Utc(1), 0.01m), "rate", "periodic", "fixture", Utc(1), Utc(1), 1)
    ], "fixture");

    private static CausalEventCursor Cursor(int offset, long sequence) =>
        new(Utc(offset), sequence);

    private static UtcTimestamp Utc(int offset) => new(BaseTime.AddMinutes(offset));
}
