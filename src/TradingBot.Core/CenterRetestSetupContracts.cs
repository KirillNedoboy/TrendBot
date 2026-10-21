using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies the explicit Center Retest setup-family version.</summary>
public sealed record CenterRetestSetupVersion
{
    public string Value { get; }

    [JsonConstructor]
    public CenterRetestSetupVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// Caller-supplied Center Retest configuration. The family has no numeric
/// parameters; a missing or unsupported version remains explicit unknown
/// evidence.
/// </summary>
public sealed record CenterRetestSetupConfiguration
{
    public CenterRetestSetupVersion? AlgorithmVersion { get; }

    [JsonIgnore]
    public CenterRetestSetupVersion? Version => AlgorithmVersion;

    public CenterRetestSetupConfiguration()
        : this(algorithmVersion: null)
    {
    }

    [JsonConstructor]
    public CenterRetestSetupConfiguration(
        CenterRetestSetupVersion? algorithmVersion)
    {
        AlgorithmVersion = algorithmVersion;
    }
}

/// <summary>
/// Per-cursor Center Retest observation state. This is not the Phase 8 setup
/// lifecycle; it reports only the family evaluation at one causal cursor.
/// </summary>
public enum CenterRetestSetupState
{
    Ready,
    NoSetup,
    Invalidated,
    UnknownConfiguration,
    UnknownInput,
    UnknownWarmup,
    UnknownQuality,
    UnknownOverflow,
    UnknownZeroDenominator
}

/// <summary>Literal pass/fail evidence for each SETUP-02 condition.</summary>
public sealed record CenterRetestConditionEvidence
{
    public bool HtfContextMatches { get; }

    public bool LocalTrendMatches { get; }

    public bool CenterTouched { get; }

    public bool OpenHoldsCenter { get; }

    public bool CloseHoldsCenter { get; }

    public bool CandleDirectionMatches { get; }

    public bool ClosedThroughCenter { get; }

    public bool BarConfirmed { get; }

    public bool DataQualityPassed { get; }

    [JsonIgnore]
    public bool AllConditionsPassed => HtfContextMatches && LocalTrendMatches
        && CenterTouched && OpenHoldsCenter && CloseHoldsCenter
        && CandleDirectionMatches && BarConfirmed && DataQualityPassed;

    [JsonConstructor]
    public CenterRetestConditionEvidence(bool htfContextMatches,
        bool localTrendMatches, bool centerTouched, bool openHoldsCenter,
        bool closeHoldsCenter, bool candleDirectionMatches,
        bool closedThroughCenter, bool barConfirmed, bool dataQualityPassed)
    {
        HtfContextMatches = htfContextMatches;
        LocalTrendMatches = localTrendMatches;
        CenterTouched = centerTouched;
        OpenHoldsCenter = openHoldsCenter;
        CloseHoldsCenter = closeHoldsCenter;
        CandleDirectionMatches = candleDirectionMatches;
        ClosedThroughCenter = closedThroughCenter;
        BarConfirmed = barConfirmed;
        DataQualityPassed = dataQualityPassed;
    }
}

/// <summary>
/// Immutable evidence for one direction of the Center Retest family at one
/// causal cursor. It retains the exact center, trend, HTF, and signal-bar values
/// used by the evaluation.
/// </summary>
public sealed record CenterRetestSetupObservation
{
    public Direction Direction { get; }

    public CenterRetestSetupState State { get; }

    public ScreenerBar? SignalBar { get; }

    public decimal? ChannelCenter { get; }

    public PriceContextTrendDirection? LocalTrendDirection { get; }

    public PriceContextHtfRegime? ConfirmedHtfRegime { get; }

    public PriceContextHtfCandidate? PendingHtfCandidate { get; }

    public int HtfPendingCount { get; }

    public CenterRetestConditionEvidence Conditions { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == CenterRetestSetupState.Ready;

    [JsonIgnore]
    public bool IsInvalidated => State == CenterRetestSetupState.Invalidated;

    [JsonConstructor]
    public CenterRetestSetupObservation(Direction direction,
        CenterRetestSetupState state, ScreenerBar? signalBar,
        decimal? channelCenter, PriceContextTrendDirection? localTrendDirection,
        PriceContextHtfRegime? confirmedHtfRegime,
        PriceContextHtfCandidate? pendingHtfCandidate, int htfPendingCount,
        CenterRetestConditionEvidence conditions,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentOutOfRangeException.ThrowIfNegative(htfPendingCount);

        Direction = direction;
        State = state;
        SignalBar = signalBar;
        ChannelCenter = channelCenter;
        LocalTrendDirection = localTrendDirection;
        ConfirmedHtfRegime = confirmedHtfRegime;
        PendingHtfCandidate = pendingHtfCandidate;
        HtfPendingCount = htfPendingCount;
        Conditions = conditions;
        Reasons = new ImmutableValueList<string>(StableReasons(reasons));
    }

    private static IEnumerable<string> StableReasons(IEnumerable<string> reasons) =>
        reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal);
}

#pragma warning disable CA1720 // Long and Short are the domain's direction terms.

/// <summary>
/// Immutable Center Retest evaluation for one causal price-context and quality
/// snapshot. Long and Short observations are always both present.
/// </summary>
public sealed record CenterRetestSetupResult
{
    public const string CurrentAlgorithmVersion = "center-retest-v1";

    public string AlgorithmVersion { get; }

    public CenterRetestSetupConfiguration Configuration { get; }

    public PriceContextFeatureResult PriceContext { get; }

    public FeatureQualityEvaluation Quality { get; }

    public CenterRetestSetupObservation Long { get; }

    public CenterRetestSetupObservation Short { get; }

    [JsonIgnore]
    public CausalEventCursor Cursor => PriceContext.Cursor;

    [JsonIgnore]
    public InstrumentId InstrumentId => PriceContext.Input.InstrumentId;

    [JsonIgnore]
    public string SourceId => PriceContext.Input.SourceId;

    [JsonIgnore]
    public string SetupTimeframe => PriceContext.Input.SetupTimeframe;

    [JsonIgnore]
    public ImmutableValueList<CenterRetestSetupObservation> Observations =>
        new(new[] { Long, Short });

    [JsonIgnore]
    public bool IsReady => Long.IsReady || Short.IsReady;

    [JsonConstructor]
    public CenterRetestSetupResult(string algorithmVersion,
        CenterRetestSetupConfiguration configuration,
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        CenterRetestSetupObservation @long,
        CenterRetestSetupObservation @short)
    {
        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentException(
                "The Center Retest algorithm version is unsupported.",
                nameof(algorithmVersion));
        }

        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(priceContext);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(@long);
        ArgumentNullException.ThrowIfNull(@short);
        if (quality.Cursor != priceContext.Cursor)
        {
            throw new ArgumentException(
                "Price-context and quality evidence must use the same causal cursor.",
                nameof(quality));
        }

        if (@long.Direction != Direction.Long)
        {
            throw new ArgumentException(
                "The long observation must use the Long direction.",
                nameof(@long));
        }

        if (@short.Direction != Direction.Short)
        {
            throw new ArgumentException(
                "The short observation must use the Short direction.",
                nameof(@short));
        }

        AlgorithmVersion = algorithmVersion;
        Configuration = configuration;
        PriceContext = priceContext;
        Quality = quality;
        Long = @long;
        Short = @short;
    }
}

#pragma warning restore CA1720 // Long and Short are the domain's direction terms.
