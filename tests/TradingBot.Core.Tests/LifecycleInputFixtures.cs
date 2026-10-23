using TradingBot.Core;

namespace TradingBot.Core.Tests;

internal sealed record LifecycleInputFixture(
    string Name,
    SetupEvidenceSnapshot Evidence,
    SetupEvidenceState ExpectedState);

internal static class LifecycleInputFixtures
{
    internal static IReadOnlyList<LifecycleInputFixture> All { get; } = Create();

    internal static SetupEvidenceSnapshot CreateEvidence(LaneAFixtureCase fixture,
        bool boundary, Direction direction)
    {
        return boundary
            ? SetupEvidenceSnapshot.Create(
                LaneASetupFixtures.EvaluateBoundary(fixture), direction)
            : SetupEvidenceSnapshot.Create(
                LaneASetupFixtures.EvaluateCenter(fixture), direction);
    }

    private static List<LifecycleInputFixture> Create()
    {
        List<LifecycleInputFixture> fixtures = [];
        Add("both-long-ready", true, Direction.Long, SetupEvidenceState.Ready);
        Add("both-long-invalidated", true, Direction.Long, SetupEvidenceState.Invalidated);
        Add("center-long-only", true, Direction.Long, SetupEvidenceState.NoSetup);
        Add("both-long-unknown-quality", true, Direction.Long,
            SetupEvidenceState.UnknownQuality);
        Add("both-short-ready", true, Direction.Short, SetupEvidenceState.Ready);
        Add("both-short-invalidated", true, Direction.Short, SetupEvidenceState.Invalidated);
        Add("center-short-only", true, Direction.Short, SetupEvidenceState.NoSetup);
        Add("both-short-unknown-quality", true, Direction.Short,
            SetupEvidenceState.UnknownQuality);
        Add("both-long-ready", false, Direction.Long, SetupEvidenceState.Ready);
        Add("center-long-invalidated-only", false, Direction.Long,
            SetupEvidenceState.Invalidated);
        Add("boundary-long-only", false, Direction.Long, SetupEvidenceState.Invalidated);
        Add("both-long-unknown-quality", false, Direction.Long,
            SetupEvidenceState.UnknownQuality);
        Add("both-short-ready", false, Direction.Short, SetupEvidenceState.Ready);
        Add("center-short-invalidated-only", false, Direction.Short,
            SetupEvidenceState.Invalidated);
        Add("boundary-short-only", false, Direction.Short,
            SetupEvidenceState.Invalidated);
        Add("both-short-unknown-quality", false, Direction.Short,
            SetupEvidenceState.UnknownQuality);

        CausalEventCursor cursor = new(
            new UtcTimestamp(LaneASetupFixtures.BaseTime.AddMinutes(1)), 10_000);
        (PriceContextFeatureResult context, FeatureQualityEvaluation quality) =
            LaneASetupFixtures.Calculate(Array.Empty<ScreenerBar>(),
                Array.Empty<ScreenerBar>(), cursor);
        BoundaryRejectionSetupResult warmup = LaneASetupFixtures.EvaluateBoundary(
            context, quality, LaneASetupFixtures.BoundaryMinimumWidth);
        fixtures.Add(new LifecycleInputFixture("warmup-no-signal",
            SetupEvidenceSnapshot.Create(warmup, Direction.Long),
            SetupEvidenceState.UnknownWarmup));
        return fixtures;

        void Add(string name, bool boundary, Direction direction,
            SetupEvidenceState expected)
        {
            LaneAFixtureCase source = LaneASetupFixtures.Cases.Single(
                fixture => fixture.Name == name);
            fixtures.Add(new LifecycleInputFixture(
                $"{(boundary ? "boundary" : "center")}-{direction}-{name}",
                CreateEvidence(source, boundary, direction), expected));
        }
    }
}
