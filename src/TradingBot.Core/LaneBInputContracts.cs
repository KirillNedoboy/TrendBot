using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Explicit state of a Lane B input selection.</summary>
public enum LaneBInputStatus
{
    Ready,
    MissingData,
    Unavailable,
    Incompatible,
    Conflict,
    InsufficientHistory,
    UnknownConfiguration,
    ArithmeticOverflow
}

/// <summary>Compatibility name for callers that describe selection as state.</summary>
public enum LaneBInputState
{
    Ready = LaneBInputStatus.Ready,
    MissingData = LaneBInputStatus.MissingData,
    Unavailable = LaneBInputStatus.Unavailable,
    Incompatible = LaneBInputStatus.Incompatible,
    Conflict = LaneBInputStatus.Conflict,
    InsufficientHistory = LaneBInputStatus.InsufficientHistory,
    UnknownConfiguration = LaneBInputStatus.UnknownConfiguration,
    ArithmeticOverflow = LaneBInputStatus.ArithmeticOverflow
}

/// <summary>Identifies one immutable Lane B observation in a source stream.</summary>
public sealed record LaneBObservationIdentity
{
    public string SourceId { get; }

    [JsonIgnore]
    public string SourceIdentity => SourceId;

    public InstrumentId InstrumentId { get; }

    public string EventId { get; }

    public long StableSequence { get; }

    [JsonConstructor]
    public LaneBObservationIdentity(string sourceId, InstrumentId instrumentId,
        string eventId, long stableSequence)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(eventId, nameof(eventId));
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Lane B stable sequence cannot be negative.");
        }

        SourceId = sourceId;
        InstrumentId = instrumentId;
        EventId = eventId;
        StableSequence = stableSequence;
    }
}

/// <summary>
/// Common immutable causal and provenance fields for one Lane B observation.
/// The wrapped Core market event remains available without changing the shared
/// MarketEvent wire format.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(LaneBOpenInterestObservation), "openInterest")]
[JsonDerivedType(typeof(LaneBFundingObservation), "funding")]
public abstract record LaneBObservation : ILaneBCausalObservation
{
    public string SourceId { get; }

    [JsonIgnore]
    public string SourceIdentity => SourceId;

    public CausalMarketEvent CausalEvent { get; }

    public string EventId => CausalEvent.Payload.EventId;

    public InstrumentId InstrumentId => CausalEvent.Payload.InstrumentId;

    public UtcTimestamp EventTime => CausalEvent.Payload.EventTime;

    public UtcTimestamp ReceiveTime => CausalEvent.ReceiveTime;

    public UtcTimestamp AvailabilityTime => CausalEvent.AvailabilityTime;

    public UtcTimestamp? ExchangeTime => CausalEvent.ExchangeTime;

    public long StableSequence => CausalEvent.StableSequence;

    public string Unit { get; }

    public string Provenance { get; }

    [JsonIgnore]
    public LaneBObservationIdentity Identity => new(SourceId, InstrumentId, EventId,
        StableSequence);

    [JsonIgnore]
    public LaneBObservationIdentity EventIdentity => Identity;

    protected LaneBObservation(string sourceId, CausalMarketEvent causalEvent,
        string unit, string provenance)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ArgumentNullException.ThrowIfNull(causalEvent);
        ContractGuard.RequireText(unit, nameof(unit));
        ContractGuard.RequireText(provenance, nameof(provenance));
        SourceId = sourceId;
        CausalEvent = causalEvent;
        Unit = unit;
        Provenance = provenance;
    }

    /// <summary>Checks event time, availability, and stable sequence visibility.</summary>
    public bool IsEligible(CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return cursor.IsEligible(CausalEvent);
    }
}

/// <summary>One causally timestamped open-interest value.</summary>
public sealed record LaneBOpenInterestObservation : LaneBObservation
{
    public OIUpdate Update => (OIUpdate)CausalEvent.Payload;

    public decimal Value => Update.OpenInterest;

