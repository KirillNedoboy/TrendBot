using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Stable version of the independent, passive risk evaluation boundary.</summary>
public sealed record RiskEvaluationVersion
{
    public const int CurrentFormatVersion = 1;
    public const string CurrentValue = "risk-evaluation-v1";

    public static RiskEvaluationVersion Current { get; } = new(CurrentValue);

    public string Value { get; }

    [JsonConstructor]
    public RiskEvaluationVersion(string value)
    {
        ContractGuard.RequireText(value, nameof(value));
        Value = value;
    }
}

/// <summary>Stable reason identifiers emitted by the independent Risk Engine.</summary>
public static class RiskReasonCodes
{
    public const string CurrentVersion = "risk-reasons-v1";

    public const string DecisionOutcomeNotAllow = "risk-decision-outcome-not-allow";
    public const string DecisionProjectionMissing = "risk-decision-projection-missing";
    public const string DecisionIdentityMismatch = "risk-decision-identity-mismatch";
    public const string DecisionRecoveryRequired = "risk-decision-recovery-required";
    public const string RiskMathVersionMismatch = "risk-math-version-mismatch";
    public const string RiskMathInputMismatch = "risk-math-input-mismatch";
    public const string RiskMathCalculationFailed = "risk-math-calculation-failed";
    public const string RiskMathOverflow = "risk-math-overflow";
    public const string RiskMathUnavailable = "risk-math-unavailable";
    public const string AccountStateUnknown = "risk-account-state-unknown";
    public const string AccountStateUnhealthy = "risk-account-state-unhealthy";
    public const string PortfolioStateUnknown = "risk-portfolio-state-unknown";
    public const string PortfolioStateUnhealthy = "risk-portfolio-state-unhealthy";
    public const string MarketStateUnknown = "risk-market-state-unknown";
    public const string MarketStateUnhealthy = "risk-market-state-unhealthy";
    public const string EvidenceStale = "risk-evidence-stale";
    public const string EvidenceFuture = "risk-evidence-future";
    public const string EvidenceConflict = "risk-evidence-conflict";
    public const string EvidenceCursorMismatch = "risk-evidence-cursor-mismatch";
    public const string LimitsMissing = "risk-limits-missing";
    public const string PortfolioLimitsMissing = "risk-portfolio-limits-missing";
    public const string LimitCurrencyMismatch = "risk-limit-currency-mismatch";
    public const string PerTradeRiskExceeded = "risk-per-trade-limit-exceeded";
    public const string DailyRiskExceeded = "risk-daily-limit-exceeded";
    public const string PortfolioRiskExceeded = "risk-portfolio-risk-limit-exceeded";
    public const string NotionalExceeded = "risk-notional-limit-exceeded";
    public const string MarginExceeded = "risk-margin-limit-exceeded";
    public const string PositionSlotsExceeded = "risk-position-slots-limit-exceeded";
    public const string DailyLossUnknown = "risk-daily-loss-unknown";
    public const string DailyLossPeriodMismatch = "risk-daily-loss-period-mismatch";
    public const string CapacityUsageUnknown = "risk-capacity-usage-unknown";
    public const string CapacityArithmeticOverflow = "risk-capacity-arithmetic-overflow";
    public const string CandidateMarginUnknown = "risk-candidate-margin-unknown";
    public const string CandidateSlotsUnknown = "risk-candidate-slots-unknown";
    public const string KillSwitchUnknown = "risk-kill-switch-unknown";
    public const string KillSwitchEnabled = "risk-kill-switch-enabled";
    public const string ProtectiveEvidenceMissing = "risk-protective-evidence-missing";
    public const string ProtectiveEvidenceInvalid = "risk-protective-evidence-invalid";
    public const string ProtectiveEvidenceStale = "risk-protective-evidence-stale";
    public const string ProtectiveEvidenceFuture = "risk-protective-evidence-future";
    public const string ProtectiveEvidenceConflict = "risk-protective-evidence-conflict";
    public const string CooldownUnknown = "risk-cooldown-unknown";
    public const string CooldownActive = "risk-cooldown-active";
    public const string CooldownInstrumentMismatch = "risk-cooldown-instrument-mismatch";
    public const string InputInvalid = "risk-input-invalid";
    public const string ConfigurationInvalid = "risk-configuration-invalid";
    public const string Approved = "risk-approved";
}

public enum RiskHealthState
{
    Unknown,
    Healthy,
    Unhealthy,
    Conflicting,

    // Compatibility spelling for callers that use the adjective form.
    Conflict = Conflicting
}

