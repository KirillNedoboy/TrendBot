using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum SignalAssessmentState
{
    Research,
    ConfirmedOnClosedBar
}

public enum SignalEvidenceState
{
    Complete,
    Unknown
}

public enum SignalInputRole
{
    Setup,
    HigherTimeframe
}

public enum SignalDeliveryChannel
{
    LocalJournal,
    Json,
    Console,
    Telegram
}

public enum SignalDeliveryStatus
{
    Attempted,
    Acknowledged,
    Rejected,
    Unknown
}

public enum SignalOutcomeKind
{
    MovementObserved,
    ReferenceLevelReached,
    Expired,
    Unknown
}

/// <summary>Logical identity of one public setup occurrence, independent of its content.</summary>
public sealed record SignalIdentity
{
    public const int CurrentFormatVersion = 1;
    public const int MultiTimeframeFormatVersion = 2;

    public int FormatVersion { get; }

    public InstrumentId InstrumentId { get; }

    public string Venue { get; }

    public string SetupTimeframe { get; }

    public Direction Direction { get; }

    public SetupType Family { get; }

    public string ProfileVersion { get; }

    public string FamilyAlgorithmVersion { get; }

    public string SetupEventId { get; }

    public string SignalId { get; }

    [JsonConstructor]
    public SignalIdentity(int formatVersion, InstrumentId instrumentId, string venue,
        string setupTimeframe, Direction direction, SetupType family,
        string profileVersion, string familyAlgorithmVersion, string setupEventId,
        string signalId)
    {
        if (formatVersion is not (CurrentFormatVersion or MultiTimeframeFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The signal identity format version is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(venue, nameof(venue));
        ContractGuard.RequireText(setupTimeframe, nameof(setupTimeframe));
        if (PublicSignalTimeframes.ContextForSetup(setupTimeframe) is null
            || formatVersion == CurrentFormatVersion && setupTimeframe != "15m")
        {
            throw new ArgumentException("Signal identity has an unsupported setup timeframe.",
                nameof(setupTimeframe));
        }

        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireDefined(family, nameof(family));
        ContractGuard.RequireText(profileVersion, nameof(profileVersion));
        ContractGuard.RequireText(familyAlgorithmVersion,
            nameof(familyAlgorithmVersion));
        ContractGuard.RequireText(setupEventId, nameof(setupEventId));
        SignalContractHash.RequireSha256(signalId, nameof(signalId));

        string expectedSignalId = ComputeSignalId(formatVersion, instrumentId, venue,
            setupTimeframe, direction, family, profileVersion,
            familyAlgorithmVersion, setupEventId);
        if (!StringComparer.Ordinal.Equals(signalId, expectedSignalId))
        {
            throw new ArgumentException("The signal ID does not match its logical identity.",
                nameof(signalId));
        }

        FormatVersion = formatVersion;
        InstrumentId = instrumentId;
        Venue = venue;
        SetupTimeframe = setupTimeframe;
        Direction = direction;
        Family = family;
        ProfileVersion = profileVersion;
        FamilyAlgorithmVersion = familyAlgorithmVersion;
        SetupEventId = setupEventId;
        SignalId = signalId;
    }

    public static SignalIdentity Create(InstrumentId instrumentId, string venue,
        string setupTimeframe, Direction direction, SetupType family,
        string profileVersion, string familyAlgorithmVersion, string setupEventId)
    {
        int version = setupTimeframe == "15m" ? CurrentFormatVersion : MultiTimeframeFormatVersion;
        string signalId = ComputeSignalId(version, instrumentId, venue,
            setupTimeframe, direction, family, profileVersion,
            familyAlgorithmVersion, setupEventId);
        return new SignalIdentity(version, instrumentId, venue,
            setupTimeframe, direction, family, profileVersion,
            familyAlgorithmVersion, setupEventId, signalId);
    }

    private static string ComputeSignalId(int formatVersion,
        InstrumentId instrumentId, string venue, string setupTimeframe,
        Direction direction, SetupType family, string profileVersion,
        string familyAlgorithmVersion, string setupEventId) =>
        SignalContractHash.Compute(new SignalIdentityMaterial(formatVersion,
            instrumentId, venue, setupTimeframe, direction, family, profileVersion,
            familyAlgorithmVersion, setupEventId));

    private sealed record SignalIdentityMaterial(int FormatVersion,
        InstrumentId InstrumentId, string Venue, string SetupTimeframe,
        Direction Direction, SetupType Family, string ProfileVersion,
        string FamilyAlgorithmVersion, string SetupEventId);
}

/// <summary>One public source event retained in a signal's causal provenance.</summary>
public sealed record SignalInputReference
{
    public string SourceId { get; }

    public string SourceVersion { get; }

    public string EventId { get; }

    public string Timeframe { get; }

    public string PayloadHash { get; }

    public SignalInputRole Role { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    [JsonConstructor]
    public SignalInputReference(string sourceId, string sourceVersion, string eventId,
        string payloadHash, SignalInputRole role, string timeframe,
        UtcTimestamp eventTime, UtcTimestamp availabilityTime, long stableSequence)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(sourceVersion, nameof(sourceVersion));
        ContractGuard.RequireText(eventId, nameof(eventId));
        SignalContractHash.RequireSha256(payloadHash, nameof(payloadHash));
        ContractGuard.RequireDefined(role, nameof(role));
        ContractGuard.RequireText(timeframe, nameof(timeframe));
        bool supported = role == SignalInputRole.Setup
            ? PublicSignalTimeframes.ContextForSetup(timeframe) is not null
            : timeframe is "15m" or "1h" or "4h";
        if (!supported)
        {
            throw new ArgumentException(
                "The input timeframe does not match its signal provenance role.",
                nameof(timeframe));
        }
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "A stable sequence cannot be negative.");
        }

        SourceId = sourceId;
        SourceVersion = sourceVersion;
        EventId = eventId;
        PayloadHash = payloadHash;
        Timeframe = timeframe;
        Role = role;
        EventTime = eventTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
    }
}

