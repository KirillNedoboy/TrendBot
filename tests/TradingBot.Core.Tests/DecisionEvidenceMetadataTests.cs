using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DecisionEvidenceMetadataTests
{
    [Fact]
    public void ReadyJoinCreatesConfirmEvidenceAndUnreadyQualityMapsToUnknown()
    {
        DecisionEvidence ready = DecisionEvidence.FromLaneBJoin(BuildJoin(), "fixture");
        DecisionEvidence missing = DecisionEvidence.FromLaneBJoin(
            BuildJoin(includeFunding: false), "fixture");
        DecisionEvidence stale = DecisionEvidence.FromLaneBJoin(
            BuildJoin(freshness: TimeSpan.FromSeconds(30)), "fixture");
        DecisionEvidence conflict = DecisionEvidence.FromLaneBJoin(
            BuildJoin(conflict: true), "fixture");

        Assert.Equal(DecisionEvidenceStatus.Confirm, ready.Status);
        Assert.Equal(DecisionEvidenceStatus.Unknown, missing.Status);
        Assert.Equal(DecisionEvidenceStatus.Unknown, stale.Status);
        Assert.Equal(DecisionEvidenceStatus.Unknown, conflict.Status);
        Assert.Contains(missing.LaneBMetadata!.QualityReasons,
            reason => reason.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(stale.LaneBMetadata!.QualityReasons,
            reason => reason.Contains("stale", StringComparison.Ordinal));
        Assert.Contains(conflict.LaneBMetadata!.QualityReasons,
            reason => reason.Contains("conflict", StringComparison.Ordinal));
        Assert.Equal(ready.Cursor, ready.LaneBMetadata!.Cursor);
        Assert.Equal(ready.EvidenceId, ready.LaneBMetadata.ShadowEvidenceId);
    }

    [Fact]
    public void MetadataRoundTripsAndAbsentOptionalMetadataKeepsLegacyJsonProjection()
    {
        DecisionEvidence withMetadata = DecisionEvidence.FromLaneBJoin(BuildJoin(), "fixture");
        string withMetadataJson = CoreJsonSerializer.Serialize(withMetadata);
        DecisionEvidence restored = CoreJsonSerializer.Deserialize<DecisionEvidence>(
            withMetadataJson);
        DecisionEvidence legacy = new(DecisionLane.LaneB, DecisionEvidenceStatus.Confirm,
            "legacy-evidence", "lane-b-shadow-v1", BuildJoin().Cursor, "fixture");
        string legacyJson = CoreJsonSerializer.Serialize(legacy);
        DecisionEvidence legacyRestored = CoreJsonSerializer.Deserialize<DecisionEvidence>(
            legacyJson);

        Assert.Equal(withMetadata, restored);
        Assert.Contains("laneBMetadata", withMetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("laneBMetadata", legacyJson, StringComparison.Ordinal);
        Assert.Equal(legacy, legacyRestored);
    }

    [Fact]
    public void InconsistentMetadataCursorIdentityAndStatusAreRejected()
    {
        LaneBJoinSnapshot join = BuildJoin();
        LaneBDecisionEvidenceMetadata metadata =
            LaneBDecisionEvidenceMetadata.Create(join);
        CausalEventCursor otherCursor = new(
            new UtcTimestamp(join.Cursor.AsOf.Value.AddMinutes(1)),
            join.Cursor.MaxStableSequence + 1);

        LaneBDecisionEvidenceMetadata cursorMismatch = new(
            metadata.FormatVersion, metadata.AlgorithmVersion, metadata.JoinIdentity,
            metadata.ShadowEvidenceId, metadata.ShadowAlgorithmVersion,
            metadata.ShadowStatus, metadata.QualityStatus, metadata.EvidenceStatus,
            otherCursor, metadata.ObservationReferences, metadata.QualityReasons);
        Assert.Throws<ArgumentException>(() => new DecisionEvidence(
            DecisionLane.LaneB, metadata.EvidenceStatus, metadata.ShadowEvidenceId,
            metadata.ShadowAlgorithmVersion, join.Cursor, "fixture", cursorMismatch));
        Assert.Throws<ArgumentException>(() => new LaneBDecisionEvidenceMetadata(
            metadata.FormatVersion, metadata.AlgorithmVersion, "different-join",
            metadata.ShadowEvidenceId, metadata.ShadowAlgorithmVersion,
            metadata.ShadowStatus, metadata.QualityStatus, metadata.EvidenceStatus,
            metadata.Cursor, metadata.ObservationReferences, metadata.QualityReasons));
        Assert.Throws<ArgumentException>(() => new LaneBDecisionEvidenceMetadata(
            metadata.FormatVersion, metadata.AlgorithmVersion, metadata.JoinIdentity,
            metadata.ShadowEvidenceId, metadata.ShadowAlgorithmVersion,
            metadata.ShadowStatus, metadata.QualityStatus, DecisionEvidenceStatus.Unknown,
            metadata.Cursor, metadata.ObservationReferences, metadata.QualityReasons));
    }

    [Fact]
    public void UnknownLaneBQualityBlocksDecisionAndLifecycleRecoveryStillBlocksReadyEvidence()
    {
        LaneBJoinSnapshot missingJoin = BuildJoin(includeFunding: false);
        DecisionEvidence missing = DecisionEvidence.FromLaneBJoin(missingJoin, "fixture");
        SetupEvidenceSnapshot laneA = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleConfiguration lifecycleConfiguration = DecisionProjectionFixtures
            .CreateLifecycleConfiguration();
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            new InstrumentId("BTCUSDT"), SetupType.BoundaryRejection, Direction.Long,
            "decision-metadata", laneA.Cursor.AsOf, "context-v1");
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity,
            lifecycleConfiguration) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = laneA.Cursor
        };
        DecisionEngineConfiguration configuration =
            DecisionProjectionFixtures.CreateDecisionConfiguration(DecisionEngineProfile.Allow,
                blocked: false);

        DecisionEngineResult unknownResult = DecisionEngine.Evaluate(new DecisionEngineInput(
            "unknown-quality", lifecycle, laneA, [missing], "fixture"), configuration);
        DecisionEngineResult recoveryResult = DecisionEngine.Evaluate(new DecisionEngineInput(
            "recovery", lifecycle with { RecoveryRequired = true }, laneA,
            [DecisionEvidence.FromLaneBJoin(BuildJoin(), "fixture")], "fixture"), configuration);

        Assert.Equal(DecisionOutcome.Block, unknownResult.Decision.Outcome);
        Assert.Contains(DecisionReasonCodes.LaneBUnknown, unknownResult.Reasons);
        Assert.Equal(DecisionOutcome.Block, recoveryResult.Decision.Outcome);
        Assert.Contains(DecisionReasonCodes.LifecycleRecoveryRequired, recoveryResult.Reasons);
        Assert.DoesNotContain("riskDecision", unknownResult.DecisionInputSnapshotJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("orderIntent", unknownResult.DecisionInputSnapshotJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataAndDecisionTraceAreDeterministicAcrossJoinPermutations()
    {
        LaneBJoinSnapshot leftJoin = BuildJoin();
        LaneBJoinSnapshot rightJoin = BuildJoin();
        DecisionEvidence left = DecisionEvidence.FromLaneBJoin(leftJoin, "fixture");
        DecisionEvidence right = DecisionEvidence.FromLaneBJoin(rightJoin, "fixture");

        Assert.Equal(left, right);
        Assert.Equal(CoreJsonSerializer.Serialize(left), CoreJsonSerializer.Serialize(right));
        Assert.Equal(left.LaneBMetadata!.QualityReasons, right.LaneBMetadata!.QualityReasons);
    }

    private static LaneBJoinSnapshot BuildJoin(bool includeFunding = true,
        TimeSpan? freshness = null, bool conflict = false)
    {
        InstrumentId instrument = new("BTCUSDT");
        DateTimeOffset baseTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        UtcTimestamp Utc(int offset) => new(baseTime.AddMinutes(offset));
        LaneBFundingObservation[] observations = includeFunding
            ? [new LaneBFundingObservation("funding-source",
                new FundingUpdate("funding-1", instrument, Utc(1), 0.01m), "rate", "periodic",
                "fixture", Utc(1), Utc(1), 1)]
            : [];
        if (conflict)
        {
            observations = [
                observations[0],
                new LaneBFundingObservation("funding-source",
                    new FundingUpdate("funding-1", instrument, Utc(1), 0.02m), "rate",
                    "periodic", "fixture", Utc(1), Utc(1), 1)
            ];
        }

        LaneBInput input = new(instrument, [], observations, "fixture");
        LaneBInputConfiguration inputConfiguration = new(
            "input-v1", null, "rate", "periodic", false, true, false);
        LaneBRuleTable rules = new("rules-v1", [
            new LaneBRule("funding-rule", SetupType.BoundaryRejection, Direction.Long,
                "rule-v1", [new LaneBRuleCondition("funding", LaneBMetricKind.FundingRate,
                    LaneBComparisonOperator.GreaterThan, 0m)], LaneBShadowStatus.Confirm)
        ]);
        CausalEventCursor cursor = new(Utc(2), 2);
        LaneBFreshnessPolicy policy = new(null, freshness ?? TimeSpan.FromMinutes(5), null, null);
        LaneBShadowResult shadow = new LaneBEvaluator(rules).Evaluate(input, cursor,
            inputConfiguration, [], [], SetupType.BoundaryRejection, Direction.Long, policy);
        LaneBQualityState quality = LaneBQualityEvaluator.Evaluate(shadow);
        return LaneBJoinSnapshot.Create(cursor, observations, quality, shadow);
    }
}