    [JsonIgnore]
    public decimal OpenInterest => Value;

    [JsonIgnore]
    public string OpenInterestUnit => Unit;

    [JsonConstructor]
    public LaneBOpenInterestObservation(string sourceId, CausalMarketEvent causalEvent,
        string unit, string provenance)
        : base(sourceId, RequireOpenInterest(causalEvent), unit, provenance)
    {
    }

    public LaneBOpenInterestObservation(string sourceId, OIUpdate update, string unit,
        string provenance, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, UtcTimestamp? exchangeTime = null)
        : this(sourceId, new CausalMarketEvent(RequireUpdate(update), receiveTime,
            availabilityTime, stableSequence, exchangeTime), unit, provenance)
    {
    }

    private static CausalMarketEvent RequireOpenInterest(CausalMarketEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Payload is OIUpdate
            ? value
            : throw new ArgumentException("The causal payload must be an OIUpdate.",
                nameof(value));
    }

    private static OIUpdate RequireUpdate(OIUpdate value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}

/// <summary>One causally timestamped funding observation with explicit kind and unit.</summary>
public sealed record LaneBFundingObservation : LaneBObservation
{
    public FundingUpdate Update => (FundingUpdate)CausalEvent.Payload;

    public decimal FundingRate => Update.FundingRate;

    public string FundingKind { get; }

    [JsonIgnore]
    public string FundingUnit => Unit;

    [JsonConstructor]
    public LaneBFundingObservation(string sourceId, CausalMarketEvent causalEvent,
        string unit, string fundingKind, string provenance)
        : base(sourceId, RequireFunding(causalEvent), unit, provenance)
    {
        ContractGuard.RequireText(fundingKind, nameof(fundingKind));
        FundingKind = fundingKind;
    }

    public LaneBFundingObservation(string sourceId, FundingUpdate update, string unit,
        string fundingKind, string provenance, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, long stableSequence, UtcTimestamp? exchangeTime = null)
        : this(sourceId, new CausalMarketEvent(RequireUpdate(update), receiveTime,
            availabilityTime, stableSequence, exchangeTime), unit, fundingKind, provenance)
    {
    }

    private static CausalMarketEvent RequireFunding(CausalMarketEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Payload is FundingUpdate
            ? value
            : throw new ArgumentException("The causal payload must be a FundingUpdate.",
                nameof(value));
    }

    private static FundingUpdate RequireUpdate(FundingUpdate value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }
}

/// <summary>
/// Caller-supplied Lane B input selection configuration. Null or incomplete
/// configuration remains an explicit unknown state; no unit, source, or history
/// default is supplied.
/// </summary>
public sealed record LaneBInputConfiguration
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string? ConfigurationVersion { get; }

    public string? OpenInterestUnit { get; }

    public string? OpenInterestSourceId { get; }

    public string? FundingUnit { get; }

    public string? FundingSourceId { get; }

    public string? FundingKind { get; }

    public bool RequireOpenInterest { get; }

    public bool RequireFunding { get; }

    public bool RequireOpenInterestDelta { get; }

    public LaneBObservationIdentity? OpenInterestDeltaFrom { get; }

    public LaneBObservationIdentity? OpenInterestDeltaTo { get; }

    [JsonIgnore]
    public bool IsKnown => FormatVersion == CurrentFormatVersion
        && !string.IsNullOrWhiteSpace(ConfigurationVersion)
        && (!RequireOpenInterest || !string.IsNullOrWhiteSpace(OpenInterestUnit))
        && (!RequireFunding || (!string.IsNullOrWhiteSpace(FundingUnit)
            && !string.IsNullOrWhiteSpace(FundingKind)))
        && (!RequireOpenInterestDelta || (!string.IsNullOrWhiteSpace(OpenInterestUnit)
            && OpenInterestDeltaFrom is not null && OpenInterestDeltaTo is not null));