/// <summary>Versioned public inputs and the exact decision and higher-timeframe cuts.</summary>
public sealed record SignalProvenance
{
    public const int CurrentFormatVersion = 1;
    public const int MultiTimeframeFormatVersion = 2;

    public int FormatVersion { get; }

    public string ProfileVersion { get; }

    public string ProfileConfigurationHash { get; }

    public CausalEventCursor DecisionCut { get; }

    public CausalEventCursor HigherTimeframeCut { get; }

    public ImmutableValueList<SignalInputReference> InputReferences { get; }

    [JsonConstructor]
    public SignalProvenance(int formatVersion, string profileVersion,
        string profileConfigurationHash, CausalEventCursor decisionCut,
        CausalEventCursor higherTimeframeCut,
        ImmutableValueList<SignalInputReference> inputReferences)
    {
        if (formatVersion is not (CurrentFormatVersion or MultiTimeframeFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The signal provenance format version is unsupported.");
        }

        ContractGuard.RequireText(profileVersion, nameof(profileVersion));
        SignalContractHash.RequireSha256(profileConfigurationHash,
            nameof(profileConfigurationHash));
        ArgumentNullException.ThrowIfNull(decisionCut);
        ArgumentNullException.ThrowIfNull(higherTimeframeCut);
        ArgumentNullException.ThrowIfNull(inputReferences);
        if (IsAfter(higherTimeframeCut, decisionCut))
        {
            throw new ArgumentException(
                "The higher-timeframe cut cannot be after the decision cut.",
                nameof(higherTimeframeCut));
        }

        if (inputReferences.Count == 0 || inputReferences.Any(reference => reference is null))
        {
            throw new ArgumentException(
                "Signal provenance requires non-null public input references.",
                nameof(inputReferences));
        }

        bool hasSetupInput = false;
        bool hasHigherTimeframeInput = false;
        string? setupTimeframe = null;
        string? contextTimeframe = null;
        foreach (SignalInputReference reference in inputReferences)
        {
            CausalEventCursor cut = reference.Role == SignalInputRole.Setup
                ? decisionCut
                : higherTimeframeCut;
            if (!IsEligible(reference, cut))
            {
                throw new ArgumentException(
                    "A signal input is not eligible at its causal cut.",
                    nameof(inputReferences));
            }

            hasSetupInput |= reference.Role == SignalInputRole.Setup;
            hasHigherTimeframeInput |= reference.Role == SignalInputRole.HigherTimeframe;
            if (reference.Role == SignalInputRole.Setup)
            {
                setupTimeframe ??= reference.Timeframe;
                if (setupTimeframe != reference.Timeframe)
                {
                    throw new ArgumentException("Setup references use different timeframes.",
                        nameof(inputReferences));
                }
            }
            else
            {
                contextTimeframe ??= reference.Timeframe;
                if (contextTimeframe != reference.Timeframe)
                {
                    throw new ArgumentException("Context references use different timeframes.",
                        nameof(inputReferences));
                }
            }
        }

        if (!hasSetupInput || !hasHigherTimeframeInput)
        {
            throw new ArgumentException(
                "Signal provenance requires setup and higher-timeframe input references.",
                nameof(inputReferences));
        }
        if (PublicSignalTimeframes.ContextForSetup(setupTimeframe) != contextTimeframe
            || formatVersion == CurrentFormatVersion
                && (setupTimeframe != "15m" || contextTimeframe != "1h"))
        {
            throw new ArgumentException("The setup and context timeframes are inconsistent.",
                nameof(inputReferences));
        }


        FormatVersion = formatVersion;
        ProfileVersion = profileVersion;
        ProfileConfigurationHash = profileConfigurationHash;
        DecisionCut = decisionCut;
        HigherTimeframeCut = higherTimeframeCut;
        InputReferences = inputReferences;
    }

    public static SignalProvenance Create(string profileVersion,
        string profileConfigurationHash, CausalEventCursor decisionCut,
        CausalEventCursor higherTimeframeCut,
        IEnumerable<SignalInputReference> inputReferences)
    {
        ImmutableValueList<SignalInputReference> references = new(inputReferences);
        int version = references.All(reference => reference.Role == SignalInputRole.Setup
                ? reference.Timeframe == "15m" : reference.Timeframe == "1h")
            ? CurrentFormatVersion : MultiTimeframeFormatVersion;
        return new SignalProvenance(version, profileVersion, profileConfigurationHash,
            decisionCut, higherTimeframeCut, references);
    }

    private static bool IsEligible(SignalInputReference reference,
        CausalEventCursor cut) =>
        reference.EventTime.Value.CompareTo(cut.AsOf.Value) <= 0
        && reference.AvailabilityTime.Value.CompareTo(cut.AsOf.Value) <= 0
        && reference.StableSequence <= cut.MaxStableSequence;

    private static bool IsAfter(CausalEventCursor candidate,
        CausalEventCursor boundary) =>
        candidate.AsOf.Value.CompareTo(boundary.AsOf.Value) > 0
        || candidate.MaxStableSequence > boundary.MaxStableSequence;
}

/// <summary>Immutable evidence and provenance linked to one logical signal.</summary>
public sealed record SignalEvidenceRecord
{
    public const int CurrentFormatVersion = 1;
    public const int MultiLaneFormatVersion = 2;
    public const int MarketContextFormatVersion = 3;