public enum RiskEvidenceFreshness
{
    Unknown,
    Fresh,
    Stale,
    Conflicting,

    // Compatibility spelling for callers that use the current-state form.
    Current = Fresh,
    Conflict = Conflicting
}

/// <summary>Versioned causal health evidence supplied by an account, portfolio, or market provider.</summary>
public sealed record RiskHealthEvidence
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public RiskHealthState State { get; }
    public RiskEvidenceFreshness Freshness { get; }
    public string SourceId { get; }
    public string EvidenceVersion { get; }
    public CausalEventCursor Cursor { get; }
    public string Provenance { get; }
    public InstrumentId? InstrumentId { get; }
    public PortfolioId? PortfolioId { get; }

    [JsonIgnore]
    public string Source => SourceId;

    [JsonIgnore]
    public string Version => EvidenceVersion;

    [JsonConstructor]
    public RiskHealthEvidence(int formatVersion, RiskHealthState state,
        RiskEvidenceFreshness freshness, string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance, InstrumentId? instrumentId = null,
        PortfolioId? portfolioId = null)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk health evidence format is unsupported.");
        }

        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireDefined(freshness, nameof(freshness));
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(evidenceVersion, nameof(evidenceVersion));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        FormatVersion = formatVersion;
        State = state;
        Freshness = freshness;
        SourceId = sourceId;
        EvidenceVersion = evidenceVersion;
        Cursor = cursor;
        Provenance = provenance;
        InstrumentId = instrumentId;
        PortfolioId = portfolioId;
    }

    public RiskHealthEvidence(string sourceId, string evidenceVersion,
        RiskHealthState state, RiskEvidenceFreshness freshness, CausalEventCursor cursor,
        string provenance, InstrumentId? instrumentId = null, PortfolioId? portfolioId = null)
        : this(CurrentFormatVersion, state, freshness, sourceId, evidenceVersion, cursor,
            provenance, instrumentId, portfolioId)
    {
    }

    public static RiskHealthEvidence Healthy(string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance, InstrumentId? instrumentId = null,
        PortfolioId? portfolioId = null) => new(sourceId, evidenceVersion,
            RiskHealthState.Healthy, RiskEvidenceFreshness.Fresh, cursor, provenance,
            instrumentId, portfolioId);

    public static RiskHealthEvidence Unknown(string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance, InstrumentId? instrumentId = null,
        PortfolioId? portfolioId = null) => new(sourceId, evidenceVersion,
            RiskHealthState.Unknown, RiskEvidenceFreshness.Unknown, cursor, provenance,
            instrumentId, portfolioId);
}

public enum RiskKillSwitchStatus
{
    Unknown,
    Enabled,
    Clear,

    // Compatibility spelling for callers that describe a cleared switch.
    Disabled = Clear
}

/// <summary>Explicit, versioned kill-switch evidence. Absence and unknown remain blocking.</summary>
public sealed record RiskKillSwitchEvidence
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public RiskKillSwitchStatus Status { get; }
    public RiskEvidenceFreshness Freshness { get; }
    public string SourceId { get; }
    public string EvidenceVersion { get; }
    public CausalEventCursor Cursor { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public string Source => SourceId;

    [JsonIgnore]
    public string Version => EvidenceVersion;

    [JsonConstructor]
    public RiskKillSwitchEvidence(int formatVersion, RiskKillSwitchStatus status,
        RiskEvidenceFreshness freshness, string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk kill-switch evidence format is unsupported.");
        }

        ContractGuard.RequireDefined(status, nameof(status));
        ContractGuard.RequireDefined(freshness, nameof(freshness));
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(evidenceVersion, nameof(evidenceVersion));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        FormatVersion = formatVersion;
        Status = status;
        Freshness = freshness;
        SourceId = sourceId;
        EvidenceVersion = evidenceVersion;
        Cursor = cursor;
        Provenance = provenance;
    }

    public RiskKillSwitchEvidence(string sourceId, string evidenceVersion,
        RiskKillSwitchStatus status, RiskEvidenceFreshness freshness,
        CausalEventCursor cursor, string provenance)
        : this(CurrentFormatVersion, status, freshness, sourceId, evidenceVersion, cursor,
            provenance)
    {
    }

    public static RiskKillSwitchEvidence Clear(string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance) => new(sourceId, evidenceVersion,
            RiskKillSwitchStatus.Clear, RiskEvidenceFreshness.Fresh, cursor, provenance);

    public static RiskKillSwitchEvidence Enabled(string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance) => new(sourceId, evidenceVersion,
            RiskKillSwitchStatus.Enabled, RiskEvidenceFreshness.Fresh, cursor, provenance);
}

