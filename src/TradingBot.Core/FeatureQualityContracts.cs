using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Quality freshness for one caller-supplied causal input series.</summary>
public enum FeatureQualityFreshness
{
    Unknown,
    Fresh,
    Stale
}

/// <summary>Whether the known causal input coverage is complete.</summary>
public enum FeatureQualityCoverage
{
    Unknown,
    Partial,
    Complete
}

/// <summary>Input series used by the rolling and price-context calculators.</summary>
public enum FeatureQualityInputSeries
{
    Rolling,
    Setup,
    Htf,
    PriceVolume
}

/// <summary>One of the versioned feature outputs whose quality is evaluated.</summary>
public enum FeatureQualityFeatureKind
{
    RollingAtr,
    RollingMovingAverage,
    RollingRelativeVolume,
    PriceContextConfirmedPivots,
    PriceContextStructuralLevels,
    PriceContextChannel,
    PriceContextLocalTrend,
    PriceContextHtfContext,
    PriceContextVwap,
    PriceContextPoc
}

/// <summary>Derived quality readiness state.</summary>
public enum FeatureQualityStatus
{
    Unknown,
    Ready
}

/// <summary>Version of the independent feature-quality evaluator.</summary>
public sealed record FeatureQualityVersion
{
    public string Value { get; }

    [JsonConstructor]
    public FeatureQualityVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// Caller-supplied freshness policy. Null and non-positive TTLs remain
/// explicit unknown policy; this contract never invents defaults or reads a
/// wall clock.
/// </summary>
public sealed record FeatureQualityPolicy
{
    public TimeSpan? RollingTtl { get; }

    public TimeSpan? SetupTtl { get; }

    public TimeSpan? HtfTtl { get; }

    public TimeSpan? PriceVolumeTtl { get; }

    [JsonIgnore]
    public TimeSpan? RollingTimeToLive => RollingTtl;

    [JsonIgnore]
    public TimeSpan? SetupTimeToLive => SetupTtl;

    [JsonIgnore]
    public TimeSpan? HtfTimeToLive => HtfTtl;

    [JsonIgnore]
    public TimeSpan? PriceVolumeTimeToLive => PriceVolumeTtl;

    public FeatureQualityPolicy()
        : this(null, null, null, null)
    {
    }

    [JsonConstructor]
    public FeatureQualityPolicy(TimeSpan? rollingTtl, TimeSpan? setupTtl,
        TimeSpan? htfTtl, TimeSpan? priceVolumeTtl)
    {
        RollingTtl = rollingTtl;
        SetupTtl = setupTtl;
        HtfTtl = htfTtl;
        PriceVolumeTtl = priceVolumeTtl;
    }

    internal TimeSpan? GetTtl(FeatureQualityInputSeries series) => series switch
    {
        FeatureQualityInputSeries.Rolling => RollingTtl,
        FeatureQualityInputSeries.Setup => SetupTtl,
        FeatureQualityInputSeries.Htf => HtfTtl,
        FeatureQualityInputSeries.PriceVolume => PriceVolumeTtl,
        _ => null
    };

    internal static bool IsValidTtl(TimeSpan? ttl) => ttl is { } value
        && value > TimeSpan.Zero;
}

/// <summary>
/// Immutable evidence for one feature after applying a caller policy to the
/// original calculator output. Ages are measured against the result cursor.
/// </summary>
public sealed record FeatureQualityEvidence
{
    public FeatureQualityFeatureKind Kind { get; }

    public FeatureQualityInputSeries InputSeries { get; }

    public string FormulaVersion { get; }

    public string OriginalState { get; }

    public string OriginalCompleteness { get; }

    public FeatureQualityFreshness OriginalFreshness { get; }

    public FeatureQualityFreshness Freshness { get; }

    public TimeSpan? MarketDataAge { get; }

    public TimeSpan? AvailabilityAge { get; }

    public TimeSpan? TimeToLive { get; }

    public FeatureQualityCoverage Coverage { get; }

    public decimal? KnownCoverage { get; }

    public int KnownObservationCount { get; }

    public int RequiredObservationCount { get; }

