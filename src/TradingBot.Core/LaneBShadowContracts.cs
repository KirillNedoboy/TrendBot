using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Common causal fields exposed by standalone Lane B evidence.</summary>
public interface ILaneBCausalObservation
{
    string SourceId { get; }

    string EventId { get; }

    InstrumentId InstrumentId { get; }

    UtcTimestamp EventTime { get; }

    UtcTimestamp ReceiveTime { get; }

    UtcTimestamp AvailabilityTime { get; }

    UtcTimestamp? ExchangeTime { get; }

    long StableSequence { get; }

    string Provenance { get; }

    LaneBObservationIdentity Identity { get; }

    bool IsEligible(CausalEventCursor cursor);
}

/// <summary>Observed side of one liquidation event.</summary>
public enum LaneBLiquidationSide
{
    Buy,
    Sell
}

/// <summary>Standalone liquidation observation; it is not a MarketEvent.</summary>
public sealed record LaneBLiquidationObservation : ILaneBCausalObservation
{
    public string SourceId { get; }

    [JsonIgnore]
    public string SourceIdentity => SourceId;

    public string EventId { get; }

    public InstrumentId InstrumentId { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public UtcTimestamp? ExchangeTime { get; }

    public long StableSequence { get; }

    public TradeSide Side { get; }

    [JsonIgnore]
    public LaneBLiquidationSide LiquidationSide => (LaneBLiquidationSide)Side;

    public Quantity Quantity { get; }

    public Price Price { get; }

    public string Provenance { get; }

    [JsonIgnore]
    public LaneBObservationIdentity Identity => new(SourceId, InstrumentId, EventId,
        StableSequence);

    [JsonConstructor]
    public LaneBLiquidationObservation(string sourceId, string eventId,
        InstrumentId instrumentId, UtcTimestamp eventTime, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, UtcTimestamp? exchangeTime, long stableSequence,
        TradeSide side, Quantity quantity, Price price, string provenance)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability time cannot precede receive time.",
                nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Lane B stable sequence cannot be negative.");
        }

        ContractGuard.RequireDefined(side, nameof(side));
        ArgumentNullException.ThrowIfNull(quantity);
        ArgumentNullException.ThrowIfNull(price);
        if (quantity.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity.Value,
                "A liquidation quantity must be greater than zero.");
        }

        ContractGuard.RequireText(provenance, nameof(provenance));
        SourceId = sourceId;
        EventId = eventId;
        InstrumentId = instrumentId;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        ExchangeTime = exchangeTime;
        StableSequence = stableSequence;
        Side = side;
        Quantity = quantity;
        Price = price;
        Provenance = provenance;
    }

    public bool IsEligible(CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return EventTime.Value <= cursor.AsOf.Value
            && AvailabilityTime.Value <= cursor.AsOf.Value
            && StableSequence <= cursor.MaxStableSequence;
    }
}

/// <summary>Declared status of an externally supplied failed-continuation observation.</summary>
public enum LaneBFailedContinuationOutcome
{
    Unknown,
    Observed,
    NotObserved
}

/// <summary>
/// External failed-continuation evidence. The contract retains the method
/// version and causal references but intentionally contains no formula.
/// </summary>
public sealed record LaneBFailedContinuationObservation : ILaneBCausalObservation
{
    public string SourceId { get; }

    [JsonIgnore]
    public string SourceIdentity => SourceId;

    public string EventId { get; }

