using System.Globalization;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies one of the bounded minimum screener features.</summary>
public enum ScreenerFeatureKind
{
    RelativeVolume,
    PriceAcceleration,
    AtrExpansion,
    Turnover,
    Spread,
    OpenInterestAnomaly
}

/// <summary>Describes whether one screener feature has a usable value.</summary>
public enum ScreenerFeatureState
{
    Ready,
    UnknownWarmup,
    UnknownConfiguration,
    UnknownInput,
    UnknownZeroDenominator,
    UnknownOverflow,
    UnknownQuality
}

/// <summary>Describes whether the inputs needed by one feature are complete.</summary>
public enum ScreenerFeatureCompleteness
{
    Complete,
    Warmup,
    Incomplete,
    Unknown
}

/// <summary>Describes freshness against a caller-supplied TTL and cursor.</summary>
public enum ScreenerFeatureFreshness
{
    Unknown,
    Fresh,
    Stale
}

/// <summary>Identifies the formula version used by a screener calculation.</summary>
public sealed record ScreenerFeatureVersion
{
    public string Value { get; }

    [JsonConstructor]
    public ScreenerFeatureVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>Associates one source with an explicit freshness TTL.</summary>
public sealed record ScreenerSourceTtl
{
    public string SourceId { get; }

    public TimeSpan? TimeToLive { get; }

    [JsonIgnore]
    public TimeSpan? Ttl => TimeToLive;

    [JsonConstructor]
    public ScreenerSourceTtl(string sourceId, TimeSpan? timeToLive)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ValidateTtl(timeToLive, nameof(timeToLive));
        SourceId = sourceId;
        TimeToLive = timeToLive;
    }

    private static void ValidateTtl(TimeSpan? value, string parameterName)
    {
        if (value is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A known screener freshness TTL must be positive.");
        }
    }
}

/// <summary>
/// Caller-supplied freshness policy. A missing TTL remains unknown; this policy
/// never reads a clock or invents a numeric default.
/// </summary>
public sealed record ScreenerFreshnessPolicy
{
    public TimeSpan? DefaultTtl { get; }

    public TimeSpan? BarTtl { get; }

    public TimeSpan? QuoteTtl { get; }

    public TimeSpan? OpenInterestTtl { get; }

    public ImmutableValueList<ScreenerSourceTtl> SourceTtls { get; }

    [JsonIgnore]
    public ImmutableValueList<ScreenerSourceTtl> Ttls => SourceTtls;

    public ScreenerFreshnessPolicy()
        : this(null, null, null, null, Array.Empty<ScreenerSourceTtl>())
    {
    }

    public ScreenerFreshnessPolicy(TimeSpan? defaultTtl)
        : this(defaultTtl, null, null, null, Array.Empty<ScreenerSourceTtl>())
    {
    }

    public ScreenerFreshnessPolicy(TimeSpan? barTtl, TimeSpan? quoteTtl,
        TimeSpan? openInterestTtl)
        : this(null, barTtl, quoteTtl, openInterestTtl, Array.Empty<ScreenerSourceTtl>())
    {
    }

    public ScreenerFreshnessPolicy(IEnumerable<ScreenerSourceTtl> sourceTtls)
        : this(null, null, null, null, sourceTtls)
    {
    }

    public ScreenerFreshnessPolicy(TimeSpan? defaultTtl, TimeSpan? barTtl,
        TimeSpan? quoteTtl, TimeSpan? openInterestTtl,
        IEnumerable<ScreenerSourceTtl>? sourceTtls)
    {
        ValidateTtl(defaultTtl, nameof(defaultTtl));
        ValidateTtl(barTtl, nameof(barTtl));
        ValidateTtl(quoteTtl, nameof(quoteTtl));
        ValidateTtl(openInterestTtl, nameof(openInterestTtl));
        ArgumentNullException.ThrowIfNull(sourceTtls);

        ScreenerSourceTtl[] snapshot = sourceTtls.ToArray();
        if (snapshot.Any(static ttl => ttl is null))
        {
            throw new ArgumentException("Source TTL entries cannot be null.", nameof(sourceTtls));
        }

        DefaultTtl = defaultTtl;
        BarTtl = barTtl;
        QuoteTtl = quoteTtl;
        OpenInterestTtl = openInterestTtl;
        SourceTtls = new ImmutableValueList<ScreenerSourceTtl>(snapshot
            .OrderBy(static ttl => ttl.SourceId, StringComparer.Ordinal)
            .ThenBy(static ttl => ttl.TimeToLive));
    }

    [JsonConstructor]
    public ScreenerFreshnessPolicy(TimeSpan? defaultTtl, TimeSpan? barTtl,
        TimeSpan? quoteTtl, TimeSpan? openInterestTtl,
        ImmutableValueList<ScreenerSourceTtl> sourceTtls)
        : this(defaultTtl, barTtl, quoteTtl, openInterestTtl,
            (IEnumerable<ScreenerSourceTtl>)sourceTtls)
    {
    }

    internal bool TryGetTimeToLive(ScreenerFeatureKind kind, string sourceId,
        out TimeSpan? ttl, out string? reason)
    {
        ScreenerSourceTtl[] matches = SourceTtls
            .Where(item => StringComparer.Ordinal.Equals(item.SourceId, sourceId))
            .ToArray();
        if (matches.Length > 1 && matches.Select(static item => item.TimeToLive).Distinct().Count() > 1)
        {
            ttl = null;
            reason = $"freshness-ttl-conflict:{sourceId}";
            return false;
        }

        if (matches.Length > 0)
        {
            ttl = matches[0].TimeToLive;
            reason = ttl is null ? $"freshness-ttl-missing:{sourceId}" : null;
            return ttl is not null;
        }

        ttl = kind switch
        {
            ScreenerFeatureKind.RelativeVolume
                or ScreenerFeatureKind.PriceAcceleration
                or ScreenerFeatureKind.AtrExpansion
                or ScreenerFeatureKind.Turnover => BarTtl,
            ScreenerFeatureKind.Spread => QuoteTtl,
            ScreenerFeatureKind.OpenInterestAnomaly => OpenInterestTtl,
            _ => null
        } ?? DefaultTtl;
        reason = ttl is null ? $"freshness-ttl-missing:{sourceId}" : null;
        return ttl is not null;
    }

    private static void ValidateTtl(TimeSpan? value, string parameterName)
    {
        if (value is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A known screener freshness TTL must be positive.");
        }
    }
}

/// <summary>
/// Holds explicit screener timeframe and lookback configuration. Nullable values
/// are intentional: an omitted value remains unknown instead of receiving a
/// numeric or timeframe default.
/// </summary>
public sealed record ScreenerFeatureConfiguration
{
    public string? Timeframe { get; }

    public int? RelativeVolumeWindow { get; }

    public int? PriceAccelerationWindow { get; }

    public int? AtrExpansionWindow { get; }

    public int? TurnoverWindow { get; }

    public int? OpenInterestAnomalyWindow { get; }

    public ScreenerFeatureVersion? Version { get; }

