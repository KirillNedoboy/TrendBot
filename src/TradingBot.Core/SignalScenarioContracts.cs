using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum SignalScenarioInputKind
{
    Confirm,
    Invalidate
}

public enum SignalScenarioEpisodeState
{
    Active,
    Canceled,
    Expired
}

public enum SignalScenarioEventKind
{
    AlertCreated,
    Continued,
    Canceled,
    Expired,
    OverlapLinked
}

public enum SignalScenarioTransitionStatus
{
    Applied,
    Continued,
    Duplicate,
    Late,
    Rejected,
    IdentityConflict,
    UnknownPolicy
}

/// <summary>Expiry rules require an explicit positive duration; null means unknown.</summary>
public sealed record SignalScenarioExpiryPolicy
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string PolicyVersion { get; }

    public TimeSpan? MaximumAge { get; }

    [JsonConstructor]
    public SignalScenarioExpiryPolicy(int formatVersion, string policyVersion,
        TimeSpan? maximumAge)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario expiry policy format is unsupported.");
        }

        ContractGuard.RequireText(policyVersion, nameof(policyVersion));
        if (maximumAge is { } age && age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), maximumAge,
                "A known scenario lifetime must be positive.");
        }

        FormatVersion = formatVersion;
        PolicyVersion = policyVersion;
        MaximumAge = maximumAge;
    }
}

/// <summary>Rearm rules are explicit; a rearm must use a new structural anchor.</summary>
public sealed record SignalScenarioRearmPolicy
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string PolicyVersion { get; }

    public TimeSpan? MinimumAfterTerminal { get; }

    public bool RequiresNewAnchor { get; }

    [JsonConstructor]
    public SignalScenarioRearmPolicy(int formatVersion, string policyVersion,
        TimeSpan? minimumAfterTerminal, bool requiresNewAnchor)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario rearm policy format is unsupported.");
        }

        ContractGuard.RequireText(policyVersion, nameof(policyVersion));
        if (minimumAfterTerminal is { } delay && delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAfterTerminal), delay,
                "A rearm delay cannot be negative.");
        }

        FormatVersion = formatVersion;
        PolicyVersion = policyVersion;
        MinimumAfterTerminal = minimumAfterTerminal;
        RequiresNewAnchor = requiresNewAnchor;
    }
}

/// <summary>Defines versioned responses to explicit and opposite-direction invalidation.</summary>
public sealed record SignalScenarioInvalidationPolicy
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string PolicyVersion { get; }

    public bool CancelOnExplicitInvalidation { get; }

    public bool CancelOnOppositeDirection { get; }

    [JsonConstructor]
    public SignalScenarioInvalidationPolicy(int formatVersion, string policyVersion,
        bool cancelOnExplicitInvalidation, bool cancelOnOppositeDirection)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario invalidation policy format is unsupported.");
        }

        ContractGuard.RequireText(policyVersion, nameof(policyVersion));
        FormatVersion = formatVersion;
        PolicyVersion = policyVersion;
        CancelOnExplicitInvalidation = cancelOnExplicitInvalidation;
        CancelOnOppositeDirection = cancelOnOppositeDirection;
    }
}

/// <summary>Required policy set for passive scenario projection; incomplete sets are not ready.</summary>
public sealed record SignalScenarioPolicySet
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public SignalScenarioExpiryPolicy? Expiry { get; }

    public SignalScenarioRearmPolicy? Rearm { get; }

    public SignalScenarioInvalidationPolicy? Invalidation { get; }

    public ImmutableValueList<string> UnknownReasons { get; }

    public bool IsReady => UnknownReasons.Count == 0;

    [JsonConstructor]
    public SignalScenarioPolicySet(int formatVersion, SignalScenarioExpiryPolicy? expiry,
        SignalScenarioRearmPolicy? rearm,
        SignalScenarioInvalidationPolicy? invalidation)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario policy-set format is unsupported.");
        }

        List<string> reasons = [];
        if (expiry is null)
        {
            reasons.Add("scenario-expiry-policy-missing");
        }
        else if (expiry.MaximumAge is null)
        {
            reasons.Add("scenario-expiry-duration-unknown");
        }

        if (rearm is null)
        {
            reasons.Add("scenario-rearm-policy-missing");
        }
        else
        {
            if (rearm.MinimumAfterTerminal is null)
            {
                reasons.Add("scenario-rearm-delay-unknown");
            }

            if (!rearm.RequiresNewAnchor)
            {
                reasons.Add("scenario-rearm-requires-new-anchor");
            }
        }

        if (invalidation is null)
        {
            reasons.Add("scenario-invalidation-policy-missing");
        }

        FormatVersion = formatVersion;
        Expiry = expiry;
        Rearm = rearm;
        Invalidation = invalidation;
        UnknownReasons = new ImmutableValueList<string>(reasons);
    }
}

