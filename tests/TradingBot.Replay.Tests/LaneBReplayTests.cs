using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class LaneBReplayTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ExistingReplayRunnerProducesPermutationDeterministicLaneBPrefixes()
    {
        ReplayEnvelope[] source = Envelopes();
        LaneBInputConfiguration configuration = new(
            "lane-b-input-v1", null, "rate", "periodic", false, true, false);

        List<string> ordered = Run(source, configuration);
        List<string> permuted = Run([source[1], source[0]], configuration);

        Assert.Equal(ordered, permuted);
        Assert.Equal("oi:MissingData:0", ordered[0]);
        Assert.Equal("funding:Ready:1", ordered[1]);
    }

    [Fact]
    public void DelayedAvailabilityDoesNotRewriteTheEarlierLaneBResult()
    {
        ReplayEnvelope[] source = Envelopes();
        LaneBInputConfiguration configuration = new(
            "lane-b-input-v1", null, "rate", "periodic", false, true, false);
        List<(string EventId, LaneBInputStatus Status, int FundingCount)> decisions = [];

        ReplayRunResult result = HistoricalReplayRunner.Run("lane-b", source, context =>
        {
            LaneBInput input = BuildInput(context.Prefix);
            LaneBInputSelection selection = input.Select(context.Cursor, configuration);
            decisions.Add((context.Event.EventId, selection.Status, selection.Funding.Count));
            if (context.Event.EventId == "oi-1")
            {
                Assert.DoesNotContain(context.Prefix.Events,
                    envelope => envelope.CausalEvent.Payload.EventId == "funding-1");
            }

            return selection.Status;
        });

        Assert.True(result.Succeeded);
        Assert.Equal(LaneBInputStatus.MissingData, decisions[0].Status);
        Assert.Equal(0, decisions[0].FundingCount);
        Assert.Equal(LaneBInputStatus.Ready, decisions[^1].Status);
        Assert.Equal(1, decisions[^1].FundingCount);
    }

    [Fact]
    public void ReplayPrefixFeedsTheSameLaneBEvaluatorAtEveryCursor()
    {
        LaneBInputConfiguration inputConfiguration = new(
            "lane-b-input-v1", null, "rate", "periodic", false, true, false);
        LaneBRuleTable rules = new("rules-v1", [
            new LaneBRule("funding-rule", SetupType.BoundaryRejection, Direction.Long,
                "rule-v1", [
                    new LaneBRuleCondition("funding", LaneBMetricKind.FundingRate,
                        LaneBComparisonOperator.GreaterThan, 0m)
                ], LaneBShadowStatus.Confirm)
        ]);
        LaneBEvaluator evaluator = new(rules);
        List<LaneBShadowStatus> outcomes = [];

        ReplayRunResult run = HistoricalReplayRunner.Run("lane-b", Envelopes(), context =>
        {
            LaneBShadowResult shadow = evaluator.Evaluate(BuildInput(context.Prefix),
                context.Cursor, inputConfiguration, [], [], SetupType.BoundaryRejection,
                Direction.Long);
            outcomes.Add(shadow.Status);
        });

        Assert.True(run.Succeeded);
        Assert.Equal([LaneBShadowStatus.InsufficientData, LaneBShadowStatus.Confirm], outcomes);
    }

    [Fact]
    public void EqualTimestampsUseStableSequenceBeforeBuildingLaneBInput()
    {
        ReplayEnvelope laterSequence = new("lane-b", new CausalMarketEvent(
            new FundingUpdate("funding-2", Instrument, Utc(1), 0.02m), Utc(1), Utc(1), 2));
        ReplayEnvelope earlierSequence = new("lane-b", new CausalMarketEvent(
            new FundingUpdate("funding-1", Instrument, Utc(1), 0.01m), Utc(1), Utc(1), 1));
        List<string> observed = [];

        ReplayRunResult run = HistoricalReplayRunner.Run("lane-b",
            [laterSequence, earlierSequence], context =>
            {
                observed.Add(context.Event.EventId);
                return null;
            });

        Assert.True(run.Succeeded);
        Assert.Equal(["funding-1", "funding-2"], observed);
    }

    private static List<string> Run(IEnumerable<ReplayEnvelope> envelopes,
        LaneBInputConfiguration configuration)
    {
        List<string> decisions = [];
        ReplayRunResult result = HistoricalReplayRunner.Run("lane-b", envelopes, context =>
        {
            LaneBInputSelection selection = BuildInput(context.Prefix).Select(
                context.Cursor, configuration);
            decisions.Add($"{context.Event.GetType().Name.Replace("Update", "",
                StringComparison.OrdinalIgnoreCase).ToLowerInvariant()}:{selection.Status}:"
                + selection.Funding.Count);
        });
        Assert.True(result.Succeeded);
        return decisions;
    }

    private static LaneBInput BuildInput(ReplayStreamSnapshot prefix)
    {
        List<LaneBOpenInterestObservation> openInterest = [];
        List<LaneBFundingObservation> funding = [];
        foreach (ReplayEnvelope envelope in prefix.Events)
        {
            switch (envelope.CausalEvent.Payload)
            {
                case OIUpdate oi:
                    openInterest.Add(new LaneBOpenInterestObservation(
                        "fixture-oi", envelope.CausalEvent, "contracts", "replay-fixture"));
                    break;
                case FundingUpdate update:
                    funding.Add(new LaneBFundingObservation(
                        "fixture-funding", envelope.CausalEvent, "rate", "periodic",
                        "replay-fixture"));
                    break;
            }
        }

        return new LaneBInput(Instrument, openInterest, funding, "replay-fixture");
    }

    private static ReplayEnvelope[] Envelopes() =>
    [
        new("lane-b", new CausalMarketEvent(
            new OIUpdate("oi-1", Instrument, Utc(0), 100m), Utc(0), Utc(0), 1)),
        new("lane-b", new CausalMarketEvent(
            new FundingUpdate("funding-1", Instrument, Utc(1), 0.01m), Utc(1), Utc(3), 2))
    ];

    private static UtcTimestamp Utc(int offset) => new(BaseTime.AddMinutes(offset));
}
