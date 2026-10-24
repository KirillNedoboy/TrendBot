using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>The evidence lane that contributes to a strategy decision.</summary>
public enum DecisionLane
{
    LaneA,
    LaneB,
    LaneC
}

/// <summary>Lane evidence status consumed by the Decision Engine.</summary>
public enum DecisionEvidenceStatus
{
    Confirm,
    Neutral,
    Veto,
    Unknown
}

/// <summary>Explicit profile selected by the caller.</summary>
public enum DecisionEngineProfile
{
    Allow,
    Shadow
}

/// <summary>One immutable confirmation or veto from a decision lane.</summary>
public sealed record DecisionEvidence
{
    public DecisionLane Lane { get; }

    public DecisionEvidenceStatus Status { get; }

    public string EvidenceId { get; }

    public string AlgorithmVersion { get; }

    public CausalEventCursor Cursor { get; }

    public string Provenance { get; }

    /// <summary>Optional validated Lane B join trace; omitted for legacy evidence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaneBDecisionEvidenceMetadata? LaneBMetadata { get; }

    /// <summary>Optional validated Lane C shadow reason trace.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LaneCDecisionEvidenceMetadata? LaneCMetadata { get; }

    [JsonIgnore]
    public LaneBDecisionEvidenceMetadata? Metadata => LaneBMetadata;

    [JsonIgnore]
    public LaneBDecisionEvidenceMetadata? LaneBShadowMetadata => LaneBMetadata;

    [JsonIgnore]
    public LaneBDecisionEvidenceMetadata? LaneBShadowEvidence => LaneBMetadata;

    [JsonConstructor]
    public DecisionEvidence(DecisionLane lane, DecisionEvidenceStatus status,
        string evidenceId, string algorithmVersion, CausalEventCursor cursor,
        string provenance, LaneBDecisionEvidenceMetadata? laneBMetadata = null,
        LaneCDecisionEvidenceMetadata? laneCMetadata = null)
    {
        ContractGuard.RequireDefined(lane, nameof(lane));
        ContractGuard.RequireDefined(status, nameof(status));
        ContractGuard.RequireText(evidenceId, nameof(evidenceId));
        ContractGuard.RequireText(algorithmVersion, nameof(algorithmVersion));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireText(provenance, nameof(provenance));
        if (laneBMetadata is not null)
        {
            if (lane != DecisionLane.LaneB)
            {
                throw new ArgumentException(
                    "Lane B metadata can only be attached to Lane B evidence.",
                    nameof(laneBMetadata));
            }

            if (laneBMetadata.Cursor != cursor)
            {
                throw new ArgumentException(
                    "Lane B metadata must use the evidence cursor.", nameof(laneBMetadata));
            }

            if (!StringComparer.Ordinal.Equals(evidenceId,
                    laneBMetadata.ShadowEvidenceId))
            {
                throw new ArgumentException(
                    "Lane B metadata must use the shadow evidence identity.",
                    nameof(laneBMetadata));
            }

            if (!StringComparer.Ordinal.Equals(algorithmVersion,
                    laneBMetadata.ShadowAlgorithmVersion))
            {
                throw new ArgumentException(
                    "Lane B metadata must use the shadow algorithm version.",
                    nameof(laneBMetadata));
            }

            if (status != laneBMetadata.EvidenceStatus)
            {
                throw new ArgumentException(
                    "Lane B metadata must use the effective evidence status.",
                    nameof(laneBMetadata));
            }
        }

        if (laneCMetadata is not null)
        {
            if (lane != DecisionLane.LaneC || laneBMetadata is not null)
            {
                throw new ArgumentException(
                    "Lane C metadata can only be attached to Lane C evidence.",
                    nameof(laneCMetadata));
            }

            if (laneCMetadata.Cursor != cursor
                || laneCMetadata.Status switch
                {
                    LaneCShadowStatus.Confirm => DecisionEvidenceStatus.Confirm,
                    LaneCShadowStatus.Neutral => DecisionEvidenceStatus.Neutral,
                    LaneCShadowStatus.Veto => DecisionEvidenceStatus.Veto,
                    LaneCShadowStatus.InsufficientData => DecisionEvidenceStatus.Unknown,
                    _ => DecisionEvidenceStatus.Unknown
                } != status
                || !StringComparer.Ordinal.Equals(evidenceId,
                    laneCMetadata.ShadowResultIdentity))
            {
                throw new ArgumentException(
                    "Lane C metadata must match the evidence identity, cursor, and status.",
                    nameof(laneCMetadata));
            }

            if (!StringComparer.Ordinal.Equals(algorithmVersion,
                    LaneCShadowResult.CurrentAlgorithmVersion))
            {
                throw new ArgumentException(
                    "Lane C evidence must use the shadow algorithm version.",
                    nameof(algorithmVersion));
            }
        }

        Lane = lane;
        Status = status;
        EvidenceId = evidenceId;
        AlgorithmVersion = algorithmVersion;
        Cursor = cursor;
        Provenance = provenance;
        LaneBMetadata = laneBMetadata;
        LaneCMetadata = laneCMetadata;
    }

    /// <summary>Creates Decision Evidence from a validated Lane B join.</summary>
    public static DecisionEvidence FromLaneBJoin(LaneBJoinSnapshot join, string provenance)
    {
        ArgumentNullException.ThrowIfNull(join);
        LaneBDecisionEvidenceMetadata metadata =
            LaneBDecisionEvidenceMetadata.Create(join);
        return new DecisionEvidence(DecisionLane.LaneB, metadata.EvidenceStatus,
            metadata.ShadowEvidenceId, metadata.ShadowAlgorithmVersion, metadata.Cursor,
            provenance, metadata);
    }

