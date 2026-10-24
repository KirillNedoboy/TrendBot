using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneCShadowTests
{
    private static readonly string[] MatchedRuleIds = ["confirm-book", "veto-flow"];
    private static readonly CausalEventCursor Cursor = new(
        new UtcTimestamp(DateTimeOffset.Parse("2026-10-24T12:00:05Z",
            System.Globalization.CultureInfo.InvariantCulture)), 8);

    [Fact]
    public void VetoWinsWhenExplicitConfirmAndVetoRulesBothMatch()
    {
        LaneCFeatureRuleTable rules = new("lane-c-rules-v1",
        [
            Rule("confirm-book", LaneCShadowStatus.Confirm,
                LaneCFeatureMetric.BookImbalance, LaneCComparisonOperator.GreaterThan, 0.25m),
            Rule("veto-flow", LaneCShadowStatus.Veto,
                LaneCFeatureMetric.AggressorImbalance,
                LaneCComparisonOperator.LessThanOrEqual, -0.5m)
        ]);

        LaneCShadowResult result = LaneCShadowEvaluator.Evaluate(
            Features(bookImbalance: 0.5m, aggressorImbalance: -0.75m), rules);

        Assert.Equal(LaneCShadowStatus.Veto, result.Status);
        Assert.Equal(DecisionEvidenceStatus.Veto, result.EvidenceStatus);
        Assert.Equal("lane-c-shadow-v1", result.AlgorithmVersion);
        Assert.Equal("lane-c-input-1", result.InputIdentity);
        Assert.Equal("lane-c-rules-v1", result.RulesVersion);
        Assert.Equal(MatchedRuleIds, result.MatchedRuleIds);
        Assert.Contains("rule-matched:veto-flow", result.Reasons);
    }

    [Fact]
    public void NoMatchingRuleProducesNeutralShadowEvidence()
    {
        LaneCFeatureRuleTable rules = new("rules-v1",
        [
            Rule("wide-spread", LaneCShadowStatus.Veto, LaneCFeatureMetric.Spread,
                LaneCComparisonOperator.GreaterThan, 2m)
        ]);

        LaneCShadowResult result = LaneCShadowEvaluator.Evaluate(
            Features(spread: 1m), rules);

        Assert.Equal(LaneCShadowStatus.Neutral, result.Status);
        Assert.Equal(DecisionEvidenceStatus.Neutral, result.EvidenceStatus);
        Assert.Equal("no-rule-matched", Assert.Single(result.Reasons));
        Assert.Empty(result.MatchedRuleIds);
    }

    [Fact]
    public void EmptyRulesAndUnavailableInputsRemainInsufficient()
    {
        LaneCShadowResult emptyRules = LaneCShadowEvaluator.Evaluate(
            Features(), new LaneCFeatureRuleTable("rules-empty-v1", []));
        LaneCShadowResult missingMetric = LaneCShadowEvaluator.Evaluate(
            Features(priceResponse: null), new LaneCFeatureRuleTable("rules-v1",
            [Rule("response", LaneCShadowStatus.Confirm,
                LaneCFeatureMetric.PriceResponsePerAggressorVolume,
                LaneCComparisonOperator.LessThan, 1m)]));
        LaneCShadowResult stale = LaneCShadowEvaluator.Evaluate(
            Features(state: LaneCFeatureState.Stale, reason: "book-stale"),
            new LaneCFeatureRuleTable("rules-v1",
                [Rule("book", LaneCShadowStatus.Confirm,
                    LaneCFeatureMetric.BookImbalance,
                    LaneCComparisonOperator.GreaterThan, 0m)]));

        Assert.Equal(LaneCShadowStatus.InsufficientData, emptyRules.Status);
        Assert.Equal("rules-empty", Assert.Single(emptyRules.Reasons));
        Assert.Equal(LaneCShadowStatus.InsufficientData, missingMetric.Status);
        Assert.Contains("feature-unavailable:PriceResponsePerAggressorVolume",
            missingMetric.Reasons);
        Assert.Equal(LaneCShadowStatus.InsufficientData, stale.Status);
        Assert.Contains("feature-not-ready:Stale", stale.Reasons);
        Assert.Contains("book-stale", stale.Reasons);
    }

    [Fact]
    public void ShadowIdentityAndReasonsRoundTripDeterministically()
    {
        LaneCFeatureRuleTable rules = new("rules-v1",
        [
            Rule("buy-flow", LaneCShadowStatus.Confirm,
                LaneCFeatureMetric.AggressorImbalance,
                LaneCComparisonOperator.GreaterThanOrEqual, 0.5m)
        ]);
        LaneCFeatureResult features = Features(aggressorImbalance: 0.5m);
        LaneCShadowResult first = LaneCShadowEvaluator.Evaluate(features, rules);
        LaneCShadowResult second = LaneCShadowEvaluator.Evaluate(features, rules);
        string json = CoreJsonSerializer.Serialize(first);
        LaneCShadowResult restored = CoreJsonSerializer.Deserialize<LaneCShadowResult>(json);

        Assert.Equal(LaneCShadowStatus.Confirm, first.Status);
        Assert.Equal(first, second);
        Assert.Equal(first, restored);
        Assert.Equal(first.ResultIdentity, restored.ResultIdentity);
        Assert.Equal(Cursor, restored.Cursor);
    }

    private static LaneCFeatureRule Rule(string id, LaneCShadowStatus outcome,
        LaneCFeatureMetric metric, LaneCComparisonOperator comparison, decimal value) =>
        new(id, $"{id}-v1", [new LaneCFeatureRuleCondition(
            $"{id}-condition", metric, comparison, value)], outcome);

    private static LaneCFeatureResult Features(decimal? bookImbalance = 0.5m,
        decimal? aggressorImbalance = 0.5m, decimal? spread = 1m,
        decimal? priceResponse = 0.01m, LaneCFeatureState state = LaneCFeatureState.Ready,
        string? reason = null) => new(LaneCFeatureResult.CurrentAlgorithmVersion,
        "lane-c-input-1", "formula-v1", state, Cursor,
        99m, 100m, spread, 10m, 12m, bookImbalance,
        4m, 2m, aggressorImbalance, 1m, 0m, 2m, 0m,
        0.5m, priceResponse, null, 0,
        new ImmutableValueList<LaneCLevelFeature>([]),
        new ImmutableValueList<string>(reason is null ? [] : [reason]));
}
