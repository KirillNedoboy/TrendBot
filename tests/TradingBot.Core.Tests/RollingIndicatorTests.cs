using System.Globalization;
using System.Reflection;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class RollingIndicatorTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ManualVectorsCoverAllMovingAveragesAtrModesAndRvol()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 12m, 13m, 10m, 20m),
            Bar(2, 14m, 15m, 11m, 30m),
            Bar(3, 13m, 16m, 12m, 40m),
            Bar(4, 18m, 19m, 15m, 50m)
        ];
        RollingIndicatorInput input = Input(bars);
        CausalEventCursor cursor = Cursor(5, 4);

        Assert.Equal(15m, Calculate(input, cursor, RollingMovingAverageKind.Sma)
            .MovingAverage.Value);
        Assert.Equal((14m + (13m * 2m) + (18m * 3m)) / 6m,
            Calculate(input, cursor, RollingMovingAverageKind.Wma).MovingAverage.Value);

        decimal alpha = 2m / 4m;
        decimal ema = 10m + 12m + 14m;
        ema /= 3m;
        ema = ema + (alpha * (13m - ema));
        ema = ema + (alpha * (18m - ema));
        Assert.Equal(ema, Calculate(input, cursor, RollingMovingAverageKind.Ema)
            .MovingAverage.Value);

        decimal rma = (10m + 12m + 14m) / 3m;
        rma += (13m - rma) / 3m;
        rma += (18m - rma) / 3m;
        Assert.Equal(rma, Calculate(input, cursor, RollingMovingAverageKind.Rma)
            .MovingAverage.Value);

        RollingIndicatorResult atrSma = Calculate(input, cursor,
            RollingMovingAverageKind.Sma, RollingAtrMode.Sma);
        Assert.Equal((4m + 4m + 6m) / 3m, atrSma.Atr.Value);

        RollingIndicatorResult atrRma = Calculate(input, cursor,
            RollingMovingAverageKind.Sma, RollingAtrMode.Rma);
        decimal seededAtr = (2m + 3m + 4m) / 3m;
        seededAtr += (4m - seededAtr) / 3m;
        seededAtr += (6m - seededAtr) / 3m;
        Assert.Equal(seededAtr, atrRma.Atr.Value);
        Assert.Equal(50m / ((20m + 30m + 40m) / 3m), atrSma.RelativeVolume.Value);
    }

    [Fact]
    public void WarmupRequiresExactNForMaAndNPlusOneForAtrAndRvol()
    {
        RollingIndicatorInput threeBars = Input(
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 12m, 13m, 10m, 20m),
            Bar(2, 14m, 15m, 11m, 30m)
        ]);
        RollingIndicatorResult result = Calculate(threeBars, Cursor(3, 2),
            RollingMovingAverageKind.Ema, RollingAtrMode.Rma);

        Assert.True(result.MovingAverage.IsReady);
        Assert.Equal(3, result.MovingAverage.RequiredBars);
        Assert.Equal(3, result.MovingAverage.AvailableBars);
        Assert.Equal(RollingIndicatorState.UnknownWarmup, result.Atr.State);
        Assert.Equal(RollingIndicatorState.UnknownWarmup, result.RelativeVolume.State);
        Assert.Equal(4, result.Atr.RequiredBars);
        Assert.Equal(4, result.RelativeVolume.RequiredBars);

        RollingIndicatorResult empty = Calculate(Input([]), Cursor(3, 2),
            RollingMovingAverageKind.Sma, RollingAtrMode.Sma);
        Assert.All([empty.Atr, empty.MovingAverage, empty.RelativeVolume], value =>
        {
            Assert.Equal(RollingIndicatorState.UnknownWarmup, value.State);
            Assert.Equal(0, value.AvailableBars);
        });

        RollingIndicatorInput fourBars = Input(
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 12m, 13m, 10m, 20m),
            Bar(2, 14m, 15m, 11m, 30m),
            Bar(3, 13m, 16m, 12m, 40m)
        ]);
        RollingIndicatorResult complete = Calculate(fourBars, Cursor(4, 3),
            RollingMovingAverageKind.Sma, RollingAtrMode.Sma);
        Assert.True(complete.Atr.IsReady);
        Assert.True(complete.RelativeVolume.IsReady);
        Assert.Equal(4, complete.Atr.AvailableBars);
    }

    [Fact]
    public void MissingInvalidAndUnsupportedConfigurationRemainsUnknown()
    {
        RollingIndicatorInput input = Input([Bar(0, 10m, 11m, 9m, 10m)]);
        CausalEventCursor cursor = Cursor(1, 0);

        RollingIndicatorResult missing = RollingIndicatorCalculator.Calculate(input, cursor,
            new RollingIndicatorConfiguration());
        Assert.All([missing.Atr, missing.MovingAverage, missing.RelativeVolume], value =>
        {
            Assert.Equal(RollingIndicatorState.UnknownConfiguration, value.State);
            Assert.Null(value.Value);
        });

        RollingIndicatorResult unsupported = RollingIndicatorCalculator.Calculate(input, cursor,
            Configuration(0, (RollingAtrMode)99, 0, (RollingMovingAverageKind)99, 0,
                "rolling-v999"));
        Assert.All([unsupported.Atr, unsupported.MovingAverage, unsupported.RelativeVolume],
            value => Assert.Equal(RollingIndicatorState.UnknownConfiguration, value.State));

        RollingIndicatorInput futureHistory = Input([Bar(0, 10m, 11m, 9m, 10m)],
            historyStart: Time(2));
        RollingIndicatorResult invalidInput = RollingIndicatorCalculator.Calculate(
            futureHistory, cursor, Configuration());
        Assert.All([invalidInput.Atr, invalidInput.MovingAverage, invalidInput.RelativeVolume],
            value => Assert.Equal(RollingIndicatorState.UnknownInput, value.State));
    }

    [Fact]
    public void CausalFutureAndUnavailableBarsCannotChangeHistoricValuesOrReadiness()
    {
        ScreenerBar[] historic =
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 12m, 13m, 10m, 20m),
            Bar(2, 14m, 15m, 11m, 30m),
            Bar(3, 13m, 16m, 12m, 40m),
            Bar(4, 18m, 19m, 15m, 50m)
        ];
        RollingIndicatorInput baselineInput = Input(historic);
        RollingIndicatorResult baseline = Calculate(baselineInput, Cursor(5, 4),
            RollingMovingAverageKind.Ema, RollingAtrMode.Rma);

        ScreenerBar future = Bar(6, 900m, 901m, 899m, 900m);
        ScreenerBar unavailable = Bar(4, 999m, 1000m, 998m, 999m,
            availabilityOffsetMinutes: 2);
        RollingIndicatorResult withInvisible = Calculate(Input(
            historic.Concat([future, unavailable])), Cursor(5, 4),
            RollingMovingAverageKind.Ema, RollingAtrMode.Rma);

        Assert.Equal(baseline.Atr.Value, withInvisible.Atr.Value);
        Assert.Equal(baseline.MovingAverage.Value, withInvisible.MovingAverage.Value);
        Assert.Equal(baseline.RelativeVolume.Value, withInvisible.RelativeVolume.Value);
        Assert.Equal(baseline.Atr.IsReady, withInvisible.Atr.IsReady);
        Assert.Equal(baseline.MovingAverage.IsReady, withInvisible.MovingAverage.IsReady);
        Assert.Equal(baseline.RelativeVolume.IsReady, withInvisible.RelativeVolume.IsReady);
        Assert.Equal(baseline.EligibleBars, withInvisible.EligibleBars);
        Assert.Contains("future", withInvisible.Reasons);
        Assert.Contains("late-availability", withInvisible.Reasons);
    }

    [Fact]
    public void DuplicatesCollapseConflictsAndGapsBlockWithoutFilling()
    {
        ScreenerBar first = Bar(0, 10m, 11m, 9m, 10m);
        ScreenerBar second = Bar(1, 12m, 13m, 10m, 20m);
        ScreenerBar third = Bar(2, 14m, 15m, 11m, 30m);

        RollingIndicatorResult duplicate = Calculate(Input([first, first, second, third]),
            Cursor(3, 2), RollingMovingAverageKind.Sma, RollingAtrMode.Sma);
        RollingIndicatorResult clean = Calculate(Input([first, second, third]),
            Cursor(3, 2), RollingMovingAverageKind.Sma, RollingAtrMode.Sma);
        Assert.Equal(clean.MovingAverage.Value, duplicate.MovingAverage.Value);
        Assert.Equal(clean.EligibleBars, duplicate.EligibleBars);
        Assert.Contains("duplicate", duplicate.Reasons);

        ScreenerBar conflict = Bar(1, 99m, 100m, 98m, 20m);
        RollingIndicatorResult conflicted = Calculate(Input([first, second, conflict, third]),
            Cursor(3, 2), RollingMovingAverageKind.Sma, RollingAtrMode.Sma);
        Assert.Equal(RollingIndicatorState.UnknownQuality, conflicted.MovingAverage.State);
        Assert.Contains("conflict", conflicted.Reasons);

        RollingIndicatorResult gap = Calculate(Input([first, third]), Cursor(3, 2),
            RollingMovingAverageKind.Sma, RollingAtrMode.Sma);
        Assert.Equal(RollingIndicatorState.UnknownQuality, gap.MovingAverage.State);
        Assert.Contains("gap", gap.Reasons);
        Assert.Contains("gap", gap.Atr.Reasons);
    }

    [Fact]
    public void ZeroDenominatorAndDecimalOverflowRemainExplicitUnknowns()
    {
        RollingIndicatorInput zero = Input(
        [
            Bar(0, 10m, 11m, 9m, 0m),
            Bar(1, 12m, 13m, 10m, 0m),
            Bar(2, 14m, 15m, 11m, 0m),
            Bar(3, 13m, 16m, 12m, 30m)
        ]);
        RollingIndicatorResult zeroResult = Calculate(zero, Cursor(4, 3),
            RollingMovingAverageKind.Sma, RollingAtrMode.Sma, atrPeriod: 2,
            movingAveragePeriod: 2, relativeVolumePeriod: 2);
        Assert.Equal(RollingIndicatorState.UnknownZeroDenominator,
            zeroResult.RelativeVolume.State);

        RollingIndicatorInput overflow = Input(
        [
            Bar(0, decimal.MaxValue, decimal.MaxValue, 1m, 1m),
            Bar(1, decimal.MaxValue, decimal.MaxValue, 1m, 1m),
            Bar(2, decimal.MaxValue, decimal.MaxValue, 1m, 1m)
        ]);
        RollingIndicatorResult overflowResult = Calculate(overflow, Cursor(3, 2),
            RollingMovingAverageKind.Wma, RollingAtrMode.Sma, atrPeriod: 2,
            movingAveragePeriod: 2, relativeVolumePeriod: 2);
        Assert.Equal(RollingIndicatorState.UnknownOverflow,
            overflowResult.MovingAverage.State);
        Assert.Equal(RollingIndicatorState.UnknownOverflow, overflowResult.Atr.State);
    }

    [Fact]
    public void HistoryStartAndRecursiveSeedProvenanceAreExplicit()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 1m),
            Bar(1, 10m, 11m, 9m, 10m),
            Bar(2, 12m, 13m, 10m, 20m),
            Bar(3, 14m, 15m, 11m, 30m),
            Bar(4, 16m, 17m, 12m, 40m)
        ];
        RollingIndicatorInput input = Input(bars, historyStart: Time(1));
        RollingIndicatorResult result = Calculate(input, Cursor(5, 4),
            RollingMovingAverageKind.Ema, RollingAtrMode.Rma, atrPeriod: 2,
            movingAveragePeriod: 2, relativeVolumePeriod: 2);

        Assert.Equal(4, result.EligibleBars.Count);
        Assert.Equal(4, result.MovingAverage.AvailableBars);
        Assert.Equal(2, result.MovingAverage.RequiredBars);
        Assert.Equal(2, result.MovingAverage.Provenance.Count);
        Assert.Equal(3, result.Atr.Provenance.Count);
        Assert.Contains("history-start", result.Reasons);
        Assert.Equal(15m, result.MovingAverage.Value);
    }

    [Fact]
    public void PermutationsCultureAndJsonRoundTripAreDeterministic()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 10m, 11m, 9m, 10m),
            Bar(1, 12m, 13m, 10m, 20m),
            Bar(2, 14m, 15m, 11m, 30m),
            Bar(3, 13m, 16m, 12m, 40m),
            Bar(4, 18m, 19m, 15m, 50m)
        ];
        RollingIndicatorConfiguration configuration = Configuration();
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            RollingIndicatorResult first = RollingIndicatorCalculator.Calculate(
                Input(bars), Cursor(5, 4), configuration);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            RollingIndicatorResult second = RollingIndicatorCalculator.Calculate(
                Input(bars.Reverse()), Cursor(5, 4), configuration);

            Assert.Equal(first, second);
            string json = CoreJsonSerializer.Serialize(first);
            RollingIndicatorResult restored =
                CoreJsonSerializer.Deserialize<RollingIndicatorResult>(json);
            Assert.Equal(first, restored);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void RollingContractsRemainImmutableAndCoreBoundarySafe()
    {
        Type[] types =
        [
            typeof(RollingIndicatorInput), typeof(RollingIndicatorConfiguration),
            typeof(RollingIndicatorResult), typeof(RollingIndicatorValue),
            typeof(RollingBarQuality)
        ];
        Assert.All(types, static type => Assert.All(
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
            property => Assert.Null(property.SetMethod)));
    }

    private static RollingIndicatorResult Calculate(RollingIndicatorInput input,
        CausalEventCursor cursor, RollingMovingAverageKind movingAverageKind,
        RollingAtrMode atrMode = RollingAtrMode.Sma, int atrPeriod = 3,
        int movingAveragePeriod = 3, int relativeVolumePeriod = 3) =>
        RollingIndicatorCalculator.Calculate(input, cursor,
            Configuration(atrPeriod, atrMode, movingAveragePeriod, movingAverageKind,
                relativeVolumePeriod));

    private static RollingIndicatorConfiguration Configuration(int? atrPeriod = 3,
        RollingAtrMode? atrMode = RollingAtrMode.Sma, int? movingAveragePeriod = 3,
        RollingMovingAverageKind? movingAverageKind = RollingMovingAverageKind.Sma,
        int? relativeVolumePeriod = 3, string algorithmVersion = "rolling-v1") =>
        new("1m", new RollingIndicatorVersion(algorithmVersion), atrPeriod, atrMode,
            movingAveragePeriod, movingAverageKind, relativeVolumePeriod);

    private static RollingIndicatorInput Input(IEnumerable<ScreenerBar> bars,
        UtcTimestamp? historyStart = null) => new(
        "feed-a", Instrument, "1m", historyStart ?? Time(0), bars);

    private static ScreenerBar Bar(int index, decimal close, decimal high, decimal low,
        decimal volume, int availabilityOffsetMinutes = 0)
    {
        UtcTimestamp eventTime = Time(index);
        UtcTimestamp receive = new(eventTime.Value.AddMinutes(
            Math.Min(0, availabilityOffsetMinutes)));
        UtcTimestamp availability = new(eventTime.Value.AddMinutes(availabilityOffsetMinutes));
        Bar bar = new($"bar-{index}-{close}", Instrument, eventTime, eventTime,
            Time(index + 1), new Price(Math.Max(0.0001m, close - 1m)),
            new Price(high), new Price(low), new Price(close), new Quantity(volume));
        return new ScreenerBar("feed-a", bar, volume, receive, availability, index);
    }

    private static CausalEventCursor Cursor(int asOf, long stableSequence) =>
        new(Time(asOf), stableSequence);

    private static UtcTimestamp Time(int index) =>
        new(BaseTime.AddMinutes(index));
}
