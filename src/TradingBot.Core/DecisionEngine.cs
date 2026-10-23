namespace TradingBot.Core;

/// <summary>
/// Side-effect-free strategy decision boundary. It consumes setup/lane evidence,
/// creates only the existing Decision contract, and never creates risk or order
/// records.
/// </summary>
public sealed class DecisionEngine
{
    private readonly DecisionEngineConfiguration _configuration;

    public DecisionEngine(DecisionEngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
    }

    public DecisionEngineResult Evaluate(DecisionEngineInput input) =>
        Evaluate(input, _configuration);

    public static DecisionEngineResult Evaluate(DecisionEngineInput input,
        DecisionEngineConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);

        List<string> reasons = [];
        SetupLifecycleSnapshot lifecycle = input.Lifecycle;
        SetupEvidenceSnapshot laneA = input.LaneAEvidence;

        if (!laneA.IsReady)
        {
            reasons.Add("lane-a-not-ready");
        }

        if (lifecycle.State != SetupState.EntryArmed)
        {
            reasons.Add("lifecycle-not-entry-armed");
        }

        if (lifecycle.RecoveryRequired)
        {
            reasons.Add("lifecycle-recovery-required");
        }

        if (!CursorIsVisible(laneA.Cursor, lifecycle.ProcessedCursor))
        {
            reasons.Add("lane-a-future-input");
        }
        else if (CursorIsStale(laneA.Cursor, lifecycle.ProcessedCursor))
        {
            reasons.Add("lane-a-stale-input");
        }

        foreach (DecisionEvidence evidence in input.LaneEvidence)
        {
            if (!CursorIsVisible(evidence.Cursor, lifecycle.ProcessedCursor))
            {
                reasons.Add($"{LaneName(evidence.Lane)}-future-input");
            }
            else if (CursorIsStale(evidence.Cursor, lifecycle.ProcessedCursor))
            {
                reasons.Add($"{LaneName(evidence.Lane)}-stale-input");
            }

            if (evidence.Status == DecisionEvidenceStatus.Veto)
            {
                reasons.Add($"{LaneName(evidence.Lane)}-veto");
            }
            else if (evidence.Status == DecisionEvidenceStatus.Unknown)
            {
                reasons.Add($"{LaneName(evidence.Lane)}-unknown");
            }
        }

        EvaluateLaneRequirement(DecisionLane.LaneB, configuration.RequireLaneB,
            configuration.AllowNeutralLaneB, input.LaneEvidence, reasons);
        EvaluateLaneRequirement(DecisionLane.LaneC, configuration.RequireLaneC,
            configuration.AllowNeutralLaneC, input.LaneEvidence, reasons);

        DecisionOutcome outcome = reasons.Count != 0
            ? DecisionOutcome.Block
            : configuration.Profile == DecisionEngineProfile.Shadow
                ? DecisionOutcome.Shadow
                : DecisionOutcome.Allow;
        if (reasons.Count == 0)
        {
            reasons.Add(outcome == DecisionOutcome.Shadow
                ? "shadow-profile"
                : "decision-allow");
        }

        Decision decision = new(input.DecisionId, lifecycle.Identity.Fingerprint,
            outcome, reasons);
        return new DecisionEngineResult(decision, input, configuration.RulesVersion,
            configuration.ConfigurationVersion, new ImmutableValueList<string>(reasons));
    }

    private static void EvaluateLaneRequirement(DecisionLane lane, bool required,
        bool allowNeutral, ImmutableValueList<DecisionEvidence> evidence,
        List<string> reasons)
    {
        DecisionEvidence? selected = evidence.LastOrDefault(item => item.Lane == lane);
        if (selected is null)
        {
            if (required)
            {
                reasons.Add($"{LaneName(lane)}-missing");
            }

            return;
        }

        if (selected.Status == DecisionEvidenceStatus.Neutral && required && !allowNeutral)
        {
            reasons.Add($"{LaneName(lane)}-neutral-required-confirm");
        }

        if (selected.Status == DecisionEvidenceStatus.Unknown)
        {
            reasons.Add($"{LaneName(lane)}-unknown");
        }
    }

    private static bool CursorIsVisible(CausalEventCursor candidate,
        CausalEventCursor lifecycle)
    {
        int asOf = candidate.AsOf.Value.CompareTo(lifecycle.AsOf.Value);
        return asOf < 0 || (asOf == 0
            && candidate.MaxStableSequence <= lifecycle.MaxStableSequence);
    }

    private static bool CursorIsStale(CausalEventCursor candidate,
        CausalEventCursor lifecycle)
    {
        int asOf = candidate.AsOf.Value.CompareTo(lifecycle.AsOf.Value);
        return asOf < 0 || (asOf == 0
            && candidate.MaxStableSequence < lifecycle.MaxStableSequence);
    }

    private static string LaneName(DecisionLane lane) => lane switch
    {
        DecisionLane.LaneA => "lane-a",
        DecisionLane.LaneB => "lane-b",
        DecisionLane.LaneC => "lane-c",
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane,
            "The decision lane is unsupported.")
    };
}
