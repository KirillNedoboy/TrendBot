using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SetupEvidenceSnapshotTests
{
    private static readonly Type[] EvidenceContractTypes =
    [
        typeof(SetupEvidenceSnapshot),
        typeof(SetupEvidenceInputSequence),
        typeof(SetupEvidenceInvalidation)
    ];

    private static readonly Type[] ForbiddenExecutionTypes =
    [
        typeof(Decision), typeof(RiskDecision), typeof(OrderIntent), typeof(Position),
        typeof(Setup), typeof(SetupState)
    ];

    private static readonly string[] ExpectedEvidenceStates =
    [
        "Ready", "NoSetup", "Invalidated", "UnknownConfiguration", "UnknownInput",
        "UnknownWarmup", "UnknownQuality", "UnknownOverflow", "UnknownZeroDenominator"
    ];

    [Fact]
    public void BoundaryEvidenceCapturesCausalSequenceAndInvalidation()
    {
        LaneAFixtureCase fixture = Case("both-long-invalidated");
        BoundaryRejectionSetupResult result = LaneASetupFixtures.EvaluateBoundary(fixture);
        SetupEvidenceSnapshot evidence = SetupEvidenceSnapshot.Create(result,
            Direction.Long);

        Assert.Equal(SetupType.BoundaryRejection, evidence.SetupType);
        Assert.Equal(Direction.Long, evidence.Direction);
        Assert.Equal(SetupEvidenceState.Invalidated, evidence.State);
        Assert.True(evidence.IsInvalidated);
        Assert.True(evidence.Invalidation.ClosedThrough);
        Assert.Equal(result.Long.Reasons.ToArray(),
            evidence.Invalidation.Reasons.ToArray());
        Assert.Same(result.PriceContext, evidence.PriceContext);
        Assert.Same(result.Quality, evidence.Quality);
        Assert.Same(result.Configuration, evidence.BoundaryRejectionConfiguration);
        Assert.Same(result.Long, evidence.BoundaryRejectionObservation);
        Assert.Null(evidence.CenterRetestConfiguration);
        Assert.Null(evidence.CenterRetestObservation);

        Assert.Equal(result.Cursor, evidence.InputSequence.Cursor);
        Assert.Equal(result.PriceContext.Input.SourceId,
            evidence.InputSequence.SourceId);
        Assert.Equal(result.PriceContext.Input.InstrumentId,
            evidence.InputSequence.InstrumentId);
        Assert.Equal(result.PriceContext.Input.SetupTimeframe,
            evidence.InputSequence.SetupTimeframe);
        Assert.Equal(result.PriceContext.Input.HtfTimeframe,
            evidence.InputSequence.HtfTimeframe);
        Assert.Equal(result.PriceContext.Input.SetupHistoryStart,
            evidence.InputSequence.SetupHistoryStart);
        Assert.Equal(result.PriceContext.Input.HtfHistoryStart,
            evidence.InputSequence.HtfHistoryStart);
        Assert.Same(result.Long.SignalBar, evidence.InputSequence.SignalBar);
        Assert.Equal(BoundaryRejectionSetupResult.CurrentAlgorithmVersion,
            evidence.InputSequence.FamilyAlgorithmVersion);
        Assert.Equal(result.PriceContext.Configuration.AlgorithmVersion?.Value,
            evidence.InputSequence.PriceContextFormulaVersion);
        Assert.Equal(result.Quality.AlgorithmVersion,
            evidence.InputSequence.QualityAlgorithmVersion);
        Assert.Equal(result.Long.SignalBar?.StableSequence,
            evidence.InputSequence.StableSequence);
        Assert.Equal(result.Long.SignalBar?.CloseTime,
            evidence.InputSequence.CloseTime);
        Assert.Matches("^[0-9a-f]{64}$", evidence.SnapshotId);
        Assert.Equal(evidence, evidence.Replay());
    }

    [Fact]
    public void CenterEvidenceCapturesCausalSequenceAndInvalidation()
    {
        LaneAFixtureCase fixture = Case("center-long-invalidated-only");
        CenterRetestSetupResult result = LaneASetupFixtures.EvaluateCenter(fixture);
        SetupEvidenceSnapshot evidence = SetupEvidenceSnapshot.Create(result,
            Direction.Long);

        Assert.Equal(SetupType.CenterRetest, evidence.SetupType);
        Assert.Equal(Direction.Long, evidence.Direction);
        Assert.Equal(SetupEvidenceState.Invalidated, evidence.State);
        Assert.True(evidence.Invalidation.ClosedThrough);
        Assert.Equal(result.Long.Reasons.ToArray(),
            evidence.Invalidation.Reasons.ToArray());
        Assert.Same(result.PriceContext, evidence.PriceContext);
        Assert.Same(result.Quality, evidence.Quality);
        Assert.Same(result.Configuration, evidence.CenterRetestConfiguration);
        Assert.Same(result.Long, evidence.CenterRetestObservation);
        Assert.Null(evidence.BoundaryRejectionConfiguration);
        Assert.Null(evidence.BoundaryRejectionObservation);
        Assert.Equal(CenterRetestSetupResult.CurrentAlgorithmVersion,
            evidence.InputSequence.FamilyAlgorithmVersion);
        Assert.Equal(evidence, evidence.Replay());
    }

    [Fact]
    public void EvidenceCoversFamilyStatesAndDirections()
    {
        (string CaseName, bool IsBoundary, Direction Direction,
            SetupEvidenceState ExpectedState)[] cases =
        [
            ("both-long-ready", true, Direction.Long, SetupEvidenceState.Ready),
            ("both-long-invalidated", true, Direction.Long,
                SetupEvidenceState.Invalidated),
            ("center-long-only", true, Direction.Long, SetupEvidenceState.NoSetup),
            ("both-long-unknown-quality", true, Direction.Long,
                SetupEvidenceState.UnknownQuality),
            ("both-long-ready", false, Direction.Long, SetupEvidenceState.Ready),
            ("center-long-invalidated-only", false, Direction.Long,
                SetupEvidenceState.Invalidated),
            ("both-long-ready", false, Direction.Short, SetupEvidenceState.NoSetup),
            ("both-long-unknown-quality", false, Direction.Long,
                SetupEvidenceState.UnknownQuality)
        ];

        foreach ((string caseName, bool isBoundary, Direction direction,
            SetupEvidenceState expectedState) in cases)
        {
            LaneAFixtureCase fixture = Case(caseName);
            SetupEvidenceSnapshot evidence;
            bool observationInvalidated;
            string[] observationReasons;
            if (isBoundary)
            {
                BoundaryRejectionSetupResult result =
                    LaneASetupFixtures.EvaluateBoundary(fixture);
                BoundaryRejectionSetupObservation observation =
                    direction == Direction.Long ? result.Long : result.Short;
                evidence = SetupEvidenceSnapshot.Create(result, direction);
                observationInvalidated = observation.IsInvalidated;
                observationReasons = observation.Reasons.ToArray();
            }
            else
            {
                CenterRetestSetupResult result =
                    LaneASetupFixtures.EvaluateCenter(fixture);
                CenterRetestSetupObservation observation =
                    direction == Direction.Long ? result.Long : result.Short;
                evidence = SetupEvidenceSnapshot.Create(result, direction);
                observationInvalidated = observation.IsInvalidated;
                observationReasons = observation.Reasons.ToArray();
            }

            Assert.Equal(expectedState, evidence.State);
            Assert.Equal(observationInvalidated, evidence.IsInvalidated);
            Assert.Equal(observationInvalidated,
                evidence.Invalidation.ClosedThrough);
            Assert.Equal(observationReasons, evidence.Invalidation.Reasons.ToArray());
            Assert.Equal(evidence, evidence.Replay());
        }
    }

    [Fact]
    public void EvidenceIdentityIsCanonicalDirectionSpecificAndExcludesIdentity()
    {
        LaneAFixtureCase fixture = Case("both-long-ready");
        BoundaryRejectionSetupResult result = LaneASetupFixtures.EvaluateBoundary(fixture);
        SetupEvidenceSnapshot longEvidence = SetupEvidenceSnapshot.Create(result,
            Direction.Long);
        SetupEvidenceSnapshot longAgain = SetupEvidenceSnapshot.Create(result,
            Direction.Long);
        SetupEvidenceSnapshot shortEvidence = SetupEvidenceSnapshot.Create(result,
            Direction.Short);

        Assert.Equal(longEvidence.SnapshotId, longAgain.SnapshotId);
        Assert.Equal(longEvidence, longAgain);
        Assert.NotEqual(longEvidence.SnapshotId, shortEvidence.SnapshotId);
        Assert.NotEqual(longEvidence.Direction, shortEvidence.Direction);

        PriceContextFeatureResult restoredContext =
            CoreJsonSerializer.Deserialize<PriceContextFeatureResult>(
                CoreJsonSerializer.Serialize(result.PriceContext));
        FeatureQualityEvaluation restoredQuality =
            CoreJsonSerializer.Deserialize<FeatureQualityEvaluation>(
                CoreJsonSerializer.Serialize(result.Quality));
        BoundaryRejectionSetupResult restoredResult =
            BoundaryRejectionSetupEvaluator.Evaluate(restoredContext, restoredQuality,
                result.Configuration);
        SetupEvidenceSnapshot replayedEvidence = SetupEvidenceSnapshot.Create(
            restoredResult, Direction.Long);
        Assert.Equal(longEvidence, replayedEvidence);
        Assert.Equal(longEvidence.SnapshotId, replayedEvidence.SnapshotId);

        string json = CoreJsonSerializer.Serialize(longEvidence);
        Assert.DoesNotContain("\"snapshotId\"", json);
        Assert.DoesNotContain("\"identity\"", json);
        string expectedIdentity = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        Assert.Equal(expectedIdentity, longEvidence.SnapshotId);
        Assert.Equal(longEvidence,
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(json));
        Assert.Equal(longEvidence, longEvidence.Replay());
    }

    [Fact]
    public void EvidenceRoundTripsAndRejectsTamperedPayloads()
    {
        BoundaryRejectionSetupResult result = LaneASetupFixtures.EvaluateBoundary(
            Case("both-long-ready"));
        SetupEvidenceSnapshot evidence = SetupEvidenceSnapshot.Create(result,
            Direction.Long);
        string json = CoreJsonSerializer.Serialize(evidence);
        Assert.Equal(evidence,
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(json));

        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(Tamper(json,
                static document => document["direction"] = "short")));
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(Tamper(json,
                static document => document["setupType"] = "centerRetest")));
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(Tamper(json,
                static document =>
                    ((JsonObject)document["invalidation"]!)["closedThrough"] = true)));
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(Tamper(json,
                static document =>
                    ((JsonObject)((JsonObject)document["inputSequence"]!)["cursor"]!)
                    ["maxStableSequence"] = 999)));
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(Tamper(json,
                static document =>
                    ((JsonObject)((JsonObject)document["boundaryRejectionObservation"]!)
                        ["conditions"]!)["closedThroughBoundary"] = true)));
    }

    [Fact]
    public void EvidenceSupportsUnknownEvidenceWithoutSignalBar()
    {
        CausalEventCursor cursor = new(
            new UtcTimestamp(LaneASetupFixtures.BaseTime.AddMinutes(1)), 10_000);
        (PriceContextFeatureResult context, FeatureQualityEvaluation quality) =
            LaneASetupFixtures.Calculate(Array.Empty<ScreenerBar>(),
                Array.Empty<ScreenerBar>(), cursor);
        BoundaryRejectionSetupResult result = LaneASetupFixtures.EvaluateBoundary(
            context, quality, LaneASetupFixtures.BoundaryMinimumWidth);

        Assert.Null(result.Long.SignalBar);
        Assert.Equal(BoundaryRejectionSetupState.UnknownWarmup, result.Long.State);

        SetupEvidenceSnapshot evidence = SetupEvidenceSnapshot.Create(result,
            Direction.Long);
        Assert.Equal(SetupEvidenceState.UnknownWarmup, evidence.State);
        Assert.False(evidence.IsInvalidated);
        Assert.False(evidence.Invalidation.ClosedThrough);
        Assert.Null(evidence.InputSequence.SignalBar);
        Assert.Null(evidence.InputSequence.StableSequence);
        Assert.Equal(evidence, evidence.Replay());
    }

    [Fact]
    public void EvidenceExposesNoExecutionOrLifecycleContracts()
    {
        Assert.All(EvidenceContractTypes, static type => Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.DoesNotContain(property.PropertyType,
                ForbiddenExecutionTypes)));
        Assert.Equal(ExpectedEvidenceStates,
            Enum.GetNames<SetupEvidenceState>());
        Assert.DoesNotContain("Observing",
            Enum.GetNames<SetupEvidenceState>());
        Assert.DoesNotContain("SetupConfirmed",
            Enum.GetNames<SetupEvidenceState>());
        Assert.DoesNotContain("EntryArmed",
            Enum.GetNames<SetupEvidenceState>());
    }

    private static LaneAFixtureCase Case(string name)
    {
        return LaneASetupFixtures.Cases.Single(fixture => fixture.Name == name);
    }

    private static string Tamper(string json, Action<JsonObject> mutate)
    {
        JsonObject document = JsonNode.Parse(json)!.AsObject();
        mutate(document);
        return document.ToJsonString();
    }
}