/// <summary>Explicit instrument cooldown state. It carries no duration default.</summary>
public sealed record RiskCooldownState
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public InstrumentId InstrumentId { get; }
    public RiskCooldownStatus Status { get; }
    public UtcTimestamp? ActiveUntil { get; }
    public RiskEvidenceFreshness Freshness { get; }
    public string SourceId { get; }
    public string EvidenceVersion { get; }
    public CausalEventCursor Cursor { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public string Source => SourceId;

    [JsonIgnore]
    public string Version => EvidenceVersion;

    [JsonConstructor]
    public RiskCooldownState(int formatVersion, InstrumentId instrumentId,
        RiskCooldownStatus status, UtcTimestamp? activeUntil,
        RiskEvidenceFreshness freshness, string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk cooldown format is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(status, nameof(status));
        ContractGuard.RequireDefined(freshness, nameof(freshness));
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(evidenceVersion, nameof(evidenceVersion));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        if (status == RiskCooldownStatus.ActiveUntil && activeUntil is null)
        {
            throw new ArgumentException(
                "An active cooldown must provide its explicit expiry.", nameof(activeUntil));
        }

        InstrumentId = instrumentId;
        Status = status;
        ActiveUntil = activeUntil;
        Freshness = freshness;
        SourceId = sourceId;
        EvidenceVersion = evidenceVersion;
        Cursor = cursor;
        Provenance = provenance;
        FormatVersion = formatVersion;
    }

    public RiskCooldownState(InstrumentId instrumentId, RiskCooldownStatus status,
        UtcTimestamp? activeUntil, RiskEvidenceFreshness freshness, string sourceId,
        string evidenceVersion, CausalEventCursor cursor, string provenance)
        : this(CurrentFormatVersion, instrumentId, status, activeUntil, freshness, sourceId,
            evidenceVersion, cursor, provenance)
    {
    }

    public static RiskCooldownState Clear(InstrumentId instrumentId, string sourceId,
        string evidenceVersion, CausalEventCursor cursor, string provenance) => new(
        instrumentId, RiskCooldownStatus.Clear, null, RiskEvidenceFreshness.Fresh,
        sourceId, evidenceVersion, cursor, provenance);

    public static RiskCooldownState Active(InstrumentId instrumentId, UtcTimestamp activeUntil,
        string sourceId, string evidenceVersion, CausalEventCursor cursor,
        string provenance) => new(instrumentId, RiskCooldownStatus.ActiveUntil, activeUntil,
            RiskEvidenceFreshness.Fresh, sourceId, evidenceVersion, cursor, provenance);
}

public enum RiskCooldownStatus
{
    Unknown,
    ActiveUntil,
    Clear,

    // Compatibility spelling for callers that use an explicit expired state.
    Expired = Clear
}

public enum RiskProtectionLevelKind
{
    ProtectiveStop,
    TakeProfit1,
    TakeProfit2
}

/// <summary>One immutable causal protective-level observation.</summary>
public sealed record RiskProtectionLevelEvidence
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public RiskProtectionLevelKind Kind { get; }
    public Price Price { get; }
    public InstrumentId InstrumentId { get; }
    public Direction Direction { get; }
    public string SetupId { get; }
    public string MethodId { get; }
    public string SourceId { get; }
    public string EvidenceVersion { get; }
    public RiskEvidenceFreshness Freshness { get; }
    public CausalEventCursor Cursor { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public string Source => SourceId;

    [JsonIgnore]
    public string Version => EvidenceVersion;

    [JsonConstructor]
    public RiskProtectionLevelEvidence(int formatVersion, RiskProtectionLevelKind kind,
        Price price, InstrumentId instrumentId, Direction direction, string setupId,
        string methodId, string sourceId, string evidenceVersion,
        RiskEvidenceFreshness freshness, CausalEventCursor cursor, string provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk protection evidence format is unsupported.");
        }

        ContractGuard.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireText(setupId, nameof(setupId));
        ContractGuard.RequireText(methodId, nameof(methodId));
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(evidenceVersion, nameof(evidenceVersion));
        ContractGuard.RequireDefined(freshness, nameof(freshness));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        FormatVersion = formatVersion;
        Kind = kind;
        Price = price;
        InstrumentId = instrumentId;
        Direction = direction;
        SetupId = setupId;
        MethodId = methodId;
        SourceId = sourceId;
        EvidenceVersion = evidenceVersion;
        Freshness = freshness;
        Cursor = cursor;
        Provenance = provenance;
    }

    public RiskProtectionLevelEvidence(RiskProtectionLevelKind kind, Price price,
        InstrumentId instrumentId, Direction direction, string setupId, string methodId,
        string sourceId, string evidenceVersion, RiskEvidenceFreshness freshness,
        CausalEventCursor cursor, string provenance)
        : this(CurrentFormatVersion, kind, price, instrumentId, direction, setupId, methodId,
            sourceId, evidenceVersion, freshness, cursor, provenance)
    {
    }
}

