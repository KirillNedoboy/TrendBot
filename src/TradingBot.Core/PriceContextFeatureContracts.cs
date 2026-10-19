using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies one output of the price-context v2 calculator.</summary>
public enum PriceContextFeatureKind
{
    ConfirmedPivots,
    StructuralLevels,
    Channel,
    LocalTrend,
    HtfContext,
    Vwap,
    Poc
}

/// <summary>Describes whether a price-context output is usable.</summary>
public enum PriceContextFeatureState
{
    Ready,
    UnknownWarmup,
    UnknownConfiguration,
    UnknownInput,
    UnknownZeroDenominator,
    UnknownOverflow,
    UnknownQuality
}

/// <summary>Describes the completeness of one output's required evidence.</summary>
public enum PriceContextFeatureCompleteness
{
    Complete,
    Warmup,
    Incomplete,
    Unknown
}

/// <summary>Moving-average formulas allowed by the price-context contract.</summary>
public enum PriceContextMovingAverageKind
{
    Sma,
    Ema,
    Wma,
    Rma
}

/// <summary>ATR smoothing formulas allowed for channel and level buffers.</summary>
public enum PriceContextAtrMode
{
    Sma,
    Rma
}

/// <summary>Compatibility spelling for the ATR smoothing enum.</summary>
public enum PriceContextAtrSmoothingKind
{
    Sma,
    Rma
}

/// <summary>Center formula used by the local channel.</summary>
public enum PriceContextChannelCenterKind
{
    Ema,
    Rma,
    Hlc3Smoothed
}

/// <summary>Half-width formula used by the local channel.</summary>
public enum PriceContextChannelWidthKind
{
    Atr,
    StandardDeviation
}

/// <summary>Strict structural pivot type.</summary>
public enum PriceContextPivotKind
{
    Low,
    High
}

/// <summary>Role held by one active structural level.</summary>
public enum PriceContextLevelRole
{
    Support,
    Resistance
}

/// <summary>Candidate HTF regime before confirmation.</summary>
public enum PriceContextHtfCandidate
{
    Bull,
    Bear,
    Neutral
}

/// <summary>Confirmed HTF regime. Pending is exposed only as a status alias.</summary>
public enum PriceContextHtfRegime
{
    Bull,
    Bear,
    Neutral,
    Pending
}

/// <summary>Direction calculated from two local channel centers.</summary>
public enum PriceContextTrendDirection
{
    Bull,
    Bear,
    Neutral,
    Up = Bull,
    Down = Bear,
    Flat = Neutral
}

/// <summary>Identifies the explicit price-context algorithm.</summary>
public sealed record PriceContextFeatureVersion
{
    public string Value { get; }

    [JsonConstructor]
    public PriceContextFeatureVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>
/// One caller-supplied exact trade price and quantity. OHLCV is never used as
/// a substitute for this observation in VWAP or POC.
/// </summary>
public sealed record PriceVolumeObservation
{
    public string SourceId { get; }

    public InstrumentId InstrumentId { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public Price Price { get; }

    public Quantity Volume { get; }

    [JsonIgnore]
    public decimal VolumeValue => Volume.Value;

    public PriceVolumeObservation(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, decimal price, decimal volume)
        : this(sourceId, instrumentId, eventTime, receiveTime, availabilityTime,
            stableSequence, new Price(price), new Quantity(volume))
    {
    }

    [JsonConstructor]
    public PriceVolumeObservation(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, Price price, Quantity volume)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException(
                "Availability time cannot precede receive time.", nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Stable sequence cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(volume);
        SourceId = sourceId;
        InstrumentId = instrumentId;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        Price = price;
        Volume = volume;
    }
}

/// <summary>Explicit, independently causal setup or HTF bar history.</summary>
public sealed record PriceContextBarHistory
{
    public string SourceId { get; }

    public InstrumentId InstrumentId { get; }

    public string Timeframe { get; }

    public UtcTimestamp HistoryStart { get; }

    public ImmutableValueList<ScreenerBar> ClosedBars { get; }

    [JsonIgnore]
    public ImmutableValueList<ScreenerBar> Bars => ClosedBars;

    public PriceContextBarHistory(string sourceId, InstrumentId instrumentId,
        string timeframe, UtcTimestamp historyStart, IEnumerable<ScreenerBar> closedBars)
        : this(sourceId, instrumentId, timeframe, historyStart,
            new ImmutableValueList<ScreenerBar>(RequireBars(closedBars)))
    {
    }