    public InstrumentId InstrumentId { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public UtcTimestamp? ExchangeTime { get; }

    public long StableSequence { get; }

    public string? MethodVersion { get; }

    [JsonIgnore]
    public string? AlgorithmVersion => MethodVersion;

    public LaneBFailedContinuationOutcome Outcome { get; }

    public ImmutableValueList<string> CausalReferences { get; }

    [JsonIgnore]
    public ImmutableValueList<string> CausalLinks => CausalReferences;

    public string Provenance { get; }

    [JsonIgnore]
    public LaneBObservationIdentity Identity => new(SourceId, InstrumentId, EventId,
        StableSequence);

    [JsonConstructor]
    public LaneBFailedContinuationObservation(string sourceId, string eventId,
        InstrumentId instrumentId, UtcTimestamp eventTime, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, UtcTimestamp? exchangeTime, long stableSequence,
        string? methodVersion, LaneBFailedContinuationOutcome outcome,
        ImmutableValueList<string> causalReferences, string provenance)
        : this(sourceId, eventId, instrumentId, eventTime, receiveTime, availabilityTime,
            exchangeTime, stableSequence, methodVersion, outcome,
            (IEnumerable<string>)causalReferences, provenance)
    {
    }

    public LaneBFailedContinuationObservation(string sourceId, string eventId,
        InstrumentId instrumentId, UtcTimestamp eventTime, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, UtcTimestamp? exchangeTime, long stableSequence,
        string? methodVersion, LaneBFailedContinuationOutcome outcome,
        IEnumerable<string> causalReferences, string provenance)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability time cannot precede receive time.",
                nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Lane B stable sequence cannot be negative.");
        }

        if (methodVersion is not null && string.IsNullOrWhiteSpace(methodVersion))
        {
            throw new ArgumentException("A failed-continuation method version cannot be blank.",
                nameof(methodVersion));
        }

        ContractGuard.RequireDefined(outcome, nameof(outcome));
        ArgumentNullException.ThrowIfNull(causalReferences);
        string[] references = causalReferences.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToArray();
        ContractGuard.RequireText(provenance, nameof(provenance));
        SourceId = sourceId;
        EventId = eventId;
        InstrumentId = instrumentId;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        ExchangeTime = exchangeTime;
        StableSequence = stableSequence;
        MethodVersion = methodVersion;
        Outcome = outcome;
        CausalReferences = new ImmutableValueList<string>(references);
        Provenance = provenance;
    }

    public bool IsEligible(CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return EventTime.Value <= cursor.AsOf.Value
            && AvailabilityTime.Value <= cursor.AsOf.Value
            && StableSequence <= cursor.MaxStableSequence;
    }
}

/// <summary>Metric names allowed in a caller-declared Lane B experiment rule.</summary>
public enum LaneBMetricKind
{
    OpenInterestDeltaAbsolute,
    FundingRate,
    LiquidationCount,
    LiquidationQuantity,
    FailedContinuation
}

/// <summary>Comparison operators for explicit, caller-owned experiment values.</summary>
public enum LaneBComparisonOperator
{
    Exists,
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

/// <summary>One declared metric comparison. No condition is inferred.</summary>
public sealed record LaneBRuleCondition
{
    public string ConditionId { get; }

    public LaneBMetricKind Metric { get; }

    public LaneBComparisonOperator Operator { get; }

    public decimal? ExpectedValue { get; }

    [JsonConstructor]
    public LaneBRuleCondition(string conditionId, LaneBMetricKind metric,
        LaneBComparisonOperator @operator, decimal? expectedValue)
    {
        ContractGuard.RequireText(conditionId, nameof(conditionId));
        ContractGuard.RequireDefined(metric, nameof(metric));
        ContractGuard.RequireDefined(@operator, nameof(@operator));
        if (@operator == LaneBComparisonOperator.Exists && expectedValue is not null)
        {
            throw new ArgumentException("Exists conditions cannot carry an expected value.",
                nameof(expectedValue));
        }

        if (@operator != LaneBComparisonOperator.Exists && expectedValue is null)
        {
            throw new ArgumentNullException(nameof(expectedValue),
                "Comparison conditions require an explicit expected value.");
        }

        ConditionId = conditionId;
        Metric = metric;
        Operator = @operator;
        ExpectedValue = expectedValue;
    }
}

/// <summary>One setup/direction-specific Lane B rule and declared shadow outcome.</summary>
public sealed record LaneBRule
{
    public string RuleId { get; }

    public SetupType SetupType { get; }

    public Direction Direction { get; }

    public string RuleVersion { get; }

    public ImmutableValueList<LaneBRuleCondition> Conditions { get; }

    public LaneBShadowStatus Outcome { get; }

