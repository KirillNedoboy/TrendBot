using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class DomainInvariantTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    private static readonly UtcTimestamp EventTime = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void BarRequiresPositiveDurationAndConsistentOhlcRange()
    {
        Bar valid = CreateBar(EventTime, AddMinute(EventTime),
            open: 100m, high: 105m, low: 95m, close: 101m);

        Assert.Equal(100m, valid.Open.Value);

        ArgumentException invalidDuration = Assert.ThrowsAny<ArgumentException>(() =>
            CreateBar(EventTime, EventTime, 100m, 105m, 95m, 101m));
        Assert.Equal("closeTime", invalidDuration.ParamName);

        ArgumentException highBelowOpen = Assert.ThrowsAny<ArgumentException>(() =>
            CreateBar(EventTime, AddMinute(EventTime), 100m, 99m, 95m, 98m));
        Assert.Equal("high", highBelowOpen.ParamName);

        ArgumentException lowAboveClose = Assert.ThrowsAny<ArgumentException>(() =>
            CreateBar(EventTime, AddMinute(EventTime), 100m, 105m, 102m, 101m));
        Assert.Equal("low", lowAboveClose.ParamName);

        ArgumentException invertedRange = Assert.ThrowsAny<ArgumentException>(() =>
            CreateBar(EventTime, AddMinute(EventTime), 100m, 99m, 102m, 101m));
        Assert.Equal("high", invertedRange.ParamName);
    }

    [Fact]
    public void SetupRangeMustContainItsAnchorPrice()
    {
        Setup valid = CreateSetup(anchor: 100m, high: 110m, low: 90m);
        Assert.Equal(100m, valid.AnchorPrice.Value);

        ArgumentException highBelowAnchor = Assert.ThrowsAny<ArgumentException>(() =>
            CreateSetup(anchor: 100m, high: 99m, low: 90m));
        Assert.Equal("highSinceAnchor", highBelowAnchor.ParamName);

        ArgumentException lowAboveAnchor = Assert.ThrowsAny<ArgumentException>(() =>
            CreateSetup(anchor: 100m, high: 110m, low: 101m));
        Assert.Equal("lowSinceAnchor", lowAboveAnchor.ParamName);
    }

    [Fact]
    public void PositionUpdateCannotPrecedeOpenTime()
    {
        ArgumentException error = Assert.ThrowsAny<ArgumentException>(() =>
            new Position("position-1", Instrument, Direction.Long, new Quantity(1m),
                AddMinute(EventTime), EventTime, new Price(100m)));

        Assert.Equal("updatedAt", error.ParamName);
    }

    [Fact]
    public void MarketEventComparisonIdentifiesExactDuplicatesAndIdentityConflicts()
    {
        Trade previous = CreateTrade("event-1", Instrument, EventTime, 100m);
        Trade duplicate = CreateTrade("event-1", Instrument, EventTime, 100m);
        Trade conflict = CreateTrade("event-1", Instrument, EventTime, 101m);

        Assert.Equal(MarketEventRelation.Duplicate, MarketEventComparison.Compare(previous, duplicate));
        Assert.Equal(MarketEventRelation.IdentityConflict, MarketEventComparison.Compare(previous, conflict));
    }

    [Fact]
    public void EventIdentityIncludesConcreteTypeAndInstrument()
    {
        Trade previous = CreateTrade("shared-id", Instrument, EventTime, 100m);
        Trade otherInstrument = CreateTrade("shared-id", new InstrumentId("ETHUSDT"), EventTime, 100m);
        OIUpdate otherType = new("shared-id", Instrument, EventTime, 10m);

        Assert.Equal(MarketEventRelation.DifferentInstrument,
            MarketEventComparison.Compare(previous, otherInstrument));
        Assert.Equal(MarketEventRelation.SameEventTime,
            MarketEventComparison.Compare(previous, otherType));
    }

    [Fact]
    public void EventComparisonReportsEventTimeWithoutBreakingTies()
    {
        Trade previous = CreateTrade("event-1", Instrument, EventTime, 100m);

        Assert.Equal(MarketEventRelation.EarlierEventTime,
            MarketEventComparison.Compare(previous, CreateTrade("event-2", Instrument,
                new UtcTimestamp(DateTimeOffset.UnixEpoch.AddTicks(-1)), 101m)));
        Assert.Equal(MarketEventRelation.LaterEventTime,
            MarketEventComparison.Compare(previous, CreateTrade("event-3", Instrument,
                AddMinute(EventTime), 101m)));
        Assert.Equal(MarketEventRelation.SameEventTime,
            MarketEventComparison.Compare(previous, CreateTrade("event-4", Instrument, EventTime, 101m)));
    }

    [Fact]
    public void EventComparisonRejectsNullArguments()
    {
        Trade trade = CreateTrade("event-1", Instrument, EventTime, 100m);

        Assert.Throws<ArgumentNullException>(() => MarketEventComparison.Compare(null!, trade));
        Assert.Throws<ArgumentNullException>(() => MarketEventComparison.Compare(trade, null!));
    }

    private static UtcTimestamp AddMinute(UtcTimestamp timestamp)
    {
        return new UtcTimestamp(timestamp.Value.AddMinutes(1));
    }

    private static Trade CreateTrade(string eventId, InstrumentId instrumentId,
        UtcTimestamp eventTime, decimal price)
    {
        return new Trade(eventId, instrumentId, eventTime, new Price(price),
            new Quantity(1m), TradeSide.Buy);
    }

    private static Bar CreateBar(UtcTimestamp openTime, UtcTimestamp closeTime,
        decimal open, decimal high, decimal low, decimal close)
    {
        return new Bar("bar-1", Instrument, closeTime, openTime, closeTime,
            new Price(open), new Price(high), new Price(low), new Price(close), new Quantity(10m));
    }

    private static Setup CreateSetup(decimal anchor, decimal high, decimal low)
    {
        return new Setup("setup-1", Instrument, SetupType.BoundaryRejection, Direction.Long,
            SetupState.Observing, "swing-high-1", EventTime, new Price(anchor), new Price(101m),
            new Price(high), new Price(low), "context-v1");
    }
}
