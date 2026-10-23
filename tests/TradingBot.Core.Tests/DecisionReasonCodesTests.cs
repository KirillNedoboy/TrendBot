using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DecisionReasonCodesTests
{
    [Fact]
    public void CatalogIsVersionedStableOrderedSerializableAndAuditable()
    {
        Assert.Equal("decision-reasons-v1", DecisionReasonCodes.CurrentVersion);
        Assert.Equal(DecisionReasonCodes.CurrentVersion, DecisionReasonCodes.Version);
        Assert.NotEmpty(DecisionReasonCodes.Definitions);
        Assert.Equal(DecisionReasonCodes.Definitions.Count,
            DecisionReasonCodes.Definitions.Select(item => item.Code).Distinct(
                StringComparer.Ordinal).Count());
        Assert.Equal(DecisionReasonCodes.Definitions.Select(item => item.Code),
            DecisionReasonCodes.Codes);
        Assert.All(DecisionReasonCodes.Definitions, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Code));
            Assert.False(string.IsNullOrWhiteSpace(item.Meaning));
            Assert.True(DecisionReasonCodes.IsKnown(item.Code));
        });

        string json = CoreJsonSerializer.Serialize(DecisionReasonCodes.Definitions);
        Assert.Equal(json, CoreJsonSerializer.Serialize(
            CoreJsonSerializer.Deserialize<ImmutableValueList<DecisionReasonCodeDefinition>>(json)));
    }

    [Fact]
    public void EngineReasonsUseCatalogAndPreserveOrderAndDuplicates()
    {
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleConfiguration lifecycleConfiguration =
            DecisionProjectionFixtures.CreateLifecycleConfiguration();
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            new InstrumentId("BTCUSDT"), SetupType.BoundaryRejection, Direction.Long,
            "reason-anchor", new UtcTimestamp(DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
            "context-v1");
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity,
            lifecycleConfiguration) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = evidence.Cursor
        };
        DecisionEngineConfiguration configuration = new(
            "decision-rules-v1", "decision-config-v1", DecisionEngineProfile.Allow,
            requireLaneB: true, requireLaneC: true, allowNeutralLaneB: false,
            allowNeutralLaneC: false);
        DecisionEvidence unknownB = new(DecisionLane.LaneB, DecisionEvidenceStatus.Unknown,
            "b", "b-v1", evidence.Cursor, "fixture");
        DecisionEvidence unknownC = new(DecisionLane.LaneC, DecisionEvidenceStatus.Unknown,
            "c", "c-v1", evidence.Cursor, "fixture");
        DecisionEngineResult result = DecisionEngine.Evaluate(new DecisionEngineInput(
            "reason-decision", lifecycle, evidence, [unknownB, unknownC], "fixture"),
            configuration);

        Assert.NotEmpty(result.Reasons);
        Assert.All(result.Reasons, code => Assert.True(DecisionReasonCodes.IsKnown(code)));
        Assert.Equal(result.Reasons, result.Decision.Reasons);
        Assert.Contains(DecisionReasonCodes.LaneBUnknown, result.Reasons);
        Assert.Contains(DecisionReasonCodes.LaneCUnknown, result.Reasons);
        Assert.Equal(result.Reasons.Count, result.Decision.Reasons.Count);
    }

    [Fact]
    public void ProjectionRejectsUnknownReasonCatalogVersion()
    {
        DecisionProjectionSnapshot valid = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DecisionProjectionSnapshot(valid.FormatVersion, "decision-reasons-v999",
                valid.Result, valid.LifecycleConfiguration, valid.DecisionConfiguration,
                valid.LifecycleSnapshot));
    }

    [Fact]
    public void UnknownReasonCodeIsRejectedByCatalogValidation()
    {
        Assert.False(DecisionReasonCodes.IsKnown("decision-unknown-v999"));
        Assert.Throws<ArgumentException>(() => DecisionReasonCodes.RequireKnown(
            new ImmutableValueList<string>(["decision-unknown-v999"])));
    }
}
