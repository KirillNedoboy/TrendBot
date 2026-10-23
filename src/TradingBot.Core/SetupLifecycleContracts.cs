using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>External facts that may advance one setup lifecycle.</summary>
public enum SetupLifecycleFactKind
{
    ContextReady,
    StructureFormed,
    SetupEvidence,
    Confirmation,
    ExternalPlanReady,
    OrderObserved,
    PositionObserved,
    ExitObserved,
    CooldownElapsed,
    StructureReset,
    Recovery
}

/// <summary>Result of applying one immutable lifecycle input.</summary>
public enum SetupLifecycleTransitionStatus
{
    Applied,
    Duplicate,
    Late,
    Rejected,
    IdentityConflict,
    RecoveryApplied,
    CommitRejected
}

/// <summary>The type of confirmation supplied by a later decision lane.</summary>
public enum SetupConfirmationLane
{
    LaneB,
    LaneC
}

/// <summary>Explicit confirmation result. Unknown remains fail-closed.</summary>
public enum SetupConfirmationStatus
{
    Confirm,
    Neutral,
    Veto,
    Unknown
}

/// <summary>Stable identity of one structural setup instance.</summary>
public sealed record SetupLifecycleIdentity
{
    public InstrumentId InstrumentId { get; }

    public SetupType SetupType { get; }

    public Direction Direction { get; }

    public string StructuralAnchorId { get; }

    public UtcTimestamp AnchorTime { get; }

    public string ContextVersion { get; }

    /// <summary>
    /// Lower-case SHA-256 of the six identity fields required by the lifecycle
    /// architecture: instrument, family, direction, structural anchor, anchor
    /// time and context version.
    /// </summary>
    public string Fingerprint { get; }

    [JsonIgnore]
    public string SetupFingerprint => Fingerprint;

    public SetupLifecycleIdentity(InstrumentId instrumentId, SetupType setupType,
        Direction direction, string structuralAnchorId, UtcTimestamp anchorTime,
        string contextVersion)
        : this(instrumentId, setupType, direction, structuralAnchorId, anchorTime,
            contextVersion, ComputeFingerprint(instrumentId, setupType, direction,
                structuralAnchorId, anchorTime, contextVersion))
    {
    }

    [JsonConstructor]
    public SetupLifecycleIdentity(InstrumentId instrumentId, SetupType setupType,
        Direction direction, string structuralAnchorId, UtcTimestamp anchorTime,
        string contextVersion, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireDefined(setupType, nameof(setupType));
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireText(structuralAnchorId, nameof(structuralAnchorId));
        ArgumentNullException.ThrowIfNull(anchorTime);
        ContractGuard.RequireText(contextVersion, nameof(contextVersion));
        ContractGuard.RequireText(fingerprint, nameof(fingerprint));
        string expected = ComputeFingerprint(instrumentId, setupType, direction,
            structuralAnchorId, anchorTime, contextVersion);
        if (!StringComparer.Ordinal.Equals(expected, fingerprint))
        {
            throw new ArgumentException(
                "The setup lifecycle fingerprint does not match its identity fields.",
                nameof(fingerprint));
        }

        InstrumentId = instrumentId;
        SetupType = setupType;
        Direction = direction;
        StructuralAnchorId = structuralAnchorId;
        AnchorTime = anchorTime;
        ContextVersion = contextVersion;
        Fingerprint = fingerprint;
    }

    public static SetupLifecycleIdentity Create(InstrumentId instrumentId,
        SetupType setupType, Direction direction, string structuralAnchorId,
        UtcTimestamp anchorTime, string contextVersion) => new(instrumentId,
            setupType, direction, structuralAnchorId, anchorTime, contextVersion);

    public static SetupLifecycleIdentity FromSetup(Setup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        return Create(setup.InstrumentId, setup.SetupType, setup.Direction,
            setup.StructuralAnchorId, setup.AnchorTime, setup.ContextVersion);
    }