    public int FormatVersion { get; }

    public string SignalId { get; }

    public SignalProvenance Provenance { get; }

    public SignalEvidenceState State { get; }

    public ImmutableValueList<string> QualityReasons { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SignalMultiLaneInput? MultiLaneInput { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SignalMarketContext? MarketContext { get; }

    public string RecordHash { get; }

    [JsonConstructor]
    public SignalEvidenceRecord(int formatVersion, string signalId,
        SignalProvenance provenance, SignalEvidenceState state,
        ImmutableValueList<string> qualityReasons, string recordHash,
        SignalMultiLaneInput? multiLaneInput = null, SignalMarketContext? marketContext = null)
    {
        if (formatVersion is not (CurrentFormatVersion or MultiLaneFormatVersion or MarketContextFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The signal evidence format version is unsupported.");
        }

        SignalContractHash.RequireSha256(signalId, nameof(signalId));
        ArgumentNullException.ThrowIfNull(provenance);
        ContractGuard.RequireDefined(state, nameof(state));
        ValidateReasons(qualityReasons, state, nameof(qualityReasons));
        if (formatVersion == CurrentFormatVersion && (multiLaneInput is not null || marketContext is not null)
            || formatVersion == MultiLaneFormatVersion && (multiLaneInput is null || marketContext is not null)
            || formatVersion == MarketContextFormatVersion && marketContext is null)
        {
            throw new ArgumentException("Signal evidence format and inputs disagree.",
                nameof(marketContext));
        }
        if (multiLaneInput is not null
            && (state != SignalEvidenceState.Complete
                || multiLaneInput.DecisionAt.Value < provenance.DecisionCut.AsOf.Value
                || !SignalMultiLaneResearchEvaluator.Evaluate(multiLaneInput).IsAllowed))
        {
            throw new ArgumentException(
                "A multi-lane signal requires complete, causally allowed evidence.",
                nameof(multiLaneInput));
        }
        if (marketContext is not null
            && (marketContext.AsOf.Value < provenance.DecisionCut.AsOf.Value
                || multiLaneInput is not null && marketContext.AsOf != multiLaneInput.DecisionAt))
        {
            throw new ArgumentException("The market context does not match the decision cut.",
                nameof(marketContext));
        }
        SignalContractHash.RequireSha256(recordHash, nameof(recordHash));
        string expected = SignalContractHash.Compute(new SignalEvidenceMaterial(
            formatVersion, signalId, provenance, state, qualityReasons, multiLaneInput, marketContext));
        if (!StringComparer.Ordinal.Equals(recordHash, expected))
        {
            throw new ArgumentException(
                "The evidence record hash does not match its content.", nameof(recordHash));
        }

        FormatVersion = formatVersion;
        SignalId = signalId;
        Provenance = provenance;
        State = state;
        QualityReasons = qualityReasons;
        MultiLaneInput = multiLaneInput;
        MarketContext = marketContext;
        RecordHash = recordHash;
    }

    public static SignalEvidenceRecord Create(SignalIdentity identity,
        SignalProvenance provenance, SignalEvidenceState state,
        IEnumerable<string> qualityReasons, SignalMultiLaneInput? multiLaneInput = null,
        SignalMarketContext? marketContext = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(provenance);
        if (!StringComparer.Ordinal.Equals(identity.ProfileVersion,
                provenance.ProfileVersion))
        {
            throw new ArgumentException(
                "The signal identity and provenance profile versions differ.",
                nameof(provenance));
        }

        ImmutableValueList<string> reasons = new(qualityReasons);
        ValidateReasons(reasons, state, nameof(qualityReasons));
        int formatVersion = marketContext is not null ? MarketContextFormatVersion
            : multiLaneInput is null ? CurrentFormatVersion : MultiLaneFormatVersion;
        SignalEvidenceMaterial material = new(formatVersion,
            identity.SignalId, provenance, state, reasons, multiLaneInput, marketContext);
        SignalEvidenceRecord result = new(formatVersion, identity.SignalId,
            provenance, state, reasons, SignalContractHash.Compute(material), multiLaneInput, marketContext);
        if (multiLaneInput is not null || marketContext is not null)
        {
            result.ValidateFor(identity);
        }
        return result;
    }

    public void ValidateFor(SignalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!StringComparer.Ordinal.Equals(SignalId, identity.SignalId)
            || !StringComparer.Ordinal.Equals(identity.ProfileVersion,
                Provenance.ProfileVersion)
            || Provenance.InputReferences.Any(reference =>
                reference.Role == SignalInputRole.Setup
                    ? reference.Timeframe != identity.SetupTimeframe
                    : reference.Timeframe != PublicSignalTimeframes.ContextForSetup(identity.SetupTimeframe))
            || !Provenance.InputReferences.Any(reference =>
                reference.Role == SignalInputRole.Setup
                && StringComparer.Ordinal.Equals(reference.EventId,
                    identity.SetupEventId))
            || MultiLaneInput is not null
                && (MultiLaneInput.InstrumentId != identity.InstrumentId
                    || MultiLaneInput.Direction != identity.Direction)
            || MarketContext is not null && MarketContext.InstrumentId != identity.InstrumentId)
        {
            throw new ArgumentException(
                "The evidence does not match the signal identity and provenance.",
                nameof(identity));
        }
    }

    private static void ValidateReasons(ImmutableValueList<string> reasons,
        SignalEvidenceState state, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(reasons, parameterName);
        ContractGuard.RequireDefined(state, nameof(state));
        if (reasons.Any(string.IsNullOrWhiteSpace)
            || state == SignalEvidenceState.Unknown && reasons.Count == 0)
        {
            throw new ArgumentException(
                "Evidence reasons must be nonblank, and Unknown evidence needs at least one reason.",
                parameterName);
        }
    }

    private sealed record SignalEvidenceMaterial(int FormatVersion, string SignalId,
        SignalProvenance Provenance, SignalEvidenceState State,
        ImmutableValueList<string> QualityReasons,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SignalMultiLaneInput? MultiLaneInput,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SignalMarketContext? MarketContext);
}

/// <summary>Inclusive open/close timestamps for the closed setup bar.</summary>
public sealed record SignalBarInterval
{
    public UtcTimestamp OpenTime { get; }

    public UtcTimestamp CloseTime { get; }

    [JsonConstructor]
    public SignalBarInterval(UtcTimestamp openTime, UtcTimestamp closeTime)
    {
        ArgumentNullException.ThrowIfNull(openTime);
        ArgumentNullException.ThrowIfNull(closeTime);
        if (closeTime.Value.CompareTo(openTime.Value) <= 0)
        {
            throw new ArgumentException("A signal bar must close after it opens.",
                nameof(closeTime));
        }

        OpenTime = openTime;
        CloseTime = closeTime;
    }
}

/// <summary>Public reference price linked to the source event and its observation time.</summary>
public sealed record SignalReferencePrice
{
    public string SourceId { get; }

    public string SourceVersion { get; }

    public string EventId { get; }

    public UtcTimestamp ObservedAt { get; }

    public Price Price { get; }

    [JsonConstructor]
    public SignalReferencePrice(string sourceId, string sourceVersion,
        string eventId, UtcTimestamp observedAt, Price price)
    {
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(sourceVersion, nameof(sourceVersion));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(observedAt);
        ArgumentNullException.ThrowIfNull(price);
        SourceId = sourceId;
        SourceVersion = sourceVersion;
        EventId = eventId;
        ObservedAt = observedAt;
        Price = price;
    }
}

/// <summary>Versioned passive signal card; it grants no validation or execution authority.</summary>
public sealed record SignalRecord
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public SignalIdentity Identity { get; }

    public string EvidenceHash { get; }

    public SignalAssessmentState AssessmentState { get; }

    public SignalBarInterval BarInterval { get; }

    public UtcTimestamp DecisionAt { get; }

    public SignalReferencePrice ReferencePrice { get; }

    [JsonRequired]
    public decimal? Probability { get; }

    [JsonRequired]
    public string? CalibrationVersion { get; }

    public string RecordHash { get; }

    [JsonConstructor]
    public SignalRecord(int formatVersion, SignalIdentity identity,
        string evidenceHash, SignalAssessmentState assessmentState,
        SignalBarInterval barInterval, UtcTimestamp decisionAt,
        SignalReferencePrice referencePrice, decimal? probability,
        string? calibrationVersion, string recordHash)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The signal record format version is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(identity);
        SignalContractHash.RequireSha256(evidenceHash, nameof(evidenceHash));
        ContractGuard.RequireDefined(assessmentState, nameof(assessmentState));
        ArgumentNullException.ThrowIfNull(barInterval);
        ArgumentNullException.ThrowIfNull(decisionAt);
        ArgumentNullException.ThrowIfNull(referencePrice);
        ValidateCalibration(probability, calibrationVersion);
        SignalContractHash.RequireSha256(recordHash, nameof(recordHash));
        string expected = SignalContractHash.Compute(new SignalRecordMaterial(
            formatVersion, identity, evidenceHash, assessmentState, barInterval,
            decisionAt, referencePrice, probability, calibrationVersion));
        if (!StringComparer.Ordinal.Equals(recordHash, expected))
        {
            throw new ArgumentException("The signal record hash does not match its content.",
                nameof(recordHash));
        }

        FormatVersion = formatVersion;
        Identity = identity;
        EvidenceHash = evidenceHash;
        AssessmentState = assessmentState;
        BarInterval = barInterval;
        DecisionAt = decisionAt;
        ReferencePrice = referencePrice;
        Probability = probability;
        CalibrationVersion = calibrationVersion;
        RecordHash = recordHash;
    }

