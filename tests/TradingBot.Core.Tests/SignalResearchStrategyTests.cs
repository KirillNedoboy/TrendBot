using System.Globalization;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SignalResearchStrategyTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly InstrumentId Instrument = new("TESTUSDT");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReclaimRequiresKnownLevelAndLaterClosedReturn(bool shortDirection)
    {
        ScreenerBar[] bars = ReclaimBars(shortDirection);
        SignalResearchStrategyPolicy policy = Policy(reclaim: true);
        SignalResearchStrategyEvaluation before = Evaluate(bars[..^1], policy);
        Assert.DoesNotContain(before.Candidates, item => item.Family == SetupType.FailedBreakoutReclaim);

        SignalResearchStrategyEvaluation after = Evaluate(bars, policy);
        SignalResearchStrategyCandidate candidate = Assert.Single(after.Candidates,
            item => item.Family == SetupType.FailedBreakoutReclaim
                && item.Direction == (shortDirection ? Direction.Short : Direction.Long));
        Assert.Equal(bars[1].Bar.EventId, candidate.AnchorBar.Bar.EventId);
        Assert.Equal(bars[^1].Bar.EventId, candidate.SignalBar.Bar.EventId);
    }

    [Fact]
    public void SameBarBreachAndReturnDoesNotBecomeALaterReclaim()
    {
        ScreenerBar[] original = ReclaimBars(false);
        ScreenerBar[] bars = [.. original[..4], Bar(4, 95m, 96m, 89m, 92m),
            Bar(5, 92m, 94m, 91m, 93m)];
        Assert.DoesNotContain(Evaluate(bars, Policy(reclaim: true)).Candidates,
            item => item.Family == SetupType.FailedBreakoutReclaim);
    }

    [Fact]
    public void ReclaimAtMaximumAgeIsStillEligible()
    {
        Assert.Contains(Evaluate(ReclaimBars(false), Policy(reclaim: true, maxAge: 1)).Candidates,
            item => item.Family == SetupType.FailedBreakoutReclaim
                && item.Direction == Direction.Long);
    }

    [Fact]
    public void ReclaimDoesNotUseLevelConfirmedAfterBreach()
    {
        ScreenerBar[] bars = ReclaimBars(false);
        PriceContextFeatureResult context = Context(bars);
        PriceContextPivotEvidence source = Assert.Single(context.ConfirmedPivots.Pivots,
            item => item.Kind == PriceContextPivotKind.Low && item.PivotBar.Bar.EventId == bars[1].Bar.EventId);
        PriceContextPivotEvidence late = new(source.Kind, source.PivotBar, source.Anchor,
            source.AtrAtConfirmation, source.FrozenBuffer, source.PivotLeft, source.PivotRight,
            source.PivotWindow, source.EventTime, source.ReceiveTime, source.AvailabilityTime,
            source.StableSequence, source.ClosedBarTime, bars[4].CloseTime,
            source.ConfirmationSequence);
        PriceContextFeatureResult delayed = WithPivots(context, [late]);
        Assert.DoesNotContain(SignalResearchStrategyEvaluator.Evaluate(delayed, Policy(reclaim: true)).Candidates,
            item => item.Family == SetupType.FailedBreakoutReclaim);
    }

    [Fact]
    public void PivotSequenceAfterBreachDoesNotRewriteEarlierKnowledge()
    {
        ScreenerBar[] bars = ReclaimBars(false);
        PriceContextFeatureResult context = Context(bars);
        PriceContextPivotEvidence source = Assert.Single(context.ConfirmedPivots.Pivots,
            item => item.Kind == PriceContextPivotKind.Low && item.PivotBar.Bar.EventId == bars[1].Bar.EventId);
        PriceContextPivotEvidence reordered = new(source.Kind, source.PivotBar, source.Anchor,
            source.AtrAtConfirmation, source.FrozenBuffer, source.PivotLeft, source.PivotRight,
            source.PivotWindow, source.EventTime, source.ReceiveTime, source.AvailabilityTime,
            source.StableSequence, source.ClosedBarTime, source.ConfirmationTime,
            bars[4].StableSequence);
        SignalResearchStrategyEvaluation result = SignalResearchStrategyEvaluator.Evaluate(
            WithPivots(context, [reordered]), Policy(reclaim: true));
        Assert.Empty(result.Candidates);
        Assert.Contains("research-pivot-sequence-after-bar", result.UnknownReasons);
    }

    [Fact]
    public void ReclaimExpiresWithoutLaterReturn()
    {
        ScreenerBar[] initial = ReclaimBars(false);
        ScreenerBar[] bars = [.. initial[..^1], Bar(5, 89m, 90m, 87m, 89m),
            Bar(6, 89m, 94m, 88m, 92m)];
        Assert.DoesNotContain(Evaluate(bars, Policy(reclaim: true, maxAge: 1)).Candidates,
            item => item.Family == SetupType.FailedBreakoutReclaim);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetestMustFollowBreakoutAndContinuationMustFollowRetest(bool shortDirection)
    {
        ScreenerBar[] bars = RetestBars(shortDirection);
        SignalResearchStrategyPolicy policy = Policy(retest: true);
        Assert.DoesNotContain(Evaluate(bars[..^1], policy).Candidates,
            item => item.Family == SetupType.BreakoutRetestContinuation);
        SignalResearchStrategyCandidate candidate = Assert.Single(Evaluate(bars, policy).Candidates,
            item => item.Family == SetupType.BreakoutRetestContinuation
                && item.Direction == (shortDirection ? Direction.Short : Direction.Long));
        Assert.Equal(bars[1].Bar.EventId, candidate.AnchorBar.Bar.EventId);
        Assert.Equal(bars[^1].Bar.EventId, candidate.SignalBar.Bar.EventId);
    }

    [Fact]
    public void UnavailableFutureBarsAndHistoryGapsNeverGenerateCandidates()
    {
        ScreenerBar[] bars = ReclaimBars(false);
        PriceContextFeatureResult prefix = Context(bars[..^1]);
        PriceContextFeatureResult withFuture = WithInput(prefix, bars);
        Assert.Equal(Evaluate(bars[..^1], Policy(reclaim: true)),
            SignalResearchStrategyEvaluator.Evaluate(withFuture, Policy(reclaim: true)));

        SignalResearchStrategyEvaluation gap = Evaluate([bars[0], bars[1], .. bars[3..]],
            Policy(reclaim: true));
        Assert.Empty(gap.Candidates);
        Assert.Contains("research-history-gap-or-duplicate", gap.UnknownReasons);
    }

    [Theory]
    [InlineData(SetupType.FlowStochasticRecovery, false)]
    [InlineData(SetupType.FlowRsiRecovery, false)]
    [InlineData(SetupType.FlowAtrRecovery, false)]
    [InlineData(SetupType.FlowStochasticRecovery, true)]
    [InlineData(SetupType.FlowRsiRecovery, true)]
    [InlineData(SetupType.FlowAtrRecovery, true)]
    public void FlowMethodsProduceIndependentClosedBarRecovery(SetupType family, bool shortDirection)
    {
        ScreenerBar[] bars =
        [
            Bar(0, 10m, 10.2m, 9.8m, 10m),
            Bar(1, 10m, 10.2m, 7.8m, 8m),
            Bar(2, 8m, 8.2m, 5.8m, 6m),
            Bar(3, 6m, 7.6m, 5.9m, 7.4m)
        ];
        if (shortDirection)
        {
            bars = bars.Select(Mirror).ToArray();
        }
        SignalResearchStrategyPolicy policy = Policy(flow: family);
        SignalResearchStrategyEvaluation evaluation = Evaluate(bars, policy);
        SignalResearchStrategyCandidate candidate = Assert.Single(evaluation.Candidates,
            item => item.Family == family
                && item.Direction == (shortDirection ? Direction.Short : Direction.Long)
                && item.SignalBar.Bar.EventId == bars[^1].Bar.EventId);
        string observed = Assert.Single(candidate.Reasons,
            reason => reason.StartsWith("observed-value:", StringComparison.Ordinal));
        decimal value = decimal.Parse(observed["observed-value:".Length..], CultureInfo.InvariantCulture);
        decimal expected = family switch
        {
            SetupType.FlowStochasticRecovery => 66.66666667m,
            SetupType.FlowRsiRecovery => 41.17647059m,
            SetupType.FlowAtrRecovery => 53.70370370m,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        if (shortDirection)
        {
            expected = 100m - expected;
        }
        Assert.InRange(value, expected - 0.00001m, expected + 0.00001m);
        Assert.DoesNotContain(evaluation.Candidates, item => item.Family != family);
    }

    [Fact]
    public void FlowJumpDirectlyIntoOppositeExtremeIsNotARecovery()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 10m, 10.2m, 9.8m, 10m),
            Bar(1, 10m, 10.2m, 7.8m, 8m),
            Bar(2, 8m, 8.2m, 5.8m, 6m),
            Bar(3, 6m, 8.3m, 5.9m, 8.2m)
        ];
        Assert.DoesNotContain(Evaluate(bars, Policy(flow: SetupType.FlowStochasticRecovery)).Candidates,
            item => item.Direction == Direction.Long);
    }

    [Fact]
    public void ConstantPricesDoNotInventAnOscillatorRecovery()
    {
        ScreenerBar[] bars = Enumerable.Range(0, 6)
            .Select(index => Bar(index, 10m, 10m, 10m, 10m)).ToArray();
        SignalResearchStrategyEvaluation result = Evaluate(bars,
            Policy(flow: SetupType.FlowRsiRecovery));
        Assert.Empty(result.Candidates);
        Assert.Contains("research-flow-rsi-warmup-or-zero-movement", result.UnknownReasons);
    }

    [Fact]
    public void CompressionNeedsPriorNarrowingThenExpansionAndBreakout()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 110m, 90m, 100m),
            Bar(1, 100m, 105m, 95m, 100m),
            Bar(2, 100m, 103m, 97m, 100m),
            Bar(3, 100m, 102m, 98m, 100m),
            Bar(4, 100m, 101m, 99m, 100m),
            Bar(5, 100m, 104m, 99m, 102m)
        ];
        SignalResearchStrategyPolicy policy = Policy(compression: true);
        Assert.DoesNotContain(Evaluate(bars[..^1], policy).Candidates,
            item => item.Family == SetupType.VolatilityCompressionExpansion);
        SignalResearchStrategyCandidate candidate = Assert.Single(Evaluate(bars, policy).Candidates,
            item => item.Family == SetupType.VolatilityCompressionExpansion);
        Assert.Equal(Direction.Long, candidate.Direction);
        Assert.Equal(bars[4].Bar.EventId, candidate.AnchorBar.Bar.EventId);
    }

    [Fact]
    public void DisabledTimeframesDoNotCreateResearchCandidates()
    {
        SignalResearchStrategyPolicy policy = Policy();
        SignalResearchStrategyEvaluation result = Evaluate(ReclaimBars(false), policy);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.UnknownReasons);
    }

    private static SignalResearchStrategyEvaluation Evaluate(ScreenerBar[] bars,
        SignalResearchStrategyPolicy policy) => SignalResearchStrategyEvaluator.Evaluate(Context(bars), policy);

    private static PriceContextFeatureResult Context(ScreenerBar[] bars)
    {
        PriceContextFeatureInput input = Input(bars);
        CausalEventCursor cursor = new(new UtcTimestamp(bars[^1].CloseTime.Value.AddSeconds(2)), 10_000);
        PriceContextFeatureConfiguration configuration = new("15m", "1h",
            new PriceContextFeatureVersion(PriceContextFeatureCalculator.CurrentAlgorithmVersion),
            1, 1, 2, PriceContextAtrMode.Rma, PriceContextChannelCenterKind.Ema, 2, null,
            PriceContextChannelWidthKind.Atr, 2, 1m, 1m, 1, 2,
            PriceContextMovingAverageKind.Ema, 1, null, null, null);
        PriceContextFeatureResult calculated = PriceContextFeatureCalculator.Calculate(input, cursor, configuration);
        PriceContextLocalTrend local = new(PriceContextFeatureState.Ready,
            PriceContextTrendDirection.Bull, 1m, 1m, bars[^1].Close.Value, bars[^1],
            new ImmutableValueList<string>([]));
        return new PriceContextFeatureResult(configuration, input, cursor, calculated.Quality,
            calculated.ConfirmedPivots, calculated.StructuralLevels, calculated.Channel,
            local, calculated.HtfContext, calculated.Vwap, calculated.Poc);
    }

    private static PriceContextFeatureResult WithInput(PriceContextFeatureResult original,
        ScreenerBar[] bars) => new(original.Configuration, Input(bars), original.Cursor,
            original.Quality, original.ConfirmedPivots, original.StructuralLevels,
            original.Channel, original.LocalTrend, original.HtfContext, original.Vwap, original.Poc);

    private static PriceContextFeatureResult WithPivots(PriceContextFeatureResult original,
        IEnumerable<PriceContextPivotEvidence> pivots) => new(original.Configuration,
            original.Input, original.Cursor, original.Quality,
            new PriceContextConfirmedPivots(PriceContextFeatureState.Ready,
                new ImmutableValueList<PriceContextPivotEvidence>(pivots), 1, 1, 3,
                original.Input.SetupBars.Count, PriceContextFeatureCompleteness.Complete,
                new ImmutableValueList<string>([])), original.StructuralLevels,
            original.Channel, original.LocalTrend, original.HtfContext, original.Vwap, original.Poc);

    private static PriceContextFeatureInput Input(ScreenerBar[] bars) => new("fixture", Instrument,
        "15m", new UtcTimestamp(Start), bars, "1h", new UtcTimestamp(Start), [], []);

    private static SignalResearchStrategyPolicy Policy(bool reclaim = false, bool retest = false,
        SetupType? flow = null, bool compression = false, int maxAge = 10)
    {
        ImmutableValueList<string> Frames(bool enabled) => new(enabled ? ["15m"] : []);
        return new SignalResearchStrategyPolicy(1, "fixture-policy", Frames(true), Frames(true),
            Frames(reclaim), Frames(retest),
            Frames(flow == SetupType.FlowStochasticRecovery), Frames(flow == SetupType.FlowRsiRecovery),
            Frames(flow == SetupType.FlowAtrRecovery), Frames(compression), maxAge,
            2, 1, 30m, 70m, 1m, 2);
    }

    private static ScreenerBar[] ReclaimBars(bool mirror)
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 105m, 95m, 100m),
            Bar(1, 100m, 101m, 90m, 95m),
            Bar(2, 95m, 101m, 94m, 97m),
            Bar(3, 97m, 100m, 91m, 95m),
            Bar(4, 95m, 96m, 89m, 89m),
            Bar(5, 89m, 94m, 88m, 92m)
        ];
        return mirror ? bars.Select(Mirror).ToArray() : bars;
    }

    private static ScreenerBar[] RetestBars(bool mirror)
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 105m, 95m, 100m),
            Bar(1, 100m, 110m, 95m, 105m),
            Bar(2, 105m, 108m, 97m, 100m),
            Bar(3, 100m, 109m, 98m, 105m),
            Bar(4, 105m, 113m, 104m, 112m),
            Bar(5, 112m, 113m, 109m, 111m),
            Bar(6, 111m, 115m, 110m, 114m)
        ];
        return mirror ? bars.Select(Mirror).ToArray() : bars;
    }

    private static ScreenerBar Mirror(ScreenerBar bar) => Bar((int)(bar.Bar.OpenTime.Value - Start).TotalMinutes / 15,
        200m - bar.Bar.Open.Value, 200m - bar.Bar.Low.Value,
        200m - bar.Bar.High.Value, 200m - bar.Close.Value);

    private static ScreenerBar Bar(int index, decimal open, decimal high, decimal low, decimal close)
    {
        UtcTimestamp openTime = new(Start.AddMinutes(index * 15));
        UtcTimestamp closeTime = new(Start.AddMinutes((index + 1) * 15));
        Bar bar = new($"bar-{index}", Instrument, closeTime, openTime, closeTime,
            new Price(open), new Price(high), new Price(low), new Price(close), new Quantity(10m));
        UtcTimestamp available = new(closeTime.Value.AddSeconds(1));
        return new ScreenerBar("fixture", bar, 100m, available, available, index + 1);
    }
}