    private static string ComputeFingerprint(InstrumentId instrumentId,
        SetupType setupType, Direction direction, string structuralAnchorId,
        UtcTimestamp anchorTime, string contextVersion)
    {
        string canonical = string.Join("|", instrumentId.Value,
            setupType.ToString(), direction.ToString(), structuralAnchorId,
            anchorTime.Value.ToUniversalTime().ToString("O",
                System.Globalization.CultureInfo.InvariantCulture), contextVersion);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

/// <summary>Versioned lifecycle policy. No time or trading defaults are implied.</summary>
public sealed record SetupLifecycleConfiguration
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string RulesVersion { get; }

    public string ConfigurationVersion { get; }

    public bool RequireExternalPlan { get; }

    public bool RequireConfirmationWindow { get; }

    public TimeSpan? ConfirmationWindow { get; }

    public TimeSpan? Cooldown { get; }

    public SetupLifecycleConfiguration(string rulesVersion, string configurationVersion,
        bool requireExternalPlan, bool requireConfirmationWindow,
        TimeSpan? confirmationWindow = null, TimeSpan? cooldown = null)
        : this(CurrentFormatVersion, rulesVersion, configurationVersion,
            requireExternalPlan, requireConfirmationWindow, confirmationWindow, cooldown)
    {
    }

    [JsonConstructor]
    public SetupLifecycleConfiguration(int formatVersion, string rulesVersion,
        string configurationVersion, bool requireExternalPlan,
        bool requireConfirmationWindow, TimeSpan? confirmationWindow = null,
        TimeSpan? cooldown = null)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The setup lifecycle configuration format is unsupported.");
        }

        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireText(configurationVersion, nameof(configurationVersion));
        if (confirmationWindow is { } window && window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(confirmationWindow), window,
                "A confirmation window cannot be negative.");
        }

        if (cooldown is { } cooldownValue && cooldownValue < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown), cooldownValue,
                "A cooldown cannot be negative.");
        }

        FormatVersion = formatVersion;
        RulesVersion = rulesVersion;
        ConfigurationVersion = configurationVersion;
        RequireExternalPlan = requireExternalPlan;
        RequireConfirmationWindow = requireConfirmationWindow;
        ConfirmationWindow = confirmationWindow;
        Cooldown = cooldown;
    }
}

/// <summary>One immutable observation from Lane B or Lane C.</summary>
public sealed record SetupLifecycleConfirmation
{
    public SetupConfirmationLane Lane { get; }

    public SetupConfirmationStatus Status { get; }

    public string EvidenceId { get; }

    public string AlgorithmVersion { get; }

    public CausalEventCursor Cursor { get; }

    public string Provenance { get; }

    [JsonConstructor]
    public SetupLifecycleConfirmation(SetupConfirmationLane lane,
        SetupConfirmationStatus status, string evidenceId, string algorithmVersion,
        CausalEventCursor cursor, string provenance)
    {
        ContractGuard.RequireDefined(lane, nameof(lane));
        ContractGuard.RequireDefined(status, nameof(status));
        ContractGuard.RequireText(evidenceId, nameof(evidenceId));
        ContractGuard.RequireText(algorithmVersion, nameof(algorithmVersion));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        Lane = lane;
        Status = status;
        EvidenceId = evidenceId;
        AlgorithmVersion = algorithmVersion;
        Cursor = cursor;
        Provenance = provenance;
    }
}