    public ScreenerFreshnessPolicy? FreshnessPolicy { get; }

    [JsonIgnore]
    public ScreenerFreshnessPolicy? Freshness => FreshnessPolicy;

    [JsonIgnore]
    public int? RvolWindow => RelativeVolumeWindow;

    [JsonIgnore]
    public int? PriceAccelerationLookback => PriceAccelerationWindow;

    [JsonIgnore]
    public int? AtrWindow => AtrExpansionWindow;

    [JsonIgnore]
    public int? TurnoverLookback => TurnoverWindow;

    [JsonIgnore]
    public int? OiAnomalyWindow => OpenInterestAnomalyWindow;

    [JsonIgnore]
    public bool HasCommonConfiguration => !string.IsNullOrWhiteSpace(Timeframe)
        && Version is not null;

    /// <summary>Creates an explicitly incomplete configuration with no defaults.</summary>
    public ScreenerFeatureConfiguration()
        : this(null, null, null, null, null, null, null)
    {
    }

    [JsonConstructor]
    public ScreenerFeatureConfiguration(string? timeframe, int? relativeVolumeWindow,
        int? priceAccelerationWindow, int? atrExpansionWindow, int? turnoverWindow,
        int? openInterestAnomalyWindow, ScreenerFeatureVersion? version,
        ScreenerFreshnessPolicy? freshnessPolicy = null)
    {
        Timeframe = timeframe;
        RelativeVolumeWindow = relativeVolumeWindow;
        PriceAccelerationWindow = priceAccelerationWindow;
        AtrExpansionWindow = atrExpansionWindow;
        TurnoverWindow = turnoverWindow;
        OpenInterestAnomalyWindow = openInterestAnomalyWindow;
        Version = version;
        FreshnessPolicy = freshnessPolicy;
    }
}

/// <summary>
/// Common immutable causal metadata for one screener data observation. Event time
/// and local availability remain separate, and cursor eligibility uses the same
/// event-time/availability/sequence primitive as replay.
/// </summary>
public abstract record ScreenerCausalObservation
{
    public string SourceId { get; }

    public InstrumentId InstrumentId { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    [JsonIgnore]
    public string SourceIdentity => SourceId;

    protected ScreenerCausalObservation(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence)
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

        SourceId = sourceId;
        InstrumentId = instrumentId;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
    }

    /// <summary>Checks this observation against an inclusive causal cursor.</summary>
    public virtual bool IsEligible(CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return EventTime.Value <= cursor.AsOf.Value
            && AvailabilityTime.Value <= cursor.AsOf.Value
            && StableSequence <= cursor.MaxStableSequence;
    }
}

/// <summary>One closed OHLCV bar with its exact exchange-reported quote volume.</summary>
public sealed record ScreenerBar : ScreenerCausalObservation
{
    public Bar Bar { get; }

    public decimal QuoteVolume { get; }

    public bool IsClosed { get; }

    [JsonIgnore]
    public UtcTimestamp CloseTime => Bar.CloseTime;

    [JsonIgnore]
    public Price Close => Bar.Close;

    public ScreenerBar(string sourceId, Bar bar, decimal quoteVolume,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime, long stableSequence)
        : this(sourceId, bar, quoteVolume, receiveTime, availabilityTime, stableSequence, true)
    {
    }

    [JsonConstructor]
    public ScreenerBar(string sourceId, Bar bar, decimal quoteVolume,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime, long stableSequence,
        bool isClosed)
        : base(sourceId, RequireBar(bar).InstrumentId, bar.EventTime, receiveTime,
            availabilityTime, stableSequence)
    {
        if (quoteVolume < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quoteVolume), quoteVolume,
                "Quote volume cannot be negative.");
        }

        if (!isClosed)
        {
            throw new ArgumentException("A screener bar must be closed.", nameof(isClosed));
        }

        Bar = bar;
        QuoteVolume = quoteVolume;
        IsClosed = isClosed;
    }

    private static Bar RequireBar(Bar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return bar;
    }

    public override bool IsEligible(CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return base.IsEligible(cursor) && Bar.CloseTime.Value <= cursor.AsOf.Value;
    }
}

/// <summary>One causally timestamped open-interest observation.</summary>
public sealed record ScreenerOpenInterestObservation : ScreenerCausalObservation
{
    public decimal Value { get; }

    [JsonIgnore]
    public decimal OpenInterest => Value;

    [JsonConstructor]
    public ScreenerOpenInterestObservation(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, decimal value)
        : base(sourceId, instrumentId, eventTime, receiveTime, availabilityTime, stableSequence)
    {
        if (value < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value,
                "Open interest cannot be negative.");
        }

        Value = value;
    }

    public ScreenerOpenInterestObservation(string sourceId, OIUpdate update,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime, long stableSequence)
        : this(sourceId, RequireUpdate(update).InstrumentId, update.EventTime, receiveTime,
            availabilityTime, stableSequence, update.OpenInterest)
    {
    }

    private static OIUpdate RequireUpdate(OIUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return update;
    }
}

/// <summary>One causally timestamped top-of-book bid/ask observation.</summary>
public sealed record ScreenerQuoteObservation : ScreenerCausalObservation
{
    public Price? Bid { get; }

    public Price? Ask { get; }

    public bool IsReady { get; }

    [JsonIgnore]
    public Price? BidPrice => Bid;

    [JsonIgnore]
    public Price? AskPrice => Ask;

    [JsonConstructor]
    public ScreenerQuoteObservation(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, Price? bid, Price? ask, bool isReady)
        : base(sourceId, instrumentId, eventTime, receiveTime, availabilityTime, stableSequence)
    {
        Bid = bid;
        Ask = ask;
        IsReady = isReady;
    }

    public ScreenerQuoteObservation(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, decimal? bid, decimal? ask, bool isReady)
        : this(sourceId, instrumentId, eventTime, receiveTime, availabilityTime,
            stableSequence, bid.HasValue ? new Price(bid.Value) : null,
            ask.HasValue ? new Price(ask.Value) : null, isReady)
    {
    }
}

/// <summary>
/// Immutable causal input snapshot for the minimum screener feature set. The
/// top-level provenance identifies the input snapshot; each observation retains
/// its own causal visibility fields so a cursor cannot expose future samples.
/// </summary>
public sealed record ScreenerFeatureInput
{
    public string SourceId { get; }

