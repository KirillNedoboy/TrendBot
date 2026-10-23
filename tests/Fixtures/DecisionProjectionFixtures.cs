using TradingBot.Core;

namespace TradingBot.Core.Tests;

public static class DecisionProjectionFixtures
{
    public static DecisionProjectionSnapshot CreateProjection(
        DecisionEngineProfile profile, bool blocked = false)
    {
        DecisionEngineConfiguration decisionConfiguration = CreateDecisionConfiguration(profile, blocked);
        return DecisionProjectionSnapshot.Create(CreateResult(profile, blocked),
            CreateLifecycleConfiguration(), decisionConfiguration);
    }

    public static DecisionEngineResult CreateResult(DecisionEngineProfile profile,
        bool blocked)
    {
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            new InstrumentId("BTCUSDT"), SetupType.BoundaryRejection, Direction.Long,
            "projection-anchor", new UtcTimestamp(DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            "context-v1");
        SetupLifecycleConfiguration lifecycleConfiguration = CreateLifecycleConfiguration();
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity,
            lifecycleConfiguration) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = evidence.Cursor,
            ProcessedAvailabilityTime = evidence.Cursor.AsOf,
            ProcessedEvents = new ImmutableValueList<SetupLifecycleEventReceipt>([])
        };
        DecisionEngineConfiguration decisionConfiguration = CreateDecisionConfiguration(profile, blocked);
        ImmutableValueList<DecisionEvidence> lanes = blocked
            ? new ImmutableValueList<DecisionEvidence>(Array.Empty<DecisionEvidence>())
            : new ImmutableValueList<DecisionEvidence>([
                new DecisionEvidence(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm,
                    "lane-b", "lane-b-v1", evidence.Cursor, "fixture")]);
        return DecisionEngine.Evaluate(new DecisionEngineInput(
            $"decision-{profile}-{blocked}", lifecycle, evidence, lanes, "fixture"),
            decisionConfiguration);
    }

    public static SetupLifecycleConfiguration CreateLifecycleConfiguration() => new(
        "lifecycle-rules-v1", "lifecycle-config-v1", requireExternalPlan: true,
        requireConfirmationWindow: true);

    public static DecisionEngineConfiguration CreateDecisionConfiguration(
        DecisionEngineProfile profile, bool blocked = false) => new(
        "decision-rules-v1", "decision-config-v1", profile,
        requireLaneB: blocked, requireLaneC: false, allowNeutralLaneB: false,
        allowNeutralLaneC: true);
}