    public static SignalRecord Create(SignalIdentity identity,
        SignalEvidenceRecord evidence, SignalAssessmentState assessmentState,
        SignalBarInterval barInterval, UtcTimestamp decisionAt,
        SignalReferencePrice referencePrice, decimal? probability = null,
        string? calibrationVersion = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(barInterval);
        ArgumentNullException.ThrowIfNull(decisionAt);
        ArgumentNullException.ThrowIfNull(referencePrice);
        ContractGuard.RequireDefined(assessmentState, nameof(assessmentState));
        evidence.ValidateFor(identity);
        ValidateAssessmentEvidence(assessmentState, evidence);
        ValidateCalibration(probability, calibrationVersion);
        ValidateCausalFields(evidence, barInterval, decisionAt, referencePrice);
        SignalRecordMaterial material = new(CurrentFormatVersion, identity,
            evidence.RecordHash, assessmentState, barInterval, decisionAt,
            referencePrice, probability, calibrationVersion);
        return new SignalRecord(CurrentFormatVersion, identity, evidence.RecordHash,
            assessmentState, barInterval, decisionAt, referencePrice, probability,
            calibrationVersion, SignalContractHash.Compute(material));
    }

    public void ValidateEvidence(SignalEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        evidence.ValidateFor(Identity);
        ValidateAssessmentEvidence(AssessmentState, evidence);
        if (!StringComparer.Ordinal.Equals(EvidenceHash, evidence.RecordHash))
        {
            throw new ArgumentException(
                "The signal's evidence hash does not match the linked evidence record.",
                nameof(evidence));
        }

        ValidateCausalFields(evidence, BarInterval, DecisionAt, ReferencePrice);
    }