    public InstrumentId InstrumentId { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public ImmutableValueList<ScreenerBar> ClosedBars { get; }

    public ImmutableValueList<ScreenerOpenInterestObservation> OpenInterest { get; }

    public ImmutableValueList<ScreenerQuoteObservation> Quotes { get; }

    [JsonIgnore]
    public string SourceIdentity => SourceId;

    [JsonIgnore]
    public ImmutableValueList<ScreenerBar> Bars => ClosedBars;

    [JsonIgnore]
    public ImmutableValueList<ScreenerQuoteObservation> ReadyBidAsk => Quotes;

    [JsonIgnore]
    public ImmutableValueList<ScreenerOpenInterestObservation> OpenInterestObservations =>
        OpenInterest;

    public bool IsEligible(CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return EventTime.Value <= cursor.AsOf.Value
            && AvailabilityTime.Value <= cursor.AsOf.Value
            && StableSequence <= cursor.MaxStableSequence;
    }

    public ScreenerFeatureInput(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, IEnumerable<ScreenerBar> closedBars,
        IEnumerable<ScreenerOpenInterestObservation> openInterest,
        IEnumerable<ScreenerQuoteObservation> quotes)
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

        ClosedBars = SnapshotBars(closedBars, sourceId, instrumentId);
        OpenInterest = SnapshotOpenInterest(openInterest, sourceId, instrumentId);
        Quotes = SnapshotQuotes(quotes, sourceId, instrumentId);
        SourceId = sourceId;
        InstrumentId = instrumentId;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
    }

    [JsonConstructor]
    public ScreenerFeatureInput(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, ImmutableValueList<ScreenerBar> closedBars,
        ImmutableValueList<ScreenerOpenInterestObservation> openInterest,
        ImmutableValueList<ScreenerQuoteObservation> quotes)
        : this(sourceId, instrumentId, eventTime, receiveTime, availabilityTime,
            stableSequence, (IEnumerable<ScreenerBar>)closedBars,
            (IEnumerable<ScreenerOpenInterestObservation>)openInterest,
            (IEnumerable<ScreenerQuoteObservation>)quotes)
    {
    }

    public ScreenerFeatureInput(string sourceId, InstrumentId instrumentId,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, IEnumerable<ScreenerBar> closedBars,
        IEnumerable<ScreenerOpenInterestObservation> openInterest,
        ScreenerQuoteObservation? readyBidAsk)
        : this(sourceId, instrumentId, eventTime, receiveTime, availabilityTime, stableSequence,
            closedBars, openInterest,
            readyBidAsk is null ? Array.Empty<ScreenerQuoteObservation>() : [readyBidAsk])
    {
    }

    private static ImmutableValueList<ScreenerBar> SnapshotBars(
        IEnumerable<ScreenerBar> bars, string sourceId, InstrumentId instrumentId)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ScreenerBar[] snapshot = bars.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Screener bars cannot contain null values.", nameof(bars));
        }

        ValidateIdentity(snapshot, sourceId, instrumentId, nameof(bars));
        return new ImmutableValueList<ScreenerBar>(snapshot
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Bar.OpenTime.Value)
            .ThenBy(static item => item.Bar.Close.Value)
            .ThenBy(static item => item.QuoteVolume));
    }

    private static ImmutableValueList<ScreenerOpenInterestObservation> SnapshotOpenInterest(
        IEnumerable<ScreenerOpenInterestObservation> observations, string sourceId,
        InstrumentId instrumentId)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ScreenerOpenInterestObservation[] snapshot = observations.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Open-interest observations cannot contain null values.",
                nameof(observations));
        }

        ValidateIdentity(snapshot, sourceId, instrumentId, nameof(observations));
        return new ImmutableValueList<ScreenerOpenInterestObservation>(snapshot
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Value));
    }

    private static ImmutableValueList<ScreenerQuoteObservation> SnapshotQuotes(
        IEnumerable<ScreenerQuoteObservation> quotes, string sourceId, InstrumentId instrumentId)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        ScreenerQuoteObservation[] snapshot = quotes.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Quote observations cannot contain null values.",
                nameof(quotes));
        }

        ValidateIdentity(snapshot, sourceId, instrumentId, nameof(quotes));
        return new ImmutableValueList<ScreenerQuoteObservation>(snapshot
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Bid is null ? 0m : item.Bid.Value)
            .ThenBy(static item => item.Ask is null ? 0m : item.Ask.Value)
            .ThenBy(static item => item.IsReady));
    }

    private static void ValidateIdentity<T>(IEnumerable<T> observations, string sourceId,
        InstrumentId instrumentId, string parameterName) where T : ScreenerCausalObservation
    {
        if (observations.Any(item => !StringComparer.Ordinal.Equals(item.SourceId, sourceId)
            || item.InstrumentId != instrumentId))
        {
            throw new ArgumentException(
                "Every screener observation must match the input source and instrument.",
                parameterName);
        }
    }
}

/// <summary>One feature value or a deterministic unknown reason.</summary>
public sealed record ScreenerFeatureValue
{
    public ScreenerFeatureKind Kind { get; }

    public ScreenerFeatureState State { get; }

    public decimal? Value { get; }

    public string? Reason { get; }

    public ScreenerFeatureCompleteness Completeness { get; }

    public ScreenerFeatureFreshness Freshness { get; }

    public TimeSpan? Age { get; }

    public UtcTimestamp? LatestAvailabilityTime { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsComplete => Completeness == ScreenerFeatureCompleteness.Complete;

    [JsonIgnore]
    public bool IsWarmup => Completeness == ScreenerFeatureCompleteness.Warmup
        || State == ScreenerFeatureState.UnknownWarmup;

    [JsonIgnore]
    public bool IsFresh => Freshness == ScreenerFeatureFreshness.Fresh;

    [JsonIgnore]
    public bool IsReady => State == ScreenerFeatureState.Ready
        && IsComplete && IsFresh && Value.HasValue;

    [JsonIgnore]
    public bool IsKnown => State == ScreenerFeatureState.Ready && Value.HasValue;

    public ScreenerFeatureValue(ScreenerFeatureKind kind, ScreenerFeatureState state,
        decimal? value, string? reason,
        ScreenerFeatureCompleteness completeness = ScreenerFeatureCompleteness.Unknown,
        ScreenerFeatureFreshness freshness = ScreenerFeatureFreshness.Unknown,
        TimeSpan? age = null, UtcTimestamp? latestAvailabilityTime = null,
        IEnumerable<string>? reasons = null)
    {
        ContractGuard.RequireDefined(kind, nameof(kind));
        ContractGuard.RequireDefined(state, nameof(state));
        if (state == ScreenerFeatureState.Ready)
        {
            if (!value.HasValue)
            {
                throw new ArgumentException("A ready feature requires a value.", nameof(value));
            }

            if (reason is not null)
            {
                throw new ArgumentException("A ready feature cannot have a reason.", nameof(reason));
            }
        }
        else
        {
            if (value.HasValue)
            {
                throw new ArgumentException("An unknown feature cannot have a value.", nameof(value));
            }

            ContractGuard.RequireText(reason ?? string.Empty, nameof(reason));
        }

        ContractGuard.RequireDefined(completeness, nameof(completeness));
        ContractGuard.RequireDefined(freshness, nameof(freshness));
        ArgumentNullException.ThrowIfNull(reasons);
        string[] reasonSnapshot = reasons
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (state != ScreenerFeatureState.Ready && reasonSnapshot.Length == 0)
        {
            throw new ArgumentException("An unknown feature requires at least one reason.",
                nameof(reasons));
        }

        if (age is { } observedAge && observedAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(age), age,
                "A feature age cannot be negative.");
        }

        Kind = kind;
        State = state;
        Value = value;
        Reason = reason;
        Completeness = completeness;
        Freshness = freshness;
        Age = age;
        LatestAvailabilityTime = latestAvailabilityTime;
        Reasons = new ImmutableValueList<string>(reasonSnapshot);
    }