/// <summary>One causal input to the lifecycle, including external observations.</summary>
public sealed record SetupLifecycleInput
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string StreamId { get; }

    public string EventId { get; }

    public SetupLifecycleFactKind FactKind { get; }

    public SetupLifecycleIdentity Identity { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public string RulesVersion { get; }

    public string ConfigurationVersion { get; }

    public string Provenance { get; init; }

    public SetupEvidenceSnapshot? Evidence { get; }

    public SetupLifecycleConfirmation? Confirmation { get; }

    public string? ExternalPlanId { get; }

    public string? ExternalObservationId { get; }

    public string? ShadowOutcome { get; }

    public string? Outcome { get; }

    public SetupLifecycleSnapshot? RecoverySnapshot { get; }

    [JsonIgnore]
    public string PayloadFingerprint => ComputePayloadFingerprint();

    [JsonConstructor]
    public SetupLifecycleInput(int formatVersion, string streamId, string eventId,
        SetupLifecycleFactKind factKind, SetupLifecycleIdentity identity,
        UtcTimestamp eventTime, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, long stableSequence, string rulesVersion,
        string configurationVersion, string provenance,
        SetupEvidenceSnapshot? evidence = null,
        SetupLifecycleConfirmation? confirmation = null, string? externalPlanId = null,
        string? externalObservationId = null, string? shadowOutcome = null,
        string? outcome = null, SetupLifecycleSnapshot? recoverySnapshot = null)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The setup lifecycle input format is unsupported.");
        }

        ContractGuard.RequireText(streamId, nameof(streamId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ContractGuard.RequireDefined(factKind, nameof(factKind));
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException(
                "Lifecycle availability cannot precede receive time.",
                nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Lifecycle stable sequence cannot be negative.");
        }

        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireText(configurationVersion, nameof(configurationVersion));
        ContractGuard.RequireText(provenance, nameof(provenance));
        ValidateFactPayload(factKind, identity, eventTime, stableSequence, evidence,
            confirmation, externalPlanId, externalObservationId, recoverySnapshot);

        FormatVersion = formatVersion;
        StreamId = streamId;
        EventId = eventId;
        FactKind = factKind;
        Identity = identity;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        RulesVersion = rulesVersion;
        ConfigurationVersion = configurationVersion;
        Provenance = provenance;
        Evidence = evidence;
        Confirmation = confirmation;
        ExternalPlanId = externalPlanId;
        ExternalObservationId = externalObservationId;
        ShadowOutcome = shadowOutcome;
        Outcome = outcome;
        RecoverySnapshot = recoverySnapshot;
    }

    private static void ValidateFactPayload(SetupLifecycleFactKind factKind,
        SetupLifecycleIdentity identity, UtcTimestamp eventTime, long stableSequence,
        SetupEvidenceSnapshot? evidence,
        SetupLifecycleConfirmation? confirmation, string? externalPlanId,
        string? externalObservationId, SetupLifecycleSnapshot? recoverySnapshot)
    {
        if (factKind == SetupLifecycleFactKind.SetupEvidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            if (evidence.SetupType != identity.SetupType || evidence.Direction != identity.Direction
                || evidence.InputSequence.InstrumentId != identity.InstrumentId)
            {
                throw new ArgumentException(
                    "Setup evidence must match the lifecycle identity.", nameof(evidence));
            }

            ValidateCausalVisibility(evidence.Cursor, eventTime, stableSequence);
        }

        if (factKind == SetupLifecycleFactKind.Confirmation)
        {
            ArgumentNullException.ThrowIfNull(confirmation);
            ValidateCausalVisibility(confirmation.Cursor, eventTime, stableSequence);
        }

        if (factKind == SetupLifecycleFactKind.ExternalPlanReady)
        {
            ContractGuard.RequireText(externalPlanId!, nameof(externalPlanId));
        }

        if (factKind is SetupLifecycleFactKind.OrderObserved
            or SetupLifecycleFactKind.PositionObserved
            or SetupLifecycleFactKind.ExitObserved)
        {
            ContractGuard.RequireText(externalObservationId!, nameof(externalObservationId));
        }

        if (factKind == SetupLifecycleFactKind.Recovery)
        {
            ArgumentNullException.ThrowIfNull(recoverySnapshot);
            if (recoverySnapshot.Identity.InstrumentId != identity.InstrumentId)
            {
                throw new ArgumentException(
                    "A recovery snapshot must belong to the lifecycle instrument.",
                    nameof(recoverySnapshot));
            }
        }
    }

    private static void ValidateCausalVisibility(CausalEventCursor cursor,
        UtcTimestamp? eventTime, long? stableSequence)
    {
        if (eventTime is not null)
        {
            int comparison = cursor.AsOf.Value.CompareTo(eventTime.Value);
            if (comparison > 0 || (comparison == 0 && stableSequence is not null
                && cursor.MaxStableSequence > stableSequence.Value))
            {
                throw new ArgumentException(
                    "A retained evidence cursor cannot be in the future of its input.",
                    nameof(cursor));
            }
        }
    }

    private string ComputePayloadFingerprint()
    {
        string canonical = string.Join("|", StreamId, EventId, FactKind,
            Identity.Fingerprint, EventTime.Value.ToUniversalTime().ToString("O",
                System.Globalization.CultureInfo.InvariantCulture),
            ReceiveTime.Value.ToUniversalTime().ToString("O",
                System.Globalization.CultureInfo.InvariantCulture),
            AvailabilityTime.Value.ToUniversalTime().ToString("O",
                System.Globalization.CultureInfo.InvariantCulture), StableSequence,
            RulesVersion, ConfigurationVersion, Provenance,
            Evidence is null ? string.Empty : CoreJsonSerializer.Serialize(Evidence),
            Confirmation is null ? string.Empty : CoreJsonSerializer.Serialize(Confirmation),
            ExternalPlanId ?? string.Empty, ExternalObservationId ?? string.Empty,
            ShadowOutcome ?? string.Empty, Outcome ?? string.Empty,
            RecoverySnapshot is null ? string.Empty : CoreJsonSerializer.Serialize(RecoverySnapshot));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

/// <summary>Compact receipt retained for duplicate and causal ordering checks.</summary>
public sealed record SetupLifecycleEventReceipt
{
    public string StreamId { get; }

    public string EventId { get; }

    public string PayloadFingerprint { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    [JsonConstructor]
    public SetupLifecycleEventReceipt(string streamId, string eventId,
        string payloadFingerprint, UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, long stableSequence)
    {
        ContractGuard.RequireText(streamId, nameof(streamId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ContractGuard.RequireText(payloadFingerprint, nameof(payloadFingerprint));
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);

        StreamId = streamId;
        EventId = eventId;
        PayloadFingerprint = payloadFingerprint;
        EventTime = eventTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
    }

    public static SetupLifecycleEventReceipt From(SetupLifecycleInput input) => new(
        input.StreamId, input.EventId, input.PayloadFingerprint, input.EventTime,
        input.AvailabilityTime, input.StableSequence);
}

/// <summary>Opaque observability retained on a lifecycle snapshot.</summary>
public sealed record SetupLifecycleObservation
{
    public string? ShadowOutcome { get; }

    public string? Outcome { get; }

    public ImmutableValueList<string> Diagnostics { get; }

    [JsonConstructor]
    public SetupLifecycleObservation(string? shadowOutcome, string? outcome,
        ImmutableValueList<string> diagnostics)
    {
        if (shadowOutcome is not null)
        {
            ContractGuard.RequireText(shadowOutcome, nameof(shadowOutcome));
        }

        if (outcome is not null)
        {
            ContractGuard.RequireText(outcome, nameof(outcome));
        }

        ArgumentNullException.ThrowIfNull(diagnostics);
        ShadowOutcome = shadowOutcome;
        Outcome = outcome;
        Diagnostics = new ImmutableValueList<string>(diagnostics);
    }

    public static SetupLifecycleObservation Empty { get; } = new(null, null,
        new ImmutableValueList<string>([]));
}

/// <summary>Immutable lifecycle state at one causal point.</summary>
public sealed record SetupLifecycleSnapshot
{
    public SetupLifecycleIdentity Identity { get; init; }

    public SetupState State { get; init; }

    public CausalEventCursor ProcessedCursor { get; init; }

    /// <summary>Latest retained local availability time, kept distinct from event time.</summary>
    public UtcTimestamp ProcessedAvailabilityTime { get; init; }

    public ImmutableValueList<SetupLifecycleEventReceipt> ProcessedEvents { get; init; }

    public SetupEvidenceSnapshot? LatestEvidence { get; init; }

    public SetupLifecycleObservation Observation { get; init; }

    public bool RecoveryRequired { get; init; }

    public string RulesVersion { get; init; }

    public string ConfigurationVersion { get; init; }

    [JsonConstructor]
    public SetupLifecycleSnapshot(SetupLifecycleIdentity identity, SetupState state,
        CausalEventCursor processedCursor,
        ImmutableValueList<SetupLifecycleEventReceipt> processedEvents,
        SetupEvidenceSnapshot? latestEvidence, SetupLifecycleObservation observation,
        bool recoveryRequired, string rulesVersion, string configurationVersion,
        UtcTimestamp? processedAvailabilityTime = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(processedCursor);
        processedAvailabilityTime ??= processedCursor.AsOf;
        ArgumentNullException.ThrowIfNull(processedAvailabilityTime);
        ArgumentNullException.ThrowIfNull(processedEvents);
        ArgumentNullException.ThrowIfNull(observation);
        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireText(configurationVersion, nameof(configurationVersion));
        if (latestEvidence is not null
            && (latestEvidence.InputSequence.InstrumentId != identity.InstrumentId
                || latestEvidence.SetupType != identity.SetupType
                || latestEvidence.Direction != identity.Direction))
        {
            throw new ArgumentException(
                "Latest evidence must match the lifecycle identity.", nameof(latestEvidence));
        }

        Identity = identity;
        State = state;
        ProcessedCursor = processedCursor;
        ProcessedAvailabilityTime = processedAvailabilityTime;
        ProcessedEvents = new ImmutableValueList<SetupLifecycleEventReceipt>(processedEvents);
        LatestEvidence = latestEvidence;
        Observation = observation;
        RecoveryRequired = recoveryRequired;
        RulesVersion = rulesVersion;
        ConfigurationVersion = configurationVersion;
    }

    public static SetupLifecycleSnapshot CreateInitial(SetupLifecycleIdentity identity,
        SetupLifecycleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(configuration);
        return new SetupLifecycleSnapshot(identity, SetupState.Observing,
            new CausalEventCursor(new UtcTimestamp(DateTimeOffset.MinValue), 0),
            new ImmutableValueList<SetupLifecycleEventReceipt>([]),
            null, SetupLifecycleObservation.Empty, false,
            configuration.RulesVersion, configuration.ConfigurationVersion,
            new UtcTimestamp(DateTimeOffset.MinValue));
    }
}

/// <summary>Auditable result of one lifecycle transition.</summary>
public sealed record SetupLifecycleTransition
{
    public SetupLifecycleTransitionStatus Status { get; init; }

    public SetupState PreviousState { get; init; }

    public SetupState CurrentState { get; init; }

    public SetupLifecycleIdentity PreviousIdentity { get; init; }

    public SetupLifecycleIdentity CurrentIdentity { get; init; }

    public SetupLifecycleInput Input { get; init; }

    public SetupLifecycleSnapshot NextSnapshot { get; init; }

    public ImmutableValueList<string> Reasons { get; init; }

    public string RulesVersion { get; init; }

    public string ConfigurationVersion { get; init; }

    public string DecisionInputSnapshotJson { get; init; }

    public SetupLifecycleObservation Observation { get; init; }

    [JsonConstructor]
    public SetupLifecycleTransition(SetupLifecycleTransitionStatus status,
        SetupState previousState, SetupState currentState,
        SetupLifecycleIdentity previousIdentity, SetupLifecycleIdentity currentIdentity,
        SetupLifecycleInput input, SetupLifecycleSnapshot nextSnapshot,
        ImmutableValueList<string> reasons, string rulesVersion,
        string configurationVersion, string decisionInputSnapshotJson,
        SetupLifecycleObservation observation)
    {
        ContractGuard.RequireDefined(status, nameof(status));
        ContractGuard.RequireDefined(previousState, nameof(previousState));
        ContractGuard.RequireDefined(currentState, nameof(currentState));
        ArgumentNullException.ThrowIfNull(previousIdentity);
        ArgumentNullException.ThrowIfNull(currentIdentity);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(nextSnapshot);
        ArgumentNullException.ThrowIfNull(reasons);
        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireText(configurationVersion, nameof(configurationVersion));
        ContractGuard.RequireText(decisionInputSnapshotJson, nameof(decisionInputSnapshotJson));
        ArgumentNullException.ThrowIfNull(observation);
        Status = status;
        PreviousState = previousState;
        CurrentState = currentState;
        PreviousIdentity = previousIdentity;
        CurrentIdentity = currentIdentity;
        Input = input;
        NextSnapshot = nextSnapshot;
        Reasons = new ImmutableValueList<string>(reasons);
        RulesVersion = rulesVersion;
        ConfigurationVersion = configurationVersion;
        DecisionInputSnapshotJson = decisionInputSnapshotJson;
        Observation = observation;
    }

    internal static SetupLifecycleTransition Create(SetupLifecycleTransitionStatus status,
        SetupLifecycleSnapshot previous, SetupLifecycleSnapshot next,
        SetupLifecycleInput input, IEnumerable<string> reasons,
        SetupLifecycleObservation observation)
    {
        string snapshot = CoreJsonSerializer.Serialize(input);
        return new SetupLifecycleTransition(status, previous.State, next.State,
            previous.Identity, next.Identity, input, next,
            new ImmutableValueList<string>(reasons), input.RulesVersion,
            input.ConfigurationVersion, snapshot, observation);
    }
}
