using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LifecycleInputFixtureTests
{
    [Fact]
    public void FixtureMatrixReplaysWithStableIdentityAndCausalEvidence()
    {
        Assert.Equal(17, LifecycleInputFixtures.All.Count);
        foreach (LifecycleInputFixture fixture in LifecycleInputFixtures.All)
        {
            SetupEvidenceSnapshot evidence = fixture.Evidence;
            Assert.Equal(fixture.ExpectedState, evidence.State);
            Assert.Equal(evidence, evidence.Replay());

            string json = CoreJsonSerializer.Serialize(evidence);
            SetupEvidenceSnapshot restored =
                CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(json);
            Assert.Equal(evidence, restored);
            Assert.Equal(evidence.SnapshotId, restored.SnapshotId);
            Assert.Equal(evidence.Cursor, evidence.InputSequence.Cursor);
            Assert.Equal(evidence.PriceContext.Input.SourceId,
                evidence.InputSequence.SourceId);
            Assert.Equal(evidence.PriceContext.Input.InstrumentId,
                evidence.InputSequence.InstrumentId);
            Assert.Equal(evidence.Invalidation.Reasons.ToArray(),
                evidence.Invalidation.Reasons.OrderBy(reason => reason,
                    StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
            Assert.Equal(evidence.IsInvalidated, evidence.Invalidation.ClosedThrough);
            Assert.Equal(evidence.State == SetupEvidenceState.UnknownWarmup,
                evidence.SignalBar is null);
        }
    }

    [Fact]
    public void EvidenceReplayIsRepeatableWithoutLifecycleDeduplication()
    {
        foreach (LifecycleInputFixture fixture in LifecycleInputFixtures.All)
        {
            SetupEvidenceSnapshot first = fixture.Evidence.Replay();
            SetupEvidenceSnapshot second = fixture.Evidence.Replay();
            Assert.Equal(first, second);
            Assert.Equal(first.SnapshotId, second.SnapshotId);
        }
    }

    [Fact]
    public void CursorPrefixReplayDoesNotUpgradeEarlierEvidenceWithFutureBars()
    {
        (ScreenerBar[] setup, ScreenerBar[] htf) =
            LaneASetupFixtures.CreateLongHistory();
        SetupEvidenceSnapshot? firstBoundary = null;
        SetupEvidenceSnapshot? firstCenter = null;
        IReadOnlyList<CausalEventCursor> cursors = LaneASetupFixtures.ReplayCursors();

        for (int index = 0; index < cursors.Count; index++)
        {
            CausalEventCursor cursor = cursors[index];
            (ScreenerBar[] Setup, ScreenerBar[] Htf) prefix =
                LaneASetupFixtures.Prefix(setup, htf, cursor.AsOf);
            (PriceContextFeatureResult context, FeatureQualityEvaluation quality) =
                LaneASetupFixtures.Calculate(prefix.Setup, prefix.Htf, cursor);
            SetupEvidenceSnapshot boundary = SetupEvidenceSnapshot.Create(
                LaneASetupFixtures.EvaluateBoundary(context, quality,
                    LaneASetupFixtures.BoundaryMinimumWidth), Direction.Long);
            SetupEvidenceSnapshot center = SetupEvidenceSnapshot.Create(
                LaneASetupFixtures.EvaluateCenter(context, quality), Direction.Long);

            Assert.Equal(boundary, boundary.Replay());
            Assert.Equal(center, center.Replay());
            if (index == 0)
            {
                firstBoundary = boundary;
                firstCenter = center;
            }
            if (index < cursors.Count - 1)
            {
                Assert.False(boundary.IsReady);
                Assert.False(center.IsReady);
            }
        }

        Assert.NotNull(firstBoundary);
        Assert.NotNull(firstCenter);
        Assert.Equal(firstBoundary, firstBoundary!.Replay());
        Assert.Equal(firstCenter, firstCenter!.Replay());
        Assert.True(LifecycleInputFixtures.CreateEvidence(
            LaneASetupFixtures.Cases.Single(fixture =>
                fixture.Name == "both-long-ready"), true, Direction.Long).IsReady);
    }

    [Fact]
    public void ReadyEvidenceHasNoOrderLifecycleOrDecisionSurface()
    {
        Type[] forbidden =
        [
            typeof(Decision), typeof(RiskDecision), typeof(OrderIntent),
            typeof(Position), typeof(SetupState)
        ];
        Type[] roots =
        [
            typeof(SetupEvidenceSnapshot), typeof(SetupEvidenceInputSequence),
            typeof(SetupEvidenceInvalidation)
        ];
        foreach (Type type in roots)
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                AssertNoForbiddenType(property.PropertyType, forbidden);
            }
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                AssertNoForbiddenType(method.ReturnType, forbidden);
                AssertNoForbiddenType(method.ReturnParameter.ParameterType, forbidden);
            }
            Assert.DoesNotContain(type.GetMethods(BindingFlags.Public
                | BindingFlags.Static | BindingFlags.DeclaredOnly), method =>
                method.Name.Contains("Order", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Authorize", StringComparison.OrdinalIgnoreCase));
        }

        string json = CoreJsonSerializer.Serialize(LifecycleInputFixtures.All
            .First(fixture => fixture.ExpectedState == SetupEvidenceState.Ready)
            .Evidence);
        JsonObject tampered = JsonNode.Parse(json)!.AsObject();
        tampered["orderIntent"] = new JsonObject();
        tampered["authorized"] = true;
        Assert.Throws<JsonException>(() =>
            CoreJsonSerializer.Deserialize<SetupEvidenceSnapshot>(tampered.ToJsonString()));
    }

    [Fact]
    public void EvidenceGraphRejectsExecutionContractsThroughCollectionsAndReturns()
    {
        Type[] forbidden =
        [
            typeof(Decision), typeof(RiskDecision), typeof(OrderIntent),
            typeof(Position), typeof(SetupState)
        ];
        foreach (Type type in new[] { typeof(SetupEvidenceSnapshot),
                     typeof(SetupEvidenceInputSequence), typeof(SetupEvidenceInvalidation) })
        {
            foreach (PropertyInfo property in type.GetProperties())
            {
                AssertNoForbiddenType(property.PropertyType, forbidden);
            }
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public
                         | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                AssertNoForbiddenType(method.ReturnType, forbidden);
            }
        }
    }

    private static void AssertNoForbiddenType(Type type, Type[] forbidden)
    {
        if (type.IsByRef || type.IsPointer || type.IsArray)
        {
            AssertNoForbiddenType(type.GetElementType()!, forbidden);
            return;
        }
        Assert.DoesNotContain(type, forbidden);
        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                AssertNoForbiddenType(argument, forbidden);
            }
        }
    }
}
