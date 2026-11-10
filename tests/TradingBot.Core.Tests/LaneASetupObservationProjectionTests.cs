using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneASetupObservationProjectionTests
{
    [Fact]
    public void EligibleCutsExposeAllFamilyAndDirectionStatesWithoutSuppressingOverlap()
    {
        foreach (LaneAFixtureCase fixture in LaneASetupFixtures.Cases)
        {
            BoundaryRejectionSetupResult boundary =
                LaneASetupFixtures.EvaluateBoundary(fixture);
            CenterRetestSetupResult center = LaneASetupFixtures.EvaluateCenter(fixture);
            LaneASetupObservationProjection projection = new(boundary, center);

            Assert.Equal(Normalize(fixture.ExpectedBoundaryLong),
                projection.BoundaryLongStatus);
            Assert.Equal(Normalize(fixture.ExpectedBoundaryShort),
                projection.BoundaryShortStatus);
            Assert.Equal(Normalize(fixture.ExpectedCenterLong),
                projection.CenterRetestLongStatus);
            Assert.Equal(Normalize(fixture.ExpectedCenterShort),
                projection.CenterRetestShortStatus);
            Assert.Equal(ExpectedOverlap(Normalize(fixture.ExpectedBoundaryLong),
                Normalize(fixture.ExpectedCenterLong)), projection.LongOverlapStatus);
            Assert.Equal(ExpectedOverlap(Normalize(fixture.ExpectedBoundaryShort),
                Normalize(fixture.ExpectedCenterShort)), projection.ShortOverlapStatus);
            Assert.Equal(projection.LongOverlapStatus == LaneASetupOverlapStatus.Confirmed,
                projection.HasLongOverlap);
            Assert.Equal(projection.BoundaryLongStatus != LaneASetupObservationStatus.Unknown
                && projection.BoundaryShortStatus != LaneASetupObservationStatus.Unknown
                && projection.CenterRetestLongStatus != LaneASetupObservationStatus.Unknown
                && projection.CenterRetestShortStatus != LaneASetupObservationStatus.Unknown,
                projection.IsComplete);
        }
    }

    [Fact]
    public void RequiredUnknownBlocksCompletenessWithoutErasingIndependentFamilyEvidence()
    {
        LaneAFixtureCase fixture = LaneASetupFixtures.Cases.Single(
            static item => item.Name == "both-long-ready");
        BoundaryRejectionSetupResult boundary = BoundaryRejectionSetupEvaluator.Evaluate(
            fixture.PriceContext, fixture.Quality,
            new BoundaryRejectionSetupConfiguration());
        CenterRetestSetupResult center = LaneASetupFixtures.EvaluateCenter(fixture);
        LaneASetupObservationProjection projection = new(boundary, center);

        Assert.Equal(LaneASetupObservationStatus.Unknown, projection.BoundaryLongStatus);
        Assert.Equal(LaneASetupObservationStatus.Confirmed,
            projection.CenterRetestLongStatus);
        Assert.False(projection.IsComplete);
        Assert.Equal(LaneASetupOverlapStatus.Unknown, projection.LongOverlapStatus);
        Assert.False(projection.HasLongOverlap);
    }

    [Fact]
    public void OptionalUnknownFeaturesDoNotGateConfirmedSetupsOrTheirOverlap()
    {
        LaneAFixtureCase fixture = LaneASetupFixtures.Cases.Single(
            static item => item.Name == "both-long-ready");
        LaneASetupObservationProjection projection = new(
            LaneASetupFixtures.EvaluateBoundary(fixture),
            LaneASetupFixtures.EvaluateCenter(fixture));

        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            fixture.PriceContext.Vwap.State);
        Assert.Equal(PriceContextFeatureState.UnknownConfiguration,
            fixture.PriceContext.Poc.State);
        Assert.Equal(LaneASetupObservationStatus.Confirmed, projection.BoundaryLongStatus);
        Assert.Equal(LaneASetupObservationStatus.Confirmed,
            projection.CenterRetestLongStatus);
        Assert.Equal(LaneASetupOverlapStatus.Confirmed, projection.LongOverlapStatus);
        Assert.True(projection.HasLongOverlap);
    }

    [Fact]
    public void PrefixReplayTransitionsFromUnknownToConfirmedWithSameInputParity()
    {
        (ScreenerBar[] setup, ScreenerBar[] htf) = LaneASetupFixtures.CreateLongHistory();
        List<LaneASetupObservationProjection> projections = [];

        foreach (CausalEventCursor cursor in LaneASetupFixtures.ReplayCursors())
        {
            (ScreenerBar[] Setup, ScreenerBar[] Htf) prefix =
                LaneASetupFixtures.Prefix(setup, htf, cursor.AsOf);
            (PriceContextFeatureResult context, FeatureQualityEvaluation quality) =
                LaneASetupFixtures.Calculate(prefix.Setup, prefix.Htf, cursor);
            LaneASetupObservationProjection current = new(
                LaneASetupFixtures.EvaluateBoundary(context, quality,
                    LaneASetupFixtures.BoundaryMinimumWidth),
                LaneASetupFixtures.EvaluateCenter(context, quality));

            PriceContextFeatureResult restoredContext =
                CoreJsonSerializer.Deserialize<PriceContextFeatureResult>(
                    CoreJsonSerializer.Serialize(context));
            FeatureQualityEvaluation restoredQuality =
                CoreJsonSerializer.Deserialize<FeatureQualityEvaluation>(
                    CoreJsonSerializer.Serialize(quality));
            LaneASetupObservationProjection replayed = new(
                LaneASetupFixtures.EvaluateBoundary(restoredContext, restoredQuality,
                    LaneASetupFixtures.BoundaryMinimumWidth),
                LaneASetupFixtures.EvaluateCenter(restoredContext, restoredQuality));

            Assert.Equal(current, replayed);
            Assert.Equal(cursor, current.Cursor);
            projections.Add(current);
        }

        Assert.Equal(LaneASetupObservationStatus.Unknown,
            projections[0].BoundaryLongStatus);
        Assert.Equal(LaneASetupObservationStatus.Unknown,
            projections[0].CenterRetestLongStatus);
        Assert.Equal(LaneASetupObservationStatus.Confirmed,
            projections[^1].BoundaryLongStatus);
        Assert.Equal(LaneASetupObservationStatus.Confirmed,
            projections[^1].CenterRetestLongStatus);
        Assert.Equal(LaneASetupOverlapStatus.Confirmed,
            projections[^1].LongOverlapStatus);
        Assert.True(projections[^1].HasLongOverlap);
    }

    [Fact]
    public void FamiliesFromDifferentContextSnapshotsCannotBeCombinedAtOneCut()
    {
        LaneAFixtureCase first = LaneASetupFixtures.Cases.Single(
            static item => item.Name == "both-long-ready");
        LaneAFixtureCase second = LaneASetupFixtures.Cases.Single(
            static item => item.Name == "boundary-long-only");
        BoundaryRejectionSetupResult boundary = LaneASetupFixtures.EvaluateBoundary(first);
        CenterRetestSetupResult center = LaneASetupFixtures.EvaluateCenter(second);

        Assert.Throws<ArgumentException>(() => new LaneASetupObservationProjection(
            boundary, center));
    }

    private static LaneASetupOverlapStatus ExpectedOverlap(
        LaneASetupObservationStatus first, LaneASetupObservationStatus second)
    {
        if (first == LaneASetupObservationStatus.Confirmed
            && second == LaneASetupObservationStatus.Confirmed)
        {
            return LaneASetupOverlapStatus.Confirmed;
        }

        if ((first == LaneASetupObservationStatus.Unknown
                && second is LaneASetupObservationStatus.Confirmed
                    or LaneASetupObservationStatus.Unknown)
            || (second == LaneASetupObservationStatus.Unknown
                && first == LaneASetupObservationStatus.Confirmed))
        {
            return LaneASetupOverlapStatus.Unknown;
        }

        return LaneASetupOverlapStatus.NoOverlap;
    }

    private static LaneASetupObservationStatus Normalize(
        BoundaryRejectionSetupState state) => state switch
        {
            BoundaryRejectionSetupState.Ready => LaneASetupObservationStatus.Confirmed,
            BoundaryRejectionSetupState.NoSetup => LaneASetupObservationStatus.NoSetup,
            BoundaryRejectionSetupState.Invalidated => LaneASetupObservationStatus.Invalidated,
            _ => LaneASetupObservationStatus.Unknown
        };

    private static LaneASetupObservationStatus Normalize(
        CenterRetestSetupState state) => state switch
        {
            CenterRetestSetupState.Ready => LaneASetupObservationStatus.Confirmed,
            CenterRetestSetupState.NoSetup => LaneASetupObservationStatus.NoSetup,
            CenterRetestSetupState.Invalidated => LaneASetupObservationStatus.Invalidated,
            _ => LaneASetupObservationStatus.Unknown
        };
}
