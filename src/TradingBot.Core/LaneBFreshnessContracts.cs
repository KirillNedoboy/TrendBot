using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Lane B series whose caller-supplied freshness is assessed.</summary>
public enum LaneBFreshnessKind
{
    OpenInterest,
    Funding,
    Liquidation,
    FailedContinuation
}

/// <summary>Freshness outcome for one explicit TTL policy.</summary>
public enum LaneBFreshnessStatus
{
    Unknown,
    Fresh,
    Stale
}

/// <summary>
/// Caller-owned Lane B freshness policy. Missing and non-positive values remain
/// unknown; this type never supplies a numeric default or reads a clock.
/// </summary>
public sealed record LaneBFreshnessPolicy
{
    public TimeSpan? OpenInterestTtl { get; }

    public TimeSpan? FundingTtl { get; }

    public TimeSpan? LiquidationTtl { get; }

    public TimeSpan? FailedContinuationTtl { get; }

    [JsonIgnore]
    public TimeSpan? OpenInterestTimeToLive => OpenInterestTtl;

    [JsonIgnore]
    public TimeSpan? FundingTimeToLive => FundingTtl;

    [JsonIgnore]
    public TimeSpan? LiquidationTimeToLive => LiquidationTtl;

    [JsonIgnore]
    public TimeSpan? FailedContinuationTimeToLive => FailedContinuationTtl;

    public LaneBFreshnessPolicy()
        : this(null, null, null, null)
    {
    }

    [JsonConstructor]
    public LaneBFreshnessPolicy(TimeSpan? openInterestTtl, TimeSpan? fundingTtl,
        TimeSpan? liquidationTtl, TimeSpan? failedContinuationTtl)
    {
        OpenInterestTtl = openInterestTtl;
        FundingTtl = fundingTtl;
        LiquidationTtl = liquidationTtl;
        FailedContinuationTtl = failedContinuationTtl;
    }

    internal TimeSpan? GetTtl(LaneBFreshnessKind kind) => kind switch
    {
        LaneBFreshnessKind.OpenInterest => OpenInterestTtl,
        LaneBFreshnessKind.Funding => FundingTtl,
        LaneBFreshnessKind.Liquidation => LiquidationTtl,
        LaneBFreshnessKind.FailedContinuation => FailedContinuationTtl,
        _ => null
    };
}

/// <summary>Independent event/availability age assessment for one series.</summary>
public sealed record LaneBFreshnessEvidence
{
    public LaneBFreshnessKind Kind { get; }

    public LaneBFreshnessStatus Status { get; }

    public TimeSpan? EventAge { get; }

    public TimeSpan? AvailabilityAge { get; }

    public TimeSpan? TimeToLive { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => Status == LaneBFreshnessStatus.Fresh;

    [JsonIgnore]
    public bool IsFresh => IsReady;

    [JsonConstructor]
    public LaneBFreshnessEvidence(LaneBFreshnessKind kind, LaneBFreshnessStatus status,
        TimeSpan? eventAge, TimeSpan? availabilityAge, TimeSpan? timeToLive,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(kind, nameof(kind));
        ContractGuard.RequireDefined(status, nameof(status));
        ValidateAge(eventAge, nameof(eventAge));
        ValidateAge(availabilityAge, nameof(availabilityAge));
        ArgumentNullException.ThrowIfNull(reasons);
        Kind = kind;
        Status = status;
        EventAge = eventAge;
        AvailabilityAge = availabilityAge;
        TimeToLive = timeToLive;
        Reasons = new ImmutableValueList<string>(reasons);
    }

    private static void ValidateAge(TimeSpan? age, string parameterName)
    {
        if (age is { } value && value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, age,
                "A Lane B freshness age cannot be negative.");
        }
    }
}

/// <summary>All explicitly requested Lane B freshness assessments for one cursor.</summary>
public sealed record LaneBFreshnessEvaluation
{
    public CausalEventCursor Cursor { get; }

    public LaneBFreshnessPolicy Policy { get; }

    public ImmutableValueList<LaneBFreshnessEvidence> Evidence { get; }

    [JsonIgnore]
    public ImmutableValueList<LaneBFreshnessEvidence> Inputs => Evidence;

    [JsonIgnore]
    public bool IsReady => Evidence.Count > 0 && Evidence.All(static value => value.IsReady);

    [JsonConstructor]
    public LaneBFreshnessEvaluation(CausalEventCursor cursor, LaneBFreshnessPolicy policy,
        ImmutableValueList<LaneBFreshnessEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Any(static value => value is null))
        {
            throw new ArgumentException("Lane B freshness evidence cannot contain null values.",
                nameof(evidence));
        }