/// <summary>Complete caller-supplied stop and take-profit evidence.</summary>
public sealed record RiskProtectionEvidence
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public InstrumentId InstrumentId { get; }
    public Direction Direction { get; }
    public string SetupId { get; }
    public string MethodId { get; }
    public string SourceId { get; }
    public string EvidenceVersion { get; }
    public RiskEvidenceFreshness Freshness { get; }
    public CausalEventCursor Cursor { get; }
    public RiskProtectionLevelEvidence ProtectiveStop { get; }
    public RiskProtectionLevelEvidence TakeProfit1 { get; }
    public RiskProtectionLevelEvidence TakeProfit2 { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public RiskProtectionLevelEvidence Stop => ProtectiveStop;

    [JsonIgnore]
    public Price ProtectiveStopPrice => ProtectiveStop.Price;

    [JsonIgnore]
    public Price TakeProfit1Price => TakeProfit1.Price;

    [JsonIgnore]
    public Price TakeProfit2Price => TakeProfit2.Price;

    [JsonIgnore]
    public string Source => SourceId;

    [JsonIgnore]
    public string Version => EvidenceVersion;

    [JsonConstructor]
    public RiskProtectionEvidence(int formatVersion, InstrumentId instrumentId,
        Direction direction, string setupId, string methodId, string sourceId,
        string evidenceVersion, RiskEvidenceFreshness freshness,
        CausalEventCursor cursor, RiskProtectionLevelEvidence protectiveStop,
        RiskProtectionLevelEvidence takeProfit1, RiskProtectionLevelEvidence takeProfit2,
        string provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk protection evidence format is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireText(setupId, nameof(setupId));
        ContractGuard.RequireText(methodId, nameof(methodId));
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(evidenceVersion, nameof(evidenceVersion));
        ContractGuard.RequireDefined(freshness, nameof(freshness));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(protectiveStop);
        ArgumentNullException.ThrowIfNull(takeProfit1);
        ArgumentNullException.ThrowIfNull(takeProfit2);
        ContractGuard.RequireText(provenance, nameof(provenance));
        ValidateLevel(protectiveStop, RiskProtectionLevelKind.ProtectiveStop, instrumentId,
            direction, setupId, methodId, sourceId, evidenceVersion, freshness, cursor,
            nameof(protectiveStop));
        ValidateLevel(takeProfit1, RiskProtectionLevelKind.TakeProfit1, instrumentId,
            direction, setupId, methodId, sourceId, evidenceVersion, freshness, cursor,
            nameof(takeProfit1));
        ValidateLevel(takeProfit2, RiskProtectionLevelKind.TakeProfit2, instrumentId,
            direction, setupId, methodId, sourceId, evidenceVersion, freshness, cursor,
            nameof(takeProfit2));
        InstrumentId = instrumentId;
        Direction = direction;
        SetupId = setupId;
        MethodId = methodId;
        SourceId = sourceId;
        EvidenceVersion = evidenceVersion;
        Freshness = freshness;
        Cursor = cursor;
        ProtectiveStop = protectiveStop;
        TakeProfit1 = takeProfit1;
        TakeProfit2 = takeProfit2;
        Provenance = provenance;
        FormatVersion = formatVersion;
    }

    public RiskProtectionEvidence(InstrumentId instrumentId, Direction direction,
        string setupId, string methodId, string sourceId, string evidenceVersion,
        RiskEvidenceFreshness freshness, CausalEventCursor cursor,
        RiskProtectionLevelEvidence protectiveStop, RiskProtectionLevelEvidence takeProfit1,
        RiskProtectionLevelEvidence takeProfit2, string provenance)
        : this(CurrentFormatVersion, instrumentId, direction, setupId, methodId, sourceId,
            evidenceVersion, freshness, cursor, protectiveStop, takeProfit1, takeProfit2,
            provenance)
    {
    }

    public RiskProtectionEvidence(InstrumentId instrumentId, Direction direction,
        string setupId, string methodId, string sourceId, string evidenceVersion,
        RiskEvidenceFreshness freshness, CausalEventCursor cursor, Price protectiveStop,
        Price takeProfit1, Price takeProfit2, string provenance)
        : this(instrumentId, direction, setupId, methodId, sourceId, evidenceVersion,
            freshness, cursor,
            new RiskProtectionLevelEvidence(RiskProtectionLevelKind.ProtectiveStop,
                protectiveStop, instrumentId, direction, setupId, methodId, sourceId,
                evidenceVersion, freshness, cursor, provenance),
            new RiskProtectionLevelEvidence(RiskProtectionLevelKind.TakeProfit1, takeProfit1,
                instrumentId, direction, setupId, methodId, sourceId, evidenceVersion,
                freshness, cursor, provenance),
            new RiskProtectionLevelEvidence(RiskProtectionLevelKind.TakeProfit2, takeProfit2,
                instrumentId, direction, setupId, methodId, sourceId, evidenceVersion,
                freshness, cursor, provenance), provenance)
    {
    }

    private static void ValidateLevel(RiskProtectionLevelEvidence level,
        RiskProtectionLevelKind expectedKind, InstrumentId instrumentId, Direction direction,
        string setupId, string methodId, string sourceId, string evidenceVersion,
        RiskEvidenceFreshness freshness, CausalEventCursor cursor, string parameterName)
    {
        if (level.Kind != expectedKind || level.InstrumentId != instrumentId
            || level.Direction != direction
            || !StringComparer.Ordinal.Equals(level.SetupId, setupId)
            || !StringComparer.Ordinal.Equals(level.MethodId, methodId)
            || !StringComparer.Ordinal.Equals(level.SourceId, sourceId)
            || !StringComparer.Ordinal.Equals(level.EvidenceVersion, evidenceVersion)
            || level.Freshness != freshness || level.Cursor != cursor)
        {
            throw new ArgumentException(
                "Protective level evidence must match its aggregate source, method, identity, "
                + "freshness, and causal cursor.", parameterName);
        }
    }
}

