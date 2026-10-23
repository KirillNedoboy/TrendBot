using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Independent readiness state for Lane B evidence. The state describes data
/// quality only; it is deliberately separate from the shadow outcome.
/// </summary>
public enum LaneBQualityStatus
{
    Unknown,
    Ready,
    Missing,
    Stale,
    Unavailable,
    Conflict,
    Incompatible,
    ArithmeticOverflow,

    // Compatibility spellings for callers that use the descriptive forms.
    MissingData = Missing,
    Conflicting = Conflict,
    Overflow = ArithmeticOverflow
}

/// <summary>
/// Immutable, versioned Lane B quality state for one causal cursor. It keeps
/// stable observation identities, the caller-owned freshness assessment, and
/// deterministic reasons without introducing a time-to-live or trading value.
/// </summary>
public sealed record LaneBQualityState
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentAlgorithmVersion = "lane-b-quality-v1";

    public int FormatVersion { get; }

    public string AlgorithmVersion { get; }

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<LaneBObservationIdentity> ObservationReferences { get; }

    [JsonIgnore]
    public ImmutableValueList<LaneBObservationIdentity> ObservationIds =>
        ObservationReferences;

    public LaneBFreshnessEvaluation? Freshness { get; }

    [JsonIgnore]
    public LaneBFreshnessEvaluation? FreshnessEvidence => Freshness;

    public LaneBQualityStatus Status { get; }

    [JsonIgnore]
    public LaneBQualityStatus QualityStatus => Status;

    [JsonIgnore]
    public LaneBQualityStatus State => Status;

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => Status == LaneBQualityStatus.Ready;

    [JsonIgnore]
    public bool IsKnown => Status != LaneBQualityStatus.Unknown;

    public LaneBQualityState(CausalEventCursor cursor,
        IEnumerable<LaneBObservationIdentity> observationReferences,
        LaneBFreshnessEvaluation? freshness, LaneBQualityStatus status,
        IEnumerable<string> reasons)
        : this(CurrentFormatVersion, CurrentAlgorithmVersion, cursor,
            new ImmutableValueList<LaneBObservationIdentity>(observationReferences), freshness,
            status, new ImmutableValueList<string>(reasons))
    {
    }

    [JsonConstructor]
    public LaneBQualityState(int formatVersion, string algorithmVersion,
        CausalEventCursor cursor,
        ImmutableValueList<LaneBObservationIdentity> observationReferences,
        LaneBFreshnessEvaluation? freshness, LaneBQualityStatus status,
        ImmutableValueList<string> reasons)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The Lane B quality format version is unsupported.");
        }

        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion), algorithmVersion,
                "The Lane B quality algorithm version is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(observationReferences);
        ArgumentNullException.ThrowIfNull(reasons);
        ContractGuard.RequireDefined(status, nameof(status));
        if (observationReferences.Any(static value => value is null))
        {
            throw new ArgumentException(
                "Lane B quality observation references cannot contain null values.",
                nameof(observationReferences));
        }

        if (freshness is not null && freshness.Cursor != cursor)
        {
            throw new ArgumentException(
                "Lane B freshness evidence must use the quality cursor.", nameof(freshness));
        }

        if (status == LaneBQualityStatus.Ready
            && (freshness is null || !freshness.IsReady))
        {
            throw new ArgumentException(
                "Ready Lane B quality requires fresh caller-owned freshness evidence.",
                nameof(status));
        }

        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        Cursor = cursor;
        ObservationReferences = new ImmutableValueList<LaneBObservationIdentity>(
            observationReferences.OrderBy(static value => value.SourceId,
                    StringComparer.Ordinal)
                .ThenBy(static value => value.InstrumentId.Value, StringComparer.Ordinal)
                .ThenBy(static value => value.EventId, StringComparer.Ordinal)
                .ThenBy(static value => value.StableSequence));
        Freshness = freshness;
        Status = status;
        Reasons = new ImmutableValueList<string>(StableReasons(reasons));
    }

    internal LaneBQualityState WithStatus(LaneBQualityStatus status,
        IEnumerable<string> additionalReasons,
        IEnumerable<LaneBObservationIdentity>? references = null)
    {
        ArgumentNullException.ThrowIfNull(additionalReasons);
        return new LaneBQualityState(FormatVersion, AlgorithmVersion, Cursor,
            new ImmutableValueList<LaneBObservationIdentity>(references ?? ObservationReferences),
            Freshness, status, new ImmutableValueList<string>(
                StableReasons(Reasons.Concat(additionalReasons))));
    }

    private static IEnumerable<string> StableReasons(IEnumerable<string> reasons) => reasons
        .Where(static reason => !string.IsNullOrWhiteSpace(reason))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static reason => reason, StringComparer.Ordinal);
}

