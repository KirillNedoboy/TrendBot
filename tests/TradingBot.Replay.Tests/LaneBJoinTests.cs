using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class LaneBJoinTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void JoinIsPermutationDeterministicAndPreservesCausalProvenance()
    {
        ILaneBCausalObservation[] observations = Observations();
        LaneBJoinSnapshot ordered = BuildJoin(observations, Cursor(3, 3));
        LaneBJoinSnapshot permuted = BuildJoin([observations[2], observations[0], observations[1]],
            Cursor(3, 3));

        Assert.Equal(ordered.Identity, permuted.Identity);
        Assert.Equal(ordered.Observations, permuted.Observations);
        LaneBObservationReference first = ordered.Observations[0];
        Assert.Equal(Utc(1), first.EventTime);
        Assert.Equal(Utc(0), first.ExchangeTime);
        Assert.Equal(Utc(2), first.ReceiveTime);
        Assert.Equal(Utc(3), first.AvailabilityTime);
        Assert.Equal(1, first.StableSequence);
        Assert.Equal("funding-source", first.SourceId);
        Assert.Equal("fixture-provenance", first.Provenance);
    }

    [Fact]
    public void EqualTimesUseStableSequenceAndExactDuplicatesCollapse()
    {
        LaneBFundingObservation first = Funding("funding-1", 0.01m, 2);
        LaneBFundingObservation exactDuplicate = Funding("funding-1", 0.01m, 2);
        LaneBFundingObservation second = Funding("funding-2", 0.02m, 1);
        LaneBJoinSnapshot join = BuildJoin([first, exactDuplicate, second]);

        Assert.Equal(2, join.Observations.Count);
        Assert.Equal("funding-2", join.Observations[0].EventId);
        Assert.Equal("funding-1", join.Observations[1].EventId);
        Assert.Contains(join.Reasons, reason => reason.Contains("duplicate", StringComparison.Ordinal));
        Assert.NotEqual(LaneBQualityStatus.Conflict, join.Quality.Status);
    }

    [Fact]
    public void ConflictingIdentityPreservesBothRowsAndBlocksQuality()
    {
        LaneBFundingObservation original = Funding("funding-1", 0.01m, 1);
        LaneBFundingObservation conflict = Funding("funding-1", 0.02m, 1);
        LaneBJoinSnapshot join = BuildJoin([original, conflict]);

        Assert.Equal(2, join.Observations.Count);
        Assert.Equal(LaneBQualityStatus.Conflict, join.Quality.Status);
        Assert.Contains(join.Quality.Reasons,
            reason => reason.Contains("conflict", StringComparison.Ordinal));
        Assert.Contains(join.Reasons,
            reason => reason.Contains("conflict", StringComparison.Ordinal));
    }

    [Fact]
    public void FutureAndDelayedObservationsAreFilteredBeforeOrderingAndReasons()
    {
        LaneBFundingObservation visible = Funding("visible", 0.01m, 1);
        LaneBFundingObservation future = new("funding-source",
            new FundingUpdate("future", Instrument, Utc(10), 0.03m), "rate", "periodic",
            "fixture-provenance", Utc(10), Utc(10), 10);
        LaneBFundingObservation delayed = new("funding-source",
            new FundingUpdate("delayed", Instrument, Utc(1), 0.04m), "rate", "periodic",
            "fixture-provenance", Utc(1), Utc(5), 3);

        LaneBJoinSnapshot join = BuildJoin([future, delayed, visible], Cursor(2, 2));

        Assert.Single(join.Observations);
        Assert.Equal("visible", join.Observations[0].EventId);
        Assert.DoesNotContain(join.Reasons, reason => reason.Contains("future", StringComparison.Ordinal));
        Assert.DoesNotContain(join.Reasons, reason => reason.Contains("delayed", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoricalReplayRunnerFeedsOnlyTheCurrentPrefixToTheJoin()
    {
        ReplayEnvelope[] envelopes = [
            new("lane-b", new CausalMarketEvent(
                new FundingUpdate("funding-1", Instrument, Utc(1), 0.01m),
                Utc(1), Utc(1), 1)),
            new("lane-b", new FundingUpdate("unused", Instrument, Utc(3), 0.02m)
                .ToCausal(Utc(3), Utc(4), 2))
        ];
        List<(string EventId, int Count)> observed = [];

        ReplayRunResult run = HistoricalReplayRunner.Run("lane-b", envelopes, context =>
        {
            ILaneBCausalObservation[] prefix = context.Prefix.Events
                .Where(value => value.CausalEvent.Payload is FundingUpdate)
                .Select(value => (ILaneBCausalObservation)new LaneBFundingObservation(
                    "funding-source", value.CausalEvent, "rate", "periodic",
                    "fixture-provenance"))
                .ToArray();
            LaneBJoinSnapshot join = BuildJoin(prefix, context.Cursor);
            observed.Add((context.Event.EventId, join.Observations.Count));
        });

        Assert.True(run.Succeeded);
        Assert.Equal(("funding-1", 1), observed[0]);
        Assert.Equal(("unused", 2), observed[1]);
    }

    [Fact]
    public void JoinSnapshotRoundTripsStrictly()
    {
        LaneBJoinSnapshot join = BuildJoin(Observations(), Cursor(3, 3));

        LaneBJoinSnapshot restored = CoreJsonSerializer.Deserialize<LaneBJoinSnapshot>(
            CoreJsonSerializer.Serialize(join));

        Assert.Equal(join, restored);
        Assert.Equal(join.Identity, restored.Identity);
        Assert.Equal(join.Quality, restored.Quality);
        Assert.Equal(join.Shadow, restored.Shadow);
    }

    private static LaneBJoinSnapshot BuildJoin(IEnumerable<ILaneBCausalObservation> observations,
        CausalEventCursor? cursor = null)
    {
        CausalEventCursor effectiveCursor = cursor ?? Cursor(3, 3);
        ILaneBCausalObservation[] source = observations.ToArray();
        LaneBInput input = new(Instrument,
            source.OfType<LaneBOpenInterestObservation>(),
            source.OfType<LaneBFundingObservation>(), "fixture");
        LaneBInputConfiguration configuration = new(
            "input-v1", null, "rate", "periodic", false, true, false);
        LaneBRuleTable rules = new("rules-v1", [
            new LaneBRule("funding-rule", SetupType.BoundaryRejection, Direction.Long,
                "rule-v1", [
                    new LaneBRuleCondition("funding", LaneBMetricKind.FundingRate,
                        LaneBComparisonOperator.GreaterThan, 0m)
                ], LaneBShadowStatus.Confirm)
        ]);
        LaneBFreshnessPolicy policy = new(null, TimeSpan.FromMinutes(5), null, null);
        LaneBShadowResult shadow = new LaneBEvaluator(rules).Evaluate(input, effectiveCursor,
            configuration, [], [], SetupType.BoundaryRejection, Direction.Long, policy);
        LaneBQualityState quality = LaneBQualityEvaluator.Evaluate(shadow);
        return LaneBJoinSnapshot.Create(effectiveCursor, source, quality, shadow);
    }

    private static ILaneBCausalObservation[] Observations() => [
        Funding("funding-1", 0.01m, 1, Utc(0), Utc(2), Utc(3)),
        Funding("funding-2", 0.02m, 2),
        new LaneBFailedContinuationObservation("behavior-source", "fc-1", Instrument,
            Utc(1), Utc(1), Utc(1), Utc(0), 3, "failed-continuation-v1",
            LaneBFailedContinuationOutcome.Observed, ["setup-1"], "fixture-provenance")
    ];

    private static LaneBFundingObservation Funding(string eventId, decimal value,
        long stableSequence, UtcTimestamp? exchangeTime = null,
        UtcTimestamp? receiveTime = null, UtcTimestamp? availabilityTime = null) =>
        new("funding-source", new FundingUpdate(eventId, Instrument, Utc(1), value), "rate",
            "periodic", "fixture-provenance", receiveTime ?? Utc(1),
            availabilityTime ?? Utc(1), stableSequence, exchangeTime);

    private static CausalEventCursor Cursor(int offset, long sequence) =>
        new(Utc(offset), sequence);

    private static UtcTimestamp Utc(int offset) => new(BaseTime.AddMinutes(offset));
}

internal static class LaneBJoinTestExtensions
{
    public static CausalMarketEvent ToCausal(this FundingUpdate value,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime, long stableSequence) =>
        new(value, receiveTime, availabilityTime, stableSequence);
}