/// <summary>
/// Pre-confirmation structural anchor. Its event ID seeds SignalId; its time and
/// sequence prove causality but are not part of logical identity.
/// </summary>
public sealed record SignalScenarioAnchor
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public SignalIdentity Identity { get; }

    public string AnchorEventId => Identity.SetupEventId;

    public UtcTimestamp AnchorEventTime { get; }

    public long AnchorStableSequence { get; }

    [JsonConstructor]
    public SignalScenarioAnchor(int formatVersion, SignalIdentity identity,
        UtcTimestamp anchorEventTime, long anchorStableSequence)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario anchor format is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(anchorEventTime);
        if (anchorStableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorStableSequence),
                anchorStableSequence, "An anchor sequence cannot be negative.");
        }

        FormatVersion = formatVersion;
        Identity = identity;
        AnchorEventTime = anchorEventTime;
        AnchorStableSequence = anchorStableSequence;
    }

    public static SignalScenarioAnchor Create(InstrumentId instrumentId, string venue,
        string setupTimeframe, Direction direction, SetupType family,
        string profileVersion, string familyAlgorithmVersion, string anchorEventId,
        UtcTimestamp anchorEventTime, long anchorStableSequence) => new(
            CurrentFormatVersion, SignalIdentity.Create(instrumentId, venue,
                setupTimeframe, direction, family, profileVersion,
                familyAlgorithmVersion, anchorEventId), anchorEventTime,
            anchorStableSequence);
}

/// <summary>Immutable public observation consumed by the pure scenario projector.</summary>
public sealed record SignalScenarioInput
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string StreamId { get; }

    public string EventId { get; }

    public SignalScenarioInputKind Kind { get; }

    public SignalScenarioAnchor? Anchor { get; }

    public string? TargetSignalId { get; }

    public string? ReasonCode { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public string InputHash { get; }

    [JsonConstructor]
    public SignalScenarioInput(int formatVersion, string streamId, string eventId,
        SignalScenarioInputKind kind, SignalScenarioAnchor? anchor,
        string? targetSignalId, string? reasonCode, UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, long stableSequence, string inputHash)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario input format is unsupported.");
        }

        ContractGuard.RequireText(streamId, nameof(streamId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ContractGuard.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "An input sequence cannot be negative.");
        }

        if (kind == SignalScenarioInputKind.Confirm)
        {
            ArgumentNullException.ThrowIfNull(anchor);
            if (targetSignalId is not null || reasonCode is not null)
            {
                throw new ArgumentException(
                    "Confirmation input cannot contain invalidation fields.");
            }

            if (anchor.AnchorEventTime.Value > eventTime.Value
                || (anchor.AnchorEventTime.Value == eventTime.Value
                    && anchor.AnchorStableSequence >= stableSequence))
            {
                throw new ArgumentException(
                    "The structural anchor must causally precede its confirmation.",
                    nameof(anchor));
            }
        }
        else
        {
            if (anchor is not null)
            {
                throw new ArgumentException(
                    "Invalidation input cannot contain a confirmation anchor.",
                    nameof(anchor));
            }

            SignalContractHash.RequireSha256(targetSignalId!, nameof(targetSignalId));
            ContractGuard.RequireText(reasonCode!, nameof(reasonCode));
        }

        SignalContractHash.RequireSha256(inputHash, nameof(inputHash));
        string expected = ComputeInputHash(formatVersion, streamId, eventId, kind,
            anchor, targetSignalId, reasonCode, eventTime, availabilityTime,
            stableSequence);
        if (!StringComparer.Ordinal.Equals(inputHash, expected))
        {
            throw new ArgumentException("The scenario input hash does not match its content.",
                nameof(inputHash));
        }

        FormatVersion = formatVersion;
        StreamId = streamId;
        EventId = eventId;
        Kind = kind;
        Anchor = anchor;
        TargetSignalId = targetSignalId;
        ReasonCode = reasonCode;
        EventTime = eventTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        InputHash = inputHash;
    }

    public static SignalScenarioInput Confirm(string streamId, string eventId,
        SignalScenarioAnchor anchor, UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, long stableSequence) => Create(streamId,
            eventId, SignalScenarioInputKind.Confirm, anchor, null, null, eventTime,
            availabilityTime, stableSequence);

    public static SignalScenarioInput Invalidate(string streamId, string eventId,
        string targetSignalId, string reasonCode, UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, long stableSequence) => Create(streamId,
            eventId, SignalScenarioInputKind.Invalidate, null, targetSignalId,
            reasonCode, eventTime, availabilityTime, stableSequence);

    private static SignalScenarioInput Create(string streamId, string eventId,
        SignalScenarioInputKind kind, SignalScenarioAnchor? anchor,
        string? targetSignalId, string? reasonCode, UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, long stableSequence)
    {
        string hash = ComputeInputHash(CurrentFormatVersion, streamId, eventId,
            kind, anchor, targetSignalId, reasonCode, eventTime, availabilityTime,
            stableSequence);
        return new SignalScenarioInput(CurrentFormatVersion, streamId, eventId,
            kind, anchor, targetSignalId, reasonCode, eventTime, availabilityTime,
            stableSequence, hash);
    }

    private static string ComputeInputHash(int formatVersion, string streamId,
        string eventId, SignalScenarioInputKind kind, SignalScenarioAnchor? anchor,
        string? targetSignalId, string? reasonCode, UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, long stableSequence) =>
        SignalContractHash.Compute(new SignalScenarioInputMaterial(formatVersion,
            streamId, eventId, kind, anchor, targetSignalId, reasonCode,
            eventTime, availabilityTime, stableSequence));

    private sealed record SignalScenarioInputMaterial(int FormatVersion,
        string StreamId, string EventId, SignalScenarioInputKind Kind,
        SignalScenarioAnchor? Anchor, string? TargetSignalId, string? ReasonCode,
        UtcTimestamp EventTime, UtcTimestamp AvailabilityTime, long StableSequence);
}