    [JsonConstructor]
    public PriceContextBarHistory(string sourceId, InstrumentId instrumentId,
        string timeframe, UtcTimestamp historyStart,
        ImmutableValueList<ScreenerBar> closedBars)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(timeframe, nameof(timeframe));
        ArgumentNullException.ThrowIfNull(historyStart);
        ArgumentNullException.ThrowIfNull(closedBars);
        ScreenerBar[] bars = closedBars.ToArray();
        ValidateBars(sourceId, instrumentId, bars, nameof(closedBars));
        SourceId = sourceId;
        InstrumentId = instrumentId;
        Timeframe = timeframe;
        HistoryStart = historyStart;
        ClosedBars = new ImmutableValueList<ScreenerBar>(CanonicalBars(bars));
    }

    private static IEnumerable<ScreenerBar> RequireBars(IEnumerable<ScreenerBar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        return bars;
    }

    internal static void ValidateBars(string sourceId, InstrumentId instrumentId,
        IEnumerable<ScreenerBar> bars, string parameterName)
    {
        ScreenerBar[] snapshot = bars.ToArray();
        if (snapshot.Any(static item => item is null))
        {
            throw new ArgumentException("A bar history cannot contain null values.", parameterName);
        }

        if (snapshot.Any(item => !StringComparer.Ordinal.Equals(item.SourceId, sourceId)))
        {
            throw new ArgumentException("Every history bar must use the input source.",
                parameterName);
        }

        if (snapshot.Any(item => item.InstrumentId != instrumentId))
        {
            throw new ArgumentException("Every history bar must use the input instrument.",
                parameterName);
        }
    }

    internal static IEnumerable<ScreenerBar> CanonicalBars(IEnumerable<ScreenerBar> bars) =>
        bars.OrderBy(static item => item.Bar.OpenTime.Value)
            .ThenBy(static item => item.Bar.CloseTime.Value)
            .ThenBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.AvailabilityTime.Value)
            .ThenBy(static item => item.ReceiveTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Bar.EventId, StringComparer.Ordinal)
            .ThenBy(static item => item.Bar.Open.Value)
            .ThenBy(static item => item.Bar.High.Value)
            .ThenBy(static item => item.Bar.Low.Value)
            .ThenBy(static item => item.Bar.Close.Value)
            .ThenBy(static item => item.Bar.Volume.Value)
            .ThenBy(static item => item.QuoteVolume);
}

/// <summary>
/// Immutable input containing separate setup and HTF histories. The histories
/// are never aggregated or selected by the calculator.
/// </summary>
public sealed record PriceContextFeatureInput
{
    public string SourceId { get; }

    public InstrumentId InstrumentId { get; }

    public string SetupTimeframe { get; }

    public UtcTimestamp SetupHistoryStart { get; }

    public ImmutableValueList<ScreenerBar> SetupBars { get; }

    public string HtfTimeframe { get; }

    public UtcTimestamp HtfHistoryStart { get; }

    public ImmutableValueList<ScreenerBar> HtfBars { get; }

    public ImmutableValueList<PriceVolumeObservation> PriceVolumeObservations { get; }

    [JsonIgnore]
    public string Timeframe => SetupTimeframe;

    [JsonIgnore]
    public UtcTimestamp HistoryStart => SetupHistoryStart;

    [JsonIgnore]
    public ImmutableValueList<ScreenerBar> ClosedBars => SetupBars;

    [JsonIgnore]
    public ImmutableValueList<ScreenerBar> Bars => SetupBars;

    [JsonIgnore]
    public ImmutableValueList<PriceVolumeObservation> Trades => PriceVolumeObservations;

    public PriceContextFeatureInput(string sourceId, InstrumentId instrumentId,
        string setupTimeframe, UtcTimestamp setupHistoryStart,
        IEnumerable<ScreenerBar> setupBars, string htfTimeframe,
        UtcTimestamp htfHistoryStart, IEnumerable<ScreenerBar> htfBars,
        IEnumerable<PriceVolumeObservation> priceVolumeObservations)
        : this(sourceId, instrumentId, setupTimeframe, setupHistoryStart,
            new ImmutableValueList<ScreenerBar>(RequireValues(setupBars)), htfTimeframe,
            htfHistoryStart, new ImmutableValueList<ScreenerBar>(RequireValues(htfBars)),
            new ImmutableValueList<PriceVolumeObservation>(RequireValues(
                priceVolumeObservations)))
    {
    }

    public PriceContextFeatureInput(string sourceId, InstrumentId instrumentId,
        PriceContextBarHistory setupHistory, PriceContextBarHistory htfHistory,
        IEnumerable<PriceVolumeObservation> priceVolumeObservations)
        : this(sourceId, instrumentId, setupHistory.Timeframe, setupHistory.HistoryStart,
            setupHistory.ClosedBars, htfHistory.Timeframe, htfHistory.HistoryStart,
            htfHistory.ClosedBars, priceVolumeObservations)
    {
        ArgumentNullException.ThrowIfNull(setupHistory);
        ArgumentNullException.ThrowIfNull(htfHistory);
    }

