using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SignalScenarioProjectionTests
{
    private static readonly UtcTimestamp BaseTime = At("2026-11-09T00:00:00Z");

    [Fact]
    public void UnknownRequiredPolicyBlocksProjectionWithoutCreatingAnAlert()
    {
        SignalScenarioPolicySet incomplete = new(
            SignalScenarioPolicySet.CurrentFormatVersion,
            new SignalScenarioExpiryPolicy(1, "expiry-v1", null),
            new SignalScenarioRearmPolicy(1, "rearm-v1", TimeSpan.Zero, true),
            null);
        SignalScenarioProjectionEngine engine = new(incomplete);
        SignalScenarioProjectionSnapshot initial = engine.CreateInitialSnapshot();
        SignalScenarioProjectionTransition transition = engine.Apply(initial,
            Confirm("signal-1", Anchor("anchor-1"), 2, 2));

        Assert.False(incomplete.IsReady);
        Assert.Contains("scenario-expiry-duration-unknown", incomplete.UnknownReasons);
        Assert.Contains("scenario-invalidation-policy-missing", incomplete.UnknownReasons);
        Assert.Equal(SignalScenarioTransitionStatus.UnknownPolicy, transition.Status);
        Assert.Empty(transition.EmittedEvents);
        Assert.Same(initial, transition.NextSnapshot);
    }

    [Fact]
    public void RepeatedEventAndSameScenarioDoNotCreateAnotherAlert()
    {
        SignalScenarioProjectionEngine engine = new(Policies());
        SignalScenarioAnchor originalAnchor = Anchor("anchor-1", anchorMinute: 2,
            anchorSequence: 2);
        SignalScenarioProjectionTransition created = engine.Apply(
            engine.CreateInitialSnapshot(), Confirm("signal-1", originalAnchor, 3, 3));
        SignalScenarioProjectionTransition duplicate = engine.Apply(created.NextSnapshot,
            Confirm("signal-1", originalAnchor, 3, 3));
        SignalScenarioProjectionTransition conflict = engine.Apply(created.NextSnapshot,
            Confirm("signal-1", originalAnchor, 4, 4));
        Assert.Equal(SignalScenarioTransitionStatus.IdentityConflict, conflict.Status);
        Assert.Same(created.NextSnapshot, conflict.NextSnapshot);
        Assert.Empty(conflict.EmittedEvents);
        SignalScenarioAnchor sameLogicalAnchor = Anchor("anchor-1", anchorMinute: 4,
            anchorSequence: 4);
        SignalScenarioProjectionTransition continued = engine.Apply(
            duplicate.NextSnapshot, Confirm("signal-2", sameLogicalAnchor, 5, 5));

        Assert.Equal(SignalScenarioTransitionStatus.Applied, created.Status);
        Assert.Single(created.EmittedEvents);
        Assert.Equal(SignalScenarioEventKind.AlertCreated, created.EmittedEvents[0].Kind);
        Assert.Equal(SignalScenarioTransitionStatus.Duplicate, duplicate.Status);
        Assert.Empty(duplicate.EmittedEvents);
        Assert.Equal(SignalScenarioTransitionStatus.Continued, continued.Status);
        Assert.Single(continued.EmittedEvents);
        Assert.Equal(SignalScenarioEventKind.Continued, continued.EmittedEvents[0].Kind);
        Assert.Equal(originalAnchor.Identity.SignalId, sameLogicalAnchor.Identity.SignalId);
        Assert.Single(continued.NextSnapshot.Events, item =>
            item.Kind == SignalScenarioEventKind.AlertCreated);
        Assert.Equal(originalAnchor.Identity.SignalId,
            continued.NextSnapshot.Episodes.Single().Identity.SignalId);
    }

    [Fact]
    public void LateOppositeConfirmationCannotRewriteStateButOnTimeOppositeCancelsLinkedAlert()
    {
        SignalScenarioProjectionEngine engine = new(Policies());
        SignalScenarioProjectionTransition created = engine.Apply(
            engine.CreateInitialSnapshot(), Confirm("long", Anchor("long-anchor"), 10, 10));
        string originalAlertId = created.EmittedEvents.Single().EventId;
        string originalSignalId = created.EmittedEvents.Single().SignalId;
        SignalScenarioProjectionSnapshot beforeLate = created.NextSnapshot;
        SignalScenarioProjectionTransition late = engine.Apply(beforeLate,
            Confirm("late-short", Anchor("late-anchor", direction: Direction.Short,
                anchorMinute: 8, anchorSequence: 8), 9, 9));

        Assert.Equal(SignalScenarioTransitionStatus.Late, late.Status);
        Assert.Same(beforeLate, late.NextSnapshot);
        Assert.Empty(late.EmittedEvents);

        SignalScenarioProjectionTransition opposite = engine.Apply(beforeLate,
            Confirm("short", Anchor("short-anchor", direction: Direction.Short,
                anchorMinute: 10, anchorSequence: 10), 11, 11));
        SignalScenarioEvent canceled = Assert.Single(opposite.EmittedEvents,
            item => item.Kind == SignalScenarioEventKind.Canceled);
        SignalScenarioEvent newAlert = Assert.Single(opposite.EmittedEvents,
            item => item.Kind == SignalScenarioEventKind.AlertCreated);

        Assert.Equal(SignalScenarioTransitionStatus.Applied, opposite.Status);
        Assert.Equal(originalSignalId, canceled.SignalId);
        Assert.Equal(originalAlertId, canceled.EpisodeAlertEventId);
        Assert.Equal("opposite-direction-confirmed", canceled.ReasonCode);
        Assert.Equal(SignalScenarioEpisodeState.Canceled,
            opposite.NextSnapshot.Episodes.Single(item =>
                item.Identity.SignalId == originalSignalId).State);
        Assert.NotEqual(originalSignalId, newAlert.SignalId);
    }

    [Fact]
    public void DifferentSetupFamiliesLinkOverlappingActiveEpisodes()
    {
        SignalScenarioProjectionEngine engine = new(Policies());
        SignalScenarioProjectionTransition boundary = engine.Apply(
            engine.CreateInitialSnapshot(), Confirm("boundary", Anchor("anchor-b",
                family: SetupType.BoundaryRejection), 10, 10));
        SignalScenarioProjectionTransition center = engine.Apply(boundary.NextSnapshot,
            Confirm("center", Anchor("anchor-c", family: SetupType.CenterRetest,
                anchorMinute: 10, anchorSequence: 10), 11, 11));
        SignalScenarioEvent link = Assert.Single(center.EmittedEvents,
            item => item.Kind == SignalScenarioEventKind.OverlapLinked);
        SignalScenarioEpisode first = center.NextSnapshot.Episodes.Single(item =>
            item.Identity.SignalId == link.SignalId);
        SignalScenarioEpisode second = center.NextSnapshot.Episodes.Single(item =>
            item.Identity.SignalId == link.RelatedSignalId);

        Assert.Equal(SignalScenarioTransitionStatus.Applied, center.Status);
        Assert.Equal(first.AlertEventId, link.EpisodeAlertEventId);
        Assert.Equal(second.AlertEventId, link.RelatedEpisodeAlertEventId);
        Assert.NotEqual(first.Identity.Family, second.Identity.Family);
        Assert.Equal(first.Identity.InstrumentId, second.Identity.InstrumentId);
        Assert.Equal(first.Identity.Direction, second.Identity.Direction);
    }

    [Fact]
    public void ExpiryAndRearmUseExplicitInclusiveBoundariesAndNewAnchorIdentity()
    {
        SignalScenarioProjectionEngine engine = new(Policies(
            lifetime: TimeSpan.FromMinutes(5), rearmDelay: TimeSpan.FromMinutes(2)));
        SignalScenarioProjectionTransition first = engine.Apply(
            engine.CreateInitialSnapshot(), Confirm("first", Anchor("anchor-first"),
                10, 10));
        SignalScenarioEvent alert = first.EmittedEvents.Single();
        SignalScenarioProjectionTransition expiry = engine.Apply(first.NextSnapshot,
            SignalScenarioInput.Invalidate("public", "expiry-clock",
                alert.SignalId, "boundary-probe", AtMinute(15), AtMinute(15), 11));
        SignalScenarioEvent expired = Assert.Single(expiry.EmittedEvents);

        Assert.Equal(SignalScenarioEventKind.Expired, expired.Kind);
        Assert.Equal(alert.EventId, expired.EpisodeAlertEventId);
        Assert.Equal(AtMinute(15), expired.EffectiveAt);
        Assert.Equal(SignalScenarioEpisodeState.Expired,
            expiry.NextSnapshot.Episodes.Single().State);

        SignalScenarioProjectionTransition tooEarly = engine.Apply(expiry.NextSnapshot,
            Confirm("early-rearm", Anchor("anchor-early", anchorMinute: 16,
                anchorSequence: 12), 16, 13));
        Assert.Equal(SignalScenarioTransitionStatus.Rejected, tooEarly.Status);
        Assert.Equal("rearm-delay-not-elapsed", tooEarly.ReasonCode);
        Assert.DoesNotContain(tooEarly.EmittedEvents, item =>
            item.Kind == SignalScenarioEventKind.AlertCreated);

        SignalScenarioProjectionTransition atBoundary = engine.Apply(
            tooEarly.NextSnapshot, Confirm("boundary-rearm", Anchor("anchor-new",
                anchorMinute: 16, anchorSequence: 14), 17, 15));
        SignalScenarioEvent rearmed = Assert.Single(atBoundary.EmittedEvents);

        Assert.Equal(SignalScenarioTransitionStatus.Applied, atBoundary.Status);
        Assert.Equal(SignalScenarioEventKind.AlertCreated, rearmed.Kind);
        Assert.NotEqual(alert.SignalId, rearmed.SignalId);
        Assert.Equal("anchor-new", atBoundary.NextSnapshot.Episodes.Single(item =>
            item.State == SignalScenarioEpisodeState.Active).Anchor.AnchorEventId);
    }

    [Fact]
    public void ContinuousAndRestoredProjectionProduceIdenticalImmutableEvents()
    {
        SignalScenarioProjectionEngine engine = new(Policies(
            lifetime: TimeSpan.FromMinutes(5), rearmDelay: TimeSpan.FromMinutes(1)));
        SignalScenarioProjectionSnapshot initial = engine.CreateInitialSnapshot();
        SignalScenarioInput[] inputs =
        [
            Confirm("boundary", Anchor("anchor-b", family: SetupType.BoundaryRejection),
                10, 10),
            Confirm("center", Anchor("anchor-c", family: SetupType.CenterRetest,
                anchorMinute: 10, anchorSequence: 10), 11, 11),
            Confirm("boundary-repeat", Anchor("anchor-b", family: SetupType.BoundaryRejection,
                anchorMinute: 10, anchorSequence: 10), 12, 12),
            SignalScenarioInput.Invalidate("public", "cancel-center",
                Anchor("anchor-c", family: SetupType.CenterRetest).Identity.SignalId,
                "evidence-invalidated", AtMinute(13), AtMinute(13), 13),
            SignalScenarioInput.Invalidate("public", "expire-boundary",
                Anchor("anchor-b", family: SetupType.BoundaryRejection).Identity.SignalId,
                "expiry-probe", AtMinute(15), AtMinute(15), 14)
        ];
        ImmutableValueList<SignalScenarioProjectionTransition> continuous =
            engine.Replay(initial, inputs);
        ImmutableValueList<SignalScenarioProjectionTransition> prefixTransitions =
            engine.Replay(initial, inputs[..3]);
        SignalScenarioProjectionSnapshot prefix =
            prefixTransitions[prefixTransitions.Count - 1].NextSnapshot;
        SignalScenarioProjectionSnapshot restored = CoreJsonSerializer.Deserialize<
            SignalScenarioProjectionSnapshot>(CoreJsonSerializer.Serialize(prefix));
        ImmutableValueList<SignalScenarioProjectionTransition> resumed = engine.Replay(
            restored, inputs[3..]);
        SignalScenarioProjectionSnapshot continuousSnapshot =
            continuous[continuous.Count - 1].NextSnapshot;
        SignalScenarioProjectionSnapshot resumedSnapshot =
            resumed[resumed.Count - 1].NextSnapshot;

        string continuousJson = CoreJsonSerializer.Serialize(continuousSnapshot);
        string resumedJson = CoreJsonSerializer.Serialize(resumedSnapshot);

        Assert.Equal(continuousJson, resumedJson);
        Assert.Equal(continuousSnapshot.Events, resumedSnapshot.Events);
        Assert.Contains(continuousSnapshot.Events, item =>
            item.Kind == SignalScenarioEventKind.Canceled);
        Assert.Contains(continuousSnapshot.Events, item =>
            item.Kind == SignalScenarioEventKind.Expired);
        Assert.Contains(continuousSnapshot.Events, item =>
            item.Kind == SignalScenarioEventKind.OverlapLinked);
        Assert.All(continuousSnapshot.Events, item =>
            Assert.Equal(item.EventId, CoreJsonSerializer.Deserialize<SignalScenarioEvent>(
                CoreJsonSerializer.Serialize(item)).EventId));
    }

    [Fact]
    public void ScenarioInputAndSnapshotRejectMissingOrAlteredContractData()
    {
        SignalScenarioProjectionEngine engine = new(Policies());
        SignalScenarioProjectionTransition created = engine.Apply(
            engine.CreateInitialSnapshot(), Confirm("confirm", Anchor("anchor"), 3, 3));
        JsonObject input = JsonNode.Parse(CoreJsonSerializer.Serialize(
            Confirm("another-confirm", Anchor("another-anchor"), 4, 4)))!.AsObject();
        input.Remove("inputHash");
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<
            SignalScenarioInput>(input.ToJsonString()));

        Assert.Throws<ArgumentException>(() => Confirm("anchor-not-before-event",
            Anchor("same-time-anchor", anchorMinute: 5, anchorSequence: 5), 5, 5));
        JsonObject altered = JsonNode.Parse(CoreJsonSerializer.Serialize(
            created.NextSnapshot))!.AsObject();
        altered["events"]![0]!["signalId"] = new string('0', 64);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<
            SignalScenarioProjectionSnapshot>(altered.ToJsonString()));
    }

    private static SignalScenarioPolicySet Policies(TimeSpan? lifetime = null,
        TimeSpan? rearmDelay = null, bool cancelOnExplicitInvalidation = true,
        bool cancelOnOppositeDirection = true) => new(
        SignalScenarioPolicySet.CurrentFormatVersion,
        new SignalScenarioExpiryPolicy(SignalScenarioExpiryPolicy.CurrentFormatVersion,
            "scenario-expiry-v1", lifetime ?? TimeSpan.FromMinutes(5)),
        new SignalScenarioRearmPolicy(SignalScenarioRearmPolicy.CurrentFormatVersion,
            "scenario-rearm-v1", rearmDelay ?? TimeSpan.FromMinutes(2), true),
        new SignalScenarioInvalidationPolicy(
            SignalScenarioInvalidationPolicy.CurrentFormatVersion,
            "scenario-invalidation-v1", cancelOnExplicitInvalidation,
            cancelOnOppositeDirection));

    private static SignalScenarioAnchor Anchor(string eventId,
        SetupType family = SetupType.BoundaryRejection,
        Direction direction = Direction.Long, int anchorMinute = 1,
        long anchorSequence = 1) => SignalScenarioAnchor.Create(
        new InstrumentId("BTCUSDT"), "BINANCE_USDM", "15m", direction, family,
        "profile-v1", family == SetupType.BoundaryRejection
            ? "boundary-rejection-v1" : "center-retest-v1", eventId,
        AtMinute(anchorMinute), anchorSequence);

    private static SignalScenarioInput Confirm(string eventId,
        SignalScenarioAnchor anchor, int eventMinute, long sequence) =>
        SignalScenarioInput.Confirm("public", eventId, anchor, AtMinute(eventMinute),
            AtMinute(eventMinute), sequence);

    private static UtcTimestamp AtMinute(int minute) => new(BaseTime.Value.AddMinutes(minute));

    private static UtcTimestamp At(string value) => new(DateTimeOffset.Parse(value,
        System.Globalization.CultureInfo.InvariantCulture));
}
