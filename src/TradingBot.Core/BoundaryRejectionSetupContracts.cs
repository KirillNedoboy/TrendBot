using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies the explicit Boundary Rejection setup-family version.</summary>
public sealed record BoundaryRejectionSetupVersion
{
    public string Value { get; }

    [JsonConstructor]
    public BoundaryRejectionSetupVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// Caller-supplied Boundary Rejection configuration. Missing or unsupported
/// values remain explicit unknown evidence; this contract supplies no default
/// channel width or trading threshold.
/// </summary>
public sealed record BoundaryRejectionSetupConfiguration
{
    public BoundaryRejectionSetupVersion? AlgorithmVersion { get; }

    public decimal? MinimumChannelWidth { get; }

    [JsonIgnore]
    public BoundaryRejectionSetupVersion? Version => AlgorithmVersion;

    public BoundaryRejectionSetupConfiguration()
        : this(null, null)
    {
    }

    [JsonConstructor]
    public BoundaryRejectionSetupConfiguration(
        BoundaryRejectionSetupVersion? algorithmVersion, decimal? minimumChannelWidth)
    {
        AlgorithmVersion = algorithmVersion;
        MinimumChannelWidth = minimumChannelWidth;
    }
}

/// <summary>
/// Per-cursor Boundary Rejection observation state. This is not the Phase 8
/// setup lifecycle; it reports only the family evaluation at one causal cursor.
/// </summary>
public enum BoundaryRejectionSetupState
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

/// <summary>Literal pass/fail evidence for each SETUP-01 condition.</summary>
public sealed record BoundaryRejectionConditionEvidence
{
    public bool HtfContextMatches { get; }

    public bool LocalTrendMatches { get; }

    public bool BoundaryTouched { get; }

    public bool CloseReclaimed { get; }

    public bool ClosedThroughBoundary { get; }

    public bool BarConfirmed { get; }

    public bool MinimumChannelWidthPassed { get; }

    public bool DataQualityPassed { get; }

    [JsonIgnore]
    public bool AllConditionsPassed => HtfContextMatches && LocalTrendMatches
        && BoundaryTouched && CloseReclaimed && BarConfirmed
        && MinimumChannelWidthPassed && DataQualityPassed;

    [JsonConstructor]
    public BoundaryRejectionConditionEvidence(bool htfContextMatches,
        bool localTrendMatches, bool boundaryTouched, bool closeReclaimed,
        bool closedThroughBoundary, bool barConfirmed,
        bool minimumChannelWidthPassed, bool dataQualityPassed)
    {
        HtfContextMatches = htfContextMatches;
        LocalTrendMatches = localTrendMatches;
        BoundaryTouched = boundaryTouched;
        CloseReclaimed = closeReclaimed;
        ClosedThroughBoundary = closedThroughBoundary;
        BarConfirmed = barConfirmed;
        MinimumChannelWidthPassed = minimumChannelWidthPassed;
        DataQualityPassed = dataQualityPassed;
    }
}

/// <summary>
/// Immutable evidence for one direction of the Boundary Rejection family at one
/// causal cursor. It retains the exact channel, trend, HTF, and signal-bar values
/// used by the evaluation.
/// </summary>
public sealed record BoundaryRejectionSetupObservation
{
    public Direction Direction { get; }

    public BoundaryRejectionSetupState State { get; }

    public decimal? MinimumChannelWidth { get; }

    public ScreenerBar? SignalBar { get; }

    public decimal? ChannelCenter { get; }

    public decimal? ChannelPriorCenter { get; }

    public decimal? ChannelHalfWidth { get; }

    public decimal? ChannelWidth { get; }

    public decimal? LowerBand { get; }

    public decimal? UpperBand { get; }

    public PriceContextTrendDirection? LocalTrendDirection { get; }

    public PriceContextHtfRegime? ConfirmedHtfRegime { get; }

    public PriceContextHtfCandidate? PendingHtfCandidate { get; }

    public int HtfPendingCount { get; }

    public BoundaryRejectionConditionEvidence Conditions { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == BoundaryRejectionSetupState.Ready;

    [JsonIgnore]
    public bool IsInvalidated => State == BoundaryRejectionSetupState.Invalidated;

    [JsonConstructor]
    public BoundaryRejectionSetupObservation(Direction direction,
        BoundaryRejectionSetupState state, decimal? minimumChannelWidth,
        ScreenerBar? signalBar, decimal? channelCenter, decimal? channelPriorCenter,
        decimal? channelHalfWidth, decimal? channelWidth, decimal? lowerBand,
        decimal? upperBand, PriceContextTrendDirection? localTrendDirection,
        PriceContextHtfRegime? confirmedHtfRegime,
        PriceContextHtfCandidate? pendingHtfCandidate, int htfPendingCount,
        BoundaryRejectionConditionEvidence conditions,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentOutOfRangeException.ThrowIfNegative(htfPendingCount);

        Direction = direction;
        State = state;
        MinimumChannelWidth = minimumChannelWidth;
        SignalBar = signalBar;
        ChannelCenter = channelCenter;
        ChannelPriorCenter = channelPriorCenter;
        ChannelHalfWidth = channelHalfWidth;
        ChannelWidth = channelWidth;
        LowerBand = lowerBand;
        UpperBand = upperBand;
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
/// Immutable Boundary Rejection evaluation for one causal price-context and
/// quality snapshot. Long and Short observations are always both present.
/// </summary>
public sealed record BoundaryRejectionSetupResult
{
    public const string CurrentAlgorithmVersion = "boundary-rejection-v1";

    public string AlgorithmVersion { get; }

    public BoundaryRejectionSetupConfiguration Configuration { get; }

    public PriceContextFeatureResult PriceContext { get; }

    public FeatureQualityEvaluation Quality { get; }

    public BoundaryRejectionSetupObservation Long { get; }

    public BoundaryRejectionSetupObservation Short { get; }

    [JsonIgnore]
    public CausalEventCursor Cursor => PriceContext.Cursor;

    [JsonIgnore]
    public InstrumentId InstrumentId => PriceContext.Input.InstrumentId;

    [JsonIgnore]
    public string SourceId => PriceContext.Input.SourceId;

    [JsonIgnore]
    public string SetupTimeframe => PriceContext.Input.SetupTimeframe;

    [JsonIgnore]
    public ImmutableValueList<BoundaryRejectionSetupObservation> Observations =>
        new(new[] { Long, Short });

    [JsonIgnore]
    public bool IsReady => Long.IsReady || Short.IsReady;

    [JsonConstructor]
    public BoundaryRejectionSetupResult(string algorithmVersion,
        BoundaryRejectionSetupConfiguration configuration,
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        BoundaryRejectionSetupObservation @long,
        BoundaryRejectionSetupObservation @short)
    {
        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentException(
                "The Boundary Rejection algorithm version is unsupported.",
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

        if (@long.MinimumChannelWidth != configuration.MinimumChannelWidth
            || @short.MinimumChannelWidth != configuration.MinimumChannelWidth)
        {
            throw new ArgumentException(
                "Observation evidence must use the configured minimum channel width.",
                nameof(configuration));
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
