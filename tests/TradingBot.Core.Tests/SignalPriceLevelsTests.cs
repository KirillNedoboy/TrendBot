using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SignalPriceLevelsTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> EnabledProfiles()
    {
        foreach (SetupType family in Enum.GetValues<SetupType>()
            .Where(static family => family != SetupType.DonchianFlowBreakout))
        {
            string[] timeframes = family switch
            {
                SetupType.BoundaryRejection or SetupType.CenterRetest => ["5m", "15m", "30m", "1h"],
                SetupType.VolatilityCompressionExpansion => ["30m", "1h"],
                _ => ["15m", "30m", "1h"]
            };
            foreach (string timeframe in timeframes)
            {
                yield return [family, timeframe, Direction.Long];
                yield return [family, timeframe, Direction.Short];
            }
        }
    }

    [Theory]
    [MemberData(nameof(EnabledProfiles))]
    public void EveryEnabledProfileCalculatesOrderedDirectionalLevels(
        SetupType family, string timeframe, Direction direction)
    {
        ScreenerBar[] bars = Bars(timeframe);
        SignalPriceLevels levels = SignalPriceLevelEvaluator.Evaluate(Context(timeframe, bars),
            bars[^1], family, direction, Policy());
        decimal minimumStopAtr = family is SetupType.BreakoutRetestContinuation
            or SetupType.VolatilityCompressionExpansion ? 2m : 1.5m;
        decimal secondTarget = family is SetupType.CenterRetest or SetupType.BreakoutRetestContinuation
            or SetupType.VolatilityCompressionExpansion ? 2.5m : 2m;
        decimal sign = direction == Direction.Long ? 1m : -1m;
        decimal stopDistanceMultiplier = family is SetupType.FlowAtrRecovery or SetupType.FlowStochasticRecovery
            ? 2m : 1m;
        decimal risk = minimumStopAtr * 2m * stopDistanceMultiplier;
        Assert.Equal(SignalPriceLevelsState.Ready, levels.State);
        Assert.Equal(2m, levels.Atr);
        Assert.Equal(100m - sign * risk, levels.StopLoss);
        Assert.Equal(100m + sign * risk, levels.TakeProfit1);
        Assert.Equal(100m + sign * risk * secondTarget, levels.TakeProfit2);
        Assert.Equal(stopDistanceMultiplier, levels.Parameters.StopDistanceMultiplier);
        Assert.Equal(levels, CoreJsonSerializer.Deserialize<SignalPriceLevels>(
            CoreJsonSerializer.Serialize(levels)));
    }

    [Theory]
    [InlineData(Direction.Long)]
    [InlineData(Direction.Short)]
    public void StopRemainsBeyondTheClosedSignalWick(Direction direction)
    {
        ScreenerBar[] bars = Bars("15m");
        bars[^1] = Bar("15m", bars.Length - 1, 100m, 110m, 90m, 100m);
        SignalPriceLevels levels = SignalPriceLevelEvaluator.Evaluate(Context("15m", bars),
            bars[^1], SetupType.FailedBreakoutReclaim, direction, Policy());
        Assert.Equal(SignalPriceLevelsState.Ready, levels.State);
        if (direction == Direction.Long)
        {
            Assert.True(levels.StopLoss < 90m);
        }
        else
        {
            Assert.True(levels.StopLoss > 110m);
        }
    }

    [Theory]
    [InlineData(Direction.Long)]
    [InlineData(Direction.Short)]
    public void FlowRecoveryRiskMultiplierScalesTheFullWickDerivedStop(Direction direction)
    {
        ScreenerBar[] bars = Bars("15m");
        bars[^1] = direction == Direction.Long
            ? Bar("15m", bars.Length - 1, 100m, 101m, 90m, 100m)
            : Bar("15m", bars.Length - 1, 100m, 110m, 99m, 100m);

        SignalPriceLevels levels = SignalPriceLevelEvaluator.Evaluate(Context("15m", bars),
            bars[^1], SetupType.FlowAtrRecovery, direction, Policy());

        Assert.Equal(SignalPriceLevelsState.Ready, levels.State);
        Assert.Equal(2m, levels.Parameters.StopDistanceMultiplier);
        decimal atr = levels.Atr!.Value;
        decimal baseStop = direction == Direction.Long ? 90m - 0.1m * atr : 110m + 0.1m * atr;
        decimal baseRisk = Math.Abs(100m - baseStop);
        decimal sign = direction == Direction.Long ? 1m : -1m;
        Assert.Equal(100m - sign * baseRisk * 2m, levels.StopLoss);
        Assert.Equal(100m + sign * baseRisk * 2m, levels.TakeProfit1);
        Assert.Equal(100m + sign * baseRisk * 4m, levels.TakeProfit2);
    }

    [Fact]
    public void FutureBarsCannotChangeTheEarlierLevelAndAreNotEligibleAsSignalBars()
    {
        ScreenerBar[] bars = Bars("15m");
        ScreenerBar future = Bar("15m", bars.Length, 100m, 150m, 50m, 110m);
        CausalEventCursor cut = Cursor(bars[^1]);
        PriceContextFeatureResult original = Context("15m", bars, cut);
        PriceContextFeatureResult withFuture = Context("15m", [.. bars, future], cut);
        SignalPriceLevels expected = SignalPriceLevelEvaluator.Evaluate(original, bars[^1],
            SetupType.CenterRetest, Direction.Long, Policy());
        SignalPriceLevels replayed = SignalPriceLevelEvaluator.Evaluate(withFuture, bars[^1],
            SetupType.CenterRetest, Direction.Long, Policy());
        Assert.Equal(expected, replayed);
        SignalPriceLevels rejected = SignalPriceLevelEvaluator.Evaluate(withFuture, future,
            SetupType.CenterRetest, Direction.Long, Policy());
        Assert.Equal(SignalPriceLevelsState.Unknown, rejected.State);
        Assert.Null(rejected.StopLoss);
        Assert.Null(rejected.TakeProfit1);
        Assert.Null(rejected.TakeProfit2);
    }

    [Fact]
    public void GapOrZeroAtrNeverProducesPrices()
    {
        ScreenerBar[] bars = Bars("15m");
        ScreenerBar[] gap = [.. bars[..10], .. bars[11..]];
        SignalPriceLevels gapResult = SignalPriceLevelEvaluator.Evaluate(Context("15m", gap),
            gap[^1], SetupType.BoundaryRejection, Direction.Long, Policy());
        ScreenerBar[] flat = Enumerable.Range(0, 21)
            .Select(index => Bar("15m", index, 100m, 100m, 100m, 100m)).ToArray();
        SignalPriceLevels flatResult = SignalPriceLevelEvaluator.Evaluate(Context("15m", flat),
            flat[^1], SetupType.BoundaryRejection, Direction.Long, Policy());
        Assert.Equal(SignalPriceLevelsState.Unknown, gapResult.State);
        Assert.Null(gapResult.StopLoss);
        Assert.Equal(SignalPriceLevelsState.Unknown, flatResult.State);
        Assert.Null(flatResult.TakeProfit1);
        Assert.Null(flatResult.TakeProfit2);
    }

    [Fact]
    public void NonpositiveShortTargetIsUnknownRatherThanInvented()
    {
        ScreenerBar[] bars = Enumerable.Range(0, 21)
            .Select(index => Bar("15m", index, 1m, 2m, 0.5m, 1m)).ToArray();
        SignalPriceLevels levels = SignalPriceLevelEvaluator.Evaluate(Context("15m", bars),
            bars[^1], SetupType.BoundaryRejection, Direction.Short, Policy());
        Assert.Equal(SignalPriceLevelsState.Unknown, levels.State);
        Assert.Null(levels.StopLoss);
        Assert.Null(levels.TakeProfit1);
        Assert.Null(levels.TakeProfit2);
        Assert.Equal("price-levels-unrepresentable-or-nonpositive-price", levels.UnknownReason);
    }

    [Fact]
    public void LegacyFormulaLevelsRemainReadableWithoutAStopMultiplier()
    {
        ScreenerBar[] bars = Bars("15m");
        SignalPriceLevels levels = SignalPriceLevelEvaluator.Evaluate(Context("15m", bars),
            bars[^1], SetupType.FlowAtrRecovery, Direction.Long, LegacyPolicy());

        Assert.Equal("signal-atr-wick-levels-v1", levels.FormulaVersion);
        Assert.Null(levels.Parameters.StopDistanceMultiplier);
        Assert.Equal(97m, levels.StopLoss);
        string json = CoreJsonSerializer.Serialize(levels);
        Assert.DoesNotContain("\"stopDistanceMultiplier\"", json);
        Assert.Equal(levels, CoreJsonSerializer.Deserialize<SignalPriceLevels>(json));
    }

    private static SignalPriceLevelPolicy LegacyPolicy() => new(
        SignalPriceLevelPolicy.CurrentFormatVersion, "fixture-levels-v1",
        SignalPriceLevelPolicy.CurrentFormulaVersion, 14,
        new ImmutableValueList<SignalPriceLevelParameters>(Enum.GetValues<SetupType>()
            .Where(static family => family != SetupType.DonchianFlowBreakout)
            .Select(family => new SignalPriceLevelParameters(family,
                family is SetupType.BreakoutRetestContinuation or SetupType.VolatilityCompressionExpansion ? 2m : 1.5m,
                0.1m, 1m,
                family is SetupType.CenterRetest or SetupType.BreakoutRetestContinuation
                    or SetupType.VolatilityCompressionExpansion ? 2.5m : 2m))));


    private static SignalPriceLevelPolicy Policy() => new(
        SignalPriceLevelPolicy.ScaledFormatVersion, "fixture-levels-v2",
        SignalPriceLevelPolicy.ScaledFormulaVersion, 14,
        new ImmutableValueList<SignalPriceLevelParameters>(Enum.GetValues<SetupType>()
            .Where(static family => family != SetupType.DonchianFlowBreakout)
            .Select(family => new SignalPriceLevelParameters(family,
                family is SetupType.BreakoutRetestContinuation or SetupType.VolatilityCompressionExpansion ? 2m : 1.5m,
                0.1m, 1m,
                family is SetupType.CenterRetest or SetupType.BreakoutRetestContinuation
                    or SetupType.VolatilityCompressionExpansion ? 2.5m : 2m,
                family is SetupType.FlowAtrRecovery or SetupType.FlowStochasticRecovery ? 2m : 1m))));

    private static PriceContextFeatureResult Context(string timeframe, ScreenerBar[] bars,
        CausalEventCursor? cursor = null)
    {
        string contextTimeframe = PublicSignalTimeframes.ContextForSetup(timeframe)!;
        PriceContextFeatureInput input = new("fixture", Instrument, timeframe,
            new UtcTimestamp(Start), bars, contextTimeframe, new UtcTimestamp(Start), [], []);
        PriceContextFeatureConfiguration configuration = new(timeframe, contextTimeframe,
            new PriceContextFeatureVersion(PriceContextFeatureCalculator.CurrentAlgorithmVersion),
            2, 2, 14, PriceContextAtrMode.Rma, PriceContextChannelCenterKind.Ema, 20, null,
            PriceContextChannelWidthKind.Atr, 14, 1m, 1m, 1, 2,
            PriceContextMovingAverageKind.Ema, 1, null, null, null);
        return PriceContextFeatureCalculator.Calculate(input, cursor ?? Cursor(bars[^1]), configuration);
    }

    private static CausalEventCursor Cursor(ScreenerBar bar) =>
        new(new UtcTimestamp(bar.CloseTime.Value.AddSeconds(2)), 10_000);

    private static ScreenerBar[] Bars(string timeframe) => Enumerable.Range(0, 21)
        .Select(index => Bar(timeframe, index, 100m, 101m, 99m, 100m)).ToArray();

    private static ScreenerBar Bar(string timeframe, int index, decimal open, decimal high,
        decimal low, decimal close)
    {
        int minutes = timeframe switch
        {
            "5m" => 5,
            "15m" => 15,
            "30m" => 30,
            "1h" => 60,
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };
        UtcTimestamp openTime = new(Start.AddMinutes(index * minutes));
        UtcTimestamp closeTime = new(Start.AddMinutes((index + 1) * minutes));
        Bar bar = new($"{timeframe}-bar-{index}", Instrument, closeTime, openTime, closeTime,
            new Price(open), new Price(high), new Price(low), new Price(close), new Quantity(10m));
        UtcTimestamp available = new(closeTime.Value.AddSeconds(1));
        return new ScreenerBar("fixture", bar, 100m, available, available, index + 1);
    }
}