    private static void ValidateAssessmentEvidence(
        SignalAssessmentState assessmentState, SignalEvidenceRecord evidence)
    {
        if (assessmentState == SignalAssessmentState.ConfirmedOnClosedBar
            && evidence.State != SignalEvidenceState.Complete)
        {
            throw new ArgumentException(
                "A confirmed-on-closed-bar signal requires complete evidence.",
                nameof(evidence));
        }

    }

    private static void ValidateCalibration(decimal? probability,
        string? calibrationVersion)
    {
        if (probability.HasValue != (calibrationVersion is not null))
        {
            throw new ArgumentException(
                "Probability and calibration version must either both be present or both be unknown.",
                nameof(calibrationVersion));
        }

        if (probability is < 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(probability), probability,
                "Probability must be within the closed unit interval.");
        }

        if (calibrationVersion is not null)
        {
            ContractGuard.RequireText(calibrationVersion, nameof(calibrationVersion));
        }
    }

    private static void ValidateCausalFields(SignalEvidenceRecord evidence,
        SignalBarInterval barInterval, UtcTimestamp decisionAt,
        SignalReferencePrice referencePrice)
    {
        SignalProvenance provenance = evidence.Provenance;
        if (decisionAt.Value.CompareTo(provenance.DecisionCut.AsOf.Value) < 0
            || barInterval.CloseTime.Value.CompareTo(provenance.DecisionCut.AsOf.Value) > 0
            || barInterval.CloseTime.Value.CompareTo(decisionAt.Value) > 0
            || evidence.MultiLaneInput is not null
                && evidence.MultiLaneInput.DecisionAt.Value > decisionAt.Value
            || evidence.MarketContext is not null
                && evidence.MarketContext.AsOf.Value > decisionAt.Value)
        {
            throw new ArgumentException(
                "The signal bar and decision time are incompatible with the decision cut.",
                nameof(decisionAt));
        }

        bool hasReferenceInput = provenance.InputReferences.Any(reference =>
            StringComparer.Ordinal.Equals(reference.SourceId,
                referencePrice.SourceId)
            && StringComparer.Ordinal.Equals(reference.SourceVersion,
                referencePrice.SourceVersion)
            && StringComparer.Ordinal.Equals(reference.EventId,
                referencePrice.EventId)
            && reference.EventTime == referencePrice.ObservedAt);
        if (!hasReferenceInput)
        {
            throw new ArgumentException(
                "The reference price must link to a retained causal input.",
                nameof(referencePrice));
        }
    }

