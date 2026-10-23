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

    [JsonConstructor]
    public DecisionEvidence(DecisionLane lane, DecisionEvidenceStatus status,
        string evidenceId, string algorithmVersion, CausalEventCursor cursor,
        string provenance)
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