/// <summary>Immutable episode state linked to its original passive alert.</summary>
public sealed record SignalScenarioEpisode
{
    public SignalIdentity Identity { get; }

    public SignalScenarioAnchor Anchor { get; }

    public SignalScenarioEpisodeState State { get; }

    public string AlertEventId { get; }

    public UtcTimestamp CreatedAt { get; }

    public UtcTimestamp ExpiresAt { get; }

    public UtcTimestamp? TerminalAt { get; }

    public long? TerminalStableSequence { get; }

    public string? TerminalEventId { get; }

    [JsonConstructor]
    public SignalScenarioEpisode(SignalIdentity identity, SignalScenarioAnchor anchor,
        SignalScenarioEpisodeState state, string alertEventId,
        UtcTimestamp createdAt, UtcTimestamp expiresAt, UtcTimestamp? terminalAt,
        long? terminalStableSequence, string? terminalEventId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(anchor);
        ContractGuard.RequireDefined(state, nameof(state));
        if (identity != anchor.Identity)
        {
            throw new ArgumentException("The episode identity must match its anchor.",
                nameof(anchor));
        }

        SignalContractHash.RequireSha256(alertEventId, nameof(alertEventId));
        ArgumentNullException.ThrowIfNull(createdAt);
        ArgumentNullException.ThrowIfNull(expiresAt);
        if (expiresAt.Value <= createdAt.Value)
        {
            throw new ArgumentException("An episode expiry must follow its creation.",
                nameof(expiresAt));
        }

        if (state == SignalScenarioEpisodeState.Active)
        {
            if (terminalAt is not null || terminalStableSequence is not null
                || terminalEventId is not null)
            {
                throw new ArgumentException("An active episode cannot have terminal fields.");
            }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(terminalAt);
            if (terminalStableSequence is null or < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(terminalStableSequence),
                    "A terminal episode requires a non-negative sequence.");
            }

            SignalContractHash.RequireSha256(terminalEventId!, nameof(terminalEventId));
            if (terminalAt.Value < createdAt.Value)
            {
                throw new ArgumentException("An episode cannot terminate before creation.",
                    nameof(terminalAt));
            }
        }

        Identity = identity;
        Anchor = anchor;
        State = state;
        AlertEventId = alertEventId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        TerminalAt = terminalAt;
        TerminalStableSequence = terminalStableSequence;
        TerminalEventId = terminalEventId;
    }

    internal SignalScenarioEpisode Close(SignalScenarioEpisodeState state,
        UtcTimestamp terminalAt, long terminalStableSequence, string terminalEventId) =>
        new(Identity, Anchor, state, AlertEventId, CreatedAt, ExpiresAt, terminalAt,
            terminalStableSequence, terminalEventId);
}