    public LaneBInputConfiguration(string? configurationVersion,
        string? openInterestUnit, string? fundingUnit, string? fundingKind,
        bool requireOpenInterest, bool requireFunding, bool requireOpenInterestDelta,
        LaneBObservationIdentity? openInterestDeltaFrom = null,
        LaneBObservationIdentity? openInterestDeltaTo = null,
        string? openInterestSourceId = null, string? fundingSourceId = null)
        : this(CurrentFormatVersion, configurationVersion, openInterestUnit, fundingUnit,
            fundingKind, requireOpenInterest, requireFunding, requireOpenInterestDelta,
            openInterestDeltaFrom, openInterestDeltaTo, openInterestSourceId, fundingSourceId)
    {
    }

    [JsonConstructor]
    public LaneBInputConfiguration(int formatVersion, string? configurationVersion,
        string? openInterestUnit, string? fundingUnit, string? fundingKind,
        bool requireOpenInterest, bool requireFunding, bool requireOpenInterestDelta,
        LaneBObservationIdentity? openInterestDeltaFrom,
        LaneBObservationIdentity? openInterestDeltaTo, string? openInterestSourceId,
        string? fundingSourceId)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The Lane B input configuration format is unsupported.");
        }

        ValidateOptionalText(configurationVersion, nameof(configurationVersion));
        ValidateOptionalText(openInterestUnit, nameof(openInterestUnit));
        ValidateOptionalText(openInterestSourceId, nameof(openInterestSourceId));
        ValidateOptionalText(fundingUnit, nameof(fundingUnit));
        ValidateOptionalText(fundingSourceId, nameof(fundingSourceId));
        ValidateOptionalText(fundingKind, nameof(fundingKind));
        if (requireOpenInterestDelta && (openInterestDeltaFrom is null
            || openInterestDeltaTo is null))
        {
            throw new ArgumentException(
                "An open-interest delta requirement needs explicit from and to identities.",
                nameof(openInterestDeltaFrom));
        }

        FormatVersion = formatVersion;
        ConfigurationVersion = configurationVersion;
        OpenInterestUnit = openInterestUnit;
        OpenInterestSourceId = openInterestSourceId;
        FundingUnit = fundingUnit;
        FundingSourceId = fundingSourceId;
        FundingKind = fundingKind;
        RequireOpenInterest = requireOpenInterest;
        RequireFunding = requireFunding;
        RequireOpenInterestDelta = requireOpenInterestDelta;
        OpenInterestDeltaFrom = openInterestDeltaFrom;
        OpenInterestDeltaTo = openInterestDeltaTo;
    }

    private static void ValidateOptionalText(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An optional configuration value cannot be blank.",
                parameterName);
        }
    }
}

/// <summary>Selected Lane B observations and explicit diagnostics for one cursor.</summary>
public sealed record LaneBInputSelection
{
    public LaneBInputStatus Status { get; }

    [JsonIgnore]
    public LaneBInputState State => (LaneBInputState)Status;

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<LaneBOpenInterestObservation> OpenInterest { get; }

    public ImmutableValueList<LaneBFundingObservation> Funding { get; }

    public decimal? AbsoluteOpenInterestDelta { get; }

    [JsonIgnore]
    public decimal? OpenInterestDelta => AbsoluteOpenInterestDelta;

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsReady => Status == LaneBInputStatus.Ready;

    [JsonConstructor]
    public LaneBInputSelection(LaneBInputStatus status, CausalEventCursor cursor,
        ImmutableValueList<LaneBOpenInterestObservation> openInterest,
        ImmutableValueList<LaneBFundingObservation> funding,
        decimal? absoluteOpenInterestDelta, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireDefined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(openInterest);
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(reasons);
        Status = status;
        Cursor = cursor;
        OpenInterest = new ImmutableValueList<LaneBOpenInterestObservation>(openInterest);
        Funding = new ImmutableValueList<LaneBFundingObservation>(funding);
        AbsoluteOpenInterestDelta = absoluteOpenInterestDelta;
        Reasons = new ImmutableValueList<string>(reasons);
    }
}