/// <summary>Caller-supplied limit policies. Null values are unknown, never defaults.</summary>
public sealed record RiskLimitConfiguration
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public Money? MaxPerTradeRisk { get; }
    public Money? MaxDailyLoss { get; }
    public Money? MaxMargin { get; }
    public string? DailyLossPeriodId { get; }

    [JsonConstructor]
    public RiskLimitConfiguration(int formatVersion, Money? maxPerTradeRisk,
        Money? maxDailyLoss, Money? maxMargin, string? dailyLossPeriodId)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk limit configuration format is unsupported.");
        }

        ValidateNonNegative(maxPerTradeRisk, nameof(maxPerTradeRisk));
        ValidateNonNegative(maxDailyLoss, nameof(maxDailyLoss));
        ValidateNonNegative(maxMargin, nameof(maxMargin));
        if (dailyLossPeriodId is not null)
        {
            ContractGuard.RequireText(dailyLossPeriodId, nameof(dailyLossPeriodId));
        }

        FormatVersion = formatVersion;
        MaxPerTradeRisk = maxPerTradeRisk;
        MaxDailyLoss = maxDailyLoss;
        MaxMargin = maxMargin;
        DailyLossPeriodId = dailyLossPeriodId;
    }

    public RiskLimitConfiguration(Money? maxPerTradeRisk, Money? maxDailyLoss,
        Money? maxMargin, string? dailyLossPeriodId)
        : this(CurrentFormatVersion, maxPerTradeRisk, maxDailyLoss, maxMargin,
            dailyLossPeriodId)
    {
    }

    private static void ValidateNonNegative(Money? value, string parameterName)
    {
        if (value is not null && value.Amount < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value.Amount,
                "A risk limit cannot be negative.");
        }
    }
}

