using System.Text.Json;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ReplayEnvelopeTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3));

    [Fact]
    public void StrictJsonRoundTripPreservesAllFiveEventKindsAndCausalTuple()
    {
        CausalMarketEvent[] events =
        [
            new(new Trade("trade-1", Instrument, Time(0),
                    new Price(12345678901234567890123456789m),
                    new Quantity(1.00000000000000000000000000001m), TradeSide.Buy),
                Time(1), Time(2), 0, Time(3)),
            new(new Bar("bar-1", Instrument, Time(1), Time(0), Time(1),
                    new Price(100m), new Price(105m), new Price(95m), new Price(101m),
                    new Quantity(12.34567890123456789m)),
                Time(2), Time(3), 1),
            new(new BookDelta("book-1", Instrument, Time(2),
                    [new BookLevel(new Price(99m), new Quantity(1.23456789m))],
                    [new BookLevel(new Price(101m), new Quantity(2.3456789m))]),
                Time(3), Time(4), 2),
            new(new FundingUpdate("funding-1", Instrument, Time(3),
                    0.00012345678901234567890123456789m, Time(11)),
                Time(4), Time(5), 3),
            new(new OIUpdate("oi-1", Instrument, Time(4),
                    123456.78901234567890123456789m),
                Time(5), Time(6), 4)
        ];

        foreach (CausalMarketEvent causalEvent in events)
        {
            ReplayEnvelope envelope = new("stream-a", causalEvent);
            string json = CoreJsonSerializer.Serialize(envelope);
            ReplayEnvelope restored = CoreJsonSerializer.Deserialize<ReplayEnvelope>(json);

            Assert.Equal(envelope, restored);
            Assert.Contains("\"formatVersion\":1", json);
            Assert.Contains("\"streamId\":\"stream-a\"", json);
        }

        ReplayEnvelope tradeEnvelope = new("stream-a", events[0]);
        Trade trade = Assert.IsType<Trade>(
            CoreJsonSerializer.Deserialize<ReplayEnvelope>(
                CoreJsonSerializer.Serialize(tradeEnvelope)).CausalEvent.Payload);
        Assert.Equal(12345678901234567890123456789m, trade.Price.Value);
        Assert.Equal(events[0].ReceiveTime, tradeEnvelope.CausalEvent.ReceiveTime);
        Assert.Equal(events[0].AvailabilityTime, tradeEnvelope.CausalEvent.AvailabilityTime);
        Assert.Equal(events[0].ExchangeTime, tradeEnvelope.CausalEvent.ExchangeTime);
    }

    [Fact]
    public void UnknownEnvelopeVersionsAndMembersAreRejected()
    {
        ReplayEnvelope envelope = new("stream-a", CreateTrade("trade-1", 0, Time(0).Value));
        string json = CoreJsonSerializer.Serialize(envelope);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ReplayEnvelope(2, "stream-a", envelope.CausalEvent));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<ReplayEnvelope>(
            json.Replace("\"formatVersion\":1", "\"formatVersion\":2",
                StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<ReplayEnvelope>(
            json[..^1] + ",\"unexpected\":true}"));
    }

    [Fact]
    public void EnvelopeRequiresNonEmptyStreamAndCausalEvent()
    {
        CausalMarketEvent causalEvent = CreateTrade("trade-1", 0, Time(0).Value);

        Assert.Throws<ArgumentException>(() => new ReplayEnvelope(" ", causalEvent));
        Assert.Throws<ArgumentNullException>(() => new ReplayEnvelope("stream-a", null!));
    }

    private static CausalMarketEvent CreateTrade(string eventId, long sequence,
        DateTimeOffset eventTime) => new(
        new Trade(eventId, Instrument, new UtcTimestamp(eventTime), new Price(100m),
            new Quantity(1m), TradeSide.Buy), new UtcTimestamp(eventTime),
        new UtcTimestamp(eventTime.AddSeconds(1)), sequence);

    private static UtcTimestamp Time(int minutes) =>
        new(BaseTime.AddMinutes(minutes).ToUniversalTime());
}