    [JsonConstructor]
    public PriceContextFeatureInput(string sourceId, InstrumentId instrumentId,
        string setupTimeframe, UtcTimestamp setupHistoryStart,
        ImmutableValueList<ScreenerBar> setupBars, string htfTimeframe,
        UtcTimestamp htfHistoryStart, ImmutableValueList<ScreenerBar> htfBars,
        ImmutableValueList<PriceVolumeObservation> priceVolumeObservations)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(setupTimeframe, nameof(setupTimeframe));
        ContractGuard.RequireText(htfTimeframe, nameof(htfTimeframe));
        ArgumentNullException.ThrowIfNull(setupHistoryStart);
        ArgumentNullException.ThrowIfNull(htfHistoryStart);
        ArgumentNullException.ThrowIfNull(setupBars);
        ArgumentNullException.ThrowIfNull(htfBars);
        ArgumentNullException.ThrowIfNull(priceVolumeObservations);
        PriceContextBarHistory.ValidateBars(sourceId, instrumentId, setupBars,
            nameof(setupBars));
        PriceContextBarHistory.ValidateBars(sourceId, instrumentId, htfBars,
            nameof(htfBars));
        PriceVolumeObservation[] observations = priceVolumeObservations.ToArray();
        if (observations.Any(static item => item is null))
        {
            throw new ArgumentException("Price-volume observations cannot contain null values.",
                nameof(priceVolumeObservations));
        }

        if (observations.Any(item => !StringComparer.Ordinal.Equals(item.SourceId, sourceId)))
        {
            throw new ArgumentException(
                "Every price-volume observation must use the input source.",
                nameof(priceVolumeObservations));
        }

        if (observations.Any(item => item.InstrumentId != instrumentId))
        {
            throw new ArgumentException(
                "Every price-volume observation must use the input instrument.",
                nameof(priceVolumeObservations));
        }

        SourceId = sourceId;
        InstrumentId = instrumentId;
        SetupTimeframe = setupTimeframe;
        SetupHistoryStart = setupHistoryStart;
        SetupBars = new ImmutableValueList<ScreenerBar>(
            PriceContextBarHistory.CanonicalBars(setupBars));
        HtfTimeframe = htfTimeframe;
        HtfHistoryStart = htfHistoryStart;
        HtfBars = new ImmutableValueList<ScreenerBar>(
            PriceContextBarHistory.CanonicalBars(htfBars));
        PriceVolumeObservations = new ImmutableValueList<PriceVolumeObservation>(
            CanonicalPriceVolume(observations));
    }

    private static IEnumerable<T> RequireValues<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values;
    }

    private static IEnumerable<PriceVolumeObservation> CanonicalPriceVolume(
        IEnumerable<PriceVolumeObservation> observations) =>
        observations.OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.AvailabilityTime.Value)
            .ThenBy(static item => item.ReceiveTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Price.Value)
            .ThenBy(static item => item.Volume.Value);
}

/// <summary>
/// Explicit v2 configuration. Nullable values are deliberate: the calculator
/// never supplies a period, multiplier, buffer, timeframe, or enum default.
/// </summary>
public sealed record PriceContextFeatureConfiguration
{
    public string? SetupTimeframe { get; }

    public string? HtfTimeframe { get; }

    public PriceContextFeatureVersion? AlgorithmVersion { get; }

    public int? PivotLeft { get; }

    public int? PivotRight { get; }

    public int? AtrPeriod { get; }

    public PriceContextAtrMode? AtrMode { get; }

    public PriceContextChannelCenterKind? CenterKind { get; }

    public int? CenterPeriod { get; }

    public PriceContextMovingAverageKind? Hlc3SmoothingKind { get; }

    public PriceContextChannelWidthKind? WidthKind { get; }

    public int? ChannelWidthPeriod { get; }

    public decimal? ChannelWidthMultiplier { get; }

    public decimal? LevelBufferMultiplier { get; }

    public int? HtfFastPeriod { get; }

    public int? HtfSlowPeriod { get; }

    public PriceContextMovingAverageKind? HtfMovingAverageKind { get; }

    public int? HtfConfirmationCount { get; }

    public int? VwapWindow { get; }

    public int? PocWindow { get; }

    public decimal? PocBinSize { get; }

    [JsonIgnore]
    public string? Timeframe => SetupTimeframe;

    [JsonIgnore]
    public PriceContextFeatureVersion? Version => AlgorithmVersion;