/// <summary>Caller-owned current portfolio capacity usage.</summary>
public sealed record RiskPortfolioUsage
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public PortfolioId PortfolioId { get; }
    public Money Risk { get; }
    public Money NotionalExposure { get; }
    public Money Margin { get; }
    public int PositionSlots { get; }
    public string SourceId { get; }
    public string EvidenceVersion { get; }
    public CausalEventCursor Cursor { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public Money CurrentRisk => Risk;

    [JsonIgnore]
    public Money CurrentNotionalExposure => NotionalExposure;

    [JsonIgnore]
    public Money CurrentMargin => Margin;

    [JsonConstructor]
    public RiskPortfolioUsage(int formatVersion, PortfolioId portfolioId, Money risk,
        Money notionalExposure, Money margin, int positionSlots, string sourceId,
        string evidenceVersion, CausalEventCursor cursor, string provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk portfolio usage format is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(notionalExposure);
        ArgumentNullException.ThrowIfNull(margin);
        if (risk.Amount < 0m || notionalExposure.Amount < 0m || margin.Amount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(risk),
                "Current portfolio capacity usage cannot be negative.");
        }

        if (positionSlots < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(positionSlots), positionSlots,
                "Current portfolio position slots cannot be negative.");
        }

        RequireCurrency(risk, notionalExposure, nameof(notionalExposure));
        RequireCurrency(risk, margin, nameof(margin));
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(evidenceVersion, nameof(evidenceVersion));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        FormatVersion = formatVersion;
        PortfolioId = portfolioId;
        Risk = risk;
        NotionalExposure = notionalExposure;
        Margin = margin;
        PositionSlots = positionSlots;
        SourceId = sourceId;
        EvidenceVersion = evidenceVersion;
        Cursor = cursor;
        Provenance = provenance;
    }

    public RiskPortfolioUsage(PortfolioId portfolioId, Money risk, Money notionalExposure,
        Money margin, int positionSlots, string sourceId, string evidenceVersion,
        CausalEventCursor cursor, string provenance)
        : this(CurrentFormatVersion, portfolioId, risk, notionalExposure, margin,
            positionSlots, sourceId, evidenceVersion, cursor, provenance)
    {
    }

    private static void RequireCurrency(Money expected, Money actual, string parameterName)
    {
        if (!StringComparer.Ordinal.Equals(expected.Currency, actual.Currency))
        {
            throw new ArgumentException("Portfolio usage monetary values must use one currency.",
                parameterName);
        }
    }
}

/// <summary>Versioned configuration for one passive risk evaluation.</summary>
public sealed record RiskEvaluationConfiguration
{
    public int FormatVersion { get; }
    public RiskEvaluationVersion Version { get; }
    public RiskMathVersion RiskMathVersion { get; }
    public RiskLimitConfiguration? Limits { get; }
    public PortfolioLimits? PortfolioLimits { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public RiskEvaluationVersion AlgorithmVersion => Version;

    [JsonIgnore]
    public RiskMathVersion ExpectedRiskMathVersion => RiskMathVersion;

    [JsonConstructor]
    public RiskEvaluationConfiguration(int formatVersion, RiskEvaluationVersion version,
        RiskMathVersion riskMathVersion, RiskLimitConfiguration? limits,
        PortfolioLimits? portfolioLimits, string provenance)
    {
        if (formatVersion != RiskEvaluationVersion.CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk evaluation configuration format is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(riskMathVersion);
        ContractGuard.RequireText(provenance, nameof(provenance));
        FormatVersion = formatVersion;
        Version = version;
        RiskMathVersion = riskMathVersion;
        Limits = limits;
        PortfolioLimits = portfolioLimits;
        Provenance = provenance;
    }

    public RiskEvaluationConfiguration(RiskEvaluationVersion version,
        RiskMathVersion riskMathVersion, RiskLimitConfiguration? limits,
        PortfolioLimits? portfolioLimits, string provenance)
        : this(RiskEvaluationVersion.CurrentFormatVersion, version, riskMathVersion,
            limits, portfolioLimits, provenance)
    {
    }
}

/// <summary>Complete immutable input snapshot for one independent evaluation.</summary>
public sealed record RiskEvaluationInput
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public string EvaluationId { get; }
    public DecisionProjectionSnapshot? DecisionProjection { get; }
    public Decision? Decision { get; }
    public RiskMathInput RiskMathInput { get; }
    public PortfolioId PortfolioId { get; }
    public CausalEventCursor Cursor { get; }
    public RiskPortfolioUsage? PortfolioUsage { get; }
    public Money? AccumulatedDailyLoss { get; }
    public string? AccumulatedLossPeriodId { get; }
    public Money? CandidateMargin { get; }
    public int? CandidatePositionSlots { get; }
    public RiskHealthEvidence? AccountHealth { get; }
    public RiskHealthEvidence? PortfolioHealth { get; }
    public RiskHealthEvidence? MarketHealth { get; }
    public RiskKillSwitchEvidence? KillSwitch { get; }
    public RiskProtectionEvidence? Protection { get; }
    public RiskCooldownState? Cooldown { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public Decision? DecisionEvidence => DecisionProjection?.Result.Decision ?? Decision;

    [JsonIgnore]
    public RiskProtectionEvidence? ProtectiveEvidence => Protection;

    [JsonConstructor]
    public RiskEvaluationInput(int formatVersion, string evaluationId,
        DecisionProjectionSnapshot? decisionProjection, Decision? decision,
        RiskMathInput riskMathInput, PortfolioId portfolioId, CausalEventCursor cursor,
        RiskPortfolioUsage? portfolioUsage, Money? accumulatedDailyLoss,
        string? accumulatedLossPeriodId, Money? candidateMargin,
        int? candidatePositionSlots, RiskHealthEvidence? accountHealth,
        RiskHealthEvidence? portfolioHealth, RiskHealthEvidence? marketHealth,
        RiskKillSwitchEvidence? killSwitch, RiskProtectionEvidence? protection,
        RiskCooldownState? cooldown, string provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk evaluation input format is unsupported.");
        }

        ContractGuard.RequireText(evaluationId, nameof(evaluationId));
        if (decisionProjection is null && decision is null)
        {
            throw new ArgumentException(
                "Risk evaluation input requires a decision projection or decision.",
                nameof(decisionProjection));
        }

        if (decisionProjection is not null && decision is not null
            && decisionProjection.Result.Decision != decision)
        {
            throw new ArgumentException(
                "The direct decision must match the decision projection.", nameof(decision));
        }

        ArgumentNullException.ThrowIfNull(riskMathInput);
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(cursor);
        if (accumulatedDailyLoss is { Amount: < 0m })
        {
            throw new ArgumentOutOfRangeException(nameof(accumulatedDailyLoss),
                "Accumulated daily loss cannot be negative.");
        }

        if (candidateMargin is { Amount: < 0m })
        {
            throw new ArgumentOutOfRangeException(nameof(candidateMargin),
                "Candidate margin cannot be negative.");
        }

        if (candidatePositionSlots is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidatePositionSlots),
                "Candidate position slots cannot be negative.");
        }

