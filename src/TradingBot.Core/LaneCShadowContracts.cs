using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum LaneCShadowStatus
{
    Confirm,
    Neutral,
    Veto,
    InsufficientData
}

public enum LaneCFeatureMetric
{
    Spread,
    TopBidDepth,
    TopAskDepth,
    BookImbalance,
    AggressiveBuyVolume,
    AggressiveSellVolume,
    AggressorImbalance,
    PriceResponsePerAggressorVolume,
    MidPriceMove,
    AbsorptionCandidate,
    WallMigrationCandidateCount
}

public enum LaneCComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual
}

/// <summary>One caller-supplied feature comparison in a Lane C shadow rule.</summary>
public sealed record LaneCFeatureRuleCondition
{
    public string ConditionId { get; }

    public LaneCFeatureMetric Metric { get; }

    public LaneCComparisonOperator Operator { get; }

    public decimal ExpectedValue { get; }

    [JsonConstructor]
    public LaneCFeatureRuleCondition(string conditionId, LaneCFeatureMetric metric,
        LaneCComparisonOperator @operator, decimal expectedValue)
    {
        ContractGuard.RequireText(conditionId, nameof(conditionId));
        ContractGuard.RequireDefined(metric, nameof(metric));
        ContractGuard.RequireDefined(@operator, nameof(@operator));
        ConditionId = conditionId;
        Metric = metric;
        Operator = @operator;
        ExpectedValue = expectedValue;
    }
}

/// <summary>An explicit versioned Lane C confirmation, neutral, or veto rule.</summary>
public sealed record LaneCFeatureRule
{
    public string RuleId { get; }

    public string RuleVersion { get; }

    public ImmutableValueList<LaneCFeatureRuleCondition> Conditions { get; }

    public LaneCShadowStatus Outcome { get; }

    [JsonConstructor]
    public LaneCFeatureRule(string ruleId, string ruleVersion,
        ImmutableValueList<LaneCFeatureRuleCondition> conditions,
        LaneCShadowStatus outcome)
        : this(ruleId, ruleVersion,
            (IEnumerable<LaneCFeatureRuleCondition>)conditions, outcome)
    {
    }

    public LaneCFeatureRule(string ruleId, string ruleVersion,
        IEnumerable<LaneCFeatureRuleCondition> conditions, LaneCShadowStatus outcome)
    {
        ContractGuard.RequireText(ruleId, nameof(ruleId));
        ContractGuard.RequireText(ruleVersion, nameof(ruleVersion));
        ArgumentNullException.ThrowIfNull(conditions);
        ContractGuard.RequireDefined(outcome, nameof(outcome));
        if (outcome == LaneCShadowStatus.InsufficientData)
        {
            throw new ArgumentException("Insufficient data is a feature status, not a rule outcome.",
                nameof(outcome));
        }

        LaneCFeatureRuleCondition[] snapshot = conditions.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(static condition => condition is null)
            || snapshot.Select(static condition => condition.ConditionId).Distinct(
                StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "A Lane C rule requires unique, non-null conditions.", nameof(conditions));
        }

        RuleId = ruleId;
        RuleVersion = ruleVersion;
        Conditions = new ImmutableValueList<LaneCFeatureRuleCondition>(snapshot);
        Outcome = outcome;
    }
}

/// <summary>Immutable explicit rule table; an empty table evaluates as unavailable.</summary>
public sealed record LaneCFeatureRuleTable
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string RulesVersion { get; }

    public ImmutableValueList<LaneCFeatureRule> Rules { get; }

    [JsonConstructor]
    public LaneCFeatureRuleTable(int formatVersion, string rulesVersion,
        ImmutableValueList<LaneCFeatureRule> rules)
        : this(formatVersion, rulesVersion, (IEnumerable<LaneCFeatureRule>)rules)
    {
    }

    public LaneCFeatureRuleTable(string rulesVersion, IEnumerable<LaneCFeatureRule> rules)
        : this(CurrentFormatVersion, rulesVersion, rules)
    {
    }

    public LaneCFeatureRuleTable(int formatVersion, string rulesVersion,
        IEnumerable<LaneCFeatureRule> rules)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ArgumentNullException.ThrowIfNull(rules);
        LaneCFeatureRule[] snapshot = rules.ToArray();
        if (snapshot.Any(static rule => rule is null)
            || snapshot.Select(static rule => rule.RuleId).Distinct(
                StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Lane C rule IDs must be unique.", nameof(rules));
        }

        FormatVersion = formatVersion;
        RulesVersion = rulesVersion;
        Rules = new ImmutableValueList<LaneCFeatureRule>(snapshot);
    }
}