    [JsonIgnore]
    public PriceContextChannelCenterKind? ChannelCenterKind => CenterKind;

    [JsonIgnore]
    public PriceContextChannelWidthKind? ChannelWidthKind => WidthKind;

    [JsonIgnore]
    public int? WidthPeriod => ChannelWidthPeriod;

    [JsonIgnore]
    public int? HtfFastMaPeriod => HtfFastPeriod;

    [JsonIgnore]
    public int? HtfSlowMaPeriod => HtfSlowPeriod;

    public PriceContextFeatureConfiguration()
        : this(null, null, null, null, null, null, (PriceContextAtrMode?)null, null, null, null, null,
            null, null, null, null, null, null, null, null, null, null)
    {
    }

    [JsonConstructor]
    public PriceContextFeatureConfiguration(string? setupTimeframe, string? htfTimeframe,
        PriceContextFeatureVersion? algorithmVersion, int? pivotLeft, int? pivotRight,
        int? atrPeriod, PriceContextAtrMode? atrMode,
        PriceContextChannelCenterKind? centerKind, int? centerPeriod,
        PriceContextMovingAverageKind? hlc3SmoothingKind,
        PriceContextChannelWidthKind? widthKind, int? channelWidthPeriod,
        decimal? channelWidthMultiplier, decimal? levelBufferMultiplier,
        int? htfFastPeriod, int? htfSlowPeriod,
        PriceContextMovingAverageKind? htfMovingAverageKind, int? htfConfirmationCount,
        int? vwapWindow, int? pocWindow, decimal? pocBinSize)
    {
        SetupTimeframe = setupTimeframe;
        HtfTimeframe = htfTimeframe;
        AlgorithmVersion = algorithmVersion;
        PivotLeft = pivotLeft;
        PivotRight = pivotRight;
        AtrPeriod = atrPeriod;
        AtrMode = atrMode;
        CenterKind = centerKind;
        CenterPeriod = centerPeriod;
        Hlc3SmoothingKind = hlc3SmoothingKind;
        WidthKind = widthKind;
        ChannelWidthPeriod = channelWidthPeriod;
        ChannelWidthMultiplier = channelWidthMultiplier;
        LevelBufferMultiplier = levelBufferMultiplier;
        HtfFastPeriod = htfFastPeriod;
        HtfSlowPeriod = htfSlowPeriod;
        HtfMovingAverageKind = htfMovingAverageKind;
        HtfConfirmationCount = htfConfirmationCount;
        VwapWindow = vwapWindow;
        PocWindow = pocWindow;
        PocBinSize = pocBinSize;
    }

    public PriceContextFeatureConfiguration(string? setupTimeframe, string? htfTimeframe,
        PriceContextFeatureVersion? algorithmVersion, int? pivotLeft, int? pivotRight,
        int? atrPeriod, PriceContextAtrSmoothingKind? atrSmoothingKind,
        PriceContextChannelCenterKind? centerKind, int? centerPeriod,
        PriceContextMovingAverageKind? hlc3SmoothingKind,
        PriceContextChannelWidthKind? widthKind, int? channelWidthPeriod,
        decimal? channelWidthMultiplier, decimal? levelBufferMultiplier,
        int? htfFastPeriod, int? htfSlowPeriod,
        PriceContextMovingAverageKind? htfMovingAverageKind, int? htfConfirmationCount,
        int? vwapWindow, int? pocWindow, decimal? pocBinSize)
        : this(setupTimeframe, htfTimeframe, algorithmVersion, pivotLeft, pivotRight,
            atrPeriod, atrSmoothingKind is null ? null : (PriceContextAtrMode?)
                (int)atrSmoothingKind.Value, centerKind, centerPeriod,
            hlc3SmoothingKind, widthKind, channelWidthPeriod, channelWidthMultiplier,
            levelBufferMultiplier, htfFastPeriod, htfSlowPeriod, htfMovingAverageKind,
            htfConfirmationCount, vwapWindow, pocWindow, pocBinSize)
    {
    }
}

/// <summary>Canonical eligible bars, volume observations, and causal reasons.</summary>
public sealed record PriceContextObservationQuality
{
    public ImmutableValueList<ScreenerBar> EligibleSetupBars { get; }

    public ImmutableValueList<ScreenerBar> EligibleHtfBars { get; }

