using System.Security.Cryptography;
using System.Text;

namespace TradingBot.Core;

/// <summary>Evaluates caller-supplied Lane C rules without producing execution state.</summary>
public static class LaneCShadowEvaluator
{
    public static LaneCShadowResult Evaluate(LaneCFeatureResult features,
        LaneCFeatureRuleTable rules)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(rules);

        if (features.State != LaneCFeatureState.Ready)
        {
            List<string> featureReasons = [$"feature-not-ready:{features.State}"];
            featureReasons.AddRange(features.Reasons);
            return CreateResult(features, rules, LaneCShadowStatus.InsufficientData, [],
                Distinct(featureReasons));
        }

        if (rules.Rules.Count == 0)
        {
            return CreateResult(features, rules, LaneCShadowStatus.InsufficientData, [],
                ["rules-empty"]);
        }

        List<LaneCFeatureRule> matchedRules = [];
        HashSet<LaneCFeatureMetric> unavailableMetrics = [];
        int evaluatedRuleCount = 0;
        foreach (LaneCFeatureRule rule in rules.Rules)
        {
            bool matched = true;
            bool complete = true;
            foreach (LaneCFeatureRuleCondition condition in rule.Conditions)
            {
                decimal? actual = ReadMetric(features, condition.Metric);
                if (actual is null)
                {
                    complete = false;
                    unavailableMetrics.Add(condition.Metric);
                    break;
                }

                if (!Compare(actual.Value, condition.Operator, condition.ExpectedValue))
                {
                    matched = false;
                }
            }

            if (!complete)
            {
                continue;
            }

            evaluatedRuleCount++;
            if (matched)
            {
                matchedRules.Add(rule);
            }
        }

        if (evaluatedRuleCount == 0)
        {
            string[] missingReasons = unavailableMetrics
                .OrderBy(static metric => metric)
                .Select(static metric => $"feature-unavailable:{metric}")
                .ToArray();
            return CreateResult(features, rules, LaneCShadowStatus.InsufficientData, [],
                missingReasons.Length == 0 ? ["rules-not-evaluable"] : missingReasons);
        }

        LaneCShadowStatus status = matchedRules.Any(static rule =>
                rule.Outcome == LaneCShadowStatus.Veto)
            ? LaneCShadowStatus.Veto
            : matchedRules.Any(static rule => rule.Outcome == LaneCShadowStatus.Confirm)
                ? LaneCShadowStatus.Confirm
                : LaneCShadowStatus.Neutral;
        string[] matchedIds = matchedRules.Select(static rule => rule.RuleId)
            .OrderBy(static id => id, StringComparer.Ordinal).ToArray();
        string[] reasons = matchedIds.Length == 0
            ? ["no-rule-matched"]
            : matchedIds.Select(id => $"rule-matched:{id}").ToArray();
        return CreateResult(features, rules, status, matchedIds, reasons);
    }

    private static LaneCShadowResult CreateResult(LaneCFeatureResult features,
        LaneCFeatureRuleTable rules, LaneCShadowStatus status, IEnumerable<string> matchedIds,
        IEnumerable<string> reasons)
    {
        ImmutableValueList<string> matched = new(matchedIds);
        ImmutableValueList<string> reasonList = new(reasons);
        string json = CoreJsonSerializer.Serialize(new
        {
            AlgorithmVersion = LaneCShadowResult.CurrentAlgorithmVersion,
            features.InputIdentity,
            rules.RulesVersion,
            features.Cursor,
            Status = status,
            MatchedRuleIds = matched,
            Reasons = reasonList
        });
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        return new LaneCShadowResult(LaneCShadowResult.CurrentAlgorithmVersion, identity,
            features.InputIdentity, rules.RulesVersion, status, features.Cursor,
            matched, reasonList);
    }

    private static ImmutableValueList<string> Distinct(IEnumerable<string> reasons) =>
        new(reasons.Distinct(StringComparer.Ordinal));

    private static decimal? ReadMetric(LaneCFeatureResult features,
        LaneCFeatureMetric metric) => metric switch
        {
            LaneCFeatureMetric.Spread => features.Spread,
            LaneCFeatureMetric.TopBidDepth => features.TopBidDepth,
            LaneCFeatureMetric.TopAskDepth => features.TopAskDepth,
            LaneCFeatureMetric.BookImbalance => features.BookImbalance,
            LaneCFeatureMetric.AggressiveBuyVolume => features.AggressiveBuyVolume,
            LaneCFeatureMetric.AggressiveSellVolume => features.AggressiveSellVolume,
            LaneCFeatureMetric.AggressorImbalance => features.AggressorImbalance,
            LaneCFeatureMetric.PriceResponsePerAggressorVolume =>
                features.PriceResponsePerAggressorVolume,
            LaneCFeatureMetric.MidPriceMove => features.MidPriceMove,
            LaneCFeatureMetric.AbsorptionCandidate => features.AbsorptionCandidate switch
            {
                true => 1m,
                false => 0m,
                null => null
            },
            LaneCFeatureMetric.WallMigrationCandidateCount =>
                features.WallMigrationCandidateCount,
            _ => null
        };

    private static bool Compare(decimal actual, LaneCComparisonOperator operation,
        decimal expected) => operation switch
        {
            LaneCComparisonOperator.Equal => actual == expected,
            LaneCComparisonOperator.NotEqual => actual != expected,
            LaneCComparisonOperator.LessThan => actual < expected,
            LaneCComparisonOperator.LessThanOrEqual => actual <= expected,
            LaneCComparisonOperator.GreaterThan => actual > expected,
            LaneCComparisonOperator.GreaterThanOrEqual => actual >= expected,
            _ => false
        };
}
