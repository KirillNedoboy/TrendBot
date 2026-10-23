using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DecisionProjectionSnapshotTests
{
    [Theory]
    [InlineData(DecisionEngineProfile.Allow, DecisionOutcome.Allow)]
    [InlineData(DecisionEngineProfile.Allow, DecisionOutcome.Block)]
    [InlineData(DecisionEngineProfile.Shadow, DecisionOutcome.Shadow)]
    public void ProjectionRoundTripPreservesDecisionAndLifecycleCheckpoint(
        DecisionEngineProfile profile, DecisionOutcome expectedOutcome)
    {
        DecisionEngineResult result = DecisionProjectionFixtures.CreateResult(profile,
            expectedOutcome == DecisionOutcome.Block);
        DecisionEngineConfiguration decisionConfiguration = DecisionProjectionFixtures.CreateDecisionConfiguration(profile,
            expectedOutcome == DecisionOutcome.Block);
        SetupLifecycleConfiguration lifecycleConfiguration = DecisionProjectionFixtures.CreateLifecycleConfiguration();

        DecisionProjectionSnapshot snapshot = DecisionProjectionSnapshot.Create(
            result, lifecycleConfiguration, decisionConfiguration);

        Assert.Equal(expectedOutcome, snapshot.Result.Decision.Outcome);
        Assert.Equal(snapshot.Result.Inputs.Lifecycle, snapshot.LifecycleSnapshot);
        Assert.Equal(snapshot.LifecycleIdentity, snapshot.LifecycleSnapshot.Identity);
        Assert.Equal(snapshot.LifecycleSnapshot.ProcessedCursor, snapshot.CausalCursor);
        Assert.Equal(snapshot.LifecycleSnapshot.ProcessedAvailabilityTime,
            snapshot.AvailabilityTime);
        Assert.Equal(snapshot.LifecycleSnapshot.ProcessedEvents, snapshot.Receipts);
        Assert.Equal(snapshot.LifecycleSnapshot.RecoveryRequired,
            snapshot.RecoveryRequired);

        DecisionProjectionSnapshot roundTripped = CoreJsonSerializer.Deserialize<
            DecisionProjectionSnapshot>(CoreJsonSerializer.Serialize(snapshot));

        Assert.Equal(snapshot, roundTripped);
        Assert.Equal(snapshot.SnapshotId, roundTripped.SnapshotId);
        DecisionEngineResult reevaluated = roundTripped.Reevaluate();
        Assert.Equal(result.Decision, reevaluated.Decision);
        Assert.Equal(result.Reasons, reevaluated.Reasons);
    }

    [Fact]
    public void ProjectionRejectsSemanticDriftFromStoredResult()
    {
        DecisionEngineConfiguration decisionConfiguration = DecisionProjectionFixtures.CreateDecisionConfiguration(
            DecisionEngineProfile.Allow, blocked: false);
        DecisionEngineResult result = DecisionProjectionFixtures.CreateResult(DecisionEngineProfile.Allow,
            blocked: false);
        DecisionEngineResult altered = new(
            new Decision(result.Decision.DecisionId, result.Decision.SetupId,
                DecisionOutcome.Block, new ImmutableValueList<string>(["tampered"])), result.Inputs,
            result.RulesVersion, result.ConfigurationVersion,
            new ImmutableValueList<string>(["tampered"]));

        Assert.Throws<ArgumentException>(() => DecisionProjectionSnapshot.Create(
            altered, DecisionProjectionFixtures.CreateLifecycleConfiguration(), decisionConfiguration));
    }
}