    [JsonConstructor]
    public ScreenerFeatureValue(ScreenerFeatureKind kind, ScreenerFeatureState state,
        decimal? value, string? reason, ScreenerFeatureCompleteness completeness,
        ScreenerFeatureFreshness freshness, TimeSpan? age,
        UtcTimestamp? latestAvailabilityTime, ImmutableValueList<string> reasons)
        : this(kind, state, value, reason, completeness, freshness, age,
            latestAvailabilityTime, (IEnumerable<string>)reasons)
    {
    }

    public ScreenerFeatureValue(ScreenerFeatureKind kind, ScreenerFeatureState state,
        decimal? value, string? reason)
        : this(kind, state, value, reason,
            InferCompleteness(state), ScreenerFeatureFreshness.Unknown, null, null,
            reason is null ? Array.Empty<string>() : [reason])
    {
    }

    public static ScreenerFeatureValue Ready(ScreenerFeatureKind kind, decimal value) =>
        new(kind, ScreenerFeatureState.Ready, value, null,
            ScreenerFeatureCompleteness.Complete, ScreenerFeatureFreshness.Unknown,
            null, null, Array.Empty<string>());

    public static ScreenerFeatureValue Unknown(ScreenerFeatureKind kind,
        ScreenerFeatureState state, string reason) =>
        new(kind, state, null, reason, InferCompleteness(state),
            ScreenerFeatureFreshness.Unknown, null, null, [reason]);

    internal ScreenerFeatureValue WithMetadata(ScreenerFeatureCompleteness completeness,
        ScreenerFeatureFreshness freshness, TimeSpan? age, UtcTimestamp? latestAvailabilityTime,
        IEnumerable<string> additionalReasons)
    {
        ArgumentNullException.ThrowIfNull(additionalReasons);
        string[] allReasons = Reasons.Concat(additionalReasons)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static item => item, StringComparer.Ordinal)
            .ToArray();
        return new ScreenerFeatureValue(Kind, State, Value, Reason, completeness, freshness,
            age, latestAvailabilityTime, allReasons);
    }

    private static ScreenerFeatureCompleteness InferCompleteness(ScreenerFeatureState state) =>
        state switch
        {
            ScreenerFeatureState.Ready => ScreenerFeatureCompleteness.Complete,
            ScreenerFeatureState.UnknownWarmup => ScreenerFeatureCompleteness.Warmup,
            ScreenerFeatureState.UnknownQuality or ScreenerFeatureState.UnknownInput =>
                ScreenerFeatureCompleteness.Incomplete,
            _ => ScreenerFeatureCompleteness.Unknown
        };
}

/// <summary>Versioned result containing exactly the six minimum screener features.</summary>
public sealed record ScreenerFeatureResult
{
    public ScreenerFeatureVersion? Version { get; }

    public ScreenerFeatureInput Input { get; }

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<ScreenerFeatureValue> Features { get; }

    public ScreenerFeatureValue RelativeVolume { get; }

    public ScreenerFeatureValue PriceAcceleration { get; }

    public ScreenerFeatureValue AtrExpansion { get; }

    public ScreenerFeatureValue Turnover { get; }

    public ScreenerFeatureValue Spread { get; }

    public ScreenerFeatureValue OpenInterestAnomaly { get; }

    [JsonIgnore]
    public ScreenerFeatureValue RVOL => RelativeVolume;

    [JsonIgnore]
    public ScreenerFeatureValue OIAnomaly => OpenInterestAnomaly;

    [JsonIgnore]
    public ScreenerFeatureValue OpenInterest => OpenInterestAnomaly;

    [JsonIgnore]
    public bool IsReady => Features.All(static feature => feature.IsReady);

    public ScreenerFeatureResult(ScreenerFeatureVersion? version,
        ScreenerFeatureInput input, CausalEventCursor cursor,
        IEnumerable<ScreenerFeatureValue> features)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(features);
        ScreenerFeatureValue[] snapshot = features.ToArray();
        if (snapshot.Length != 6)
        {
            throw new ArgumentException("A screener result must contain six features.", nameof(features));
        }

        Features = new ImmutableValueList<ScreenerFeatureValue>(snapshot);
        RelativeVolume = RequireFeature(snapshot, ScreenerFeatureKind.RelativeVolume);
        PriceAcceleration = RequireFeature(snapshot, ScreenerFeatureKind.PriceAcceleration);
        AtrExpansion = RequireFeature(snapshot, ScreenerFeatureKind.AtrExpansion);
        Turnover = RequireFeature(snapshot, ScreenerFeatureKind.Turnover);
        Spread = RequireFeature(snapshot, ScreenerFeatureKind.Spread);
        OpenInterestAnomaly = RequireFeature(snapshot, ScreenerFeatureKind.OpenInterestAnomaly);
        Version = version;
        Input = input;
        Cursor = cursor;
    }

    [JsonConstructor]
    public ScreenerFeatureResult(ScreenerFeatureVersion? version,
        ScreenerFeatureInput input, CausalEventCursor cursor,
        ImmutableValueList<ScreenerFeatureValue> features)
        : this(version, input, cursor, (IEnumerable<ScreenerFeatureValue>)features)
    {
    }

    private static ScreenerFeatureValue RequireFeature(
        IEnumerable<ScreenerFeatureValue> features, ScreenerFeatureKind kind)
    {
        ScreenerFeatureValue[] matches = features.Where(feature => feature.Kind == kind).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ArgumentException($"The result must contain one {kind} feature.", nameof(features));
    }
}

/// <summary>
/// Applies the shared inclusive causal selection primitive to screener observations.
/// </summary>
public static class ScreenerCausalSelection
{
    public static ImmutableValueList<T> Select<T>(IEnumerable<T> observations,
        CausalEventCursor cursor) where T : ScreenerCausalObservation
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(cursor);
        T[] snapshot = observations.ToArray();
        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Screener observations cannot contain null values.",
                nameof(observations));
        }

        IEnumerable<T> ordered = snapshot
            .Where(item => item.IsEligible(cursor))
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.StableSequence);
        return new ImmutableValueList<T>(ordered);
    }
}

internal readonly record struct ScreenerObservationKey(string SourceId,
    string InstrumentId, DateTimeOffset Time, long StableSequence)
{
    public string SortKey => string.Create(CultureInfo.InvariantCulture,
        $"{SourceId}\u001f{InstrumentId}\u001f{Time:O}\u001f{StableSequence}");
}

internal sealed record ScreenerObservationQuality<T>(
    ImmutableValueList<T> Observations, ImmutableValueList<string> Reasons)
    where T : ScreenerCausalObservation
{
    public bool HasBlockingReasons => Reasons.Count > 0;
}

internal sealed record ScreenerFreshnessAssessment(
    ScreenerFeatureFreshness Status, TimeSpan? Age,
    UtcTimestamp? LatestAvailabilityTime, ImmutableValueList<string> Reasons);

