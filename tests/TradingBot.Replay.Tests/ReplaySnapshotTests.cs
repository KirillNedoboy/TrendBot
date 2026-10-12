using TradingBot.Backtesting;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Replay.Tests;

public sealed class ReplaySnapshotTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void SnapshotFiltersEligibilityBeforeOrderingByEventTimeAndStableSequence()
    {
        ReplayEnvelope[] envelopes =
        [
            CreateEnvelope("late-arrival", 0, BaseTime.AddMinutes(2), BaseTime.AddMinutes(2)),
            CreateEnvelope("equal-second", 2, BaseTime, BaseTime),
            CreateEnvelope("equal-first", 1, BaseTime, BaseTime),
            CreateEnvelope("unavailable", 3, BaseTime.AddMinutes(-1), BaseTime.AddMinutes(3)),
            CreateEnvelope("future", 4, BaseTime.AddMinutes(3), BaseTime.AddMinutes(3))
        ];

        ReplayStreamSnapshot snapshot = ReplaySnapshotBuilder.Build(
            "stream-a", envelopes, new CausalEventCursor(Time(BaseTime, 2), 3));

        Assert.Equal("stream-a", snapshot.StreamId);
        Assert.Equal(["equal-first", "equal-second", "late-arrival"],
            snapshot.Events.Select(static item => item.CausalEvent.Payload.EventId));
        Assert.Equal(snapshot.Events, snapshot.Envelopes);

        IList<ReplayEnvelope> mutableView = Assert.IsAssignableFrom<IList<ReplayEnvelope>>(
            snapshot.Events);
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
    }

    [Fact]
    public void SnapshotRejectsEventsFromAnotherStream()
    {
        ReplayEnvelope[] envelopes =
        [CreateEnvelope("trade-1", 0, BaseTime, BaseTime),
            new("stream-b", envelopesPlaceholder())];

        Assert.Throws<ArgumentException>(() => ReplaySnapshotBuilder.Build(
            "stream-a", envelopes, new CausalEventCursor(Time(BaseTime, 1), 1)));
    }

    private static ReplayEnvelope CreateEnvelope(string id, long sequence,
        DateTimeOffset eventTime, DateTimeOffset availabilityTime) => new(
        "stream-a", new CausalMarketEvent(
            new Trade(id, Instrument, new UtcTimestamp(eventTime), new Price(100m),
                new Quantity(1m), TradeSide.Buy), new UtcTimestamp(eventTime),
            new UtcTimestamp(availabilityTime), sequence));

    private static CausalMarketEvent envelopesPlaceholder() =>
        new(new Trade("trade-2", Instrument, new UtcTimestamp(BaseTime), new Price(100m),
            new Quantity(1m), TradeSide.Buy), new UtcTimestamp(BaseTime),
            new UtcTimestamp(BaseTime), 1);

    private static UtcTimestamp Time(DateTimeOffset value, int minutes) =>
        new(value.AddMinutes(minutes));
}