        Cursor = cursor;
        Policy = policy;
        Evidence = new ImmutableValueList<LaneBFreshnessEvidence>(evidence
            .OrderBy(static value => value.Kind));
    }

    public LaneBFreshnessEvidence? For(LaneBFreshnessKind kind) =>
        Evidence.FirstOrDefault(value => value.Kind == kind);
}

/// <summary>
/// Pure Lane B freshness evaluator. Caller-supplied policy values are compared
/// separately with event and availability age; equality at the TTL is fresh.
/// </summary>
public static class LaneBFreshnessEvaluator
{
    public static LaneBFreshnessEvaluation Evaluate(LaneBInputSelection selection,
        IEnumerable<LaneBLiquidationObservation> liquidations,
        IEnumerable<LaneBFailedContinuationObservation> failedContinuations,
        CausalEventCursor cursor, LaneBFreshnessPolicy policy,
        IEnumerable<LaneBFreshnessKind> requestedKinds)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(liquidations);
        ArgumentNullException.ThrowIfNull(failedContinuations);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(requestedKinds);
        LaneBLiquidationObservation[] liquidationSnapshot = liquidations.ToArray();
        LaneBFailedContinuationObservation[] continuationSnapshot = failedContinuations.ToArray();
        LaneBFreshnessEvidence[] evidence = requestedKinds.Distinct().OrderBy(static value => value)
            .Select(kind => EvaluateOne(kind, selection, liquidationSnapshot,
                continuationSnapshot, cursor, policy.GetTtl(kind)))
            .ToArray();
        return new LaneBFreshnessEvaluation(cursor, policy,
            new ImmutableValueList<LaneBFreshnessEvidence>(evidence));
    }

    public static LaneBFreshnessEvidence EvaluateOne(LaneBFreshnessKind kind,
        LaneBInputSelection selection, IEnumerable<LaneBLiquidationObservation> liquidations,
        IEnumerable<LaneBFailedContinuationObservation> failedContinuations,
        CausalEventCursor cursor, TimeSpan? timeToLive)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(liquidations);
        ArgumentNullException.ThrowIfNull(failedContinuations);
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireDefined(kind, nameof(kind));
        LaneBCausalTimes? latest = kind switch
        {
            LaneBFreshnessKind.OpenInterest => Latest(selection.OpenInterest),
            LaneBFreshnessKind.Funding => Latest(selection.Funding),
            LaneBFreshnessKind.Liquidation => Latest(liquidations),
            LaneBFreshnessKind.FailedContinuation => Latest(failedContinuations),
            _ => null
        };
        List<string> reasons = [];
        if (latest is null)
        {
            reasons.Add($"freshness-observation-missing:{kind}");
            return new LaneBFreshnessEvidence(kind, LaneBFreshnessStatus.Unknown, null, null,
                timeToLive, new ImmutableValueList<string>(reasons));
        }

        TimeSpan eventAge = cursor.AsOf.Value - latest.Value.EventTime.Value;
        TimeSpan availabilityAge = cursor.AsOf.Value - latest.Value.AvailabilityTime.Value;
        if (timeToLive is not { } ttl || ttl <= TimeSpan.Zero)
        {
            reasons.Add($"freshness-ttl-unknown:{kind}");
            return new LaneBFreshnessEvidence(kind, LaneBFreshnessStatus.Unknown, eventAge,
                availabilityAge, timeToLive, new ImmutableValueList<string>(reasons));
        }

        if (eventAge > ttl)
        {
            reasons.Add($"freshness-event-stale:{kind}");
        }

        if (availabilityAge > ttl)
        {
            reasons.Add($"freshness-availability-stale:{kind}");
        }

        LaneBFreshnessStatus status = reasons.Count == 0
            ? LaneBFreshnessStatus.Fresh : LaneBFreshnessStatus.Stale;
        return new LaneBFreshnessEvidence(kind, status, eventAge, availabilityAge, timeToLive,
            new ImmutableValueList<string>(reasons));
    }

    private static LaneBCausalTimes? Latest<T>(IEnumerable<T> values)
        where T : ILaneBCausalObservation
    {
        T? latest = values.OrderBy(static value => value.EventTime.Value)
            .ThenBy(static value => value.StableSequence).LastOrDefault();
        return latest is null
            ? null
            : new LaneBCausalTimes(latest.EventTime, latest.AvailabilityTime);
    }

    private readonly record struct LaneBCausalTimes(UtcTimestamp EventTime,
        UtcTimestamp AvailabilityTime);
}
