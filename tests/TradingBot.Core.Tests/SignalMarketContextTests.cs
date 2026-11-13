using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class SignalMarketContextTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void SessionVwapUsesExactExchangeVolumesAndRejectsMissingOrFutureBars()
    {
        ScreenerBar first = Bar(0, 2m, 202m);
        ScreenerBar second = Bar(5, 3m, 306m);
        ScreenerBar future = Bar(10, 100m, 100_000m);
        UtcTimestamp through = Utc(Start.AddMinutes(10));
        UtcTimestamp asOf = Utc(Start.AddMinutes(10).AddSeconds(1));

        SignalSessionVwap ready = SignalMarketContextCalculator.CalculateSessionVwap(
            "5m", through, asOf, [first, second, future]);
        SignalSessionVwap missing = SignalMarketContextCalculator.CalculateSessionVwap(
            "5m", through, asOf, [second]);
        SignalSessionVwap unavailable = SignalMarketContextCalculator.CalculateSessionVwap(
            "5m", through, asOf, [first, Bar(5, 3m, 306m, Start.AddMinutes(11))]);

        Assert.Equal(101.6m, ready.Value);
        Assert.Equal(5m, ready.BaseVolume);
        Assert.Equal(508m, ready.QuoteVolume);
        Assert.Equal(2, ready.BarCount);
        Assert.Null(missing.Value);
        Assert.Equal("session-history-incomplete", missing.UnknownReason);
        Assert.Null(unavailable.Value);
        Assert.Equal("session-history-incomplete", unavailable.UnknownReason);
    }

    [Fact]
    public void MidnightCloseBelongsToTheCompletedUtcSession()
    {
        DateTimeOffset dayBefore = Start.AddDays(-1);
        List<ScreenerBar> bars = [];
        for (int hour = 0; hour < 24; hour++)
        {
            bars.Add(BarAt(dayBefore.AddHours(hour), TimeSpan.FromHours(1), 2m, 200m));
        }
        SignalSessionVwap result = SignalMarketContextCalculator.CalculateSessionVwap(
            "1h", Utc(Start), Utc(Start.AddSeconds(1)), bars);

        Assert.Equal(dayBefore, result.WindowStart.Value);
        Assert.Equal(24, result.BarCount);
        Assert.Equal(100m, result.Value);
    }

    [Fact]
    public void ObservedPocRequiresCoveredWindowAndExcludesFutureTrades()
    {
        DateTimeOffset cut = Start.AddMinutes(10);
        SignalContextTrade[] trades =
        [
            new(100.02m, 2m, cut.AddSeconds(-50), cut.AddSeconds(-49), cut.AddSeconds(-49)),
            new(100.09m, 1m, cut.AddSeconds(-10), cut.AddSeconds(-9), cut.AddSeconds(-9)),
            new(100.11m, 3m, cut.AddSeconds(-5), cut.AddSeconds(-4), cut.AddSeconds(-4)),
            new(99m, 100m, cut.AddSeconds(1), cut.AddSeconds(1), cut.AddSeconds(1)),
            new(98m, 100m, cut.AddSeconds(-1), cut.AddSeconds(1), cut.AddSeconds(1)),
            new(97m, 100m, cut.AddSeconds(-1), cut.AddSeconds(-1), cut.AddSeconds(1))
        ];
        SignalObservedPoc ready = SignalMarketContextCalculator.CalculatePoc(
            Utc(cut), TimeSpan.FromMinutes(1), 0.1m, Utc(cut.AddMinutes(-1)), trades);
        SignalObservedPoc incomplete = SignalMarketContextCalculator.CalculatePoc(
            Utc(cut), TimeSpan.FromMinutes(1), 0.1m, Utc(cut.AddSeconds(-59)), trades);
        SignalObservedPoc empty = SignalMarketContextCalculator.CalculatePoc(
            Utc(cut), TimeSpan.FromMinutes(1), 0.1m, Utc(cut.AddMinutes(-1)), []);
        SignalObservedPoc missingTime = SignalMarketContextCalculator.CalculatePoc(
            Utc(cut), TimeSpan.FromMinutes(1), 0.1m, Utc(cut.AddMinutes(-1)),
            [new SignalContextTrade(100m, 1m, cut.AddSeconds(-1), cut, null)]);

        Assert.Equal(100.0m, ready.Price);
        Assert.Equal(3, ready.TradeCount);
        Assert.Null(incomplete.Price);
        Assert.Equal("trade-window-incomplete", incomplete.UnknownReason);
        Assert.Null(empty.Price);
        Assert.Equal("no-observed-trades", empty.UnknownReason);
        Assert.Null(missingTime.Price);
        Assert.Equal("trade-source-time-unavailable", missingTime.UnknownReason);
    }

    [Fact]
    public void ContextRoundTripsAndRejectsFutureLiquidations()
    {
        DateTimeOffset cut = Start.AddMinutes(5);
        SignalSessionVwap vwap = new(Utc(Start), Utc(cut), 100m, 2m, 200m, 1, null);
        SignalObservedPoc poc = new(Utc(cut.AddMinutes(-1)), Utc(cut), 0.1m, 100m, 1, null);
        SignalObservedLiquidation liquidation = new("snapshot-1", TradeSide.Sell, 2m, 99m,
            Utc(cut.AddSeconds(-1)), Utc(cut.AddSeconds(-1)), Utc(cut), Utc(cut));
        SignalMarketContext context = SignalMarketContext.Create(Instrument, Utc(cut), vwap, poc, liquidation);

        Assert.Equal(context, CoreJsonSerializer.Deserialize<SignalMarketContext>(
            CoreJsonSerializer.Serialize(context)));
        SignalObservedLiquidation future = new("snapshot-2", TradeSide.Buy, 1m, 100m,
            Utc(cut), Utc(cut), Utc(cut.AddSeconds(1)), Utc(cut.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() => SignalMarketContext.Create(Instrument, Utc(cut),
            vwap, poc, future));
    }

    private static ScreenerBar Bar(int minute, decimal volume, decimal quote,
        DateTimeOffset? availability = null) => BarAt(Start.AddMinutes(minute),
            TimeSpan.FromMinutes(5), volume, quote, availability);

    private static ScreenerBar BarAt(DateTimeOffset open, TimeSpan duration, decimal volume,
        decimal quote, DateTimeOffset? availability = null)
    {
        DateTimeOffset close = open.Add(duration);
        Bar bar = new($"bar-{open:O}", Instrument, Utc(close), Utc(open), Utc(close),
            new Price(100m), new Price(103m), new Price(99m), new Price(101m), new Quantity(volume));
        UtcTimestamp seen = Utc(availability ?? close);
        return new ScreenerBar("fixture", bar, quote, seen, seen, open.ToUnixTimeMilliseconds());
    }

    private static UtcTimestamp Utc(DateTimeOffset value) => new(value);
}
