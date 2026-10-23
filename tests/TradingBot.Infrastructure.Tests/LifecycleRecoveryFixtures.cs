using TradingBot.Core;
using TradingBot.Core.Tests;

namespace TradingBot.Infrastructure.Tests;

internal sealed record LifecycleRecoveryFixture(
    SetupLifecycleConfiguration LifecycleConfiguration,
    DecisionEngineConfiguration DecisionConfiguration,
    SetupLifecycleIdentity Identity,
    SetupEvidenceSnapshot Evidence,
    SetupLifecycleSnapshot Initial,
    ImmutableValueList<SetupLifecycleInput> Prefix,
    ImmutableValueList<SetupLifecycleInput> Tail,
    DecisionProjectionSnapshot Projection);

internal static class LifecycleRecoveryFixtures
{
    internal static LifecycleRecoveryFixture Create()
    {
        SetupLifecycleConfiguration lifecycleConfiguration =
            DecisionProjectionFixtures.CreateLifecycleConfiguration();
        DecisionEngineConfiguration decisionConfiguration =
            DecisionProjectionFixtures.CreateDecisionConfiguration(DecisionEngineProfile.Allow);
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            new InstrumentId("BTCUSDT"), SetupType.BoundaryRejection, Direction.Long,
            "recovery-anchor", new UtcTimestamp(DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            "context-v1");
        SetupLifecycleSnapshot initial = SetupLifecycleSnapshot.CreateInitial(identity,
            lifecycleConfiguration);
        UtcTimestamp cursorTime = evidence.Cursor.AsOf;

        ImmutableValueList<SetupLifecycleInput> prefix = new(
        [
            Input(identity, "context", SetupLifecycleFactKind.ContextReady, 9_998,
                cursorTime),
            Input(identity, "structure", SetupLifecycleFactKind.StructureFormed, 9_999,
                cursorTime),
            Input(identity, "evidence", SetupLifecycleFactKind.SetupEvidence, 10_000,
                cursorTime, evidence: evidence),
            Input(identity, "confirmation", SetupLifecycleFactKind.Confirmation, 10_000,
                cursorTime, confirmation: new SetupLifecycleConfirmation(
                    SetupConfirmationLane.LaneB, SetupConfirmationStatus.Confirm,
                    "lane-b", "lane-b-v1", evidence.Cursor, "fixture")),
            Input(identity, "plan", SetupLifecycleFactKind.ExternalPlanReady, 10_000,
                cursorTime, externalPlanId: "plan-1")
        ]);

        SetupLifecycleInput duplicate = prefix[0];
        SetupLifecycleInput late = Input(identity, "late", SetupLifecycleFactKind.StructureFormed,
            1, new UtcTimestamp(cursorTime.Value.AddMinutes(-1)));
        SetupLifecycleInput identityConflict = prefix[0] with { Provenance = "tampered" };
        SetupLifecycleInput recovery = Input(identity, "recovery", SetupLifecycleFactKind.Recovery,
            10_001, new UtcTimestamp(cursorTime.Value.AddSeconds(1)),
            recoverySnapshot: ApplyPrefix(initial, lifecycleConfiguration, prefix));
        ImmutableValueList<SetupLifecycleInput> tail = new(
            [duplicate, late, identityConflict, recovery]);

        SetupLifecycleSnapshot checkpoint = recovery.RecoverySnapshot!;
        DecisionEngineResult result = DecisionEngine.Evaluate(new DecisionEngineInput(
            "recovery-decision", checkpoint, evidence, [], "fixture"), decisionConfiguration);
        DecisionProjectionSnapshot projection = DecisionProjectionSnapshot.Create(result,
            lifecycleConfiguration, decisionConfiguration);

        return new LifecycleRecoveryFixture(lifecycleConfiguration, decisionConfiguration,
            identity, evidence, initial, prefix, tail, projection);
    }

    private static SetupLifecycleSnapshot ApplyPrefix(SetupLifecycleSnapshot initial,
        SetupLifecycleConfiguration configuration, IEnumerable<SetupLifecycleInput> prefix)
    {
        SetupLifecycleEngine engine = new(configuration);
        SetupLifecycleSnapshot current = initial;
        foreach (SetupLifecycleInput input in prefix)
        {
            SetupLifecycleTransition transition = engine.Apply(current, input);
            if (transition.Status != SetupLifecycleTransitionStatus.Applied)
            {
                throw new InvalidOperationException(
                    $"Fixture prefix failed at {input.EventId}: {transition.Status}.");
            }

            current = transition.NextSnapshot;
        }

        return current;
    }

    private static SetupLifecycleInput Input(SetupLifecycleIdentity identity, string eventId,
        SetupLifecycleFactKind factKind, long stableSequence, UtcTimestamp eventTime,
        SetupEvidenceSnapshot? evidence = null,
        SetupLifecycleConfirmation? confirmation = null, string? externalPlanId = null,
        SetupLifecycleSnapshot? recoverySnapshot = null) => new(
        SetupLifecycleInput.CurrentFormatVersion, "recovery-stream", eventId, factKind,
        identity, eventTime, eventTime, eventTime, stableSequence,
        "lifecycle-rules-v1", "lifecycle-config-v1", "fixture", evidence,
        confirmation, externalPlanId, null, null, null, recoverySnapshot);
}