    [JsonConstructor]
    public LaneBRule(string ruleId, SetupType setupType, Direction direction,
        string ruleVersion, ImmutableValueList<LaneBRuleCondition> conditions,
        LaneBShadowStatus outcome)
        : this(ruleId, setupType, direction, ruleVersion,
            (IEnumerable<LaneBRuleCondition>)conditions, outcome)
    {
    }

    public LaneBRule(string ruleId, SetupType setupType, Direction direction,
        string ruleVersion, IEnumerable<LaneBRuleCondition> conditions,
        LaneBShadowStatus outcome)
    {
        ContractGuard.RequireText(ruleId, nameof(ruleId));
        ContractGuard.RequireDefined(setupType, nameof(setupType));
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireText(ruleVersion, nameof(ruleVersion));
        ArgumentNullException.ThrowIfNull(conditions);
        LaneBRuleCondition[] snapshot = conditions.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("A Lane B rule requires at least one condition.",
                nameof(conditions));
        }

        ContractGuard.RequireDefined(outcome, nameof(outcome));
        RuleId = ruleId;
        SetupType = setupType;
        Direction = direction;
        RuleVersion = ruleVersion;
        Conditions = new ImmutableValueList<LaneBRuleCondition>(snapshot
            .OrderBy(static value => value.ConditionId, StringComparer.Ordinal));
        Outcome = outcome;
    }
}

/// <summary>Versioned caller-owned table of setup-specific Lane B rules.</summary>
public sealed record LaneBRuleTable
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string RulesVersion { get; }

    public ImmutableValueList<LaneBRule> Rules { get; }

    [JsonConstructor]
    public LaneBRuleTable(int formatVersion, string rulesVersion,
        ImmutableValueList<LaneBRule> rules)
        : this(formatVersion, rulesVersion, (IEnumerable<LaneBRule>)rules)
    {
    }

    public LaneBRuleTable(string rulesVersion, IEnumerable<LaneBRule> rules)
        : this(CurrentFormatVersion, rulesVersion, rules)
    {
    }

    public LaneBRuleTable(int formatVersion, string rulesVersion, IEnumerable<LaneBRule> rules)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The Lane B rule table format is unsupported.");
        }

        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ArgumentNullException.ThrowIfNull(rules);
        LaneBRule[] snapshot = rules.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Lane B rules cannot contain null values.", nameof(rules));
        }

        FormatVersion = formatVersion;
        RulesVersion = rulesVersion;
        Rules = new ImmutableValueList<LaneBRule>(snapshot
            .OrderBy(static value => value.SetupType)
            .ThenBy(static value => value.Direction)
            .ThenBy(static value => value.RuleId, StringComparer.Ordinal));
    }
}

/// <summary>Research-only Lane B shadow outcome.</summary>
public enum LaneBShadowStatus
{
    Confirm,
    Neutral,
    Veto,
    InsufficientData
}

/// <summary>Immutable, versioned result of evaluating a declared Lane B rule.</summary>
public sealed record LaneBShadowResult
{
    public const string CurrentAlgorithmVersion = "lane-b-shadow-v1";

    public string AlgorithmVersion { get; }

    public string RulesVersion { get; }

    public string? RuleId { get; }

    public SetupType SetupType { get; }

    public Direction Direction { get; }

    public LaneBShadowStatus Status { get; }

    [JsonIgnore]
    public LaneBShadowStatus Outcome => Status;

    [JsonIgnore]
    public DecisionEvidenceStatus EvidenceStatus => Status switch
    {
        LaneBShadowStatus.Confirm => DecisionEvidenceStatus.Confirm,
        LaneBShadowStatus.Neutral => DecisionEvidenceStatus.Neutral,
        LaneBShadowStatus.Veto => DecisionEvidenceStatus.Veto,
        _ => DecisionEvidenceStatus.Unknown
    };

    public CausalEventCursor Cursor { get; }

    public LaneBInputSelection InputSelection { get; }

    public ImmutableValueList<LaneBLiquidationObservation> Liquidations { get; }

    public ImmutableValueList<LaneBFailedContinuationObservation> FailedContinuations { get; }

    public ImmutableValueList<string> UsedInputIds { get; }

    public ImmutableValueList<string> Reasons { get; }

    public LaneBFreshnessEvaluation? Freshness { get; }