    public bool IsReady { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public string OriginalStatus => OriginalState;

    [JsonIgnore]
    public string StatusText => IsReady ? nameof(FeatureQualityStatus.Ready)
        : nameof(FeatureQualityStatus.Unknown);

    [JsonIgnore]
    public FeatureQualityStatus Status => IsReady
        ? FeatureQualityStatus.Ready
        : FeatureQualityStatus.Unknown;

    [JsonIgnore]
    public bool IsFresh => Freshness == FeatureQualityFreshness.Fresh;

    [JsonIgnore]
    public bool IsCoverageKnown => Coverage != FeatureQualityCoverage.Unknown;

    [JsonIgnore]
    public TimeSpan? EventAge => MarketDataAge;

    [JsonIgnore]
    public TimeSpan? AvailabilityTimeAge => AvailabilityAge;

    [JsonConstructor]
    public FeatureQualityEvidence(FeatureQualityFeatureKind kind,
        FeatureQualityInputSeries inputSeries, string formulaVersion,
        string originalState, string originalCompleteness,
        FeatureQualityFreshness originalFreshness, FeatureQualityFreshness freshness,
        TimeSpan? marketDataAge, TimeSpan? availabilityAge, TimeSpan? timeToLive,
        FeatureQualityCoverage coverage, decimal? knownCoverage,
        int knownObservationCount, int requiredObservationCount, bool isReady,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(kind, nameof(kind));
        ContractGuard.RequireDefined(inputSeries, nameof(inputSeries));
        ContractGuard.RequireText(formulaVersion, nameof(formulaVersion));
        ContractGuard.RequireText(originalState, nameof(originalState));
        ContractGuard.RequireText(originalCompleteness, nameof(originalCompleteness));
        ContractGuard.RequireDefined(originalFreshness, nameof(originalFreshness));
        ContractGuard.RequireDefined(freshness, nameof(freshness));
        ContractGuard.RequireDefined(coverage, nameof(coverage));
        ArgumentOutOfRangeException.ThrowIfNegative(knownObservationCount);
        ArgumentOutOfRangeException.ThrowIfNegative(requiredObservationCount);
        ValidateAge(marketDataAge, nameof(marketDataAge));
        ValidateAge(availabilityAge, nameof(availabilityAge));
        if (knownCoverage is { } ratio && (ratio < 0m || ratio > 1m))
        {
            throw new ArgumentOutOfRangeException(nameof(knownCoverage), knownCoverage,
                "Known coverage must be between zero and one.");
        }

        ArgumentNullException.ThrowIfNull(reasons);
        Kind = kind;
        InputSeries = inputSeries;
        FormulaVersion = formulaVersion;
        OriginalState = originalState;
        OriginalCompleteness = originalCompleteness;
        OriginalFreshness = originalFreshness;
        Freshness = freshness;
        MarketDataAge = marketDataAge;
        AvailabilityAge = availabilityAge;
        TimeToLive = timeToLive;
        Coverage = coverage;
        KnownCoverage = knownCoverage;
        KnownObservationCount = knownObservationCount;
        RequiredObservationCount = requiredObservationCount;
        IsReady = isReady;
        Reasons = new ImmutableValueList<string>(StableReasons(reasons));
    }

    internal static FeatureQualityEvidence Create(FeatureQualityFeatureKind kind,
        FeatureQualityInputSeries inputSeries, string formulaVersion, string originalState,
        string originalCompleteness, FeatureQualityFreshness originalFreshness,
        FeatureQualityFreshness freshness, TimeSpan? marketDataAge,
        TimeSpan? availabilityAge, TimeSpan? timeToLive, FeatureQualityCoverage coverage,
        decimal? knownCoverage, int knownObservationCount, int requiredObservationCount,
        bool isReady, IEnumerable<string> reasons) => new(kind, inputSeries, formulaVersion,
            originalState, originalCompleteness, originalFreshness, freshness, marketDataAge,
            availabilityAge, timeToLive, coverage, knownCoverage, knownObservationCount,
            requiredObservationCount, isReady, new ImmutableValueList<string>(StableReasons(
                reasons)));

    private static void ValidateAge(TimeSpan? age, string parameterName)
    {
        if (age is { } value && value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, age,
                "Feature quality ages cannot be negative.");
        }
    }

    private static IEnumerable<string> StableReasons(IEnumerable<string> reasons) =>
        reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal);
}

/// <summary>Immutable quality result for one cursor and explicit policy.</summary>
public sealed record FeatureQualityEvaluation
{
    public const string CurrentAlgorithmVersion = "feature-quality-v1";

    public string AlgorithmVersion { get; }

    public CausalEventCursor Cursor { get; }

    public FeatureQualityPolicy Policy { get; }

    public string? RollingFormulaVersion { get; }

    public string? PriceContextFormulaVersion { get; }

    public ImmutableValueList<FeatureQualityEvidence> Features { get; }

    [JsonIgnore]
    public FeatureQualityVersion Version => new(AlgorithmVersion);

    [JsonIgnore]
    public ImmutableValueList<FeatureQualityEvidence> Quality => Features;

    [JsonIgnore]
    public bool IsReady => Features.Count > 0 && Features.All(static feature => feature.IsReady);

    [JsonConstructor]
    public FeatureQualityEvaluation(string algorithmVersion, CausalEventCursor cursor,
        FeatureQualityPolicy policy, string? rollingFormulaVersion,
        string? priceContextFormulaVersion,
        ImmutableValueList<FeatureQualityEvidence> features)
    {
        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentException(
                "The feature quality algorithm version is unsupported.", nameof(algorithmVersion));
        }

        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(features);
        if (features.Any(static feature => feature is null))
        {
            throw new ArgumentException("Feature quality evidence cannot contain null values.",
                nameof(features));
        }

        AlgorithmVersion = algorithmVersion;
        Cursor = cursor;
        Policy = policy;
        RollingFormulaVersion = rollingFormulaVersion;
        PriceContextFormulaVersion = priceContextFormulaVersion;
        Features = new ImmutableValueList<FeatureQualityEvidence>(features
            .OrderBy(static feature => feature.Kind));
    }

    internal static FeatureQualityEvaluation Create(CausalEventCursor cursor,
        FeatureQualityPolicy policy, string? rollingFormulaVersion,
        string? priceContextFormulaVersion, IEnumerable<FeatureQualityEvidence> features) =>
        new(CurrentAlgorithmVersion, cursor, policy, rollingFormulaVersion,
            priceContextFormulaVersion, new ImmutableValueList<FeatureQualityEvidence>(features));
}