/// <summary>
/// Immutable Lane B source snapshot. Its selection method filters by the shared
/// causal cursor before selecting values and never mutates the source collections.
/// </summary>
public sealed record LaneBInput
{
    public InstrumentId InstrumentId { get; }

    public ImmutableValueList<LaneBOpenInterestObservation> OpenInterest { get; }

    public ImmutableValueList<LaneBFundingObservation> Funding { get; }

    [JsonIgnore]
    public ImmutableValueList<LaneBOpenInterestObservation> OpenInterestObservations =>
        OpenInterest;

    [JsonIgnore]
    public ImmutableValueList<LaneBFundingObservation> FundingObservations => Funding;

    public string Provenance { get; }

    public LaneBInput(InstrumentId instrumentId,
        IEnumerable<LaneBOpenInterestObservation> openInterest,
        IEnumerable<LaneBFundingObservation> funding, string provenance)
        : this(instrumentId, SnapshotOpenInterest(instrumentId, openInterest),
            SnapshotFunding(instrumentId, funding), provenance)
    {
    }

    [JsonConstructor]
    public LaneBInput(InstrumentId instrumentId,
        ImmutableValueList<LaneBOpenInterestObservation> openInterest,
        ImmutableValueList<LaneBFundingObservation> funding, string provenance)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(openInterest);
        ArgumentNullException.ThrowIfNull(funding);
        ContractGuard.RequireText(provenance, nameof(provenance));
        ValidateInstrument(openInterest, instrumentId, nameof(openInterest));
        ValidateInstrument(funding, instrumentId, nameof(funding));
        InstrumentId = instrumentId;
        OpenInterest = new ImmutableValueList<LaneBOpenInterestObservation>(openInterest);
        Funding = new ImmutableValueList<LaneBFundingObservation>(funding);
        Provenance = provenance;
    }

    /// <summary>Selects the causally visible prefix under an explicit configuration.</summary>
    public LaneBInputSelection Select(CausalEventCursor cursor,
        LaneBInputConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (configuration is null || !configuration.IsKnown
            || (!configuration.RequireOpenInterest && !configuration.RequireFunding
                && !configuration.RequireOpenInterestDelta))
        {
            return Selection(LaneBInputStatus.UnknownConfiguration, cursor, [], [], null,
                ["lane-b-configuration-unknown"]);
        }

        List<string> reasons = [];
        (List<LaneBOpenInterestObservation> openInterest, bool conflict) =
            SelectOpenInterest(cursor, configuration, reasons);
        (List<LaneBFundingObservation> funding, bool fundingConflict) =
            SelectFunding(cursor, configuration, reasons);
        conflict |= fundingConflict;

        LaneBInputStatus status = conflict
            ? LaneBInputStatus.Conflict
            : DetermineAvailability(openInterest.Count, OpenInterest.Count,
                configuration.RequireOpenInterest, "open-interest", reasons);
        if (status == LaneBInputStatus.Ready || status == LaneBInputStatus.MissingData)
        {
            status = DetermineAvailability(funding.Count, Funding.Count,
                configuration.RequireFunding, "funding", reasons, status);
        }

        bool incompatible = reasons.Any(static reason => reason.StartsWith("incompatible-",
            StringComparison.Ordinal));
        if (incompatible)
        {
            status = LaneBInputStatus.Incompatible;
        }

        decimal? delta = null;
        if (configuration.RequireOpenInterestDelta)
        {
            if (status is LaneBInputStatus.Conflict or LaneBInputStatus.Incompatible)
            {
                // Preserve the more specific input failure and do not calculate from it.
            }
            else
            {
                LaneBOpenInterestObservation? from = openInterest.FirstOrDefault(item =>
                    item.Identity == configuration.OpenInterestDeltaFrom);
                LaneBOpenInterestObservation? to = openInterest.FirstOrDefault(item =>
                    item.Identity == configuration.OpenInterestDeltaTo);
                if (from is null || to is null)
                {
                    status = LaneBInputStatus.InsufficientHistory;
                    reasons.Add("open-interest-delta-history-insufficient");
                }
                else if (!StringComparer.Ordinal.Equals(from.SourceId, to.SourceId)
                    || !StringComparer.Ordinal.Equals(from.Unit, to.Unit))
                {
                    status = LaneBInputStatus.Incompatible;
                    reasons.Add("incompatible-open-interest-delta-observations");
                }
                else
                {
                    try
                    {
                        delta = Math.Abs(checked(to.Value - from.Value));
                    }
                    catch (OverflowException)
                    {
                        status = LaneBInputStatus.ArithmeticOverflow;
                        reasons.Add("open-interest-delta-overflow");
                    }
                }
            }
        }

        if (status == LaneBInputStatus.Ready && reasons.Any(static reason =>
            reason.StartsWith("unavailable-", StringComparison.Ordinal)))
        {
            status = LaneBInputStatus.Unavailable;
        }

        return Selection(status, cursor, openInterest, funding, delta, StableReasons(reasons));
    }

    private (List<LaneBOpenInterestObservation> Selected, bool Conflict) SelectOpenInterest(
        CausalEventCursor cursor, LaneBInputConfiguration configuration, List<string> reasons)
    {
        List<LaneBOpenInterestObservation> eligible = OpenInterest
            .Where(item => item.IsEligible(cursor))
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ToList();
        List<LaneBOpenInterestObservation> selected = [];
        Dictionary<string, LaneBOpenInterestObservation> seen = [];
        bool conflict = false;
        foreach (LaneBOpenInterestObservation item in eligible)
        {
            string identityKey = IdentityKey(item);
            if (!seen.TryGetValue(identityKey, out LaneBOpenInterestObservation? existing))
            {
                seen.Add(identityKey, item);
                if (configuration.RequireOpenInterest)
                {
                    if (!StringComparer.Ordinal.Equals(item.Unit, configuration.OpenInterestUnit)
                        || (configuration.OpenInterestSourceId is not null
                            && !StringComparer.Ordinal.Equals(item.SourceId,
                                configuration.OpenInterestSourceId)))
                    {
                        reasons.Add("incompatible-open-interest-unit");
                        continue;
                    }

                    selected.Add(item);
                }

                continue;
            }

            if (existing.Equals(item))
            {
                reasons.Add($"duplicate-open-interest:{item.EventId}");
            }
            else
            {
                conflict = true;
                reasons.Add($"conflict-open-interest:{item.EventId}");
            }
        }

        if (!configuration.RequireOpenInterest && configuration.RequireOpenInterestDelta)
        {
            selected = eligible
                .Where(item => StringComparer.Ordinal.Equals(item.Unit,
                    configuration.OpenInterestUnit))
                .GroupBy(static item => item.Identity)
                .Select(static group => group.First())
                .ToList();
        }

        if (configuration.RequireOpenInterest && selected.Count > 0
            && selected.Select(static item => item.SourceId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            reasons.Add("incompatible-open-interest-sources");
            conflict = true;
        }

        if (configuration.RequireOpenInterest && selected.Count == 0 && OpenInterest.Count > 0)
        {
            reasons.Add("unavailable-open-interest-prefix");
        }

        return (selected, conflict);
    }

    private (List<LaneBFundingObservation> Selected, bool Conflict) SelectFunding(
        CausalEventCursor cursor, LaneBInputConfiguration configuration, List<string> reasons)
    {
        List<LaneBFundingObservation> eligible = Funding
            .Where(item => item.IsEligible(cursor))
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ToList();
        List<LaneBFundingObservation> selected = [];
        Dictionary<string, LaneBFundingObservation> seen = [];
        bool conflict = false;
        foreach (LaneBFundingObservation item in eligible)
        {
            string identityKey = IdentityKey(item);
            if (!seen.TryGetValue(identityKey, out LaneBFundingObservation? existing))
            {
                seen.Add(identityKey, item);
                if (configuration.RequireFunding)
                {
                    if (!StringComparer.Ordinal.Equals(item.Unit, configuration.FundingUnit)
                        || !StringComparer.Ordinal.Equals(item.FundingKind,
                            configuration.FundingKind)
                        || (configuration.FundingSourceId is not null
                            && !StringComparer.Ordinal.Equals(item.SourceId,
                                configuration.FundingSourceId)))
                    {
                        reasons.Add("incompatible-funding-unit-or-kind");
                        continue;
                    }

                    selected.Add(item);
                }

                continue;
            }

            if (existing.Equals(item))
            {
                reasons.Add($"duplicate-funding:{item.EventId}");
            }
            else
            {
                conflict = true;
                reasons.Add($"conflict-funding:{item.EventId}");
            }
        }

        if (configuration.RequireFunding && selected.Count > 0
            && selected.Select(static item => item.SourceId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            reasons.Add("incompatible-funding-sources");
            conflict = true;
        }

        if (configuration.RequireFunding && selected.Count == 0 && Funding.Count > 0)
        {
            reasons.Add("unavailable-funding-prefix");
        }

        return (selected, conflict);
    }

    private static LaneBInputStatus DetermineAvailability(int selectedCount, int totalCount,
        bool required, string name, List<string> reasons,
        LaneBInputStatus current = LaneBInputStatus.Ready)
    {
        if (!required)
        {
            return current;
        }

        if (selectedCount > 0)
        {
            return current;
        }

        if (totalCount == 0)
        {
            reasons.Add($"missing-{name}");
            return LaneBInputStatus.MissingData;
        }

        reasons.Add($"unavailable-{name}-prefix");
        return LaneBInputStatus.Unavailable;
    }

    private static LaneBInputSelection Selection(LaneBInputStatus status,
        CausalEventCursor cursor, IEnumerable<LaneBOpenInterestObservation> openInterest,
        IEnumerable<LaneBFundingObservation> funding, decimal? delta,
        IEnumerable<string> reasons) => new(status, cursor,
            new ImmutableValueList<LaneBOpenInterestObservation>(openInterest),
            new ImmutableValueList<LaneBFundingObservation>(funding), delta,
            new ImmutableValueList<string>(StableReasons(reasons)));

    private static string IdentityKey(LaneBObservation item) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{item.SourceId}\u001f{item.InstrumentId.Value}\u001f{item.EventId}");

    private static IEnumerable<string> StableReasons(IEnumerable<string> reasons) => reasons
        .Where(static reason => !string.IsNullOrWhiteSpace(reason))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static reason => reason, StringComparer.Ordinal);

    private static ImmutableValueList<LaneBOpenInterestObservation> SnapshotOpenInterest(
        InstrumentId instrumentId, IEnumerable<LaneBOpenInterestObservation> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        LaneBOpenInterestObservation[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Lane B OI observations cannot contain null values.",
                nameof(values));
        }

        ValidateInstrument(snapshot, instrumentId, nameof(values));
        return new ImmutableValueList<LaneBOpenInterestObservation>(snapshot);
    }

    private static ImmutableValueList<LaneBFundingObservation> SnapshotFunding(
        InstrumentId instrumentId, IEnumerable<LaneBFundingObservation> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        LaneBFundingObservation[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Lane B funding observations cannot contain null values.",
                nameof(values));
        }

        ValidateInstrument(snapshot, instrumentId, nameof(values));
        return new ImmutableValueList<LaneBFundingObservation>(snapshot);
    }

    private static void ValidateInstrument<T>(IEnumerable<T> values,
        InstrumentId expected, string parameterName) where T : LaneBObservation
    {
        if (values.Any(item => item.InstrumentId != expected))
        {
            throw new ArgumentException(
                "Every Lane B observation must match the input instrument.", parameterName);
        }
    }
}