    public ImmutableValueList<PriceVolumeObservation> EligiblePriceVolumeObservations { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public ImmutableValueList<ScreenerBar> EligibleBars => EligibleSetupBars;

    [JsonIgnore]
    public ImmutableValueList<PriceVolumeObservation> EligibleTrades =>
        EligiblePriceVolumeObservations;

    public PriceContextObservationQuality(IEnumerable<ScreenerBar> eligibleSetupBars,
        IEnumerable<ScreenerBar> eligibleHtfBars,
        IEnumerable<PriceVolumeObservation> eligiblePriceVolumeObservations,
        IEnumerable<string> reasons)
        : this(new ImmutableValueList<ScreenerBar>(RequireValues(eligibleSetupBars)),
            new ImmutableValueList<ScreenerBar>(RequireValues(eligibleHtfBars)),
            new ImmutableValueList<PriceVolumeObservation>(RequireValues(
                eligiblePriceVolumeObservations)),
            new ImmutableValueList<string>(RequireValues(reasons)))
    {
    }

    [JsonConstructor]
    public PriceContextObservationQuality(
        ImmutableValueList<ScreenerBar> eligibleSetupBars,
        ImmutableValueList<ScreenerBar> eligibleHtfBars,
        ImmutableValueList<PriceVolumeObservation> eligiblePriceVolumeObservations,
        ImmutableValueList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(eligibleSetupBars);
        ArgumentNullException.ThrowIfNull(eligibleHtfBars);
        ArgumentNullException.ThrowIfNull(eligiblePriceVolumeObservations);
        ArgumentNullException.ThrowIfNull(reasons);
        if (eligibleSetupBars.Any(static item => item is null)
            || eligibleHtfBars.Any(static item => item is null)
            || eligiblePriceVolumeObservations.Any(static item => item is null))
        {
            throw new ArgumentException("Eligible observations cannot contain null values.");
        }

        EligibleSetupBars = new ImmutableValueList<ScreenerBar>(eligibleSetupBars);
        EligibleHtfBars = new ImmutableValueList<ScreenerBar>(eligibleHtfBars);
        EligiblePriceVolumeObservations = new ImmutableValueList<PriceVolumeObservation>(
            eligiblePriceVolumeObservations);
        Reasons = StableReasons(reasons);
    }

    private static IEnumerable<T> RequireValues<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values;
    }

    internal static ImmutableValueList<string> StableReasons(IEnumerable<string> reasons) =>
        new(reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));
}

/// <summary>One causally confirmed strict structural pivot.</summary>
public record PriceContextPivotEvidence
{
    public PriceContextPivotKind Kind { get; }

    public ScreenerBar PivotBar { get; }

    public decimal Anchor { get; }

    public decimal? AtrAtConfirmation { get; }

    public decimal? FrozenBuffer { get; }

    public int PivotLeft { get; }

    public int PivotRight { get; }

    public ImmutableValueList<ScreenerBar> PivotWindow { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public UtcTimestamp ClosedBarTime { get; }

    public UtcTimestamp ConfirmationTime { get; }

    public long ConfirmationSequence { get; }

    [JsonIgnore]
    public PriceContextPivotKind PivotType => Kind;

    [JsonIgnore]
    public decimal? Buffer => FrozenBuffer;

    [JsonIgnore]
    public long ConfirmationStableSequence => ConfirmationSequence;

    [JsonConstructor]
    public PriceContextPivotEvidence(PriceContextPivotKind kind, ScreenerBar pivotBar,
        decimal anchor, decimal? atrAtConfirmation, decimal? frozenBuffer, int pivotLeft,
        int pivotRight, ImmutableValueList<ScreenerBar> pivotWindow,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, UtcTimestamp closedBarTime, UtcTimestamp confirmationTime,
        long confirmationSequence)
    {
        ContractGuard.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(pivotBar);
        ArgumentNullException.ThrowIfNull(pivotWindow);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ArgumentNullException.ThrowIfNull(closedBarTime);
        ArgumentNullException.ThrowIfNull(confirmationTime);
        if (pivotLeft <= 0 || pivotRight <= 0 || stableSequence < 0
            || confirmationSequence < 0 || anchor <= 0m
            || (atrAtConfirmation.HasValue && atrAtConfirmation.Value < 0m)
            || (frozenBuffer.HasValue && frozenBuffer.Value < 0m)
            || atrAtConfirmation.HasValue != frozenBuffer.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(pivotLeft),
                "Pivot evidence values must be valid non-negative domain values.");
        }

        Kind = kind;
        PivotBar = pivotBar;
        Anchor = anchor;
        AtrAtConfirmation = atrAtConfirmation;
        FrozenBuffer = frozenBuffer;
        PivotLeft = pivotLeft;
        PivotRight = pivotRight;
        PivotWindow = new ImmutableValueList<ScreenerBar>(pivotWindow);
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        ClosedBarTime = closedBarTime;
        ConfirmationTime = confirmationTime;
        ConfirmationSequence = confirmationSequence;
    }
}

