using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SetupLifecycleTests
{
    [Fact]
    public void LifecycleFollowsExplicitExternalFactsAndKeepsEvidenceSeparateFromOrders()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        SetupLifecycleIdentity identity = CreateIdentity("anchor-a");
        SetupLifecycleEngine engine = new(configuration);
        SetupLifecycleSnapshot snapshot = SetupLifecycleSnapshot.CreateInitial(identity, configuration);

        snapshot = engine.Apply(snapshot, Input(identity, "context", SetupLifecycleFactKind.ContextReady, 1)).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "structure", SetupLifecycleFactKind.StructureFormed, 2)).NextSnapshot;
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleTransition evidenceTransition = engine.Apply(snapshot,
            Input(identity, "evidence", SetupLifecycleFactKind.SetupEvidence, 3, evidence));

        Assert.Equal(SetupState.SetupConfirmed, evidenceTransition.NextSnapshot.State);
        Assert.DoesNotContain(typeof(Decision), typeof(SetupLifecycleSnapshot).GetProperties()
            .Select(property => property.PropertyType));
        Assert.DoesNotContain(typeof(OrderIntent), typeof(SetupLifecycleSnapshot).GetProperties()
            .Select(property => property.PropertyType));
        Assert.Equal(SetupLifecycleTransitionStatus.Applied, evidenceTransition.Status);
    }

    [Fact]
    public void DuplicateAndLateInputsDoNotChangeStateOrCursor()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        SetupLifecycleIdentity identity = CreateIdentity("anchor-b");
        SetupLifecycleEngine engine = new(configuration);
        SetupLifecycleSnapshot snapshot = SetupLifecycleSnapshot.CreateInitial(identity, configuration);
        SetupLifecycleInput context = Input(identity, "context", SetupLifecycleFactKind.ContextReady, 1);

        SetupLifecycleTransition first = engine.Apply(snapshot, context);
        SetupLifecycleTransition duplicate = engine.Apply(first.NextSnapshot, context);
        SetupLifecycleTransition late = engine.Apply(first.NextSnapshot,
            Input(identity, "late", SetupLifecycleFactKind.StructureFormed, 0));

        Assert.Equal(SetupLifecycleTransitionStatus.Applied, first.Status);
        Assert.Equal(SetupLifecycleTransitionStatus.Duplicate, duplicate.Status);
        Assert.Equal(SetupLifecycleTransitionStatus.Late, late.Status);
        Assert.Equal(first.NextSnapshot, duplicate.NextSnapshot);
        Assert.Equal(first.NextSnapshot, late.NextSnapshot);
    }

    [Fact]
    public void IdentityConflictRequiresRecoveryAndRecoveryUsesExternalSnapshot()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        SetupLifecycleIdentity identity = CreateIdentity("anchor-c");
        SetupLifecycleEngine engine = new(configuration);
        SetupLifecycleSnapshot snapshot = SetupLifecycleSnapshot.CreateInitial(identity, configuration);
        SetupLifecycleInput original = Input(identity, "same-id", SetupLifecycleFactKind.ContextReady, 1);
        snapshot = engine.Apply(snapshot, original).NextSnapshot;

        SetupLifecycleTransition conflict = engine.Apply(snapshot,
            original with { Provenance = "tampered" });

        Assert.Equal(SetupLifecycleTransitionStatus.IdentityConflict, conflict.Status);
        Assert.True(conflict.NextSnapshot.RecoveryRequired);
        Assert.Equal(SetupState.Suspended, conflict.NextSnapshot.State);

        SetupLifecycleInput recovery = Input(identity, "recover", SetupLifecycleFactKind.Recovery, 2,
            recoverySnapshot: snapshot);
        SetupLifecycleTransition recovered = engine.Apply(conflict.NextSnapshot, recovery);
        Assert.Equal(SetupLifecycleTransitionStatus.RecoveryApplied, recovered.Status);
        Assert.False(recovered.NextSnapshot.RecoveryRequired);
        Assert.Equal(snapshot.State, recovered.NextSnapshot.State);
    }

    [Fact]
    public void NewIdentityRequiresTerminalStateAndExplicitReset()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        SetupLifecycleIdentity identity = CreateIdentity("anchor-d");
        SetupLifecycleIdentity nextIdentity = CreateIdentity("anchor-e");
        SetupLifecycleEngine engine = new(configuration);
        SetupLifecycleSnapshot snapshot = SetupLifecycleSnapshot.CreateInitial(identity, configuration);

        SetupLifecycleTransition rejected = engine.Apply(snapshot,
            Input(nextIdentity, "reset", SetupLifecycleFactKind.StructureReset, 1));
        Assert.Equal(SetupLifecycleTransitionStatus.Rejected, rejected.Status);

        snapshot = snapshot with { State = SetupState.Terminal };
        SetupLifecycleTransition reset = engine.Apply(snapshot,
            Input(nextIdentity, "reset", SetupLifecycleFactKind.StructureReset, 2));
        Assert.Equal(SetupLifecycleTransitionStatus.Applied, reset.Status);
        Assert.Equal(nextIdentity, reset.NextSnapshot.Identity);
        Assert.Equal(SetupState.Observing, reset.NextSnapshot.State);
    }

    [Fact]
    public async Task ProcessorSerializesOneInstrumentButAllowsDifferentInstruments()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        InstrumentLifecycleProcessor processor = new(configuration);
        SetupLifecycleIdentity first = CreateIdentity("anchor-f");
        SetupLifecycleIdentity second = CreateIdentity("anchor-g", "ETHUSDT");
        using ManualResetEventSlim releaseFirst = new(false);
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<SetupLifecycleTransition> firstTask = Task.Run(() => processor.Process(
            Input(first, "first", SetupLifecycleFactKind.ContextReady, 1),
            _ => { firstEntered.SetResult(); releaseFirst.Wait(); return true; }));
        await firstEntered.Task;

        Task<SetupLifecycleTransition> secondTask = Task.Run(() => processor.Process(
            Input(second, "second", SetupLifecycleFactKind.ContextReady, 1),
            _ => true));
        Task completed = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Same(secondTask, completed);
        Assert.Equal(SetupLifecycleTransitionStatus.Applied, (await secondTask).Status);
        Assert.False(firstTask.IsCompleted);

        releaseFirst.Set();
        Assert.Equal(SetupLifecycleTransitionStatus.Applied, (await firstTask).Status);
        Assert.Equal(SetupState.ContextReady, processor.GetSnapshot(first.InstrumentId).State);
    }
    [Fact]
    public void DecisionEngineFailsClosedAndKeepsRiskAndExecutionSeparate()
    {
        SetupLifecycleConfiguration lifecycleConfiguration = CreateConfiguration();
        DecisionEngineConfiguration decisionConfiguration = new(
            "decision-rules-v1", "decision-config-v1", DecisionEngineProfile.Allow,
            requireLaneB: true, requireLaneC: false, allowNeutralLaneB: false,
            allowNeutralLaneC: true);
        SetupLifecycleIdentity identity = CreateIdentity("anchor-h");
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity, lifecycleConfiguration)
            with
        { State = SetupState.EntryArmed, ProcessedCursor = evidence.Cursor };

        DecisionEngineResult blocked = DecisionEngine.Evaluate(
            new DecisionEngineInput("decision-1", lifecycle, evidence,
                [], "test-provenance"), decisionConfiguration);
        Assert.Equal(DecisionOutcome.Block, blocked.Decision.Outcome);

        DecisionEngineResult allowed = DecisionEngine.Evaluate(
            new DecisionEngineInput("decision-2", lifecycle, evidence,
                [new DecisionEvidence(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm,
                    "lane-b-1", "lane-b-v1", evidence.Cursor, "fixture")],
                "test-provenance"), decisionConfiguration);
        Assert.Equal(DecisionOutcome.Allow, allowed.Decision.Outcome);
        Assert.Equal(allowed, CoreJsonSerializer.Deserialize<DecisionEngineResult>(
            CoreJsonSerializer.Serialize(allowed)));
        Assert.DoesNotContain(typeof(RiskDecision), typeof(DecisionEngineResult).GetProperties()
            .Select(property => property.PropertyType));
        Assert.DoesNotContain("orderIntent", CoreJsonSerializer.Serialize(allowed));
    }

    [Fact]
    public void ShadowProfileProducesShadowWithoutAuthorizingExecution()
    {
        SetupLifecycleConfiguration lifecycleConfiguration = CreateConfiguration();
        DecisionEngineConfiguration decisionConfiguration = new(
            "decision-rules-v1", "decision-config-v1", DecisionEngineProfile.Shadow,
            requireLaneB: false, requireLaneC: false, allowNeutralLaneB: true,
            allowNeutralLaneC: true);
        SetupLifecycleIdentity identity = CreateIdentity("anchor-i");
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity, lifecycleConfiguration)
            with
        { State = SetupState.EntryArmed, ProcessedCursor = evidence.Cursor };
        DecisionEngineResult result = DecisionEngine.Evaluate(
            new DecisionEngineInput("decision-shadow", lifecycle, evidence, [], "fixture"),
            decisionConfiguration);

        Assert.Equal(DecisionOutcome.Shadow, result.Decision.Outcome);
    }

    [Fact]
    public void DecisionRuleTableBlocksVetoUnknownMissingAndFutureEvidence()
    {
        SetupLifecycleConfiguration lifecycleConfiguration = CreateConfiguration();
        DecisionEngineConfiguration decisionConfiguration = new(
            "decision-rules-v1", "decision-config-v1", DecisionEngineProfile.Allow,
            requireLaneB: true, requireLaneC: true, allowNeutralLaneB: false,
            allowNeutralLaneC: true);
        SetupLifecycleIdentity identity = CreateIdentity("anchor-m");
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity,
            lifecycleConfiguration) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = evidence.Cursor
        };

        DecisionEvidence Veto = new(DecisionLane.LaneC, DecisionEvidenceStatus.Veto,
            "veto", "lane-c-v1", evidence.Cursor, "fixture");
        DecisionEngineResult veto = DecisionEngine.Evaluate(new DecisionEngineInput(
            "decision-veto", lifecycle, evidence,
            [new DecisionEvidence(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm,
                "b", "lane-b-v1", evidence.Cursor, "fixture"), Veto], "fixture"),
            decisionConfiguration);
        Assert.Equal(DecisionOutcome.Block, veto.Decision.Outcome);
        Assert.Contains("lane-c-veto", veto.Reasons);

        DecisionEvidence future = new(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm,
            "future", "lane-b-v1", new CausalEventCursor(new UtcTimestamp(
                evidence.Cursor.AsOf.Value.AddMinutes(1)), evidence.Cursor.MaxStableSequence + 1),
            "fixture");
        DecisionEngineResult futureResult = DecisionEngine.Evaluate(new DecisionEngineInput(
            "decision-future", lifecycle, evidence, [future], "fixture"),
            decisionConfiguration);
        Assert.Equal(DecisionOutcome.Block, futureResult.Decision.Outcome);
        Assert.Contains("lane-b-future-input", futureResult.Reasons);

        DecisionEvidence stale = new(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm,
            "stale", "lane-b-v1", new CausalEventCursor(new UtcTimestamp(
                evidence.Cursor.AsOf.Value.AddMinutes(-1)), 0), "fixture");
        DecisionEngineResult staleResult = DecisionEngine.Evaluate(new DecisionEngineInput(
            "decision-stale", lifecycle, evidence, [stale], "fixture"),
            decisionConfiguration);
        Assert.Equal(DecisionOutcome.Block, staleResult.Decision.Outcome);
        Assert.Contains("lane-b-stale-input", staleResult.Reasons);
    }

    [Fact]
    public void StrictLifecycleJsonRejectsUnknownFields()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        SetupLifecycleIdentity identity = CreateIdentity("anchor-n");
        SetupLifecycleInput input = Input(identity, "strict", SetupLifecycleFactKind.ContextReady, 1);
        string json = CoreJsonSerializer.Serialize(input);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<SetupLifecycleInput>(
            json.TrimEnd('}') + ",\"authorized\":true}"));
        Assert.Equal(configuration, CoreJsonSerializer.Deserialize<SetupLifecycleConfiguration>(
            CoreJsonSerializer.Serialize(configuration)));
    }

    [Fact]
    public void AtomicCommitFailureLeavesProcessorStateUnchanged()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        InstrumentLifecycleProcessor processor = new(configuration);
        SetupLifecycleIdentity identity = CreateIdentity("anchor-j");
        SetupLifecycleTransition result = processor.Process(
            Input(identity, "context", SetupLifecycleFactKind.ContextReady, 1),
            _ => false);

        Assert.Equal(SetupLifecycleTransitionStatus.CommitRejected, result.Status);
        Assert.Equal(SetupState.Observing, processor.GetSnapshot(identity.InstrumentId).State);
    }

    [Fact]
    public void LifecycleInputsSnapshotsAndTransitionsRoundTripCanonically()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        SetupLifecycleIdentity identity = CreateIdentity("anchor-k");
        SetupLifecycleSnapshot snapshot = SetupLifecycleSnapshot.CreateInitial(identity,
            configuration);
        SetupLifecycleInput input = Input(identity, "serial", SetupLifecycleFactKind.ContextReady, 1);
        SetupLifecycleTransition transition = new SetupLifecycleEngine(configuration).Apply(
            snapshot, input);

        Assert.Equal(CoreJsonSerializer.Serialize(input), CoreJsonSerializer.Serialize(
            CoreJsonSerializer.Deserialize<SetupLifecycleInput>(CoreJsonSerializer.Serialize(input))));
        Assert.Equal(CoreJsonSerializer.Serialize(transition.NextSnapshot), CoreJsonSerializer.Serialize(
            CoreJsonSerializer.Deserialize<SetupLifecycleSnapshot>(
                CoreJsonSerializer.Serialize(transition.NextSnapshot))));
        Assert.Equal(CoreJsonSerializer.Serialize(transition), CoreJsonSerializer.Serialize(
            CoreJsonSerializer.Deserialize<SetupLifecycleTransition>(
                CoreJsonSerializer.Serialize(transition))));
    }

    [Fact]
    public void FullLifecycleRequiresEachExternalFactInOrder()
    {
        SetupLifecycleConfiguration configuration = CreateConfiguration();
        SetupLifecycleIdentity identity = CreateIdentity("anchor-l");
        SetupLifecycleEngine engine = new(configuration);
        SetupLifecycleSnapshot snapshot = SetupLifecycleSnapshot.CreateInitial(identity,
            configuration);
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleConfirmation confirmation = new(SetupConfirmationLane.LaneB,
            SetupConfirmationStatus.Confirm, "lane-b", "lane-b-v1", evidence.Cursor,
            "fixture");

        snapshot = engine.Apply(snapshot, Input(identity, "context", SetupLifecycleFactKind.ContextReady, 1)).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "structure", SetupLifecycleFactKind.StructureFormed, 2)).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "evidence", SetupLifecycleFactKind.SetupEvidence, 3, evidence)).NextSnapshot;
        Assert.Equal(SetupState.SetupConfirmed, snapshot.State);
        snapshot = engine.Apply(snapshot, Input(identity, "confirm", SetupLifecycleFactKind.Confirmation, 4,
            confirmation: confirmation)).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "plan", SetupLifecycleFactKind.ExternalPlanReady, 5,
            externalPlanId: "plan-1")).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "order", SetupLifecycleFactKind.OrderObserved, 6,
            externalObservationId: "order-1")).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "position", SetupLifecycleFactKind.PositionObserved, 7,
            externalObservationId: "position-1")).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "exit", SetupLifecycleFactKind.ExitObserved, 8,
            externalObservationId: "exit-1")).NextSnapshot;
        snapshot = engine.Apply(snapshot, Input(identity, "cooldown", SetupLifecycleFactKind.CooldownElapsed, 9)).NextSnapshot;

        Assert.Equal(SetupState.Terminal, snapshot.State);
        Assert.Equal(9, snapshot.ProcessedEvents.Count);
    }

    private static SetupLifecycleConfiguration CreateConfiguration() => new(
        "lifecycle-rules-v1", "lifecycle-config-v1", requireExternalPlan: true,
        requireConfirmationWindow: true);

    private static SetupLifecycleIdentity CreateIdentity(string anchor,
        string instrument = "BTCUSDT") => SetupLifecycleIdentity.Create(
        new InstrumentId(instrument), SetupType.BoundaryRejection, Direction.Long,
        anchor, new UtcTimestamp(DateTimeOffset.Parse("2026-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture)), "context-v1");

    private static SetupLifecycleInput Input(SetupLifecycleIdentity identity, string eventId,
        SetupLifecycleFactKind factKind, long sequence, SetupEvidenceSnapshot? evidence = null,
        SetupLifecycleSnapshot? recoverySnapshot = null,
        SetupLifecycleConfirmation? confirmation = null, string? externalPlanId = null,
        string? externalObservationId = null) => new(
        SetupLifecycleInput.CurrentFormatVersion, "fixture-stream", eventId, factKind, identity,
        new UtcTimestamp(DateTimeOffset.Parse("2030-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture).AddSeconds(sequence)),
        new UtcTimestamp(DateTimeOffset.Parse("2030-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture).AddSeconds(sequence)),
        new UtcTimestamp(DateTimeOffset.Parse("2030-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture).AddSeconds(sequence)),
        sequence, "lifecycle-rules-v1", "lifecycle-config-v1", "fixture", evidence,
        confirmation, externalPlanId, externalObservationId, null, null, recoverySnapshot);
}
