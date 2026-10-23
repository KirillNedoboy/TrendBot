using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneBFreshnessTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void EventAndAvailabilityAgesAreIndependentAndEqualTtlIsFresh()
    {
        LaneBInput input = new(Instrument, [], [
            new LaneBFundingObservation("funding", new FundingUpdate("funding-1", Instrument,
                Utc(0), 0.01m), "rate", "periodic", "fixture", Utc(0), Utc(4), 1)
        ], "fixture");
        LaneBInputSelection selection = input.Select(Cursor(5, 1),
            new LaneBInputConfiguration("cfg", null, "rate", "periodic", false, true, false));

        LaneBFreshnessEvidence fresh = LaneBFreshnessEvaluator.EvaluateOne(
            LaneBFreshnessKind.Funding, selection, [], [], Cursor(5, 1),
            TimeSpan.FromMinutes(5));
        LaneBFreshnessEvidence stale = LaneBFreshnessEvaluator.EvaluateOne(
            LaneBFreshnessKind.Funding, selection, [], [], Cursor(5, 1),
            TimeSpan.FromMinutes(4));

        Assert.Equal(LaneBFreshnessStatus.Fresh, fresh.Status);
        Assert.Equal(TimeSpan.FromMinutes(5), fresh.EventAge);
        Assert.Equal(TimeSpan.FromMinutes(1), fresh.AvailabilityAge);
        Assert.Equal(LaneBFreshnessStatus.Stale, stale.Status);
        Assert.Contains(stale.Reasons, reason => reason.Contains("event", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingAndNonPositiveTtlStayUnknownAndCannotBecomeReady()
    {
        LaneBInput input = new(Instrument, [], [
            new LaneBFundingObservation("funding", new FundingUpdate("funding-1", Instrument,
                Utc(0), 0.01m), "rate", "periodic", "fixture", Utc(0), Utc(0), 1)
        ], "fixture");
        LaneBInputSelection selection = input.Select(Cursor(1, 1),
            new LaneBInputConfiguration("cfg", null, "rate", "periodic", false, true, false));

        LaneBFreshnessEvaluation evaluation = LaneBFreshnessEvaluator.Evaluate(selection, [], [],
            Cursor(1, 1), new LaneBFreshnessPolicy(null, TimeSpan.Zero, null, null),
            [LaneBFreshnessKind.Funding]);

        Assert.False(evaluation.IsReady);
        Assert.Equal(LaneBFreshnessStatus.Unknown, evaluation.Evidence[0].Status);
        Assert.Contains(evaluation.Evidence[0].Reasons,
            reason => reason.Contains("ttl", StringComparison.Ordinal));
    }

    [Fact]
    public void StaleFundingBlocksAnOtherwiseMatchingShadowRule()
    {
        LaneBInput input = new(Instrument, [], [
            new LaneBFundingObservation("funding", new FundingUpdate("funding-1", Instrument,
                Utc(0), 0.01m), "rate", "periodic", "fixture", Utc(0), Utc(0), 1)
        ], "fixture");
        LaneBInputConfiguration inputConfiguration = new(
            "cfg", null, "rate", "periodic", false, true, false);
        LaneBRuleTable rules = new("rules-v1", [
            new LaneBRule("rule", SetupType.BoundaryRejection, Direction.Long, "rule-v1", [
                new LaneBRuleCondition("funding", LaneBMetricKind.FundingRate,
                    LaneBComparisonOperator.GreaterThan, 0m),
                new LaneBRuleCondition("behavior", LaneBMetricKind.FailedContinuation,
                    LaneBComparisonOperator.Equal, 1m)
            ], LaneBShadowStatus.Confirm)
        ]);
        LaneBFailedContinuationObservation behavior = new(
            "behavior", "fc-1", Instrument, Utc(1), Utc(1), Utc(1), null, 2,
            "failed-continuation-v1", LaneBFailedContinuationOutcome.Observed,
            ["setup-1"], "fixture");

        LaneBShadowResult result = new LaneBEvaluator(rules).Evaluate(
            input, Cursor(5, 5), inputConfiguration, [], [behavior],
            SetupType.BoundaryRejection, Direction.Long,
            new LaneBFreshnessPolicy(null, TimeSpan.FromMinutes(4), null,
                TimeSpan.FromMinutes(10)));

        Assert.Equal(LaneBShadowStatus.InsufficientData, result.Status);
        Assert.NotNull(result.Freshness);
        Assert.Contains(result.Reasons, reason => reason.Contains("stale", StringComparison.Ordinal));
    }

    [Fact]
    public void FutureRecordsDoNotAffectPriorSelectionAndConflictsBlockDeterministically()
    {
        LaneBFundingObservation future = new("funding", new FundingUpdate("future", Instrument,
            Utc(10), 0.02m), "rate", "periodic", "fixture", Utc(10), Utc(10), 3);
        LaneBFundingObservation first = new("funding", new FundingUpdate("funding-1", Instrument,
            Utc(1), 0.01m), "rate", "periodic", "fixture", Utc(1), Utc(1), 1);
        LaneBFundingObservation conflict = new("funding", new FundingUpdate("funding-1", Instrument,
            Utc(1), 0.02m), "rate", "periodic", "fixture", Utc(1), Utc(1), 1);
        LaneBInput input = new(Instrument, [], [future, first, conflict], "fixture");
        LaneBInputConfiguration configuration = new(
            "cfg", null, "rate", "periodic", false, true, false);

        LaneBInputSelection prior = input.Select(Cursor(2, 2), configuration);
        LaneBInputSelection permuted = new LaneBInput(Instrument, [], [conflict, first, future],
            "fixture").Select(Cursor(2, 2), configuration);

        Assert.Equal(LaneBInputStatus.Conflict, prior.Status);
        Assert.Equal(prior.Status, permuted.Status);
        Assert.DoesNotContain(prior.Funding, item => item.EventId == "future");
        Assert.Contains(prior.Reasons, reason => reason.Contains("conflict", StringComparison.Ordinal));
    }

    [Fact]
    public void LiquidationQuantityOverflowRemainsUnknown()
    {
        LaneBInput input = new(Instrument, [], [
            new LaneBFundingObservation("funding", new FundingUpdate("funding-1", Instrument,
                Utc(1), 0.01m), "rate", "periodic", "fixture", Utc(1), Utc(1), 1)
        ], "fixture");
        LaneBInputConfiguration inputConfiguration = new(
            "cfg", null, "rate", "periodic", false, true, false);
        LaneBRuleTable rules = new("rules-v1", [
            new LaneBRule("rule", SetupType.BoundaryRejection, Direction.Long, "rule-v1", [
                new LaneBRuleCondition("liquidation", LaneBMetricKind.LiquidationQuantity,
                    LaneBComparisonOperator.GreaterThan, 0m)
            ], LaneBShadowStatus.Veto)
        ]);
        LaneBLiquidationObservation one = new("liquidation", "liq-1", Instrument, Utc(1),
            Utc(1), Utc(1), null, 2, TradeSide.Sell, new Quantity(decimal.MaxValue),
            new Price(1m), "fixture");
        LaneBLiquidationObservation two = new("liquidation", "liq-2", Instrument, Utc(2),
            Utc(2), Utc(2), null, 3, TradeSide.Sell, new Quantity(decimal.MaxValue),
            new Price(1m), "fixture");

        LaneBShadowResult result = new LaneBEvaluator(rules).Evaluate(
            input, Cursor(3, 3), inputConfiguration, [one, two], [],
            SetupType.BoundaryRejection, Direction.Long,
            new LaneBFreshnessPolicy(null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), null));

        Assert.Equal(LaneBShadowStatus.InsufficientData, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("overflow", StringComparison.Ordinal));
    }

    private static CausalEventCursor Cursor(int offset, long sequence) =>
        new(Utc(offset), sequence);

    private static UtcTimestamp Utc(int offset) => new(BaseTime.AddMinutes(offset));
}