    /// <summary>Creates Decision Evidence from a Lane C shadow result and its reasons.</summary>
    public static DecisionEvidence FromLaneCShadow(LaneCShadowResult result,
        string provenance)
    {
        ArgumentNullException.ThrowIfNull(result);
        LaneCDecisionEvidenceMetadata metadata = LaneCDecisionEvidenceMetadata.Create(result);
        return new DecisionEvidence(DecisionLane.LaneC, result.EvidenceStatus,
            result.ResultIdentity, result.AlgorithmVersion, result.Cursor, provenance,
            laneCMetadata: metadata);
    }

    public static DecisionEvidence CreateFromLaneBJoin(LaneBJoinSnapshot join,
        string provenance) => FromLaneBJoin(join, provenance);
}

/// <summary>Versioned Decision Engine rule profile with no trading defaults.</summary>
public sealed record DecisionEngineConfiguration
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string RulesVersion { get; }

    public string ConfigurationVersion { get; }

    public DecisionEngineProfile Profile { get; }

    public bool RequireLaneB { get; }

    public bool RequireLaneC { get; }

    public bool AllowNeutralLaneB { get; }

    public bool AllowNeutralLaneC { get; }

    public DecisionEngineConfiguration(string rulesVersion, string configurationVersion,
        DecisionEngineProfile profile, bool requireLaneB, bool requireLaneC,
        bool allowNeutralLaneB, bool allowNeutralLaneC)
        : this(CurrentFormatVersion, rulesVersion, configurationVersion, profile,
            requireLaneB, requireLaneC, allowNeutralLaneB, allowNeutralLaneC)
    {
    }

    [JsonConstructor]
    public DecisionEngineConfiguration(int formatVersion, string rulesVersion,
        string configurationVersion, DecisionEngineProfile profile, bool requireLaneB,
        bool requireLaneC, bool allowNeutralLaneB, bool allowNeutralLaneC)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The Decision Engine configuration format is unsupported.");
        }

        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireText(configurationVersion, nameof(configurationVersion));
        ContractGuard.RequireDefined(profile, nameof(profile));
        RulesVersion = rulesVersion;
        ConfigurationVersion = configurationVersion;
        Profile = profile;
        RequireLaneB = requireLaneB;
        RequireLaneC = requireLaneC;
        AllowNeutralLaneB = allowNeutralLaneB;
        AllowNeutralLaneC = allowNeutralLaneC;
        FormatVersion = formatVersion;
    }
}

/// <summary>Complete immutable input snapshot consumed by the Decision Engine.</summary>
public sealed record DecisionEngineInput
{
    public string DecisionId { get; }

    public SetupLifecycleSnapshot Lifecycle { get; }

    public SetupEvidenceSnapshot LaneAEvidence { get; }

    public ImmutableValueList<DecisionEvidence> LaneEvidence { get; }

    public string Provenance { get; }

    [JsonConstructor]
    public DecisionEngineInput(string decisionId, SetupLifecycleSnapshot lifecycle,
        SetupEvidenceSnapshot laneAEvidence,
        ImmutableValueList<DecisionEvidence> laneEvidence, string provenance)
        : this(decisionId, lifecycle, laneAEvidence,
            (IEnumerable<DecisionEvidence>)laneEvidence, provenance)
    {
    }

    public DecisionEngineInput(string decisionId, SetupLifecycleSnapshot lifecycle,
        SetupEvidenceSnapshot laneAEvidence, IEnumerable<DecisionEvidence> laneEvidence,
        string provenance)
    {
        ContractGuard.RequireText(decisionId, nameof(decisionId));
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(laneAEvidence);
        ArgumentNullException.ThrowIfNull(laneEvidence);
        ContractGuard.RequireText(provenance, nameof(provenance));
        if (laneAEvidence.InputSequence.InstrumentId != lifecycle.Identity.InstrumentId
            || laneAEvidence.SetupType != lifecycle.Identity.SetupType
            || laneAEvidence.Direction != lifecycle.Identity.Direction)
        {
            throw new ArgumentException(
                "Lane A evidence must match the lifecycle identity.",
                nameof(laneAEvidence));
        }

        DecisionId = decisionId;
        Lifecycle = lifecycle;
        LaneAEvidence = laneAEvidence;
        LaneEvidence = new ImmutableValueList<DecisionEvidence>(laneEvidence);
        Provenance = provenance;
    }
}

/// <summary>Decision plus the exact immutable inputs and diagnostics used.</summary>
public sealed record DecisionEngineResult
{
    public Decision Decision { get; }

    public DecisionEngineInput Inputs { get; }

    public string RulesVersion { get; }

    public string ConfigurationVersion { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public DecisionEngineInput UsedInputs => Inputs;

    [JsonIgnore]
    public string DecisionInputSnapshotJson => CoreJsonSerializer.Serialize(Inputs);

    [JsonConstructor]
    public DecisionEngineResult(Decision decision, DecisionEngineInput inputs,
        string rulesVersion, string configurationVersion, ImmutableValueList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(inputs);
        ContractGuard.RequireText(rulesVersion, nameof(rulesVersion));
        ContractGuard.RequireText(configurationVersion, nameof(configurationVersion));
        ArgumentNullException.ThrowIfNull(reasons);
        Decision = decision;
        Inputs = inputs;
        RulesVersion = rulesVersion;
        ConfigurationVersion = configurationVersion;
        Reasons = new ImmutableValueList<string>(reasons);
    }
}
