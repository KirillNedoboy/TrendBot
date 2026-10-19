using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class FeatureQualitySnapshotTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SnapshotRoundTripsWithIdentityExcludedAndQualityRecomputable()
    {
        ScreenerBar[] bars =
        [
            Bar(0, 100m, 101m, 99m, 10m),
            Bar(1, 101m, 102m, 100m, 20m),
            Bar(2, 102m, 103m, 101m, 30m),
            Bar(3, 103m, 104m, 102m, 40m)
        ];
        CausalEventCursor cursor = new(Time(4), 4);
        RollingIndicatorInput input = new("source", Instrument, "1m", Time(-1), bars);
        RollingIndicatorConfiguration configuration = new(
            "1m", new RollingIndicatorVersion("rolling-v1"), 2, RollingAtrMode.Sma,
            2, RollingMovingAverageKind.Sma, 2);
        RollingIndicatorResult result = RollingIndicatorCalculator.Calculate(
            input, cursor, configuration);
        FeatureQualityPolicy policy = new(TimeSpan.FromMinutes(1), null, null, null);

        FeatureQualitySnapshot snapshot = FeatureQualitySnapshot.Create(result, policy);

        Assert.Equal(FeatureQualitySnapshot.CurrentFormatVersion, snapshot.FormatVersion);
        Assert.Equal(FeatureQualitySnapshot.CurrentAlgorithmVersion,
            snapshot.AlgorithmVersion);
        Assert.Equal(result.Input, snapshot.RollingResult!.Input);
        Assert.Equal(configuration, snapshot.RollingResult.Configuration);
        Assert.Equal(cursor, snapshot.Cursor);
        Assert.Equal(policy, snapshot.Policy);
        Assert.Equal(snapshot.Quality, snapshot.RecalculateQuality());

        string json = CoreJsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(snapshot.SnapshotId, json, StringComparison.Ordinal);
        FeatureQualitySnapshot restored = CoreJsonSerializer.Deserialize<FeatureQualitySnapshot>(json);
        Assert.Equal(snapshot, restored);
        Assert.Equal(snapshot.SnapshotId, restored.SnapshotId);
    }

    private static ScreenerBar Bar(int offset, decimal close, decimal high, decimal low,
        decimal volume)
    {
        UtcTimestamp eventTime = Time(offset);
        Bar bar = new($"bar-{offset}", Instrument, eventTime,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime,
            new Price(close - 1m), new Price(high), new Price(low), new Price(close),
            new Quantity(volume));
        return new ScreenerBar("source", bar, volume * 10m,
            new UtcTimestamp(eventTime.Value.AddMinutes(-1)), eventTime, offset);
    }

    private static UtcTimestamp Time(int offset) =>
        new(BaseTime.AddMinutes(offset));
}
