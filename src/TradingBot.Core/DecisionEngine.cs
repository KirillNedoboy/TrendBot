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
            reasons.Add(DecisionReasonCodes.LaneANotReady);
        }

        if (lifecycle.State != SetupState.EntryArmed)
        {
            reasons.Add(DecisionReasonCodes.LifecycleNotEntryArmed);
        }

        if (lifecycle.RecoveryRequired)
        {
            reasons.Add(DecisionReasonCodes.LifecycleRecoveryRequired);
        }

        if (!CursorIsVisible(laneA.Cursor, lifecycle.ProcessedCursor))
        {
            reasons.Add(DecisionReasonCodes.LaneAFutureInput);
        }
        else if (CursorIsStale(laneA.Cursor, lifecycle.ProcessedCursor))
        {
            reasons.Add(DecisionReasonCodes.LaneAStaleInput);
        }

        foreach (DecisionEvidence evidence in input.LaneEvidence)
        {
            if (!CursorIsVisible(evidence.Cursor, lifecycle.ProcessedCursor))
            {
                reasons.Add(DecisionReasonCodes.FutureInput(evidence.Lane));
            }
            else if (CursorIsStale(evidence.Cursor, lifecycle.ProcessedCursor))
            {
                reasons.Add(DecisionReasonCodes.StaleInput(evidence.Lane));
            }

            if (evidence.Status == DecisionEvidenceStatus.Veto)
            {
                reasons.Add(DecisionReasonCodes.Veto(evidence.Lane));
            }
            else if (evidence.Status == DecisionEvidenceStatus.Unknown)
            {
                reasons.Add(DecisionReasonCodes.Unknown(evidence.Lane));
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
                ? DecisionReasonCodes.ShadowProfile
                : DecisionReasonCodes.DecisionAllow);
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
                reasons.Add(DecisionReasonCodes.Missing(lane));
            }

            return;
        }

        if (selected.Status == DecisionEvidenceStatus.Neutral && required && !allowNeutral)
        {
            reasons.Add(DecisionReasonCodes.NeutralRequiredConfirm(lane));
        }

        if (selected.Status == DecisionEvidenceStatus.Unknown)
        {
            reasons.Add(DecisionReasonCodes.Unknown(lane));
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