/// <summary>
/// Pure quality evaluator over an already evaluated Lane B shadow result. It
/// consumes the freshness assessment retained by that result and never reads a
/// clock or invents a policy.
/// </summary>
public static class LaneBQualityEvaluator
{
    public static LaneBQualityState Evaluate(LaneBShadowResult shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);

        List<LaneBObservationIdentity> references = [
            .. shadow.InputSelection.OpenInterest.Select(static value => value.Identity),
            .. shadow.InputSelection.Funding.Select(static value => value.Identity),
            .. shadow.Liquidations.Select(static value => value.Identity),
            .. shadow.FailedContinuations.Select(static value => value.Identity)
        ];
        List<string> reasons = [.. shadow.InputSelection.Reasons, .. shadow.Reasons];

        if (shadow.Cursor != shadow.InputSelection.Cursor)
        {
            reasons.Add("lane-b-quality-cursor-incompatible");
            return Create(shadow, LaneBQualityStatus.Incompatible, references, reasons);
        }

        LaneBQualityStatus? inputStatus = MapInputStatus(shadow.InputSelection.Status);
        if (inputStatus is not null)
        {
            return Create(shadow, inputStatus.Value, references, reasons);
        }

        if (shadow.Freshness is null)
        {
            reasons.Add("lane-b-freshness-policy-unknown");
            return Create(shadow, LaneBQualityStatus.Unknown, references, reasons);
        }

        if (shadow.Freshness.Cursor != shadow.Cursor)
        {
            reasons.Add("lane-b-freshness-cursor-incompatible");
            return Create(shadow, LaneBQualityStatus.Incompatible, references, reasons);
        }

        foreach (LaneBFreshnessEvidence evidence in shadow.Freshness.Evidence)
        {
            reasons.AddRange(evidence.Reasons);
        }

        LaneBQualityStatus? freshnessStatus = MapFreshnessStatus(shadow.Freshness);
        if (freshnessStatus is not null)
        {
            return Create(shadow, freshnessStatus.Value, references, reasons);
        }

        if (shadow.Status == LaneBShadowStatus.InsufficientData)
        {
            reasons.Add("lane-b-shadow-insufficient");
            return Create(shadow, LaneBQualityStatus.Unknown, references, reasons);
        }

        return Create(shadow, LaneBQualityStatus.Ready, references, reasons);
    }

    public static LaneBQualityState EvaluateQuality(LaneBShadowResult shadow) =>
        Evaluate(shadow);

    private static LaneBQualityState Create(LaneBShadowResult shadow,
        LaneBQualityStatus status, IEnumerable<LaneBObservationIdentity> references,
        IEnumerable<string> reasons) => new(shadow.Cursor, references, shadow.Freshness,
            status, reasons);

    private static LaneBQualityStatus? MapInputStatus(LaneBInputStatus status) => status switch
    {
        LaneBInputStatus.Ready => null,
        LaneBInputStatus.MissingData or LaneBInputStatus.InsufficientHistory =>
            LaneBQualityStatus.Missing,
        LaneBInputStatus.Unavailable => LaneBQualityStatus.Unavailable,
        LaneBInputStatus.Conflict => LaneBQualityStatus.Conflict,
        LaneBInputStatus.Incompatible => LaneBQualityStatus.Incompatible,
        LaneBInputStatus.ArithmeticOverflow => LaneBQualityStatus.ArithmeticOverflow,
        LaneBInputStatus.UnknownConfiguration => LaneBQualityStatus.Unknown,
        _ => LaneBQualityStatus.Unknown
    };

    private static LaneBQualityStatus? MapFreshnessStatus(
        LaneBFreshnessEvaluation freshness)
    {
        if (freshness.Evidence.Any(static value =>
                value.Status == LaneBFreshnessStatus.Stale))
        {
            return LaneBQualityStatus.Stale;
        }

        LaneBFreshnessEvidence? missing = freshness.Evidence.FirstOrDefault(static value =>
            value.Status == LaneBFreshnessStatus.Unknown
            && value.Reasons.Any(reason => reason.Contains("observation-missing",
                StringComparison.Ordinal)));
        if (missing is not null)
        {
            return LaneBQualityStatus.Missing;
        }

        if (freshness.Evidence.Any(static value =>
                value.Status == LaneBFreshnessStatus.Unknown))
        {
            return LaneBQualityStatus.Unknown;
        }

        return null;
    }
}