/// <summary>Compatibility name for confirmed structural pivot evidence.</summary>
public sealed record PriceContextConfirmedPivotEvidence : PriceContextPivotEvidence
{
    [JsonConstructor]
    public PriceContextConfirmedPivotEvidence(PriceContextPivotKind kind,
        ScreenerBar pivotBar, decimal anchor, decimal? atrAtConfirmation,
        decimal? frozenBuffer, int pivotLeft, int pivotRight,
        ImmutableValueList<ScreenerBar> pivotWindow, UtcTimestamp eventTime,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime, long stableSequence,
        UtcTimestamp closedBarTime, UtcTimestamp confirmationTime,
        long confirmationSequence)
        : base(kind, pivotBar, anchor, atrAtConfirmation, frozenBuffer, pivotLeft,
            pivotRight, pivotWindow, eventTime, receiveTime, availabilityTime,
            stableSequence, closedBarTime, confirmationTime, confirmationSequence)
    {
    }
}

/// <summary>All strict pivots visible at one causal cursor.</summary>
public sealed record PriceContextConfirmedPivots
{
    public PriceContextFeatureState State { get; }

    public ImmutableValueList<PriceContextPivotEvidence> Pivots { get; }

    public int? PivotLeft { get; }

    public int? PivotRight { get; }

    public int RequiredBars { get; }

    public int AvailableBars { get; }

    public PriceContextFeatureCompleteness Completeness { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == PriceContextFeatureState.Ready;

    [JsonIgnore]
    public ImmutableValueList<PriceContextPivotEvidence> Confirmed => Pivots;

    [JsonConstructor]
    public PriceContextConfirmedPivots(PriceContextFeatureState state,
        ImmutableValueList<PriceContextPivotEvidence> pivots, int? pivotLeft,
        int? pivotRight, int requiredBars, int availableBars,
        PriceContextFeatureCompleteness completeness, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireDefined(completeness, nameof(completeness));
        ArgumentNullException.ThrowIfNull(pivots);
        ArgumentNullException.ThrowIfNull(reasons);
        if (requiredBars < 0 || availableBars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBars));
        }

        State = state;
        Pivots = new ImmutableValueList<PriceContextPivotEvidence>(pivots);
        PivotLeft = pivotLeft;
        PivotRight = pivotRight;
        RequiredBars = requiredBars;
        AvailableBars = availableBars;
        Completeness = completeness;
        Reasons = PriceContextObservationQuality.StableReasons(reasons);
    }
}

/// <summary>An active support or resistance level carrying its originating pivot.</summary>
public sealed record PriceContextStructuralLevel
{
    public PriceContextLevelRole Role { get; }

    public decimal Anchor { get; }

    public decimal FrozenBuffer { get; }

    public PriceContextPivotEvidence Pivot { get; }

    [JsonIgnore]
    public decimal Boundary => Role == PriceContextLevelRole.Support
        ? Anchor - FrozenBuffer
        : Anchor + FrozenBuffer;

    [JsonIgnore]
    public decimal Buffer => FrozenBuffer;

    [JsonConstructor]
    public PriceContextStructuralLevel(PriceContextLevelRole role, decimal anchor,
        decimal frozenBuffer, PriceContextPivotEvidence pivot)
    {
        ContractGuard.RequireDefined(role, nameof(role));
        ArgumentNullException.ThrowIfNull(pivot);
        if (anchor <= 0m || frozenBuffer < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(anchor));
        }

        Role = role;
        Anchor = anchor;
        FrozenBuffer = frozenBuffer;
        Pivot = pivot;
    }
}

/// <summary>One active support and resistance with causal quality state.</summary>
public sealed record PriceContextStructuralLevels
{
    public PriceContextFeatureState State { get; }

    public PriceContextStructuralLevel? Support { get; }

    public PriceContextStructuralLevel? Resistance { get; }

    public decimal? RawSupport { get; }

    public decimal? RawResistance { get; }

    public int RequiredBars { get; }

    public int AvailableBars { get; }

    public PriceContextFeatureCompleteness Completeness { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == PriceContextFeatureState.Ready;

    [JsonConstructor]
    public PriceContextStructuralLevels(PriceContextFeatureState state,
        PriceContextStructuralLevel? support, PriceContextStructuralLevel? resistance,
        decimal? rawSupport, decimal? rawResistance, int requiredBars, int availableBars,
        PriceContextFeatureCompleteness completeness, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireDefined(completeness, nameof(completeness));
        ArgumentNullException.ThrowIfNull(reasons);
        if (requiredBars < 0 || availableBars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBars));
        }