/// <summary>Append-only event emitted by passive scenario projection.</summary>
public sealed record SignalScenarioEvent
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string EventId { get; }

    public SignalScenarioEventKind Kind { get; }

    public string SignalId { get; }

    public string? RelatedSignalId { get; }

    public string? EpisodeAlertEventId { get; }

    public string? RelatedEpisodeAlertEventId { get; }

    public string CauseEventId { get; }
    public string CauseStreamId { get; }

    public string? ReasonCode { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public UtcTimestamp? EffectiveAt { get; }

    public string ExpiryPolicyVersion { get; }

    public string RearmPolicyVersion { get; }

    public string InvalidationPolicyVersion { get; }

    [JsonConstructor]
    public SignalScenarioEvent(int formatVersion, string eventId,
        SignalScenarioEventKind kind, string signalId, string? relatedSignalId,
        string? episodeAlertEventId, string? relatedEpisodeAlertEventId,
        string causeStreamId, string causeEventId, string? reasonCode,
        UtcTimestamp eventTime, UtcTimestamp availabilityTime, long stableSequence,
        UtcTimestamp? effectiveAt, string expiryPolicyVersion,
        string rearmPolicyVersion, string invalidationPolicyVersion)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario event format is unsupported.");
        }

        ContractGuard.RequireDefined(kind, nameof(kind));
        SignalContractHash.RequireSha256(signalId, nameof(signalId));
        ContractGuard.RequireText(causeStreamId, nameof(causeStreamId));
        ContractGuard.RequireText(causeEventId, nameof(causeEventId));
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "An event sequence cannot be negative.");
        }

        ContractGuard.RequireText(expiryPolicyVersion, nameof(expiryPolicyVersion));
        ContractGuard.RequireText(rearmPolicyVersion, nameof(rearmPolicyVersion));
        ContractGuard.RequireText(invalidationPolicyVersion,
            nameof(invalidationPolicyVersion));
        ValidateLinks(kind, signalId, relatedSignalId, episodeAlertEventId,
            relatedEpisodeAlertEventId, reasonCode, eventTime, effectiveAt);

        SignalContractHash.RequireSha256(eventId, nameof(eventId));
        string expected = ComputeEventId(formatVersion, kind, signalId,
            relatedSignalId, episodeAlertEventId, relatedEpisodeAlertEventId,
            causeStreamId, causeEventId, reasonCode, eventTime, availabilityTime,
            stableSequence, effectiveAt, expiryPolicyVersion, rearmPolicyVersion,
            invalidationPolicyVersion);
        if (!StringComparer.Ordinal.Equals(eventId, expected))
        {
            throw new ArgumentException("The scenario event ID does not match its content.",
                nameof(eventId));
        }

        FormatVersion = formatVersion;
        EventId = eventId;
        Kind = kind;
        SignalId = signalId;
        RelatedSignalId = relatedSignalId;
        EpisodeAlertEventId = episodeAlertEventId;
        RelatedEpisodeAlertEventId = relatedEpisodeAlertEventId;
        CauseEventId = causeEventId;
        CauseStreamId = causeStreamId;
        ReasonCode = reasonCode;
        EventTime = eventTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        EffectiveAt = effectiveAt;
        ExpiryPolicyVersion = expiryPolicyVersion;
        RearmPolicyVersion = rearmPolicyVersion;
        InvalidationPolicyVersion = invalidationPolicyVersion;
    }

    internal static SignalScenarioEvent Create(SignalScenarioEventKind kind,
        string signalId, string? relatedSignalId, string? episodeAlertEventId,
        string? relatedEpisodeAlertEventId, SignalScenarioInput input,
        string? reasonCode, UtcTimestamp? effectiveAt,
        SignalScenarioPolicySet policies)
    {
        string expiryVersion = policies.Expiry!.PolicyVersion;
        string rearmVersion = policies.Rearm!.PolicyVersion;
        string invalidationVersion = policies.Invalidation!.PolicyVersion;
        string eventId = ComputeEventId(CurrentFormatVersion, kind, signalId,
            relatedSignalId, episodeAlertEventId, relatedEpisodeAlertEventId,
            input.StreamId, input.EventId, reasonCode, input.EventTime,
            input.AvailabilityTime, input.StableSequence, effectiveAt,
            expiryVersion, rearmVersion, invalidationVersion);
        return new SignalScenarioEvent(CurrentFormatVersion, eventId, kind,
            signalId, relatedSignalId, episodeAlertEventId,
            relatedEpisodeAlertEventId, input.StreamId, input.EventId, reasonCode,
            input.EventTime, input.AvailabilityTime, input.StableSequence,
            effectiveAt, expiryVersion, rearmVersion, invalidationVersion);
    }

    private static void ValidateLinks(SignalScenarioEventKind kind, string signalId,
        string? relatedSignalId, string? episodeAlertEventId,
        string? relatedEpisodeAlertEventId, string? reasonCode,
        UtcTimestamp eventTime, UtcTimestamp? effectiveAt)
    {
        switch (kind)
        {
            case SignalScenarioEventKind.AlertCreated:
                if (relatedSignalId is not null || episodeAlertEventId is not null
                    || relatedEpisodeAlertEventId is not null || reasonCode is not null
                    || effectiveAt is not null)
                {
                    throw new ArgumentException("An alert event cannot link a prior episode.");
                }
                break;
            case SignalScenarioEventKind.Continued:
                RequireEpisodeLink(relatedSignalId, episodeAlertEventId,
                    relatedEpisodeAlertEventId);
                if (reasonCode is not null || effectiveAt is not null)
                {
                    throw new ArgumentException("A continuation cannot change episode state.");
                }
                break;
            case SignalScenarioEventKind.Canceled:
                RequireEpisodeLink(relatedSignalId, episodeAlertEventId,
                    relatedEpisodeAlertEventId);
                ContractGuard.RequireText(reasonCode!, nameof(reasonCode));
                ArgumentNullException.ThrowIfNull(effectiveAt);
                if (effectiveAt.Value != eventTime.Value)
                {
                    throw new ArgumentException(
                        "Cancellation effective time must match its causal event time.",
                        nameof(effectiveAt));
                }
                break;
            case SignalScenarioEventKind.Expired:
                RequireEpisodeLink(relatedSignalId, episodeAlertEventId,
                    relatedEpisodeAlertEventId);
                ContractGuard.RequireText(reasonCode!, nameof(reasonCode));
                ArgumentNullException.ThrowIfNull(effectiveAt);
                if (effectiveAt.Value > eventTime.Value)
                {
                    throw new ArgumentException(
                        "Expiry cannot be effective after the event that observed it.",
                        nameof(effectiveAt));
                }
                break;
            case SignalScenarioEventKind.OverlapLinked:
                SignalContractHash.RequireSha256(relatedSignalId!, nameof(relatedSignalId));
                SignalContractHash.RequireSha256(episodeAlertEventId!,
                    nameof(episodeAlertEventId));
                SignalContractHash.RequireSha256(relatedEpisodeAlertEventId!,
                    nameof(relatedEpisodeAlertEventId));
                if (StringComparer.Ordinal.Compare(signalId, relatedSignalId) >= 0
                    || reasonCode is not null || effectiveAt is not null)
                {
                    throw new ArgumentException(
                        "Overlap links require a canonical pair of distinct signal IDs.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void RequireEpisodeLink(string? relatedSignalId,
        string? episodeAlertEventId, string? relatedEpisodeAlertEventId)
    {
        if (relatedSignalId is not null || relatedEpisodeAlertEventId is not null)
        {
            throw new ArgumentException("The scenario transition has invalid episode links.");
        }

        SignalContractHash.RequireSha256(episodeAlertEventId!,
            nameof(episodeAlertEventId));
    }

    private static string ComputeEventId(int formatVersion,
        SignalScenarioEventKind kind, string signalId, string? relatedSignalId,
        string? episodeAlertEventId, string? relatedEpisodeAlertEventId,
        string causeStreamId, string causeEventId, string? reasonCode,
        UtcTimestamp eventTime, UtcTimestamp availabilityTime,
        long stableSequence, UtcTimestamp? effectiveAt, string expiryPolicyVersion,
        string rearmPolicyVersion, string invalidationPolicyVersion) =>
        SignalContractHash.Compute(new SignalScenarioEventMaterial(formatVersion,
            kind, signalId, relatedSignalId, episodeAlertEventId,
            relatedEpisodeAlertEventId, causeStreamId, causeEventId, reasonCode,
            eventTime, availabilityTime, stableSequence, effectiveAt,
            expiryPolicyVersion, rearmPolicyVersion, invalidationPolicyVersion));

    private sealed record SignalScenarioEventMaterial(int FormatVersion,
        SignalScenarioEventKind Kind, string SignalId, string? RelatedSignalId,
        string? EpisodeAlertEventId, string? RelatedEpisodeAlertEventId,
        string CauseStreamId, string CauseEventId, string? ReasonCode,
        UtcTimestamp EventTime, UtcTimestamp AvailabilityTime, long StableSequence,
        UtcTimestamp? EffectiveAt, string ExpiryPolicyVersion,
        string RearmPolicyVersion, string InvalidationPolicyVersion);
}

/// <summary>Compact immutable receipt for duplicate detection and causal recovery.</summary>
public sealed record SignalScenarioProcessedInput
{
    public string StreamId { get; }

    public string EventId { get; }

    public string InputHash { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    [JsonConstructor]
    public SignalScenarioProcessedInput(string streamId, string eventId,
        string inputHash, UtcTimestamp eventTime, UtcTimestamp availabilityTime,
        long stableSequence)
    {
        ContractGuard.RequireText(streamId, nameof(streamId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        SignalContractHash.RequireSha256(inputHash, nameof(inputHash));
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "A processed sequence cannot be negative.");
        }

        StreamId = streamId;
        EventId = eventId;
        InputHash = inputHash;
        EventTime = eventTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
    }

    internal static SignalScenarioProcessedInput From(SignalScenarioInput input) =>
        new(input.StreamId, input.EventId, input.InputHash, input.EventTime,
            input.AvailabilityTime, input.StableSequence);
}

/// <summary>Restart-safe pure projection checkpoint. No persistence is performed here.</summary>
public sealed record SignalScenarioProjectionSnapshot
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentProjectionVersion = "signal-scenario-projection-v1";

    public int FormatVersion { get; }

    public string ProjectionVersion { get; }

    public SignalScenarioPolicySet Policies { get; }

    public CausalEventCursor ProcessedCursor { get; }

    public UtcTimestamp ProcessedAvailabilityTime { get; }

    public ImmutableValueList<SignalScenarioProcessedInput> ProcessedInputs { get; }

    public ImmutableValueList<SignalScenarioEpisode> Episodes { get; }

    public ImmutableValueList<SignalScenarioEvent> Events { get; }

    [JsonConstructor]
    public SignalScenarioProjectionSnapshot(int formatVersion,
        string projectionVersion, SignalScenarioPolicySet policies,
        CausalEventCursor processedCursor, UtcTimestamp processedAvailabilityTime,
        ImmutableValueList<SignalScenarioProcessedInput> processedInputs,
        ImmutableValueList<SignalScenarioEpisode> episodes,
        ImmutableValueList<SignalScenarioEvent> events)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The scenario projection snapshot format is unsupported.");
        }

        if (!StringComparer.Ordinal.Equals(projectionVersion,
            CurrentProjectionVersion))
        {
            throw new ArgumentException("The scenario projection version is unsupported.",
                nameof(projectionVersion));
        }

        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(processedCursor);
        ArgumentNullException.ThrowIfNull(processedAvailabilityTime);
        ArgumentNullException.ThrowIfNull(processedInputs);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(events);
        ValidateUniqueInputs(processedInputs);
        ValidateCausalTail(processedCursor, processedAvailabilityTime, processedInputs);
        ValidateEpisodes(policies, processedInputs, episodes, events);

        FormatVersion = formatVersion;
        ProjectionVersion = projectionVersion;
        Policies = policies;
        ProcessedCursor = processedCursor;
        ProcessedAvailabilityTime = processedAvailabilityTime;
        ProcessedInputs = new ImmutableValueList<SignalScenarioProcessedInput>(processedInputs);
        Episodes = new ImmutableValueList<SignalScenarioEpisode>(episodes);
        Events = new ImmutableValueList<SignalScenarioEvent>(events);
    }

    public static SignalScenarioProjectionSnapshot CreateInitial(
        SignalScenarioPolicySet policies) => new(CurrentFormatVersion,
            CurrentProjectionVersion, policies, new CausalEventCursor(
                new UtcTimestamp(DateTimeOffset.MinValue), 0),
            new UtcTimestamp(DateTimeOffset.MinValue),
            new ImmutableValueList<SignalScenarioProcessedInput>([]),
            new ImmutableValueList<SignalScenarioEpisode>([]),
            new ImmutableValueList<SignalScenarioEvent>([]));

    private static void ValidateUniqueInputs(
        ImmutableValueList<SignalScenarioProcessedInput> inputs)
    {
        HashSet<(string StreamId, string EventId)> identities = [];
        foreach (SignalScenarioProcessedInput input in inputs)
        {
            if (!identities.Add((input.StreamId, input.EventId)))
            {
                throw new ArgumentException("Processed scenario input identities must be unique.",
                    nameof(inputs));
            }
        }
    }

    private static void ValidateCausalTail(CausalEventCursor cursor,
        UtcTimestamp availabilityTime,
        ImmutableValueList<SignalScenarioProcessedInput> inputs)
    {
        if (inputs.Count == 0)
        {
            if (cursor.AsOf.Value != DateTimeOffset.MinValue
                || cursor.MaxStableSequence != 0
                || availabilityTime.Value != DateTimeOffset.MinValue)
            {
                throw new ArgumentException("An empty projection must use its initial cursor.");
            }

            return;
        }
        for (int index = 1; index < inputs.Count; index++)
        {
            SignalScenarioProcessedInput previous = inputs[index - 1];
            SignalScenarioProcessedInput current = inputs[index];
            if (current.EventTime.Value < previous.EventTime.Value
                || current.AvailabilityTime.Value < previous.AvailabilityTime.Value
                || current.StableSequence <= previous.StableSequence)
            {
                throw new ArgumentException(
                    "Processed scenario inputs must preserve causal ordering.",
                    nameof(inputs));
            }
        }

        SignalScenarioProcessedInput last = inputs[^1];
        if (cursor.AsOf != last.EventTime
            || cursor.MaxStableSequence != last.StableSequence
            || availabilityTime != last.AvailabilityTime)
        {
            throw new ArgumentException(
                "The projection cursor must match its last processed input.");
        }
    }

    private static void ValidateEpisodes(SignalScenarioPolicySet policies,
        ImmutableValueList<SignalScenarioProcessedInput> processedInputs,
        ImmutableValueList<SignalScenarioEpisode> episodes,
        ImmutableValueList<SignalScenarioEvent> events)
    {
        if (!policies.IsReady && (episodes.Count > 0 || events.Count > 0))
        {
            throw new ArgumentException(
                "An incomplete policy set cannot contain projected episodes.",
                nameof(policies));
        }

        Dictionary<(string StreamId, string EventId), SignalScenarioProcessedInput>
            inputsByIdentity = [];
        foreach (SignalScenarioProcessedInput input in processedInputs)
        {
            inputsByIdentity.Add((input.StreamId, input.EventId), input);
        }

        HashSet<string> eventIds = new(StringComparer.Ordinal);
        Dictionary<string, SignalScenarioEvent> eventsById = new(StringComparer.Ordinal);
        foreach (SignalScenarioEvent item in events)
        {
            if (!eventIds.Add(item.EventId))
            {
                throw new ArgumentException("Scenario event IDs must be unique.", nameof(events));
            }

            if (!inputsByIdentity.TryGetValue((item.CauseStreamId, item.CauseEventId),
                out SignalScenarioProcessedInput? cause)
                || cause.EventTime != item.EventTime
                || cause.AvailabilityTime != item.AvailabilityTime
                || cause.StableSequence != item.StableSequence
                || item.ExpiryPolicyVersion != policies.Expiry?.PolicyVersion
                || item.RearmPolicyVersion != policies.Rearm?.PolicyVersion
                || item.InvalidationPolicyVersion != policies.Invalidation?.PolicyVersion)
            {
                throw new ArgumentException(
                    "A scenario event must retain its causal input and policy versions.",
                    nameof(events));
            }

            eventsById.Add(item.EventId, item);
        }

        Dictionary<string, SignalScenarioEpisode> episodesBySignalId =
            new(StringComparer.Ordinal);
        foreach (SignalScenarioEpisode episode in episodes)
        {
            if (!episodesBySignalId.TryAdd(episode.Identity.SignalId, episode))
            {
                throw new ArgumentException("Each logical scenario can have one episode.",
                    nameof(episodes));
            }

            if (!eventsById.TryGetValue(episode.AlertEventId,
                out SignalScenarioEvent? alert)
                || alert.Kind != SignalScenarioEventKind.AlertCreated
                || alert.SignalId != episode.Identity.SignalId
                || alert.EventTime != episode.CreatedAt)
            {
                throw new ArgumentException("An episode must reference its immutable alert event.",
                    nameof(episodes));
            }

            if (policies.Expiry?.MaximumAge is not { } duration
                || !TryAdd(episode.CreatedAt, duration, out UtcTimestamp? expectedExpiry)
                || expectedExpiry != episode.ExpiresAt)
            {
                throw new ArgumentException("An episode expiry must match its policy.",
                    nameof(episodes));
            }

            if (episode.State == SignalScenarioEpisodeState.Active)
            {
                continue;
            }

            SignalScenarioEventKind expectedKind =
                episode.State == SignalScenarioEpisodeState.Canceled
                    ? SignalScenarioEventKind.Canceled : SignalScenarioEventKind.Expired;
            if (!eventsById.TryGetValue(episode.TerminalEventId!,
                out SignalScenarioEvent? terminal)
                || terminal.Kind != expectedKind
                || terminal.SignalId != episode.Identity.SignalId
                || terminal.EpisodeAlertEventId != episode.AlertEventId
                || terminal.EffectiveAt != episode.TerminalAt
                || terminal.StableSequence != episode.TerminalStableSequence)
            {
                throw new ArgumentException("A terminal episode must reference its immutable event.",
                    nameof(episodes));
            }
        }

        HashSet<(string FirstSignalId, string SecondSignalId)> overlapPairs = [];
        foreach (SignalScenarioEvent item in events)
        {
            if (!episodesBySignalId.TryGetValue(item.SignalId,
                out SignalScenarioEpisode? first))
            {
                throw new ArgumentException("A scenario event must reference an episode.",
                    nameof(events));
            }

            switch (item.Kind)
            {
                case SignalScenarioEventKind.AlertCreated:
                    if (first.AlertEventId != item.EventId)
                    {
                        throw new ArgumentException("An episode has one immutable alert event.",
                            nameof(events));
                    }
                    break;
                case SignalScenarioEventKind.Continued:
                    if (item.EpisodeAlertEventId != first.AlertEventId
                        || item.EventTime.Value >= first.ExpiresAt.Value)
                    {
                        throw new ArgumentException(
                            "A continuation must link an unexpired episode alert.",
                            nameof(events));
                    }
                    break;
                case SignalScenarioEventKind.Canceled:
                case SignalScenarioEventKind.Expired:
                    if (item.EventId != first.TerminalEventId)
                    {
                        throw new ArgumentException(
                            "A terminal event must be the episode's immutable close event.",
                            nameof(events));
                    }
                    break;
                case SignalScenarioEventKind.OverlapLinked:
                    if (!episodesBySignalId.TryGetValue(item.RelatedSignalId!,
                        out SignalScenarioEpisode? second)
                        || first.Identity.Family == second.Identity.Family
                        || first.Identity.InstrumentId != second.Identity.InstrumentId
                        || first.Identity.Direction != second.Identity.Direction
                        || first.AlertEventId != item.EpisodeAlertEventId
                        || second.AlertEventId != item.RelatedEpisodeAlertEventId
                        || item.EventTime.Value < first.CreatedAt.Value
                        || item.EventTime.Value >= first.ExpiresAt.Value
                        || item.EventTime.Value < second.CreatedAt.Value
                        || item.EventTime.Value >= second.ExpiresAt.Value
                        || !overlapPairs.Add((item.SignalId, item.RelatedSignalId!)))
                    {
                        throw new ArgumentException(
                            "An overlap event must link one intersecting, compatible alert pair.",
                            nameof(events));
                    }
                    break;
            }
        }
    }

    internal static bool TryAdd(UtcTimestamp timestamp, TimeSpan duration,
        out UtcTimestamp? result)
    {
        try
        {
            result = new UtcTimestamp(timestamp.Value.Add(duration));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = null;
            return false;
        }
    }
}

/// <summary>Result of one deterministic, side-effect-free scenario transition.</summary>
public sealed record SignalScenarioProjectionTransition
{
    public SignalScenarioTransitionStatus Status { get; }

    public string ReasonCode { get; }

    public SignalScenarioProjectionSnapshot NextSnapshot { get; }

    public ImmutableValueList<SignalScenarioEvent> EmittedEvents { get; }

    internal SignalScenarioProjectionTransition(SignalScenarioTransitionStatus status,
        string reasonCode, SignalScenarioProjectionSnapshot nextSnapshot,
        IEnumerable<SignalScenarioEvent> emittedEvents)
    {
        ContractGuard.RequireDefined(status, nameof(status));
        ContractGuard.RequireText(reasonCode, nameof(reasonCode));
        ArgumentNullException.ThrowIfNull(nextSnapshot);
        ArgumentNullException.ThrowIfNull(emittedEvents);
        Status = status;
        ReasonCode = reasonCode;
        NextSnapshot = nextSnapshot;
        EmittedEvents = new ImmutableValueList<SignalScenarioEvent>(emittedEvents);
    }
}