/// <summary>Pure calculator for the minimum causal screener feature set.</summary>
public static class ScreenerFeatureCalculator
{
    public static ScreenerFeatureResult Calculate(ScreenerFeatureInput input,
        CausalEventCursor cursor, ScreenerFeatureConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Calculate(input, cursor, configuration, configuration.FreshnessPolicy);
    }

    public static ScreenerFeatureResult Calculate(ScreenerFeatureInput input,
        CausalEventCursor cursor, ScreenerFeatureConfiguration configuration,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!TryGetCommonConfigurationReason(configuration, out string? commonReason))
        {
            return new ScreenerFeatureResult(configuration.Version, input, cursor,
            [
                ConfigurationUnknown(ScreenerFeatureKind.RelativeVolume, commonReason!),
                ConfigurationUnknown(ScreenerFeatureKind.PriceAcceleration, commonReason!),
                ConfigurationUnknown(ScreenerFeatureKind.AtrExpansion, commonReason!),
                ConfigurationUnknown(ScreenerFeatureKind.Turnover, commonReason!),
                ConfigurationUnknown(ScreenerFeatureKind.Spread, commonReason!),
                ConfigurationUnknown(ScreenerFeatureKind.OpenInterestAnomaly, commonReason!)
            ]);
        }

        ScreenerObservationQuality<ScreenerBar> bars =
            AnalyzeBars(input.ClosedBars, cursor, configuration);
        ScreenerObservationQuality<ScreenerQuoteObservation> quotes =
            AnalyzeQuotes(input.Quotes, cursor);
        ScreenerObservationQuality<ScreenerOpenInterestObservation> openInterest =
            AnalyzeOpenInterest(input.OpenInterest, cursor);

        ScreenerFeatureValue relativeVolume =
            CalculateRelativeVolume(cursor, configuration, bars, freshnessPolicy);
        ScreenerFeatureValue priceAcceleration =
            CalculatePriceAcceleration(cursor, configuration, bars, freshnessPolicy);
        ScreenerFeatureValue atrExpansion =
            CalculateAtrExpansion(cursor, configuration, bars, freshnessPolicy);
        ScreenerFeatureValue turnover =
            CalculateTurnover(cursor, configuration, bars, freshnessPolicy);
        ScreenerFeatureValue spread =
            CalculateSpread(cursor, configuration, quotes, freshnessPolicy);
        ScreenerFeatureValue openInterestAnomaly =
            CalculateOpenInterestAnomaly(cursor, configuration, openInterest, freshnessPolicy);