    private sealed record SignalRecordMaterial(int FormatVersion,
        SignalIdentity Identity, string EvidenceHash,
        SignalAssessmentState AssessmentState, SignalBarInterval BarInterval,
        UtcTimestamp DecisionAt, SignalReferencePrice ReferencePrice,
        decimal? Probability, string? CalibrationVersion);
}

/// <summary>Immutable publication attempt/receipt linked to one exact signal record.</summary>
public sealed record SignalDeliveryReceipt
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string ReceiptId { get; }

    public string SignalId { get; }

    public string SignalRecordHash { get; }

    public string DeliveryAttemptId { get; }

    public SignalDeliveryChannel Channel { get; }

    public SignalDeliveryStatus Status { get; }

    public UtcTimestamp OccurredAt { get; }

    public string? ExternalReceiptId { get; }

    public string? Reason { get; }

    [JsonConstructor]
    public SignalDeliveryReceipt(int formatVersion, string receiptId,
        string signalId, string signalRecordHash, string deliveryAttemptId,
        SignalDeliveryChannel channel, SignalDeliveryStatus status,
        UtcTimestamp occurredAt, string? externalReceiptId, string? reason)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The delivery receipt format version is unsupported.");
        }

        SignalContractHash.RequireSha256(receiptId, nameof(receiptId));
        SignalContractHash.RequireSha256(signalId, nameof(signalId));
        SignalContractHash.RequireSha256(signalRecordHash, nameof(signalRecordHash));
        ContractGuard.RequireText(deliveryAttemptId, nameof(deliveryAttemptId));
        ContractGuard.RequireDefined(channel, nameof(channel));
        ContractGuard.RequireDefined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(occurredAt);
        ValidateDeliveryReason(status, reason);
        ValidateOptionalText(externalReceiptId, nameof(externalReceiptId));
        SignalDeliveryMaterial material = new(formatVersion, signalId,
            signalRecordHash, deliveryAttemptId, channel, status, occurredAt,
            externalReceiptId, reason);
        if (!StringComparer.Ordinal.Equals(receiptId,
                SignalContractHash.Compute(material)))
        {
            throw new ArgumentException(
                "The delivery receipt ID does not match its content.",
                nameof(receiptId));
        }

        FormatVersion = formatVersion;
        ReceiptId = receiptId;
        SignalId = signalId;
        SignalRecordHash = signalRecordHash;
        DeliveryAttemptId = deliveryAttemptId;
        Channel = channel;
        Status = status;
        OccurredAt = occurredAt;
        ExternalReceiptId = externalReceiptId;
        Reason = reason;
    }

    public static SignalDeliveryReceipt Create(SignalRecord signal,
        string deliveryAttemptId, SignalDeliveryChannel channel,
        SignalDeliveryStatus status, UtcTimestamp occurredAt,
        string? externalReceiptId = null, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(occurredAt);
        if (occurredAt.Value.CompareTo(signal.DecisionAt.Value) < 0)
        {
            throw new ArgumentException(
                "A delivery receipt cannot predate its signal decision.",
                nameof(occurredAt));
        }
        ContractGuard.RequireText(deliveryAttemptId, nameof(deliveryAttemptId));
        ContractGuard.RequireDefined(channel, nameof(channel));
        ContractGuard.RequireDefined(status, nameof(status));
        ValidateDeliveryReason(status, reason);
        SignalDeliveryMaterial material = new(CurrentFormatVersion,
            signal.Identity.SignalId, signal.RecordHash, deliveryAttemptId, channel,
            status, occurredAt, externalReceiptId, reason);
        return new SignalDeliveryReceipt(CurrentFormatVersion,
            SignalContractHash.Compute(material), signal.Identity.SignalId,
            signal.RecordHash, deliveryAttemptId, channel, status, occurredAt,
            externalReceiptId, reason);
    }
    public void ValidateFor(SignalRecord signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!StringComparer.Ordinal.Equals(SignalId, signal.Identity.SignalId)
            || !StringComparer.Ordinal.Equals(SignalRecordHash, signal.RecordHash)
            || OccurredAt.Value.CompareTo(signal.DecisionAt.Value) < 0)
        {
            throw new ArgumentException(
                "The delivery receipt does not match this signal record or its time.",
                nameof(signal));
        }
    }

    private static void ValidateDeliveryReason(SignalDeliveryStatus status,
        string? reason)
    {
        ValidateOptionalText(reason, nameof(reason));
        if (status is SignalDeliveryStatus.Rejected or SignalDeliveryStatus.Unknown
            && reason is null)
        {
            throw new ArgumentException(
                "Rejected or unknown delivery requires an explicit reason.",
                nameof(reason));
        }
    }

    private static void ValidateOptionalText(string? value, string parameterName)
    {
        if (value is not null)
        {
            ContractGuard.RequireText(value, parameterName);
        }
    }

    private sealed record SignalDeliveryMaterial(int FormatVersion, string SignalId,
        string SignalRecordHash, string DeliveryAttemptId,
        SignalDeliveryChannel Channel, SignalDeliveryStatus Status,
        UtcTimestamp OccurredAt, string? ExternalReceiptId, string? Reason);
}