        if (accumulatedLossPeriodId is not null)
        {
            ContractGuard.RequireText(accumulatedLossPeriodId, nameof(accumulatedLossPeriodId));
        }

        ContractGuard.RequireText(provenance, nameof(provenance));
        FormatVersion = formatVersion;
        EvaluationId = evaluationId;
        DecisionProjection = decisionProjection;
        Decision = decisionProjection?.Result.Decision ?? decision;
        RiskMathInput = riskMathInput;
        PortfolioId = portfolioId;
        Cursor = cursor;
        PortfolioUsage = portfolioUsage;
        AccumulatedDailyLoss = accumulatedDailyLoss;
        AccumulatedLossPeriodId = accumulatedLossPeriodId;
        CandidateMargin = candidateMargin;
        CandidatePositionSlots = candidatePositionSlots;
        AccountHealth = accountHealth;
        PortfolioHealth = portfolioHealth;
        MarketHealth = marketHealth;
        KillSwitch = killSwitch;
        Protection = protection;
        Cooldown = cooldown;
        Provenance = provenance;
    }

    public RiskEvaluationInput(string evaluationId, DecisionProjectionSnapshot decisionProjection,
        RiskMathInput riskMathInput, PortfolioId portfolioId, CausalEventCursor cursor,
        RiskPortfolioUsage? portfolioUsage, Money? accumulatedDailyLoss,
        string? accumulatedLossPeriodId, Money? candidateMargin,
        int? candidatePositionSlots, RiskHealthEvidence? accountHealth,
        RiskHealthEvidence? portfolioHealth, RiskHealthEvidence? marketHealth,
        RiskKillSwitchEvidence? killSwitch, string provenance,
        RiskProtectionEvidence? protection = null, RiskCooldownState? cooldown = null)
        : this(CurrentFormatVersion, evaluationId, decisionProjection, null, riskMathInput,
            portfolioId, cursor, portfolioUsage, accumulatedDailyLoss, accumulatedLossPeriodId,
            candidateMargin, candidatePositionSlots, accountHealth, portfolioHealth, marketHealth,
            killSwitch, protection, cooldown, provenance)
    {
    }

    public RiskEvaluationInput(string evaluationId, Decision decision,
        RiskMathInput riskMathInput, PortfolioId portfolioId, CausalEventCursor cursor,
        RiskPortfolioUsage? portfolioUsage, Money? accumulatedDailyLoss,
        string? accumulatedLossPeriodId, Money? candidateMargin,
        int? candidatePositionSlots, RiskHealthEvidence? accountHealth,
        RiskHealthEvidence? portfolioHealth, RiskHealthEvidence? marketHealth,
        RiskKillSwitchEvidence? killSwitch, string provenance,
        RiskProtectionEvidence? protection = null, RiskCooldownState? cooldown = null)
        : this(CurrentFormatVersion, evaluationId, null, decision, riskMathInput, portfolioId,
            cursor, portfolioUsage, accumulatedDailyLoss, accumulatedLossPeriodId,
            candidateMargin, candidatePositionSlots, accountHealth, portfolioHealth, marketHealth,
            killSwitch, protection, cooldown, provenance)
    {
    }
}

