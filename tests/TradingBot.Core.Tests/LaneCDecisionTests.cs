using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneCDecisionTests
{
    private static readonly LaneCShadowStatus[] ShadowStatuses =
    [
        LaneCShadowStatus.Confirm,
        LaneCShadowStatus.Neutral,
        LaneCShadowStatus.Veto,
        LaneCShadowStatus.InsufficientData
    ];

    [Fact]
    public void ShadowStatusMapsToExistingDecisionEvidenceWithCompleteTrace()
    {
        LaneCShadowResult[] results = ShadowStatuses.Select(static status => Shadow(status))
            .ToArray();
        DecisionEvidence[] evidence = results.Select(result =>
            DecisionEvidence.FromLaneCShadow(result, "lane-c-capture")).ToArray();

        Assert.Equal(new[]
        {
            DecisionEvidenceStatus.Confirm,
            DecisionEvidenceStatus.Neutral,
            DecisionEvidenceStatus.Veto,
            DecisionEvidenceStatus.Unknown
        }, evidence.Select(static item => item.Status));
        Assert.All(evidence, static item =>
        {
            Assert.Equal(DecisionLane.LaneC, item.Lane);
            Assert.NotNull(item.LaneCMetadata);
            Assert.Equal(item.EvidenceId, item.LaneCMetadata!.ShadowResultIdentity);
            Assert.Equal("lane-c-input", item.LaneCMetadata.InputIdentity);
            Assert.Equal("lane-c-rules-v1", item.LaneCMetadata.RulesVersion);
            Assert.Contains("fixture-reason", item.LaneCMetadata.Reasons);
        });

        string json = CoreJsonSerializer.Serialize(evidence[^1]);
        DecisionEvidence restored = CoreJsonSerializer.Deserialize<DecisionEvidence>(json);
        Assert.Equal(evidence[^1], restored);
        Assert.Equal(evidence[^1].LaneCMetadata, restored.LaneCMetadata);
    }

    [Fact]
    public void RequiredLaneCInsufficientEvidenceBlocksDecision()
    {
        SetupEvidenceSnapshot laneA = LifecycleInputFixtures.All[0].Evidence;
        DecisionEvidence unavailable = DecisionEvidence.FromLaneCShadow(
            Shadow(LaneCShadowStatus.InsufficientData, laneA.Cursor), "lane-c-capture");
        DecisionEngineResult result = DecisionEngine.Evaluate(new DecisionEngineInput(
            "lane-c-required", Lifecycle(laneA), laneA, [unavailable], "fixture"),
            DecisionConfiguration(requireLaneC: true, profile: DecisionEngineProfile.Allow));

        Assert.Equal(DecisionOutcome.Block, result.Decision.Outcome);
        Assert.Contains(DecisionReasonCodes.LaneCUnknown, result.Reasons);
        Assert.NotNull(result.Inputs.LaneEvidence[^1].LaneCMetadata);
        Assert.Equal("result-InsufficientData",
            result.Inputs.LaneEvidence[^1].LaneCMetadata!.ShadowResultIdentity);
    }

    [Fact]
    public void ConfirmedLaneCCanYieldShadowDecisionWithoutOrderSideEffects()
    {
        SetupEvidenceSnapshot laneA = LifecycleInputFixtures.All[0].Evidence;
        DecisionEvidence laneC = DecisionEvidence.FromLaneCShadow(
            Shadow(LaneCShadowStatus.Confirm, laneA.Cursor), "lane-c-capture");
        DecisionEngineResult result = DecisionEngine.Evaluate(new DecisionEngineInput(
            "lane-c-shadow", Lifecycle(laneA), laneA, [laneC], "fixture"),
            DecisionConfiguration(requireLaneC: true, profile: DecisionEngineProfile.Shadow));

        Assert.Equal(DecisionOutcome.Shadow, result.Decision.Outcome);
        Assert.DoesNotContain(result.Inputs.LaneEvidence,
            static item => item.Lane != DecisionLane.LaneC);
        Assert.DoesNotContain(typeof(OrderIntent), result.GetType().GetProperties()
            .Select(static property => property.PropertyType));
    }

    private static LaneCShadowResult Shadow(LaneCShadowStatus status,
        CausalEventCursor? cursor = null) => new(LaneCShadowResult.CurrentAlgorithmVersion,
        $"result-{status}", "lane-c-input", "lane-c-rules-v1", status,
        cursor ?? new CausalEventCursor(new UtcTimestamp(DateTimeOffset.Parse(
            "2026-10-24T12:00:05Z", System.Globalization.CultureInfo.InvariantCulture)), 8),
        new ImmutableValueList<string>(status == LaneCShadowStatus.Confirm
            ? ["confirm-rule"] : []), new ImmutableValueList<string>(["fixture-reason"]));

    private static SetupLifecycleSnapshot Lifecycle(SetupEvidenceSnapshot evidence)
    {
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            evidence.InputSequence.InstrumentId, evidence.SetupType, evidence.Direction,
            "lane-c-decision-anchor", evidence.Cursor.AsOf, "context-v1");
        return SetupLifecycleSnapshot.CreateInitial(identity,
            DecisionProjectionFixtures.CreateLifecycleConfiguration()) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = evidence.Cursor,
            ProcessedAvailabilityTime = evidence.Cursor.AsOf
        };
    }

    private static DecisionEngineConfiguration DecisionConfiguration(bool requireLaneC,
        DecisionEngineProfile profile) => new("decision-rules-v1", "lane-c-config-v1",
        profile, requireLaneB: false, requireLaneC: requireLaneC,
        allowNeutralLaneB: true, allowNeutralLaneC: false);
}