    [JsonIgnore]
    public bool IsDataSufficient => Status != LaneBShadowStatus.InsufficientData;

    [JsonConstructor]
    public LaneBShadowResult(string algorithmVersion, string rulesVersion, string? ruleId,
        SetupType setupType, Direction direction, LaneBShadowStatus status,
        CausalEventCursor cursor, LaneBInputSelection inputSelection,
        ImmutableValueList<LaneBLiquidationObservation> liquidations,
        ImmutableValueList<LaneBFailedContinuationObservation> failedContinuations,
        ImmutableValueList<string> usedInputIds, ImmutableValueList<string> reasons,
        LaneBFreshnessEvaluation? freshness = null)
    {
        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentException("The Lane B shadow algorithm version is unsupported.",
                nameof(algorithmVersion));
        }

        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        if (ruleId is not null)
        {
            ContractGuard.RequireText(ruleId, nameof(ruleId));
        }

        ContractGuard.RequireDefined(setupType, nameof(setupType));
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireDefined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(inputSelection);
        ArgumentNullException.ThrowIfNull(liquidations);
        ArgumentNullException.ThrowIfNull(failedContinuations);
        ArgumentNullException.ThrowIfNull(usedInputIds);
        ArgumentNullException.ThrowIfNull(reasons);
        AlgorithmVersion = algorithmVersion;
        RulesVersion = rulesVersion;
        RuleId = ruleId;
        SetupType = setupType;
        Direction = direction;
        Status = status;
        Cursor = cursor;
        InputSelection = inputSelection;
        Liquidations = new ImmutableValueList<LaneBLiquidationObservation>(liquidations);
        FailedContinuations = new ImmutableValueList<LaneBFailedContinuationObservation>(
            failedContinuations);
        UsedInputIds = new ImmutableValueList<string>(usedInputIds);
        Reasons = new ImmutableValueList<string>(reasons);
        Freshness = freshness;
    }
}

/// <summary>
/// Pure evaluator for a caller-declared Lane B experiment table. The evaluator
/// only returns shadow evidence and never creates a decision, risk, order, or
/// execution authorization.
/// </summary>
public sealed class LaneBEvaluator
{
    private readonly LaneBRuleTable _rules;