/// <summary>Versioned shadow evaluation with a causal trace and stable identity.</summary>
public sealed record LaneCShadowResult
{
    public const string CurrentAlgorithmVersion = "lane-c-shadow-v1";

    public string AlgorithmVersion { get; }

    public string ResultIdentity { get; }

    public string InputIdentity { get; }

    public string RulesVersion { get; }

    public LaneCShadowStatus Status { get; }

    public DecisionEvidenceStatus EvidenceStatus => Status switch
    {
        LaneCShadowStatus.Confirm => DecisionEvidenceStatus.Confirm,
        LaneCShadowStatus.Neutral => DecisionEvidenceStatus.Neutral,
        LaneCShadowStatus.Veto => DecisionEvidenceStatus.Veto,
        LaneCShadowStatus.InsufficientData => DecisionEvidenceStatus.Unknown,
        _ => DecisionEvidenceStatus.Unknown
    };

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<string> MatchedRuleIds { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public LaneCShadowResult(string algorithmVersion, string resultIdentity,
        string inputIdentity, string rulesVersion, LaneCShadowStatus status,
        CausalEventCursor cursor, ImmutableValueList<string> matchedRuleIds,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireText(algorithmVersion, nameof(algorithmVersion));
        ContractGuard.RequireText(resultIdentity, nameof(resultIdentity));
        ContractGuard.RequireText(inputIdentity, nameof(inputIdentity));
        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireDefined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(matchedRuleIds);
        ArgumentNullException.ThrowIfNull(reasons);
        if (matchedRuleIds.Any(string.IsNullOrWhiteSpace)
            || reasons.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Lane C shadow IDs and reasons must be non-empty.");
        }

        AlgorithmVersion = algorithmVersion;
        ResultIdentity = resultIdentity;
        InputIdentity = inputIdentity;
        RulesVersion = rulesVersion;
        Status = status;
        Cursor = cursor;
        MatchedRuleIds = new ImmutableValueList<string>(matchedRuleIds);
        Reasons = new ImmutableValueList<string>(reasons);
    }
}

/// <summary>Reason and rule provenance attached to Lane C decision evidence.</summary>
public sealed record LaneCDecisionEvidenceMetadata
{
    public string ShadowResultIdentity { get; }

    public string InputIdentity { get; }

    public string RulesVersion { get; }

    public LaneCShadowStatus Status { get; }

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<string> MatchedRuleIds { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public LaneCDecisionEvidenceMetadata(string shadowResultIdentity, string inputIdentity,
        string rulesVersion, LaneCShadowStatus status, CausalEventCursor cursor,
        ImmutableValueList<string> matchedRuleIds, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireText(shadowResultIdentity, nameof(shadowResultIdentity));
        ContractGuard.RequireText(inputIdentity, nameof(inputIdentity));
        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireDefined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(matchedRuleIds);
        ArgumentNullException.ThrowIfNull(reasons);
        ShadowResultIdentity = shadowResultIdentity;
        InputIdentity = inputIdentity;
        RulesVersion = rulesVersion;
        Status = status;
        Cursor = cursor;
        MatchedRuleIds = new ImmutableValueList<string>(matchedRuleIds);
        Reasons = new ImmutableValueList<string>(reasons);
    }

    public static LaneCDecisionEvidenceMetadata Create(LaneCShadowResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new LaneCDecisionEvidenceMetadata(result.ResultIdentity, result.InputIdentity,
            result.RulesVersion, result.Status, result.Cursor, result.MatchedRuleIds,
            result.Reasons);
    }
}