        State = state;
        Support = support;
        Resistance = resistance;
        RawSupport = rawSupport;
        RawResistance = rawResistance;
        RequiredBars = requiredBars;
        AvailableBars = availableBars;
        Completeness = completeness;
        Reasons = PriceContextObservationQuality.StableReasons(reasons);
    }
}

/// <summary>ATR or population-standard-deviation local channel.</summary>
public sealed record PriceContextChannel
{
    public PriceContextFeatureState State { get; }

    public decimal? Center { get; }

    public decimal? PriorCenter { get; }

    public decimal? Upper { get; }

    public decimal? Lower { get; }

    public decimal? HalfWidth { get; }

    public decimal? Width { get; }

    public decimal? DonchianWidth { get; }

    public decimal? Atr { get; }

    public decimal? StandardDeviation { get; }

    public int? CenterPeriod { get; }

    public int? WidthPeriod { get; }

    public PriceContextChannelCenterKind? CenterKind { get; }

    public PriceContextChannelWidthKind? WidthKind { get; }

    public PriceContextMovingAverageKind? Hlc3SmoothingKind { get; }

    public int RequiredBars { get; }

    public int AvailableBars { get; }

    public PriceContextFeatureCompleteness Completeness { get; }

    public ImmutableValueList<ScreenerBar> Provenance { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == PriceContextFeatureState.Ready && Center.HasValue
        && HalfWidth.HasValue;

    [JsonConstructor]
    public PriceContextChannel(PriceContextFeatureState state, decimal? center,
        decimal? priorCenter, decimal? upper, decimal? lower, decimal? halfWidth,
        decimal? width, decimal? donchianWidth, decimal? atr, decimal? standardDeviation,
        int? centerPeriod, int? widthPeriod, PriceContextChannelCenterKind? centerKind,
        PriceContextChannelWidthKind? widthKind,
        PriceContextMovingAverageKind? hlc3SmoothingKind, int requiredBars,
        int availableBars, PriceContextFeatureCompleteness completeness,
        ImmutableValueList<ScreenerBar> provenance, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireDefined(completeness, nameof(completeness));
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(reasons);
        if (requiredBars < 0 || availableBars < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBars));
        }

        State = state;
        Center = center;
        PriorCenter = priorCenter;
        Upper = upper;
        Lower = lower;
        HalfWidth = halfWidth;
        Width = width;
        DonchianWidth = donchianWidth;
        Atr = atr;
        StandardDeviation = standardDeviation;
        CenterPeriod = centerPeriod;
        WidthPeriod = widthPeriod;
        CenterKind = centerKind;
        WidthKind = widthKind;
        Hlc3SmoothingKind = hlc3SmoothingKind;
        RequiredBars = requiredBars;
        AvailableBars = availableBars;
        Completeness = completeness;
        Provenance = new ImmutableValueList<ScreenerBar>(provenance);
        Reasons = PriceContextObservationQuality.StableReasons(reasons);
    }
}

/// <summary>Local trend derived from the current and prior channel centers.</summary>
public sealed record PriceContextLocalTrend
{
    public PriceContextFeatureState State { get; }

    public PriceContextTrendDirection? Direction { get; }

    public decimal? CurrentCenter { get; }

    public decimal? PriorCenter { get; }

    public decimal? CurrentClose { get; }

    public ScreenerBar? CurrentBar { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == PriceContextFeatureState.Ready && Direction.HasValue;

    [JsonConstructor]
    public PriceContextLocalTrend(PriceContextFeatureState state,
        PriceContextTrendDirection? direction, decimal? currentCenter,
        decimal? priorCenter, decimal? currentClose, ScreenerBar? currentBar,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(reasons);
        State = state;
        Direction = direction;
        CurrentCenter = currentCenter;
        PriorCenter = priorCenter;
        CurrentClose = currentClose;
        CurrentBar = currentBar;
        Reasons = PriceContextObservationQuality.StableReasons(reasons);
    }
}

/// <summary>HTF candidate and confirmed regime state.</summary>
public sealed record PriceContextHtfContext
{
    public PriceContextFeatureState State { get; }

    public PriceContextHtfCandidate? PendingCandidate { get; }

    public int PendingCount { get; }

    public PriceContextHtfRegime? ConfirmedRegime { get; }

    public decimal? FastAverage { get; }

    public decimal? SlowAverage { get; }

    public decimal? CurrentClose { get; }

    public decimal? RawSupport { get; }

    public decimal? RawResistance { get; }

    public ScreenerBar? CurrentBar { get; }