/// <summary>Independent observed outcome linked to the signal and its evidence.</summary>
public sealed record SignalOutcomeRecord
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string OutcomeId { get; }

    public string SignalId { get; }

    public string SignalRecordHash { get; }

    public string EvidenceRecordHash { get; }

    public string MethodVersion { get; }

    public SignalOutcomeKind Kind { get; }

    public UtcTimestamp ObservedAt { get; }

    public Price? ObservedPrice { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public SignalOutcomeRecord(int formatVersion, string outcomeId,
        string signalId, string signalRecordHash, string evidenceRecordHash,
        string methodVersion, SignalOutcomeKind kind, UtcTimestamp observedAt,
        Price? observedPrice, ImmutableValueList<string> reasons)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The outcome record format version is unsupported.");
        }

        SignalContractHash.RequireSha256(outcomeId, nameof(outcomeId));
        SignalContractHash.RequireSha256(signalId, nameof(signalId));
        SignalContractHash.RequireSha256(signalRecordHash, nameof(signalRecordHash));
        SignalContractHash.RequireSha256(evidenceRecordHash,
            nameof(evidenceRecordHash));
        ContractGuard.RequireText(methodVersion, nameof(methodVersion));
        ContractGuard.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(observedAt);
        ValidateOutcomeReasons(kind, reasons);
        SignalOutcomeMaterial material = new(formatVersion, signalId,
            signalRecordHash, evidenceRecordHash, methodVersion, kind, observedAt,
            observedPrice, reasons);
        if (!StringComparer.Ordinal.Equals(outcomeId,
                SignalContractHash.Compute(material)))
        {
            throw new ArgumentException("The outcome ID does not match its content.",
                nameof(outcomeId));
        }

        FormatVersion = formatVersion;
        OutcomeId = outcomeId;
        SignalId = signalId;
        SignalRecordHash = signalRecordHash;
        EvidenceRecordHash = evidenceRecordHash;
        MethodVersion = methodVersion;
        Kind = kind;
        ObservedAt = observedAt;
        ObservedPrice = observedPrice;
        Reasons = reasons;
    }

    public static SignalOutcomeRecord Create(SignalRecord signal,
        SignalEvidenceRecord evidence, string methodVersion,
        SignalOutcomeKind kind, UtcTimestamp observedAt, Price? observedPrice,
        IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(evidence);
        ContractGuard.RequireText(methodVersion, nameof(methodVersion));
        ContractGuard.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(observedAt);
        signal.ValidateEvidence(evidence);
        if (observedAt.Value.CompareTo(signal.DecisionAt.Value) < 0)
        {
            throw new ArgumentException(
                "An outcome cannot be observed before its signal decision.",
                nameof(observedAt));
        }

        ImmutableValueList<string> reasonList = new(reasons);
        ValidateOutcomeReasons(kind, reasonList);
        SignalOutcomeMaterial material = new(CurrentFormatVersion,
            signal.Identity.SignalId, signal.RecordHash, evidence.RecordHash,
            methodVersion, kind, observedAt, observedPrice, reasonList);
        return new SignalOutcomeRecord(CurrentFormatVersion,
            SignalContractHash.Compute(material), signal.Identity.SignalId,
            signal.RecordHash, evidence.RecordHash, methodVersion, kind,
            observedAt, observedPrice, reasonList);
    }
    public void ValidateFor(SignalRecord signal, SignalEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(evidence);
        signal.ValidateEvidence(evidence);
        if (!StringComparer.Ordinal.Equals(SignalId, signal.Identity.SignalId)
            || !StringComparer.Ordinal.Equals(SignalRecordHash, signal.RecordHash)
            || !StringComparer.Ordinal.Equals(EvidenceRecordHash, evidence.RecordHash)
            || ObservedAt.Value.CompareTo(signal.DecisionAt.Value) < 0)
        {
            throw new ArgumentException(
                "The outcome does not match this signal record and evidence.",
                nameof(signal));
        }
    }

    private static void ValidateOutcomeReasons(SignalOutcomeKind kind,
        ImmutableValueList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Outcome reasons cannot be blank.", nameof(reasons));
        }

        if (kind == SignalOutcomeKind.Unknown && reasons.Count == 0)
        {
            throw new ArgumentException(
                "An unknown outcome requires an explicit reason.", nameof(reasons));
        }
    }

    private sealed record SignalOutcomeMaterial(int FormatVersion, string SignalId,
        string SignalRecordHash, string EvidenceRecordHash, string MethodVersion,
        SignalOutcomeKind Kind, UtcTimestamp ObservedAt, Price? ObservedPrice,
        ImmutableValueList<string> Reasons);
}

internal static class SignalContractHash
{
    public static string Compute<T>(T content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            CoreJsonSerializer.Serialize(content)))).ToLowerInvariant();

    public static void RequireSha256(string value, string parameterName)
    {
        if (value is null || value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A SHA-256 identity must contain 64 lowercase hexadecimal characters.",
                parameterName);
        }
    }
}
