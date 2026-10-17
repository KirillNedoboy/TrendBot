using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ScreenerSnapshotContractTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime = new(2026, 1, 2, 3, 4, 5,
        TimeSpan.Zero);

    [Fact]
    public void SnapshotIdentityIsStableForEquivalentInputOrderingAndExcludesIdentityFromJson()
    {
        ScreenerSnapshot first = CreateSnapshot(reverseCollections: false);
        ScreenerSnapshot second = CreateSnapshot(reverseCollections: true);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(first.Identity, first.SnapshotId);
        Assert.DoesNotContain(first.SnapshotId, CoreJsonSerializer.Serialize(first),
            StringComparison.Ordinal);
        Assert.Equal(first, CoreJsonSerializer.Deserialize<ScreenerSnapshot>(
            CoreJsonSerializer.Serialize(first)));
        Assert.Equal(1, first.FormatVersion);
        Assert.Equal(first.Configuration.Version, first.FormulaVersion);
    }

    [Fact]
    public void SnapshotRequiresCurrentFormatAndSupportedFormulaVersion()
    {
        ScreenerSnapshot snapshot = CreateSnapshot();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenerSnapshot(
            formatVersion: 2, snapshot.FormulaVersion, snapshot.Configuration,
            snapshot.Cursor, snapshot.Input, snapshot.Features));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenerSnapshot(
            formatVersion: 1, new ScreenerFeatureVersion("screener-v999"),
            snapshot.Configuration, snapshot.Cursor, snapshot.Input, snapshot.Features));
    }

    [Fact]
    public void ReorderedConflictingBarInputsHaveStableIdentity()
    {
        ScreenerSnapshot baseline = CreateSnapshot();
        ScreenerBar first = baseline.Input.ClosedBars[0];
        Bar conflictingBar = new("bar-conflict", Instrument, first.EventTime,
            first.Bar.OpenTime, first.Bar.CloseTime, first.Bar.Open, first.Bar.High,
            first.Bar.Low, new Price(first.Bar.Close.Value + 1m), first.Bar.Volume);
        ScreenerBar second = new("binance.usdm", conflictingBar, first.QuoteVolume,
            first.ReceiveTime, first.AvailabilityTime, first.StableSequence);
        ScreenerBar[] ordered = [first, second, .. baseline.Input.ClosedBars.Skip(1)];
        ScreenerBar[] reordered = [second, first, .. baseline.Input.ClosedBars.Skip(1)];

        ScreenerFeatureInput firstInput = new(baseline.Input.SourceId,
            baseline.Input.InstrumentId, baseline.Input.EventTime, baseline.Input.ReceiveTime,
            baseline.Input.AvailabilityTime, baseline.Input.StableSequence, ordered,
            baseline.Input.OpenInterest, baseline.Input.Quotes);
        ScreenerFeatureInput secondInput = new(baseline.Input.SourceId,
            baseline.Input.InstrumentId, baseline.Input.EventTime, baseline.Input.ReceiveTime,
            baseline.Input.AvailabilityTime, baseline.Input.StableSequence, reordered,
            baseline.Input.OpenInterest, baseline.Input.Quotes);
        ScreenerFeatureResult firstResult = ScreenerFeatureCalculator.Calculate(firstInput,
            baseline.Cursor, baseline.Configuration);
        ScreenerFeatureResult secondResult = ScreenerFeatureCalculator.Calculate(secondInput,
            baseline.Cursor, baseline.Configuration);

        ScreenerSnapshot firstSnapshot = ScreenerSnapshot.Create(firstResult,
            baseline.Configuration);
        ScreenerSnapshot secondSnapshot = ScreenerSnapshot.Create(secondResult,
            baseline.Configuration);

        Assert.Equal(firstSnapshot.SnapshotId, secondSnapshot.SnapshotId);
        Assert.Equal(CoreJsonSerializer.Serialize(firstSnapshot),
            CoreJsonSerializer.Serialize(secondSnapshot));
    }

    private static ScreenerSnapshot CreateSnapshot(bool reverseCollections = false)
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m, 100.1m),
            Bar(1, 105m, 108m, 103m, 20m, 200.2m),
            Bar(2, 111m, 114m, 109m, 30m, 300.3m),
            Bar(3, 116m, 120m, 112m, 40m, 400.4m),
            Bar(4, 119m, 123m, 115m, 50m, 500.5m)
        ];
        ScreenerOpenInterestObservation[] openInterest =
        [OpenInterest(0, 100m), OpenInterest(1, 110m), OpenInterest(2, 150m)];
        ScreenerQuoteObservation quote = Quote(4, 99m, 101m);
        if (reverseCollections)
        {
            Array.Reverse(bars);
            Array.Reverse(openInterest);
        }

        ScreenerFeatureInput input = new(
            "binance.usdm", Instrument, Time(4), At(4, 1), At(4, 2), 4, bars,
            openInterest, quote);
        ScreenerFeatureConfiguration configuration = new(
            "1m", 2, 1, 2, 3, 2, new ScreenerFeatureVersion("screener-v1"),
            new ScreenerFreshnessPolicy(
                barTtl: TimeSpan.FromMinutes(5), quoteTtl: TimeSpan.FromMinutes(5),
                openInterestTtl: TimeSpan.FromMinutes(5)));
        CausalEventCursor cursor = new(Time(4), 4);
        ScreenerFeatureResult result = ScreenerFeatureCalculator.Calculate(
            input, cursor, configuration);
        return new ScreenerSnapshot(result, configuration);
    }

    private static ScreenerBar Bar(int offset, decimal close, decimal high, decimal low,
        decimal volume, decimal quoteVolume)
    {
        UtcTimestamp open = Time(offset);
        Bar bar = new($"bar-{offset}", Instrument, Time(offset), open,
            Time(offset + 1), new Price(close - 1m), new Price(high), new Price(low),
            new Price(close), new Quantity(volume));
        return new ScreenerBar("binance.usdm", bar, quoteVolume, Time(offset),
            At(offset, 1), offset);
    }

    private static ScreenerOpenInterestObservation OpenInterest(int offset, decimal value) =>
        new("binance.usdm", Instrument, Time(offset), Time(offset),
            At(offset, 1), offset, value);

    private static ScreenerQuoteObservation Quote(int offset, decimal bid, decimal ask) =>
        new("binance.usdm", Instrument, Time(offset), Time(offset),
            At(offset, 1), offset, bid, ask, true);

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));

    private static UtcTimestamp At(int offset, int milliseconds) =>
        new(BaseTime.AddMinutes(offset).AddMilliseconds(milliseconds));
}