/// <summary>One deterministic check retained as passive evidence in a result.</summary>
public sealed record RiskCheckEvidence
{
    public string CheckId { get; }
    public bool Passed { get; }
    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public RiskCheckEvidence(string checkId, bool passed, ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireText(checkId, nameof(checkId));
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons.Count == 0)
        {
            throw new ArgumentException("A risk check must retain at least one reason.",
                nameof(reasons));
        }

        CheckId = checkId;
        Passed = passed;
        Reasons = new ImmutableValueList<string>(reasons);
    }
}

/// <summary>Immutable passive result; approval is evidence only and cannot submit an order.</summary>
public sealed record RiskEvaluationResult
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public RiskEvaluationVersion Version { get; }
    public RiskEvaluationInput Input { get; }
    public RiskEvaluationConfiguration Configuration { get; }
    public RiskDecision RiskDecision { get; }
    public RiskMathResult? RiskMathResult { get; }
    public ImmutableValueList<RiskCheckEvidence> Checks { get; }
    public ImmutableValueList<string> Reasons { get; }
    public string Provenance { get; }

    [JsonIgnore]
    public bool IsApproved => RiskDecision.Outcome == RiskDecisionOutcome.Approved;

    [JsonIgnore]
    public bool SizingPassed => Checks.Any(static check => check.CheckId == "sizing"
        && check.Passed);

    [JsonIgnore]
    public bool LimitsPassed => Checks.Any(static check => check.CheckId == "limits"
        && check.Passed);

    [JsonIgnore]
    public bool ProtectionPassed => Checks.Any(static check => check.CheckId == "protection"
        && check.Passed);

    [JsonConstructor]
    public RiskEvaluationResult(int formatVersion, RiskEvaluationVersion version,
        RiskEvaluationInput input, RiskEvaluationConfiguration configuration,
        RiskDecision riskDecision, RiskMathResult? riskMathResult,
        ImmutableValueList<RiskCheckEvidence> checks, ImmutableValueList<string> reasons,
        string provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The risk evaluation result format is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(riskDecision);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(reasons);
        ContractGuard.RequireText(provenance, nameof(provenance));
        if (version != configuration.Version)
        {
            throw new ArgumentException(
                "The risk result version must match its configuration.", nameof(version));
        }

        string expectedDecisionId = input.DecisionEvidence?.DecisionId ?? input.EvaluationId;
        if (!StringComparer.Ordinal.Equals(riskDecision.RiskDecisionId,
                $"risk-{input.EvaluationId}")
            || !StringComparer.Ordinal.Equals(riskDecision.DecisionId, expectedDecisionId))
        {
            throw new ArgumentException(
                "The risk decision identity must match the immutable evaluation input.",
                nameof(riskDecision));
        }

        if (riskMathResult is not null
            && (riskMathResult.Version != configuration.RiskMathVersion
                || riskMathResult.Input != input.RiskMathInput))
        {
            throw new ArgumentException(
                "The retained risk-math result must match the evaluation configuration and input.",
                nameof(riskMathResult));
        }

        FormatVersion = formatVersion;
        Version = version;
        Input = input;
        Configuration = configuration;
        RiskDecision = riskDecision;
        RiskMathResult = riskMathResult;
        Checks = new ImmutableValueList<RiskCheckEvidence>(checks);
        Reasons = new ImmutableValueList<string>(reasons);
        Provenance = provenance;
    }

    internal static RiskEvaluationResult Create(RiskEvaluationInput input,
        RiskEvaluationConfiguration configuration, RiskDecision decision,
        RiskMathResult? math, IEnumerable<RiskCheckEvidence> checks,
        IEnumerable<string> reasons) => new(CurrentFormatVersion, configuration.Version,
        input, configuration, decision, math,
        new ImmutableValueList<RiskCheckEvidence>(checks),
        new ImmutableValueList<string>(reasons), input.Provenance);
}
