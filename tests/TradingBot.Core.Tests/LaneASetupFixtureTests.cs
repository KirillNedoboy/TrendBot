using System.Globalization;
using System.Reflection;
using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class LaneASetupFixtureTests
{
    private static readonly SetupType[] V1SetupTypes =
        [SetupType.BoundaryRejection, SetupType.CenterRetest];

    private static readonly Type[] LaneAContractTypes =
    [
        typeof(BoundaryRejectionSetupResult),
        typeof(BoundaryRejectionSetupObservation),
        typeof(CenterRetestSetupResult),
        typeof(CenterRetestSetupObservation)
    ];

    private static readonly Type[] ForbiddenExecutionTypes =
    [
        typeof(Decision), typeof(RiskDecision), typeof(OrderIntent), typeof(Position),
        typeof(Setup), typeof(SetupState)
    ];

    [Fact]
    public void HandSpecifiedLaneAMatrixProducesExpectedStatesAndInvariants()
    {
        foreach (LaneAFixtureCase fixture in LaneASetupFixtures.Cases)
        {
            BoundaryRejectionSetupResult boundary =
                LaneASetupFixtures.EvaluateBoundary(fixture);
            CenterRetestSetupResult center = LaneASetupFixtures.EvaluateCenter(fixture);

            Assert.True(fixture.ExpectedBoundaryLong == boundary.Long.State,
                $"{fixture.Name}: Boundary LONG expected " +
                $"{fixture.ExpectedBoundaryLong}, actual {boundary.Long.State}.");
            Assert.True(fixture.ExpectedBoundaryShort == boundary.Short.State,
                $"{fixture.Name}: Boundary SHORT expected " +
                $"{fixture.ExpectedBoundaryShort}, actual {boundary.Short.State}.");
            Assert.True(fixture.ExpectedCenterLong == center.Long.State,
                $"{fixture.Name}: Center LONG expected " +
                $"{fixture.ExpectedCenterLong}, actual {center.Long.State}.");
            Assert.True(fixture.ExpectedCenterShort == center.Short.State,
                $"{fixture.Name}: Center SHORT expected " +
                $"{fixture.ExpectedCenterShort}, actual {center.Short.State}.");

            AssertBoundaryInvariants(fixture, boundary);
            AssertCenterInvariants(fixture, center);
            AssertCaseSpecificReasons(fixture, boundary, center);
        }
    }

    [Fact]
    public void SerializedSnapshotAndResultsReplayIdenticallyAcrossFamilies()
    {
        foreach (LaneAFixtureCase fixture in LaneASetupFixtures.Cases)
        {
            BoundaryRejectionSetupResult boundary =
                LaneASetupFixtures.EvaluateBoundary(fixture);
            CenterRetestSetupResult center = LaneASetupFixtures.EvaluateCenter(fixture);

            PriceContextFeatureResult restoredContext =
                CoreJsonSerializer.Deserialize<PriceContextFeatureResult>(
                    CoreJsonSerializer.Serialize(fixture.PriceContext));
            FeatureQualityEvaluation restoredQuality =
                CoreJsonSerializer.Deserialize<FeatureQualityEvaluation>(
                    CoreJsonSerializer.Serialize(fixture.Quality));
            Assert.Equal(fixture.PriceContext, restoredContext);
            Assert.Equal(fixture.Quality, restoredQuality);

            Assert.Equal(boundary, LaneASetupFixtures.EvaluateBoundary(
                restoredContext, restoredQuality, fixture.BoundaryMinimumWidth));
            Assert.Equal(center, LaneASetupFixtures.EvaluateCenter(
                restoredContext, restoredQuality));

            string boundaryJson = CoreJsonSerializer.Serialize(boundary);
            string centerJson = CoreJsonSerializer.Serialize(center);
            BoundaryRejectionSetupResult restoredBoundary =
                CoreJsonSerializer.Deserialize<BoundaryRejectionSetupResult>(
                    boundaryJson);
            CenterRetestSetupResult restoredCenter =
                CoreJsonSerializer.Deserialize<CenterRetestSetupResult>(centerJson);
            Assert.Equal(boundary, restoredBoundary);
            Assert.Equal(center, restoredCenter);

            Assert.Equal(restoredBoundary, BoundaryRejectionSetupEvaluator.Evaluate(
                restoredBoundary.PriceContext, restoredBoundary.Quality,
                restoredBoundary.Configuration));
            Assert.Equal(restoredCenter, CenterRetestSetupEvaluator.Evaluate(
                restoredCenter.PriceContext, restoredCenter.Quality,
                restoredCenter.Configuration));

            Assert.Throws<JsonException>(() =>
                CoreJsonSerializer.Deserialize<BoundaryRejectionSetupResult>(
                    boundaryJson[..^1] + ",\"unexpected\":true}"));
            Assert.Throws<JsonException>(() =>
                CoreJsonSerializer.Deserialize<CenterRetestSetupResult>(
                    centerJson[..^1] + ",\"unexpected\":true}"));
        }
    }

    [Fact]
    public void CursorPrefixReplayThroughSerializedEvidenceIsStable()
    {
        (ScreenerBar[] setup, ScreenerBar[] htf) = LaneASetupFixtures.CreateLongHistory();
        BoundaryRejectionSetupResult? finalBoundary = null;
        CenterRetestSetupResult? finalCenter = null;

        foreach (CausalEventCursor cursor in LaneASetupFixtures.ReplayCursors())
        {
            (ScreenerBar[] Setup, ScreenerBar[] Htf) prefix =
                LaneASetupFixtures.Prefix(setup, htf, cursor.AsOf);
            (PriceContextFeatureResult context, FeatureQualityEvaluation quality) =
                LaneASetupFixtures.Calculate(prefix.Setup, prefix.Htf, cursor);
            BoundaryRejectionSetupResult boundary =
                LaneASetupFixtures.EvaluateBoundary(context, quality,
                    LaneASetupFixtures.BoundaryMinimumWidth);
            CenterRetestSetupResult center =
                LaneASetupFixtures.EvaluateCenter(context, quality);

            PriceContextFeatureResult restoredContext =
                CoreJsonSerializer.Deserialize<PriceContextFeatureResult>(
                    CoreJsonSerializer.Serialize(context));
            FeatureQualityEvaluation restoredQuality =
                CoreJsonSerializer.Deserialize<FeatureQualityEvaluation>(
                    CoreJsonSerializer.Serialize(quality));

            Assert.Equal(boundary, LaneASetupFixtures.EvaluateBoundary(
                restoredContext, restoredQuality,
                LaneASetupFixtures.BoundaryMinimumWidth));
            Assert.Equal(center, LaneASetupFixtures.EvaluateCenter(
                restoredContext, restoredQuality));
            finalBoundary = boundary;
            finalCenter = center;
        }

        Assert.NotNull(finalBoundary);
        Assert.NotNull(finalCenter);
        Assert.True(finalBoundary!.Long.IsReady);
        Assert.True(finalCenter!.Long.IsReady);
    }

    [Fact]
    public void FutureDataCannotUpgradeEarlierLaneASetup()
    {
        (ScreenerBar[] setup, ScreenerBar[] htf) = LaneASetupFixtures.CreateLongHistory();
        IReadOnlyList<CausalEventCursor> cursors = LaneASetupFixtures.ReplayCursors();

        for (int index = 0; index < cursors.Count; index++)
        {
            CausalEventCursor cursor = cursors[index];
            (ScreenerBar[] Setup, ScreenerBar[] Htf) prefix =
                LaneASetupFixtures.Prefix(setup, htf, cursor.AsOf);
            (PriceContextFeatureResult prefixContext, FeatureQualityEvaluation prefixQuality) =
                LaneASetupFixtures.Calculate(prefix.Setup, prefix.Htf, cursor);
            (PriceContextFeatureResult fullContext, FeatureQualityEvaluation fullQuality) =
                LaneASetupFixtures.Calculate(setup, htf, cursor);

            Assert.Equal(prefixContext.Channel.Center, fullContext.Channel.Center);
            Assert.Equal(prefixContext.Channel.PriorCenter,
                fullContext.Channel.PriorCenter);
            Assert.Equal(prefixContext.Channel.Lower, fullContext.Channel.Lower);
            Assert.Equal(prefixContext.Channel.Upper, fullContext.Channel.Upper);
            Assert.Equal(prefixContext.Channel.Width, fullContext.Channel.Width);
            Assert.Equal(prefixContext.LocalTrend.Direction,
                fullContext.LocalTrend.Direction);
            Assert.Equal(prefixContext.LocalTrend.CurrentBar,
                fullContext.LocalTrend.CurrentBar);
            Assert.Equal(prefixContext.HtfContext.ConfirmedRegime,
                fullContext.HtfContext.ConfirmedRegime);

            BoundaryRejectionSetupResult prefixBoundary =
                LaneASetupFixtures.EvaluateBoundary(prefixContext, prefixQuality,
                    LaneASetupFixtures.BoundaryMinimumWidth);
            CenterRetestSetupResult prefixCenter =
                LaneASetupFixtures.EvaluateCenter(prefixContext, prefixQuality);
            BoundaryRejectionSetupResult fullBoundary =
                LaneASetupFixtures.EvaluateBoundary(fullContext, fullQuality,
                    LaneASetupFixtures.BoundaryMinimumWidth);
            CenterRetestSetupResult fullCenter =
                LaneASetupFixtures.EvaluateCenter(fullContext, fullQuality);

            if (index == cursors.Count - 1)
            {
                Assert.Equal(prefixBoundary, fullBoundary);
                Assert.Equal(prefixCenter, fullCenter);
                Assert.True(fullBoundary.Long.IsReady);
                Assert.True(fullCenter.Long.IsReady);
            }
            else
            {
                Assert.False(fullBoundary.IsReady);
                Assert.False(fullCenter.IsReady);
                Assert.Contains("future", fullContext.Quality.Reasons);
            }
        }
    }

    [Fact]
    public void PermutationAndCultureDoNotChangeLaneAEvidence()
    {
        (ScreenerBar[] setup, ScreenerBar[] htf) = LaneASetupFixtures.CreateLongHistory();
        CausalEventCursor cursor = new(Time(30), 10_000);
        (PriceContextFeatureResult baselineContext, FeatureQualityEvaluation baselineQuality) =
            LaneASetupFixtures.Calculate(setup, htf, cursor);
        BoundaryRejectionSetupResult baselineBoundary =
            LaneASetupFixtures.EvaluateBoundary(baselineContext, baselineQuality,
                LaneASetupFixtures.BoundaryMinimumWidth);
        CenterRetestSetupResult baselineCenter =
            LaneASetupFixtures.EvaluateCenter(baselineContext, baselineQuality);

        (PriceContextFeatureResult permutedContext, FeatureQualityEvaluation permutedQuality) =
            LaneASetupFixtures.Calculate(setup.Reverse(), htf.Reverse(), cursor);
        Assert.Equal(baselineBoundary, LaneASetupFixtures.EvaluateBoundary(
            permutedContext, permutedQuality, LaneASetupFixtures.BoundaryMinimumWidth));
        Assert.Equal(baselineCenter, LaneASetupFixtures.EvaluateCenter(
            permutedContext, permutedQuality));

        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            (PriceContextFeatureResult frenchContext, FeatureQualityEvaluation frenchQuality) =
                LaneASetupFixtures.Calculate(setup, htf, cursor);
            Assert.Equal(baselineBoundary, LaneASetupFixtures.EvaluateBoundary(
                frenchContext, frenchQuality, LaneASetupFixtures.BoundaryMinimumWidth));
            Assert.Equal(baselineCenter, LaneASetupFixtures.EvaluateCenter(
                frenchContext, frenchQuality));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            (PriceContextFeatureResult englishContext, FeatureQualityEvaluation englishQuality) =
                LaneASetupFixtures.Calculate(setup, htf, cursor);
            Assert.Equal(baselineBoundary, LaneASetupFixtures.EvaluateBoundary(
                englishContext, englishQuality, LaneASetupFixtures.BoundaryMinimumWidth));
            Assert.Equal(baselineCenter, LaneASetupFixtures.EvaluateCenter(
                englishContext, englishQuality));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void OnlyV1SetupFamiliesAndNoExecutionContractsAreExposed()
    {
        Assert.Equal(V1SetupTypes, Enum.GetValues<SetupType>());
        Assert.Equal(BoundaryRejectionSetupResult.CurrentAlgorithmVersion,
            "boundary-rejection-v1");
        Assert.Equal(CenterRetestSetupResult.CurrentAlgorithmVersion,
            "center-retest-v1");
        Assert.All(LaneAContractTypes, static type => Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => Assert.DoesNotContain(property.PropertyType,
                ForbiddenExecutionTypes)));
    }

    private static void AssertBoundaryInvariants(LaneAFixtureCase fixture,
        BoundaryRejectionSetupResult result)
    {
        Assert.Equal(2, result.Observations.Count);
        Assert.Equal(Direction.Long, result.Long.Direction);
        Assert.Equal(Direction.Short, result.Short.Direction);
        Assert.Equal(result.Long.IsReady || result.Short.IsReady, result.IsReady);
        Assert.Same(fixture.PriceContext, result.PriceContext);
        Assert.Same(fixture.Quality, result.Quality);
        Assert.Equal(BoundaryRejectionSetupResult.CurrentAlgorithmVersion,
            result.AlgorithmVersion);
        Assert.Equal(fixture.PriceContext.Cursor, result.Cursor);
        Assert.Equal<decimal?>(fixture.BoundaryMinimumWidth,
            result.Configuration.MinimumChannelWidth);
        AssertBoundaryObservation(fixture, result.Long);
        AssertBoundaryObservation(fixture, result.Short);
    }

    private static void AssertCenterInvariants(LaneAFixtureCase fixture,
        CenterRetestSetupResult result)
    {
        Assert.Equal(2, result.Observations.Count);
        Assert.Equal(Direction.Long, result.Long.Direction);
        Assert.Equal(Direction.Short, result.Short.Direction);
        Assert.Equal(result.Long.IsReady || result.Short.IsReady, result.IsReady);
        Assert.Same(fixture.PriceContext, result.PriceContext);
        Assert.Same(fixture.Quality, result.Quality);
        Assert.Equal(CenterRetestSetupResult.CurrentAlgorithmVersion,
            result.AlgorithmVersion);
        Assert.Equal(fixture.PriceContext.Cursor, result.Cursor);
        AssertCenterObservation(fixture, result.Long);
        AssertCenterObservation(fixture, result.Short);
    }

    private static void AssertBoundaryObservation(LaneAFixtureCase fixture,
        BoundaryRejectionSetupObservation observation)
    {
        Assert.Same(fixture.PriceContext.LocalTrend.CurrentBar, observation.SignalBar);
        Assert.Equal(observation.State == BoundaryRejectionSetupState.Ready,
            observation.IsReady);
        Assert.Equal(observation.State == BoundaryRejectionSetupState.Invalidated,
            observation.IsInvalidated);
        Assert.Equal<decimal?>(fixture.BoundaryMinimumWidth,
            observation.MinimumChannelWidth);
        AssertStableReasons(observation.Reasons);

        switch (observation.State)
        {
            case BoundaryRejectionSetupState.Ready:
                Assert.True(observation.Conditions.AllConditionsPassed);
                Assert.True(observation.Conditions.BarConfirmed);
                Assert.True(observation.Conditions.DataQualityPassed);
                Assert.True(observation.Conditions.MinimumChannelWidthPassed);
                break;
            case BoundaryRejectionSetupState.Invalidated:
                Assert.True(observation.Conditions.HtfContextMatches);
                Assert.True(observation.Conditions.LocalTrendMatches);
                Assert.True(observation.Conditions.BoundaryTouched);
                Assert.True(observation.Conditions.ClosedThroughBoundary);
                Assert.True(observation.Conditions.BarConfirmed);
                Assert.True(observation.Conditions.DataQualityPassed);
                Assert.False(observation.Conditions.CloseReclaimed);
                break;
            case BoundaryRejectionSetupState.NoSetup:
                Assert.False(observation.Conditions.AllConditionsPassed);
                break;
            default:
                Assert.False(observation.IsReady);
                Assert.NotEmpty(observation.Reasons);
                break;
        }
    }

    private static void AssertCenterObservation(LaneAFixtureCase fixture,
        CenterRetestSetupObservation observation)
    {
        Assert.Same(fixture.PriceContext.LocalTrend.CurrentBar, observation.SignalBar);
        Assert.Equal(observation.State == CenterRetestSetupState.Ready,
            observation.IsReady);
        Assert.Equal(observation.State == CenterRetestSetupState.Invalidated,
            observation.IsInvalidated);
        AssertStableReasons(observation.Reasons);

        switch (observation.State)
        {
            case CenterRetestSetupState.Ready:
                Assert.True(observation.Conditions.AllConditionsPassed);
                Assert.True(observation.Conditions.BarConfirmed);
                Assert.True(observation.Conditions.DataQualityPassed);
                break;
            case CenterRetestSetupState.Invalidated:
                Assert.True(observation.Conditions.HtfContextMatches);
                Assert.True(observation.Conditions.LocalTrendMatches);
                Assert.True(observation.Conditions.CenterTouched);
                Assert.True(observation.Conditions.ClosedThroughCenter);
                Assert.True(observation.Conditions.BarConfirmed);
                Assert.True(observation.Conditions.DataQualityPassed);
                Assert.False(observation.Conditions.CloseHoldsCenter);
                break;
            case CenterRetestSetupState.NoSetup:
                Assert.False(observation.Conditions.AllConditionsPassed);
                break;
            default:
                Assert.False(observation.IsReady);
                Assert.NotEmpty(observation.Reasons);
                break;
        }
    }

    private static void AssertStableReasons(ImmutableValueList<string> reasons)
    {
        string[] expected = reasons.OrderBy(static reason => reason,
                StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, reasons.ToArray());
    }

    private static void AssertCaseSpecificReasons(LaneAFixtureCase fixture,
        BoundaryRejectionSetupResult boundary, CenterRetestSetupResult center)
    {
        switch (fixture.Name)
        {
            case "center-long-only":
                Assert.Contains("lower-band-not-touched", boundary.Long.Reasons);
                break;
            case "center-short-only":
                Assert.Contains("upper-band-not-touched", boundary.Short.Reasons);
                break;
            case "center-long-no-width":
                Assert.Contains("channel-width-below-minimum", boundary.Long.Reasons);
                break;
            case "center-short-no-width":
                Assert.Contains("channel-width-below-minimum", boundary.Short.Reasons);
                break;
            case "both-long-unknown-quality":
                Assert.Contains("quality-evidence-missing:channel",
                    boundary.Long.Reasons);
                Assert.Contains("quality-evidence-missing:channel",
                    boundary.Short.Reasons);
                Assert.Contains("quality-evidence-missing:channel",
                    center.Long.Reasons);
                Assert.Contains("quality-evidence-missing:channel",
                    center.Short.Reasons);
                break;
            case "both-short-unknown-quality":
                Assert.Contains("quality-evidence-missing:channel",
                    boundary.Long.Reasons);
                Assert.Contains("quality-evidence-missing:channel",
                    boundary.Short.Reasons);
                Assert.Contains("quality-evidence-missing:channel",
                    center.Long.Reasons);
                Assert.Contains("quality-evidence-missing:channel",
                    center.Short.Reasons);
                break;
            case "both-long-invalidated":
                Assert.Contains("closed-through-lower-band", boundary.Long.Reasons);
                Assert.Contains("closed-through-center", center.Long.Reasons);
                break;
            case "both-short-invalidated":
                Assert.Contains("closed-through-upper-band", boundary.Short.Reasons);
                Assert.Contains("closed-through-center", center.Short.Reasons);
                break;
            case "center-long-invalidated-only":
                Assert.Contains("closed-through-center", center.Long.Reasons);
                break;
            case "center-short-invalidated-only":
                Assert.Contains("closed-through-center", center.Short.Reasons);
                break;
        }
    }

    private static UtcTimestamp Time(int offset) =>
        new(LaneASetupFixtures.BaseTime.AddMinutes(offset));
}