    public ImmutableValueList<ScreenerBar> Provenance { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public PriceContextHtfRegime? LastConfirmedRegime => ConfirmedRegime;

    [JsonIgnore]
    public bool IsReady => State == PriceContextFeatureState.Ready;

    [JsonConstructor]
    public PriceContextHtfContext(PriceContextFeatureState state,
        PriceContextHtfCandidate? pendingCandidate, int pendingCount,
        PriceContextHtfRegime? confirmedRegime, decimal? fastAverage,
        decimal? slowAverage, decimal? currentClose, decimal? rawSupport,
        decimal? rawResistance, ScreenerBar? currentBar,
        ImmutableValueList<ScreenerBar> provenance, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentOutOfRangeException.ThrowIfNegative(pendingCount);

        State = state;
        PendingCandidate = pendingCandidate;
        PendingCount = pendingCount;
        ConfirmedRegime = confirmedRegime;
        FastAverage = fastAverage;
        SlowAverage = slowAverage;
        CurrentClose = currentClose;
        RawSupport = rawSupport;
        RawResistance = rawResistance;
        CurrentBar = currentBar;
        Provenance = new ImmutableValueList<ScreenerBar>(provenance);
        Reasons = PriceContextObservationQuality.StableReasons(reasons);
    }
}

/// <summary>One exact VWAP or POC scalar output.</summary>
public sealed record PriceContextFeatureValue
{
    public PriceContextFeatureKind Kind { get; }

    public PriceContextFeatureState State { get; }

    public decimal? Value { get; }

    public int? Period { get; }

    public int RequiredObservations { get; }

    public int AvailableObservations { get; }

    public PriceContextFeatureCompleteness Completeness { get; }

    public ImmutableValueList<PriceVolumeObservation> Provenance { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => State == PriceContextFeatureState.Ready && Value.HasValue;

    [JsonConstructor]
    public PriceContextFeatureValue(PriceContextFeatureKind kind,
        PriceContextFeatureState state, decimal? value, int? period,
        int requiredObservations, int availableObservations,
        PriceContextFeatureCompleteness completeness,
        ImmutableValueList<PriceVolumeObservation> provenance,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(kind, nameof(kind));
        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireDefined(completeness, nameof(completeness));
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(reasons);
        if (requiredObservations < 0 || availableObservations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredObservations));
        }

        Kind = kind;
        State = state;
        Value = value;
        Period = period;
        RequiredObservations = requiredObservations;
        AvailableObservations = availableObservations;
        Completeness = completeness;
        Provenance = new ImmutableValueList<PriceVolumeObservation>(provenance);
        Reasons = PriceContextObservationQuality.StableReasons(reasons);
    }
}

/// <summary>Pure v2 calculation result with separate setup and HTF evidence.</summary>
public sealed record PriceContextFeatureResult
{
    public PriceContextFeatureConfiguration Configuration { get; }

    public PriceContextFeatureInput Input { get; }

    public CausalEventCursor Cursor { get; }

    public PriceContextObservationQuality Quality { get; }

    public PriceContextConfirmedPivots ConfirmedPivots { get; }

    public PriceContextStructuralLevels StructuralLevels { get; }

    public PriceContextChannel Channel { get; }

    public PriceContextLocalTrend LocalTrend { get; }

    public PriceContextHtfContext HtfContext { get; }

    public PriceContextFeatureValue Vwap { get; }

    public PriceContextFeatureValue Poc { get; }

    [JsonIgnore]
    public ImmutableValueList<PriceContextFeatureState> Values => new(
        [ConfirmedPivots.State, StructuralLevels.State, Channel.State,
            LocalTrend.State, HtfContext.State, Vwap.State, Poc.State]);

    [JsonIgnore]
    public ImmutableValueList<string> Reasons => Quality.Reasons;

    [JsonConstructor]
    public PriceContextFeatureResult(PriceContextFeatureConfiguration configuration,
        PriceContextFeatureInput input, CausalEventCursor cursor,
        PriceContextObservationQuality quality,
        PriceContextConfirmedPivots confirmedPivots,
        PriceContextStructuralLevels structuralLevels, PriceContextChannel channel,
        PriceContextLocalTrend localTrend, PriceContextHtfContext htfContext,
        PriceContextFeatureValue vwap, PriceContextFeatureValue poc)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(confirmedPivots);
        ArgumentNullException.ThrowIfNull(structuralLevels);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(localTrend);
        ArgumentNullException.ThrowIfNull(htfContext);
        ArgumentNullException.ThrowIfNull(vwap);
        ArgumentNullException.ThrowIfNull(poc);
        Configuration = configuration;
        Input = input;
        Cursor = cursor;
        Quality = quality;
        ConfirmedPivots = confirmedPivots;
        StructuralLevels = structuralLevels;
        Channel = channel;
        LocalTrend = localTrend;
        HtfContext = htfContext;
        Vwap = vwap;
        Poc = poc;
    }
}