        return new ScreenerFeatureResult(configuration.Version, input, cursor,
        [relativeVolume, priceAcceleration, atrExpansion, turnover, spread, openInterestAnomaly]);
    }

    private static ScreenerFeatureValue CalculateRelativeVolume(CausalEventCursor cursor,
        ScreenerFeatureConfiguration configuration, ScreenerObservationQuality<ScreenerBar> quality,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        const ScreenerFeatureKind kind = ScreenerFeatureKind.RelativeVolume;
        if (!TryGetPositiveWindow(configuration.RelativeVolumeWindow, kind, out int window,
            out ScreenerFeatureValue? unknown))
        {
            return FinalizeFeature(unknown!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        ScreenerBar[] bars = quality.Observations.ToArray();
        if (!TryGetOneWindowRequirement(window, kind, out int required,
            out ScreenerFeatureValue? oversized))
        {
            return FinalizeFeature(oversized!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        if (bars.Length < required)
        {
            return FinalizeFeature(
                Warmup(kind, $"relative-volume requires {required} eligible closed bars"),
                kind, bars, quality.Reasons, cursor, freshnessPolicy);
        }

        try
        {
            decimal priorVolumeSum = 0m;
            for (int index = bars.Length - window - 1; index < bars.Length - 1; index++)
            {
                priorVolumeSum = checked(priorVolumeSum + bars[index].Bar.Volume.Value);
            }

            if (priorVolumeSum == 0m)
            {
                return FinalizeFeature(ZeroDenominator(kind, "relative-volume prior mean is zero"),
                    kind, TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
            }

            decimal priorMean = checked(priorVolumeSum / window);
            return FinalizeFeature(Ready(kind, checked(bars[^1].Bar.Volume.Value / priorMean)),
                kind, TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
        }
        catch (OverflowException)
        {
            return FinalizeFeature(Overflow(kind, "relative-volume decimal arithmetic overflowed"),
                kind, TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
        }
    }

    private static ScreenerFeatureValue CalculatePriceAcceleration(CausalEventCursor cursor,
        ScreenerFeatureConfiguration configuration, ScreenerObservationQuality<ScreenerBar> quality,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        const ScreenerFeatureKind kind = ScreenerFeatureKind.PriceAcceleration;
        if (!TryGetPositiveWindow(configuration.PriceAccelerationWindow, kind, out int window,
            out ScreenerFeatureValue? unknown))
        {
            return FinalizeFeature(unknown!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        ScreenerBar[] bars = quality.Observations.ToArray();
        if (!TryGetTwoWindowRequirement(window, kind, out int required,
            out ScreenerFeatureValue? oversized))
        {
            return FinalizeFeature(oversized!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        if (bars.Length < required)
        {
            return FinalizeFeature(
                Warmup(kind, $"price-acceleration requires {required} eligible closed bars"),
                kind, bars, quality.Reasons, cursor, freshnessPolicy);
        }

        int start = bars.Length - required;
        try
        {
            decimal firstClose = bars[start].Bar.Close.Value;
            decimal middleClose = bars[start + window].Bar.Close.Value;
            decimal lastClose = bars[start + (2 * window)].Bar.Close.Value;
            decimal previousReturn = checked(middleClose / firstClose - 1m);
            decimal latestReturn = checked(lastClose / middleClose - 1m);
            return FinalizeFeature(Ready(kind, checked(latestReturn - previousReturn)), kind,
                TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
        }
        catch (OverflowException)
        {
            return FinalizeFeature(Overflow(kind, "price-acceleration decimal arithmetic overflowed"),
                kind, TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
        }
    }

    private static ScreenerFeatureValue CalculateAtrExpansion(CausalEventCursor cursor,
        ScreenerFeatureConfiguration configuration, ScreenerObservationQuality<ScreenerBar> quality,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        const ScreenerFeatureKind kind = ScreenerFeatureKind.AtrExpansion;
        if (!TryGetPositiveWindow(configuration.AtrExpansionWindow, kind, out int window,
            out ScreenerFeatureValue? unknown))
        {
            return FinalizeFeature(unknown!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        ScreenerBar[] bars = quality.Observations.ToArray();
        if (!TryGetTwoWindowRequirement(window, kind, out int required,
            out ScreenerFeatureValue? oversized))
        {
            return FinalizeFeature(oversized!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        if (bars.Length < required)
        {
            return FinalizeFeature(
                Warmup(kind, $"atr-expansion requires {required} eligible closed bars"),
                kind, bars, quality.Reasons, cursor, freshnessPolicy);
        }

        int start = bars.Length - required;
        try
        {
            decimal priorMean = MeanTrueRange(bars, start + 1, window);
            if (priorMean == 0m)
            {
                return FinalizeFeature(
                    ZeroDenominator(kind, "atr-expansion prior true-range mean is zero"),
                    kind, TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
            }

            decimal latestMean = MeanTrueRange(bars, start + window + 1, window);
            return FinalizeFeature(Ready(kind, checked(latestMean / priorMean - 1m)), kind,
                TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
        }
        catch (OverflowException)
        {
            return FinalizeFeature(Overflow(kind, "atr-expansion decimal arithmetic overflowed"),
                kind, TakeLatest(bars, required), quality.Reasons, cursor, freshnessPolicy);
        }
    }

    private static decimal MeanTrueRange(ScreenerBar[] bars, int start, int count)
    {
        decimal sum = 0m;
        for (int index = start; index < start + count; index++)
        {
            ScreenerBar current = bars[index];
            decimal previousClose = bars[index - 1].Bar.Close.Value;
            decimal highLow = checked(current.Bar.High.Value - current.Bar.Low.Value);
            decimal highGap = decimal.Abs(checked(current.Bar.High.Value - previousClose));
            decimal lowGap = decimal.Abs(checked(current.Bar.Low.Value - previousClose));
            decimal trueRange = Math.Max(highLow, Math.Max(highGap, lowGap));
            sum = checked(sum + trueRange);
        }

        return checked(sum / count);
    }

    private static ScreenerFeatureValue CalculateTurnover(CausalEventCursor cursor,
        ScreenerFeatureConfiguration configuration, ScreenerObservationQuality<ScreenerBar> quality,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        const ScreenerFeatureKind kind = ScreenerFeatureKind.Turnover;
        if (!TryGetPositiveWindow(configuration.TurnoverWindow, kind, out int window,
            out ScreenerFeatureValue? unknown))
        {
            return FinalizeFeature(unknown!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        ScreenerBar[] bars = quality.Observations.ToArray();
        if (bars.Length < window)
        {
            return FinalizeFeature(Warmup(kind, $"turnover requires {window} eligible closed bars"),
                kind, bars, quality.Reasons, cursor, freshnessPolicy);
        }

        try
        {
            decimal sum = 0m;
            for (int index = bars.Length - window; index < bars.Length; index++)
            {
                sum = checked(sum + bars[index].QuoteVolume);
            }

            return FinalizeFeature(Ready(kind, sum), kind, TakeLatest(bars, window),
                quality.Reasons, cursor, freshnessPolicy);
        }
        catch (OverflowException)
        {
            return FinalizeFeature(Overflow(kind, "turnover quote-volume sum overflowed"), kind,
                TakeLatest(bars, window), quality.Reasons, cursor, freshnessPolicy);
        }
    }

    private static ScreenerFeatureValue CalculateSpread(CausalEventCursor cursor,
        ScreenerFeatureConfiguration configuration,
        ScreenerObservationQuality<ScreenerQuoteObservation> quality,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        const ScreenerFeatureKind kind = ScreenerFeatureKind.Spread;
        if (!HasCommonConfiguration(configuration, kind, out ScreenerFeatureValue? unknown))
        {
            return FinalizeFeature(unknown!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        ScreenerQuoteObservation[] quotes = quality.Observations.ToArray();
        if (quotes.Length == 0)
        {
            return FinalizeFeature(InputUnknown(kind, "spread requires one eligible quote"), kind,
                quotes, quality.Reasons, cursor, freshnessPolicy);
        }

        ScreenerQuoteObservation quote = quotes[^1];
        if (!quote.IsReady || quote.Bid is null || quote.Ask is null
            || quote.Ask.Value < quote.Bid.Value)
        {
            return FinalizeFeature(InputUnknown(kind, "spread requires one ready valid bid/ask"),
                kind, [quote], quality.Reasons, cursor, freshnessPolicy);
        }

        try
        {
            decimal midpoint = checked((quote.Ask.Value + quote.Bid.Value) / 2m);
            if (midpoint == 0m)
            {
                return FinalizeFeature(ZeroDenominator(kind, "spread midpoint is zero"), kind,
                    [quote], quality.Reasons, cursor, freshnessPolicy);
            }

            decimal difference = checked(quote.Ask.Value - quote.Bid.Value);
            return FinalizeFeature(Ready(kind, checked(difference / midpoint)), kind,
                [quote], quality.Reasons, cursor, freshnessPolicy);
        }
        catch (OverflowException)
        {
            return FinalizeFeature(Overflow(kind, "spread decimal arithmetic overflowed"), kind,
                [quote], quality.Reasons, cursor, freshnessPolicy);
        }
    }

    private static ScreenerFeatureValue CalculateOpenInterestAnomaly(CausalEventCursor cursor,
        ScreenerFeatureConfiguration configuration,
        ScreenerObservationQuality<ScreenerOpenInterestObservation> quality,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        const ScreenerFeatureKind kind = ScreenerFeatureKind.OpenInterestAnomaly;
        if (!TryGetPositiveWindow(configuration.OpenInterestAnomalyWindow, kind,
            out int window, out ScreenerFeatureValue? unknown))
        {
            return FinalizeFeature(unknown!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        ScreenerOpenInterestObservation[] observations = quality.Observations.ToArray();
        if (!TryGetOneWindowRequirement(window, kind, out int required,
            out ScreenerFeatureValue? oversized))
        {
            return FinalizeFeature(oversized!, kind, [], quality.Reasons, cursor, freshnessPolicy);
        }

        if (observations.Length < required)
        {
            return FinalizeFeature(
                Warmup(kind, $"open-interest-anomaly requires {required} eligible observations"),
                kind, observations, quality.Reasons, cursor, freshnessPolicy);
        }

        try
        {
            decimal priorSum = 0m;
            for (int index = observations.Length - window - 1;
                index < observations.Length - 1; index++)
            {
                priorSum = checked(priorSum + observations[index].Value);
            }

            if (priorSum == 0m)
            {
                return FinalizeFeature(
                    ZeroDenominator(kind, "open-interest-anomaly prior mean is zero"), kind,
                    TakeLatest(observations, required), quality.Reasons, cursor, freshnessPolicy);
            }

            decimal priorMean = checked(priorSum / window);
            return FinalizeFeature(Ready(kind, checked(observations[^1].Value / priorMean - 1m)),
                kind, TakeLatest(observations, required), quality.Reasons, cursor, freshnessPolicy);
        }
        catch (OverflowException)
        {
            return FinalizeFeature(
                Overflow(kind, "open-interest-anomaly decimal arithmetic overflowed"), kind,
                TakeLatest(observations, required), quality.Reasons, cursor, freshnessPolicy);
        }
    }

    private static ScreenerObservationQuality<ScreenerBar> AnalyzeBars(
        IEnumerable<ScreenerBar> observations, CausalEventCursor cursor,
        ScreenerFeatureConfiguration configuration)
    {
        ScreenerObservationQuality<ScreenerBar> quality = Analyze(
            observations, cursor, "bar",
            static item => new ScreenerObservationKey(item.SourceId,
                item.InstrumentId.Value, item.Bar.OpenTime.Value, -1),
            static (left, right) => left.Bar == right.Bar
                && left.QuoteVolume == right.QuoteVolume
                && left.IsClosed == right.IsClosed,
            item => item.IsEligible(cursor),
            (item, asOf) => item.EventTime.Value <= asOf.AsOf.Value
                && item.AvailabilityTime.Value <= asOf.AsOf.Value
                && item.StableSequence <= cursor.MaxStableSequence
                && item.Bar.CloseTime.Value > asOf.AsOf.Value
                    ? "bar-close-after-cursor"
                    : null);

        List<string> reasons = quality.Reasons.ToList();
        AddBarGapReasons(quality.Observations, configuration.Timeframe, reasons);
        return new ScreenerObservationQuality<ScreenerBar>(quality.Observations,
            StableReasons(reasons));
    }

    private static ScreenerObservationQuality<ScreenerQuoteObservation> AnalyzeQuotes(
        IEnumerable<ScreenerQuoteObservation> observations, CausalEventCursor cursor)
    {
        return Analyze(observations, cursor, "quote",
            static item => new ScreenerObservationKey(item.SourceId,
                item.InstrumentId.Value, item.EventTime.Value, item.StableSequence),
            static (left, right) => left.Bid == right.Bid
                && left.Ask == right.Ask
                && left.IsReady == right.IsReady,
            item => item.IsEligible(cursor),
            static (_, _) => null);
    }

    private static ScreenerObservationQuality<ScreenerOpenInterestObservation> AnalyzeOpenInterest(
        IEnumerable<ScreenerOpenInterestObservation> observations, CausalEventCursor cursor)
    {
        return Analyze(observations, cursor, "open-interest",
            static item => new ScreenerObservationKey(item.SourceId,
                item.InstrumentId.Value, item.EventTime.Value, item.StableSequence),
            static (left, right) => left.Value == right.Value,
            item => item.IsEligible(cursor),
            static (_, _) => null);
    }

    private static ScreenerObservationQuality<T> Analyze<T>(IEnumerable<T> observations,
        CausalEventCursor cursor, string kind, Func<T, ScreenerObservationKey> keySelector,
        Func<T, T, bool> payloadEquals, Func<T, bool> eligibility,
        Func<T, CausalEventCursor, string?> excludedReason)
        where T : ScreenerCausalObservation
    {
        ArgumentNullException.ThrowIfNull(observations);
        T[] snapshot = observations.ToArray();
        List<string> reasons = [];
        foreach (T item in snapshot)
        {
            ScreenerObservationKey key = keySelector(item);
            if (item.EventTime.Value <= cursor.AsOf.Value
                && item.AvailabilityTime.Value > cursor.AsOf.Value)
            {
                reasons.Add("late-availability");
                reasons.Add($"late-availability:{kind}:{key.SortKey}");
            }
        }

        T[] eligible = snapshot.Where(eligibility).ToArray();
        List<T> canonical = [];
        foreach (IGrouping<ScreenerObservationKey, T> group in eligible
            .GroupBy(keySelector)
            .OrderBy(static group => group.Key.SortKey, StringComparer.Ordinal))
        {
            T first = group.First();
            if (group.Any(item => !payloadEquals(first, item)))
            {
                reasons.Add("conflict");
                reasons.Add($"conflict:{kind}:{group.Key.SortKey}");
                continue;
            }

            T selected = group
                .OrderByDescending(static item => item.AvailabilityTime.Value)
                .ThenByDescending(static item => item.ReceiveTime.Value)
                .ThenBy(static item => item.EventTime.Value)
                .ThenBy(static item => item.StableSequence)
                .First();
            canonical.Add(selected);
        }

        foreach (T item in snapshot.Where(item => !eligibility(item)))
        {
            string? exclusion = excludedReason(item, cursor);
            if (exclusion is not null)
            {
                reasons.Add(exclusion);
                reasons.Add($"{exclusion}:{keySelector(item).SortKey}");
            }
        }

        T[] ordered = canonical
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.AvailabilityTime.Value)
            .ToArray();
        return new ScreenerObservationQuality<T>(new ImmutableValueList<T>(ordered),
            StableReasons(reasons));
    }

    private static void AddBarGapReasons(IEnumerable<ScreenerBar> observations,
        string? timeframe, List<string> reasons)
    {
        ScreenerBar[] bars = observations
            .OrderBy(static item => item.Bar.OpenTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ToArray();
        if (bars.Length < 2)
        {
            return;
        }

        if (!TryParseTimeframe(timeframe, out TimeSpan interval))
        {
            reasons.Add("bar-gap-timeframe-unknown");
            return;
        }

        for (int index = 1; index < bars.Length; index++)
        {
            TimeSpan distance = bars[index].Bar.OpenTime.Value
                - bars[index - 1].Bar.OpenTime.Value;
            if (distance > interval)
            {
                reasons.Add("bar-gap");
                reasons.Add($"bar-gap:{FormatTime(bars[index - 1].Bar.OpenTime)}"
                    + $":{FormatTime(bars[index].Bar.OpenTime)}");
            }
        }
    }

    private static bool TryParseTimeframe(string? timeframe, out TimeSpan interval)
    {
        interval = default;
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return false;
        }

        string value = timeframe.Trim();
        string suffix;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            suffix = "ms";
        }
        else if (value.Length > 0)
        {
            suffix = value[^1].ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            return false;
        }

        string number = value[..^suffix.Length];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long count)
            || count <= 0)
        {
            return false;
        }

        try
        {
            interval = suffix.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(count),
                "s" => TimeSpan.FromSeconds(count),
                "m" => TimeSpan.FromMinutes(count),
                "h" => TimeSpan.FromHours(count),
                "d" => TimeSpan.FromDays(count),
                "w" => TimeSpan.FromDays(checked(count * 7L)),
                _ => default
            };
            return interval > TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static ScreenerFeatureValue FinalizeFeature(ScreenerFeatureValue value,
        ScreenerFeatureKind kind, IEnumerable<ScreenerCausalObservation> required,
        IEnumerable<string> qualityReasons, CausalEventCursor cursor,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        string[] stableQualityReasons = StableReasons(qualityReasons).ToArray();
        bool qualityUnknown = stableQualityReasons.Any(static reason =>
            StringComparer.Ordinal.Equals(reason, "conflict")
            || StringComparer.Ordinal.Equals(reason, "bar-gap")
            || StringComparer.Ordinal.Equals(reason, "bar-gap-timeframe-unknown"));
        if (qualityUnknown && value.State != ScreenerFeatureState.UnknownConfiguration)
        {
            value = ScreenerFeatureValue.Unknown(kind, ScreenerFeatureState.UnknownQuality,
                stableQualityReasons[0]);
        }

        ScreenerFreshnessAssessment freshness = AssessFreshness(kind, required, cursor,
            freshnessPolicy);
        ScreenerFeatureCompleteness completeness = value.State == ScreenerFeatureState.Ready
            ? stableQualityReasons.Length == 0
                ? ScreenerFeatureCompleteness.Complete
                : ScreenerFeatureCompleteness.Incomplete
            : value.Completeness;
        return value.WithMetadata(completeness, freshness.Status, freshness.Age,
            freshness.LatestAvailabilityTime,
            stableQualityReasons.Concat(freshness.Reasons));
    }

    private static ScreenerFreshnessAssessment AssessFreshness(ScreenerFeatureKind kind,
        IEnumerable<ScreenerCausalObservation> required, CausalEventCursor cursor,
        ScreenerFreshnessPolicy? freshnessPolicy)
    {
        ScreenerCausalObservation[] requiredSnapshot = required.ToArray();
        if (requiredSnapshot.Length == 0)
        {
            return new ScreenerFreshnessAssessment(ScreenerFeatureFreshness.Unknown, null, null,
                new ImmutableValueList<string>([$"freshness-no-observation:{kind}"]));
        }

        List<string> reasons = [];
        List<(string SourceId, UtcTimestamp Availability, TimeSpan Age)> sources = [];
        foreach (IGrouping<string, ScreenerCausalObservation> group in requiredSnapshot
            .GroupBy(static item => item.SourceId, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            ScreenerCausalObservation latest = group
                .OrderByDescending(static item => item.AvailabilityTime.Value)
                .ThenByDescending(static item => item.EventTime.Value)
                .ThenByDescending(static item => item.StableSequence)
                .First();
            TimeSpan age = cursor.AsOf.Value - latest.AvailabilityTime.Value;
            sources.Add((group.Key, latest.AvailabilityTime, age));

            TimeSpan? ttl = null;
            string? reason = null;
            bool known = freshnessPolicy is not null
                && freshnessPolicy.TryGetTimeToLive(kind, group.Key, out ttl, out reason);
            if (!known)
            {
                reasons.Add(reason is not null && reason.StartsWith("freshness-ttl-conflict:",
                    StringComparison.Ordinal)
                    ? "freshness-ttl-conflict"
                    : "freshness-ttl-missing");
                reasons.Add(reason ?? $"freshness-ttl-missing:{group.Key}");
            }
            else if (age > ttl!.Value)
            {
                reasons.Add("freshness-stale");
                reasons.Add($"freshness-stale:{group.Key}");
            }
        }

        TimeSpan maximumAge = sources.Max(static source => source.Age);
        UtcTimestamp latestAvailability = new(sources.Min(static source =>
            source.Availability.Value));
        ScreenerFeatureFreshness status = reasons.Any(static reason =>
            reason.StartsWith("freshness-stale:", StringComparison.Ordinal))
            ? ScreenerFeatureFreshness.Stale
            : reasons.Count == 0
                ? ScreenerFeatureFreshness.Fresh
                : ScreenerFeatureFreshness.Unknown;
        return new ScreenerFreshnessAssessment(status, maximumAge, latestAvailability,
            StableReasons(reasons));
    }

    private static T[] TakeLatest<T>(IReadOnlyList<T> values, int count)
    {
        if (count <= 0 || values.Count == 0)
        {
            return [];
        }

        int start = Math.Max(0, values.Count - count);
        T[] result = new T[values.Count - start];
        for (int index = start; index < values.Count; index++)
        {
            result[index - start] = values[index];
        }

        return result;
    }

    private static ImmutableValueList<string> StableReasons(IEnumerable<string> reasons) =>
        new(reasons.Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));

    private static string FormatTime(UtcTimestamp timestamp) =>
        timestamp.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool TryGetPositiveWindow(int? configuredWindow, ScreenerFeatureKind kind,
        out int window, out ScreenerFeatureValue? unknown)
    {
        if (!configuredWindow.HasValue)
        {
            window = 0;
            unknown = ConfigurationUnknown(kind, $"{kind} window is missing");
            return false;
        }

        if (configuredWindow.Value <= 0)
        {
            window = 0;
            unknown = ConfigurationUnknown(kind, $"{kind} window must be positive");
            return false;
        }

        window = configuredWindow.Value;
        unknown = null;
        return true;
    }

    private static bool TryGetTwoWindowRequirement(int window, ScreenerFeatureKind kind,
        out int required, out ScreenerFeatureValue? unknown)
    {
        long requiredLong = (2L * window) + 1L;
        if (requiredLong > int.MaxValue)
        {
            required = 0;
            unknown = ConfigurationUnknown(kind, $"{kind} window is too large");
            return false;
        }

        required = (int)requiredLong;
        unknown = null;
        return true;
    }

    private static bool TryGetOneWindowRequirement(int window, ScreenerFeatureKind kind,
        out int required, out ScreenerFeatureValue? unknown)
    {
        if (window == int.MaxValue)
        {
            required = 0;
            unknown = ConfigurationUnknown(kind, $"{kind} window is too large");
            return false;
        }

        required = window + 1;
        unknown = null;
        return true;
    }

    private static bool HasCommonConfiguration(ScreenerFeatureConfiguration configuration,
        ScreenerFeatureKind kind, out ScreenerFeatureValue? unknown)
    {
        if (!TryGetCommonConfigurationReason(configuration, out string? reason))
        {
            unknown = ConfigurationUnknown(kind, reason!);
            return false;
        }

        unknown = null;
        return true;
    }

    private static bool TryGetCommonConfigurationReason(
        ScreenerFeatureConfiguration configuration, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(configuration.Timeframe))
        {
            reason = "configuration timeframe is missing";
            return false;
        }

        if (configuration.Version is null)
        {
            reason = "configuration formula version is missing";
            return false;
        }

        reason = null;
        return true;
    }

    private static ScreenerFeatureValue Ready(ScreenerFeatureKind kind, decimal value) =>
        ScreenerFeatureValue.Ready(kind, value);

    private static ScreenerFeatureValue Warmup(ScreenerFeatureKind kind, string reason) =>
        ScreenerFeatureValue.Unknown(kind, ScreenerFeatureState.UnknownWarmup, reason);

    private static ScreenerFeatureValue ConfigurationUnknown(ScreenerFeatureKind kind,
        string reason) => ScreenerFeatureValue.Unknown(kind,
            ScreenerFeatureState.UnknownConfiguration, reason);

    private static ScreenerFeatureValue InputUnknown(ScreenerFeatureKind kind, string reason) =>
        ScreenerFeatureValue.Unknown(kind, ScreenerFeatureState.UnknownInput, reason);

    private static ScreenerFeatureValue ZeroDenominator(ScreenerFeatureKind kind,
        string reason) => ScreenerFeatureValue.Unknown(kind,
            ScreenerFeatureState.UnknownZeroDenominator, reason);

    private static ScreenerFeatureValue Overflow(ScreenerFeatureKind kind, string reason) =>
        ScreenerFeatureValue.Unknown(kind, ScreenerFeatureState.UnknownOverflow, reason);
}