    public LaneBEvaluator(LaneBRuleTable rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    public LaneBShadowResult Evaluate(LaneBInput input, CausalEventCursor cursor,
        LaneBInputConfiguration? inputConfiguration,
        IEnumerable<LaneBLiquidationObservation> liquidations,
        IEnumerable<LaneBFailedContinuationObservation> failedContinuations,
        SetupType setupType, Direction direction)
    {
        return EvaluateCore(input, cursor, inputConfiguration, liquidations,
            failedContinuations, setupType, direction, null);
    }

    /// <summary>Evaluates with an explicit caller-supplied freshness policy.</summary>
    public LaneBShadowResult Evaluate(LaneBInput input, CausalEventCursor cursor,
        LaneBInputConfiguration? inputConfiguration,
        IEnumerable<LaneBLiquidationObservation> liquidations,
        IEnumerable<LaneBFailedContinuationObservation> failedContinuations,
        SetupType setupType, Direction direction, LaneBFreshnessPolicy freshnessPolicy)
    {
        ArgumentNullException.ThrowIfNull(freshnessPolicy);
        return EvaluateCore(input, cursor, inputConfiguration, liquidations,
            failedContinuations, setupType, direction, freshnessPolicy);
    }

    private LaneBShadowResult EvaluateCore(LaneBInput input, CausalEventCursor cursor,
        LaneBInputConfiguration? inputConfiguration,
        IEnumerable<LaneBLiquidationObservation> liquidations,
        IEnumerable<LaneBFailedContinuationObservation> failedContinuations,
        SetupType setupType, Direction direction, LaneBFreshnessPolicy? freshnessPolicy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(liquidations);
        ArgumentNullException.ThrowIfNull(failedContinuations);
        ContractGuard.RequireDefined(setupType, nameof(setupType));
        ContractGuard.RequireDefined(direction, nameof(direction));

        LaneBInputSelection selection = input.Select(cursor, inputConfiguration);
        LaneBLiquidationObservation[] visibleLiquidations = CanonicalizeLiquidations(
            input.InstrumentId, cursor, liquidations, out List<string> liquidationReasons,
            out bool liquidationConflict);
        LaneBFailedContinuationObservation[] visibleContinuations = CanonicalizeContinuations(
            input.InstrumentId, cursor, failedContinuations, out List<string> continuationReasons,
            out bool continuationConflict);
        List<string> baseReasons = [.. selection.Reasons, .. liquidationReasons,
            .. continuationReasons];
        List<string> usedIds = [.. selection.OpenInterest.Select(static value => value.EventId),
            .. selection.Funding.Select(static value => value.EventId),
            .. visibleLiquidations.Select(static value => value.EventId),
            .. visibleContinuations.Select(static value => value.EventId)];

        LaneBRule? rule = _rules.Rules.FirstOrDefault(value => value.SetupType == setupType
            && value.Direction == direction);
        if (rule is null)
        {
            baseReasons.Add("lane-b-rule-missing");
            return Result(LaneBShadowStatus.InsufficientData, null, setupType, direction,
                cursor, selection, visibleLiquidations, visibleContinuations, usedIds,
                baseReasons);
        }

        LaneBFreshnessEvaluation? freshness = null;
        if (freshnessPolicy is not null)
        {
            freshness = LaneBFreshnessEvaluator.Evaluate(selection, visibleLiquidations,
                visibleContinuations, cursor,
                freshnessPolicy, rule.Conditions.Select(static condition =>
                    FreshnessKind(condition.Metric)));
            foreach (LaneBFreshnessEvidence evidence in freshness.Evidence
                         .Where(static evidence => !evidence.IsReady))
            {
                baseReasons.AddRange(evidence.Reasons);
            }

            if (!freshness.IsReady)
            {
                baseReasons.Add("lane-b-freshness-insufficient");
                return Result(LaneBShadowStatus.InsufficientData, rule.RuleId, setupType,
                    direction, cursor, selection, visibleLiquidations, visibleContinuations,
                    usedIds, baseReasons, freshness);
            }
        }

        if (!selection.IsReady || liquidationConflict || continuationConflict)
        {
            if (!selection.IsReady)
            {
                baseReasons.Add("lane-b-input-insufficient");
            }

            if (liquidationConflict || continuationConflict)
            {
                baseReasons.Add("lane-b-observation-conflict");
            }

            return Result(LaneBShadowStatus.InsufficientData, rule.RuleId, setupType, direction,
                cursor, selection, visibleLiquidations, visibleContinuations, usedIds,
                baseReasons, freshness);
        }

        bool hasNonOiCondition = rule.Conditions.Any(condition =>
            condition.Metric != LaneBMetricKind.OpenInterestDeltaAbsolute);
        bool allMatch = true;
        bool unknown = false;
        foreach (LaneBRuleCondition condition in rule.Conditions)
        {
            MetricValue metric = ReadMetric(condition.Metric, selection, visibleLiquidations,
                visibleContinuations);
            if (!metric.HasValue)
            {
                unknown = true;
                baseReasons.Add($"metric-unavailable:{condition.ConditionId}");
                if (condition.Metric == LaneBMetricKind.FailedContinuation)
                {
                    baseReasons.Add(visibleContinuations.Length == 0
                        ? "failed-continuation-coverage-missing"
                        : "failed-continuation-method-unknown");
                }
                else if (condition.Metric == LaneBMetricKind.LiquidationQuantity)
                {
                    baseReasons.Add(visibleLiquidations.Length == 0
                        ? "liquidation-coverage-missing"
                        : "liquidation-quantity-overflow-or-unavailable");
                }
                continue;
            }

            if (!Matches(metric.Value, condition))
            {
                allMatch = false;
                baseReasons.Add($"condition-false:{condition.ConditionId}");
            }
        }

        if (unknown)
        {
            return Result(LaneBShadowStatus.InsufficientData, rule.RuleId, setupType, direction,
                cursor, selection, visibleLiquidations, visibleContinuations, usedIds,
                baseReasons, freshness);
        }

        if (rule.Outcome == LaneBShadowStatus.Confirm && !hasNonOiCondition)
        {
            baseReasons.Add("oi-alone-cannot-confirm");
            return Result(LaneBShadowStatus.InsufficientData, rule.RuleId, setupType, direction,
                cursor, selection, visibleLiquidations, visibleContinuations, usedIds,
                baseReasons, freshness);
        }

        LaneBShadowStatus status = allMatch ? rule.Outcome : LaneBShadowStatus.Neutral;
        if (allMatch)
        {
            baseReasons.Add($"rule-matched:{rule.RuleId}");
        }

        return Result(status, rule.RuleId, setupType, direction, cursor, selection,
            visibleLiquidations, visibleContinuations, usedIds, baseReasons, freshness);
    }

    private LaneBShadowResult Result(LaneBShadowStatus status, string? ruleId,
        SetupType setupType, Direction direction, CausalEventCursor cursor,
        LaneBInputSelection selection, IEnumerable<LaneBLiquidationObservation> liquidations,
        IEnumerable<LaneBFailedContinuationObservation> continuations,
        IEnumerable<string> usedIds, IEnumerable<string> reasons,
        LaneBFreshnessEvaluation? freshness = null) => new(
            LaneBShadowResult.CurrentAlgorithmVersion, _rules.RulesVersion, ruleId, setupType,
            direction, status, cursor, selection,
            new ImmutableValueList<LaneBLiquidationObservation>(liquidations),
            new ImmutableValueList<LaneBFailedContinuationObservation>(continuations),
            new ImmutableValueList<string>(StableStrings(usedIds)),
            new ImmutableValueList<string>(StableStrings(reasons)), freshness);

    private static LaneBFreshnessKind FreshnessKind(LaneBMetricKind metric) => metric switch
    {
        LaneBMetricKind.OpenInterestDeltaAbsolute => LaneBFreshnessKind.OpenInterest,
        LaneBMetricKind.FundingRate => LaneBFreshnessKind.Funding,
        LaneBMetricKind.LiquidationCount or LaneBMetricKind.LiquidationQuantity =>
            LaneBFreshnessKind.Liquidation,
        LaneBMetricKind.FailedContinuation => LaneBFreshnessKind.FailedContinuation,
        _ => throw new ArgumentOutOfRangeException(nameof(metric), metric,
            "The Lane B metric has no freshness kind.")
    };

    private static MetricValue ReadMetric(LaneBMetricKind metric,
        LaneBInputSelection selection, LaneBLiquidationObservation[] liquidations,
        LaneBFailedContinuationObservation[] continuations) => metric switch
        {
            LaneBMetricKind.OpenInterestDeltaAbsolute => selection.AbsoluteOpenInterestDelta is { }
                value ? new MetricValue(value) : default,
            LaneBMetricKind.FundingRate => selection.Funding.Count == 0
                ? default : new MetricValue(selection.Funding[^1].FundingRate),
            LaneBMetricKind.LiquidationCount => liquidations.Length == 0
                ? default : new MetricValue(liquidations.Length),
            LaneBMetricKind.LiquidationQuantity => SumLiquidationQuantity(liquidations),
            LaneBMetricKind.FailedContinuation => ReadFailedContinuation(continuations),
            _ => default
        };

    private static MetricValue SumLiquidationQuantity(
        LaneBLiquidationObservation[] liquidations)
    {
        if (liquidations.Length == 0)
        {
            return default;
        }

        try
        {
            decimal total = 0m;
            foreach (LaneBLiquidationObservation liquidation in liquidations)
            {
                total = checked(total + liquidation.Quantity.Value);
            }

            return new MetricValue(total);
        }
        catch (OverflowException)
        {
            return default;
        }
    }

    private static MetricValue ReadFailedContinuation(
        LaneBFailedContinuationObservation[] observations)
    {
        if (observations.Length == 0)
        {
            return default;
        }

        LaneBFailedContinuationObservation latest = observations[^1];
        if (string.IsNullOrWhiteSpace(latest.MethodVersion)
            || latest.CausalReferences.Count == 0
            || latest.Outcome == LaneBFailedContinuationOutcome.Unknown)
        {
            return default;
        }

        return new MetricValue(latest.Outcome == LaneBFailedContinuationOutcome.Observed
            ? 1m : 0m);
    }

    private static bool Matches(decimal value, LaneBRuleCondition condition)
    {
        decimal expected = condition.ExpectedValue.GetValueOrDefault();
        return condition.Operator switch
        {
            LaneBComparisonOperator.Exists => true,
            LaneBComparisonOperator.Equal => value == expected,
            LaneBComparisonOperator.NotEqual => value != expected,
            LaneBComparisonOperator.GreaterThan => value > expected,
            LaneBComparisonOperator.GreaterThanOrEqual => value >= expected,
            LaneBComparisonOperator.LessThan => value < expected,
            LaneBComparisonOperator.LessThanOrEqual => value <= expected,
            _ => false
        };
    }

    private static LaneBLiquidationObservation[] CanonicalizeLiquidations(
        InstrumentId instrument, CausalEventCursor cursor,
        IEnumerable<LaneBLiquidationObservation> source, out List<string> reasons,
        out bool conflict)
    {
        reasons = [];
        conflict = false;
        Dictionary<string, LaneBLiquidationObservation> seen = [];
        foreach (LaneBLiquidationObservation candidate in source.Where(value => value is not null)
                     .Where(value => value.InstrumentId == instrument && value.IsEligible(cursor))
                     .OrderBy(static value => value.EventTime.Value)
                     .ThenBy(static value => value.StableSequence)
                     .ThenBy(static value => value.SourceId, StringComparer.Ordinal))
        {
            string identityKey = IdentityKey(candidate);
            if (!seen.TryGetValue(identityKey, out LaneBLiquidationObservation? existing))
            {
                seen.Add(identityKey, candidate);
            }
            else if (existing.Equals(candidate))
            {
                reasons.Add($"duplicate-liquidation:{candidate.EventId}");
            }
            else
            {
                conflict = true;
                reasons.Add($"conflict-liquidation:{candidate.EventId}");
            }
        }

        return seen.Values.OrderBy(static value => value.EventTime.Value)
            .ThenBy(static value => value.StableSequence)
            .ThenBy(static value => value.SourceId, StringComparer.Ordinal).ToArray();
    }

    private static LaneBFailedContinuationObservation[] CanonicalizeContinuations(
        InstrumentId instrument, CausalEventCursor cursor,
        IEnumerable<LaneBFailedContinuationObservation> source, out List<string> reasons,
        out bool conflict)
    {
        reasons = [];
        conflict = false;
        Dictionary<string, LaneBFailedContinuationObservation> seen = [];
        foreach (LaneBFailedContinuationObservation candidate in source.Where(value => value is not null)
                     .Where(value => value.InstrumentId == instrument && value.IsEligible(cursor))
                     .OrderBy(static value => value.EventTime.Value)
                     .ThenBy(static value => value.StableSequence)
                     .ThenBy(static value => value.SourceId, StringComparer.Ordinal))
        {
            string identityKey = IdentityKey(candidate);
            if (!seen.TryGetValue(identityKey,
                    out LaneBFailedContinuationObservation? existing))
            {
                seen.Add(identityKey, candidate);
            }
            else if (existing.Equals(candidate))
            {
                reasons.Add($"duplicate-failed-continuation:{candidate.EventId}");
            }
            else
            {
                conflict = true;
                reasons.Add($"conflict-failed-continuation:{candidate.EventId}");
            }
        }

        return seen.Values.OrderBy(static value => value.EventTime.Value)
            .ThenBy(static value => value.StableSequence)
            .ThenBy(static value => value.SourceId, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> StableStrings(IEnumerable<string> values) => values
        .Where(static value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static value => value, StringComparer.Ordinal);

    private static string IdentityKey(ILaneBCausalObservation observation) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{observation.SourceId}\u001f{observation.InstrumentId.Value}\u001f{observation.EventId}");

    private readonly record struct MetricValue
    {
        public decimal Value { get; }

        public bool HasValue { get; }

        public MetricValue(decimal value)
        {
            Value = value;
            HasValue = true;
        }
    }
}
