using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DonchianFlowSignalEvaluatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly InstrumentId Instrument = new("TESTUSDT");
    private static readonly DonchianFlowSignalPolicy Policy = new(0.000001m);

    [Fact]
    public void ClosedWickBreakoutProducesLongEvenWhenCloseReturnsInside()
    {
        ScreenerBar[] bars = History();
        bars[^1] = Bar(55, 100m, 104m, 98m, 101m, 3m);
        DonchianFlowSignalResult result = Evaluate(bars);
        Assert.Equal(DonchianFlowSignalState.Ready, result.State);
        Assert.Equal(Direction.Long, result.Direction);
        Assert.Equal(102m, result.Upper55);
        Assert.Equal(1.1m, result.VolumeMean);
        Assert.True(result.FastWma > result.SlowWma);
        Assert.True(result.SignalBar!.Close.Value < result.Upper55);
    }

    [Fact]
    public void ShortBreakoutUsesLowAndFallingFlowWma()
    {
        ScreenerBar[] bars = History();
        bars[^1] = Bar(55, 100m, 101m, 88m, 90m, 3m);
        DonchianFlowSignalResult result = Evaluate(bars);
        Assert.Equal(DonchianFlowSignalState.Ready, result.State);
        Assert.Equal(Direction.Short, result.Direction);
        Assert.Equal(98m, result.Lower55);
        Assert.True(result.FastWma < result.SlowWma);
    }

    [Fact]
    public void VolumeMeanIncludesCurrentBarAndEqualityWithChannelIsNotBreakout()
    {
        ScreenerBar[] bars = History();
        bars[^1] = Bar(55, 100m, 104m, 98m, 101m, 1.5m);
        DonchianFlowSignalResult lowVolume = Evaluate(bars);
        Assert.Equal(1.025m, lowVolume.VolumeMean);
        Assert.Null(lowVolume.Direction);
        bars[^1] = Bar(55, 100m, 102m, 98m, 101m, 3m);
        Assert.Null(Evaluate(bars).Direction);
    }

    [Fact]
    public void MissingHistoryGapAndZeroFlowFailClosed()
    {
        ScreenerBar[] bars = History();
        Assert.Equal(DonchianFlowSignalState.UnknownWarmup, Evaluate(bars[..^1]).State);
        ScreenerBar[] gap = bars.Where((_, index) => index != 20).ToArray();
        Assert.Equal(DonchianFlowSignalState.UnknownQuality,
            Evaluate([Bar(-1, 100m, 102m, 98m, 101m, 1m), .. gap]).State);
        ScreenerBar[] zeroVolume = Enumerable.Range(0, 56)
            .Select(index => Bar(index, 100m, 102m, 98m, 101m, 0m)).ToArray();
        Assert.Equal(DonchianFlowSignalState.UnknownZeroDenominator, Evaluate(zeroVolume).State);
    }

    [Fact]
    public void FutureBarCannotChangeEarlierResult()
    {
        ScreenerBar[] bars = History();
        bars[^1] = Bar(55, 100m, 104m, 98m, 101m, 3m);
        CausalEventCursor cursor = Cursor(bars[^1]);
        DonchianFlowSignalResult before = DonchianFlowSignalEvaluator.Evaluate(bars, cursor, Policy);
        ScreenerBar future = Bar(56, 100m, 105m, 90m, 95m, 5m);
        Assert.Equal(before, DonchianFlowSignalEvaluator.Evaluate([.. bars, future], cursor, Policy));
    }

    [Fact]
    public void ContextChannelsNeedTheirOwnCompletePastHistory()
    {
        ScreenerBar[] bars = Enumerable.Range(0, 145)
            .Select(index => Bar(index, 100m, 102m, 98m, 101m, 1m)).ToArray();
        bars[^1] = Bar(144, 100m, 104m, 98m, 101m, 3m);
        DonchianFlowSignalResult result = Evaluate(bars);
        Assert.Equal(98m, result.Lower13);
        Assert.Equal(102m, result.Upper21);
        Assert.Equal(98m, result.Lower89);
        Assert.Equal(102m, result.Upper144);
        Assert.Null(Evaluate(bars[^56..]).Upper89);
    }

    [Fact]
    public void SimultaneousOppositeBreakoutsDoNotCreateAnEntry()
    {
        ScreenerBar[] bars = History();
        bars[^1] = Bar(55, 100m, 104m, 97m, 101m, 3m);
        DonchianFlowSignalResult result = Evaluate(bars);
        Assert.Null(result.Direction);
        Assert.Equal("simultaneous-opposite-breakouts", result.UnknownReason);
    }

    [Fact]
    public void ThirtyMinuteHistoryUsesSameFormulaAndRejectsGapsAndMixedDurations()
    {
        ScreenerBar[] bars = Enumerable.Range(0, 145)
            .Select(index => Bar(index, 100m, 102m, 98m, 101m, 1m, 30)).ToArray();
        bars[^1] = Bar(144, 100m, 104m, 98m, 101m, 3m, 30);
        DonchianFlowSignalResult result = Evaluate(bars);
        Assert.Equal(DonchianFlowSignalState.Ready, result.State);
        Assert.Equal(Direction.Long, result.Direction);
        Assert.Equal(102m, result.Upper55);
        Assert.Equal(102m, result.Upper89);
        Assert.Equal(98m, result.Lower13);
        Assert.Equal(1.1m, result.VolumeMean);

        ScreenerBar[] gap = bars.Where((_, index) => index != 120).ToArray();
        Assert.Equal(DonchianFlowSignalState.UnknownQuality, Evaluate(gap).State);
        ScreenerBar[] mixed = (ScreenerBar[])bars.Clone();
        mixed[120] = Bar(60, 100m, 102m, 98m, 101m, 1m);
        Assert.Equal(DonchianFlowSignalState.UnknownQuality, Evaluate(mixed).State);
        mixed = (ScreenerBar[])bars.Clone();
        mixed[60] = Bar(30, 100m, 102m, 98m, 101m, 1m);
        DonchianFlowSignalResult incompleteTarget = Evaluate(mixed);
        Assert.Equal(Direction.Long, incompleteTarget.Direction);
        Assert.Null(incompleteTarget.Upper89);
        ScreenerBar[] unsupported = Enumerable.Range(0, 56)
            .Select(index => Bar(index, 100m, 102m, 98m, 101m, 1m, 15)).ToArray();
        Assert.Equal(DonchianFlowSignalState.UnknownQuality, Evaluate(unsupported).State);
    }

    private static ScreenerBar[] History() => Enumerable.Range(0, 56)
        .Select(index => Bar(index, 100m, 102m, 98m, 101m, 1m)).ToArray();

    private static DonchianFlowSignalResult Evaluate(ScreenerBar[] bars) =>
        DonchianFlowSignalEvaluator.Evaluate(bars, Cursor(bars[^1]), Policy);

    private static CausalEventCursor Cursor(ScreenerBar bar) =>
        new(new UtcTimestamp(bar.CloseTime.Value.AddSeconds(2)), 10000);

    private static ScreenerBar Bar(int index, decimal open, decimal high, decimal low,
        decimal close, decimal volume, int minutes = 60)
    {
        UtcTimestamp openTime = new(Start.AddMinutes(index * minutes));
        UtcTimestamp closeTime = new(Start.AddMinutes((index + 1) * minutes));
        Bar bar = new($"bar-{index}", Instrument, closeTime, openTime, closeTime,
            new Price(open), new Price(high), new Price(low), new Price(close), new Quantity(volume));
        UtcTimestamp available = new(closeTime.Value.AddSeconds(1));
        return new ScreenerBar("fixture", bar, volume * close, available, available, index + 2);
    }
}
