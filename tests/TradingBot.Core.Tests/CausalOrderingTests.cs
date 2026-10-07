using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CausalOrderingTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");
    private static readonly UtcTimestamp EventTime = new(DateTimeOffset.UnixEpoch);
    private static readonly string[] ExpectedOrder = ["first", "second"];
    private static readonly string[] RequiredCausalMetadataProperties =
        ["receiveTime", "availabilityTime", "stableSequence"];

    [Fact]
    public void CausalMarketEventRequiresPayloadTimesAndNonNegativeSequence()
    {
        UtcTimestamp receiveTime = AddSeconds(EventTime, 1);
        UtcTimestamp availabilityTime = AddSeconds(EventTime, 2);
        Trade trade = CreateTrade("trade-1", EventTime, 100m);

        CausalMarketEvent causal = new(trade, receiveTime, availabilityTime, 0);

        Assert.Equal(trade, causal.Payload);
        Assert.Equal(receiveTime, causal.ReceiveTime);
        Assert.Equal(availabilityTime, causal.AvailabilityTime);
        Assert.Equal(0, causal.StableSequence);
        Assert.Null(causal.ExchangeTime);

        Assert.Throws<ArgumentNullException>(() =>
            new CausalMarketEvent(null!, receiveTime, availabilityTime, 0));
        Assert.Throws<ArgumentNullException>(() =>
            new CausalMarketEvent(trade, null!, availabilityTime, 0));
        Assert.Throws<ArgumentNullException>(() =>
            new CausalMarketEvent(trade, receiveTime, null!, 0));
        Assert.Throws<ArgumentException>(() =>
            new CausalMarketEvent(trade, availabilityTime, receiveTime, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CausalMarketEvent(trade, receiveTime, availabilityTime, -1));

        CausalMarketEvent maxSequence = new(trade, receiveTime, availabilityTime, long.MaxValue);
        Assert.Equal(long.MaxValue, maxSequence.StableSequence);
    }

    [Fact]
    public void OrderingUsesEventTimeThenStableSequenceOnly()
    {
        Trade firstPayload = CreateTrade("event-a", EventTime, 100m);
        Trade secondPayload = CreateTrade("event-b", EventTime, 101m);
        UtcTimestamp receiveTime = AddSeconds(EventTime, 1);
        UtcTimestamp availabilityTime = AddSeconds(EventTime, 2);

        CausalMarketEvent first = new(firstPayload, receiveTime, availabilityTime, 7,
            AddSeconds(EventTime, 100));
        CausalMarketEvent second = new(secondPayload, receiveTime, availabilityTime, 8,
            AddSeconds(EventTime, -100));
        CausalMarketEvent earlierEventTime = new(
            CreateTrade("event-c", AddSeconds(EventTime, -1), 102m),
            receiveTime, availabilityTime, long.MaxValue);

        Assert.True(CausalMarketEventOrdering.Compare(first, second) < 0);
        Assert.True(CausalMarketEventOrdering.Compare(second, first) > 0);
        Assert.True(CausalMarketEventOrdering.Compare(earlierEventTime, first) < 0);

        CausalMarketEvent sameKey = new(CreateTrade("event-d", EventTime, 999m),
            AddSeconds(EventTime, 3), AddSeconds(EventTime, 4), 7,
            AddSeconds(EventTime, 500));
        Assert.Equal(0, CausalMarketEventOrdering.Compare(first, sameKey));
        Assert.Equal(0, CausalMarketEventOrdering.Compare(sameKey, first));
        Assert.Equal(MarketEventRelation.SameEventTime,
            MarketEventComparison.Compare(first.Payload, sameKey.Payload));
    }

    [Fact]
    public void CursorEligibilityUsesInclusiveEventAvailabilityAndSequenceBounds()
    {
        UtcTimestamp asOf = AddSeconds(EventTime, 10);
        CausalEventCursor cursor = new(asOf, 10);
        CausalMarketEvent eligible = CreateCausal("eligible", asOf, asOf, 10);
        CausalEventCursor zeroCursor = new(asOf, 0);

        Assert.True(cursor.IsEligible(eligible));
        Assert.True(CausalEventCursor.IsEligible(eligible, cursor));
        Assert.True(CausalMarketEventOrdering.IsEligible(eligible, cursor));
        Assert.True(zeroCursor.IsEligible(CreateCausal("zero", asOf, asOf, 0)));

        Assert.False(cursor.IsEligible(CreateCausal("future-event", AddSeconds(asOf, 1), asOf, 10)));
        Assert.False(cursor.IsEligible(CreateCausal("late-arrival", asOf, AddSeconds(asOf, 1), 10)));
        Assert.False(cursor.IsEligible(CreateCausal("future-sequence", asOf, asOf, 11)));

        Assert.Throws<ArgumentNullException>(() => new CausalEventCursor(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CausalEventCursor(asOf, -1));
    }

    [Fact]
    public void IneligibleEventsCannotChangeTheEligibleSortedAsOfView()
    {
        CausalEventCursor cursor = new(AddSeconds(EventTime, 10), 2);
        CausalMarketEvent first = CreateCausal("first", AddSeconds(EventTime, 1),
            AddSeconds(EventTime, 2), 1);
        CausalMarketEvent second = CreateCausal("second", AddSeconds(EventTime, 1),
            AddSeconds(EventTime, 2), 2);
        CausalMarketEvent late = CreateCausal("late", AddSeconds(EventTime, 1),
            AddSeconds(EventTime, 11), 0);
        CausalMarketEvent futureSequence = CreateCausal("future-sequence", AddSeconds(EventTime, 1),
            AddSeconds(EventTime, 2), 3);

        static string[] EligibleOrder(IEnumerable<CausalMarketEvent> events, CausalEventCursor asOf)
        {
            return events.Where(asOf.IsEligible)
                .OrderBy(static item => item, Comparer<CausalMarketEvent>.Create(
                    CausalMarketEventOrdering.Compare))
                .Select(static item => item.Payload.EventId)
                .ToArray();
        }

        string[] original = EligibleOrder([second, first], cursor);
        string[] withUnavailable = EligibleOrder([second, late, first, futureSequence], cursor);

        Assert.Equal(ExpectedOrder, original);
        Assert.Equal(original, withUnavailable);
    }

    [Fact]
    public void CausalMetadataDoesNotChangePayloadDuplicateSemantics()
    {
        Trade payload = CreateTrade("event-1", EventTime, 100m);
        CausalMarketEvent first = CreateCausal(payload, AddSeconds(EventTime, 1),
            AddSeconds(EventTime, 2), 1);
        CausalMarketEvent second = CreateCausal(payload, AddSeconds(EventTime, 3),
            AddSeconds(EventTime, 4), 2, AddSeconds(EventTime, 5));

        Assert.Equal(MarketEventRelation.Duplicate,
            MarketEventComparison.Compare(first.Payload, second.Payload));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CausalContractsRoundTripThroughCoreJsonWithPolymorphicPayload()
    {
        CausalMarketEvent original = new(
            CreateTrade("trade-1", EventTime, 123.45678901234567890123456789m),
            AddSeconds(EventTime, 1), AddSeconds(EventTime, 2), long.MaxValue,
            AddSeconds(EventTime, 3));
        CausalMarketEvent restored = CoreJsonSerializer.Deserialize<CausalMarketEvent>(
            CoreJsonSerializer.Serialize(original));

        Assert.Equal(original, restored);
        Assert.IsType<Trade>(restored.Payload);
        Assert.Equal(long.MaxValue, restored.StableSequence);
        Assert.Equal(original.Payload.EventTime, restored.Payload.EventTime);

        CausalMarketEvent withoutExchangeTime = CreateCausal("trade-2", EventTime,
            AddSeconds(EventTime, 1), 0);
        string nullJson = CoreJsonSerializer.Serialize(withoutExchangeTime);
        Assert.Contains("\"exchangeTime\":null", nullJson);
        Assert.Equal(withoutExchangeTime,
            CoreJsonSerializer.Deserialize<CausalMarketEvent>(nullJson));

        CausalEventCursor cursor = new(AddSeconds(EventTime, 5), long.MaxValue);
        Assert.Equal(cursor, CoreJsonSerializer.Deserialize<CausalEventCursor>(
            CoreJsonSerializer.Serialize(cursor)));
    }

    [Fact]
    public void CausalJsonRequiresReceiveAvailabilityAndSequenceFields()
    {
        CausalMarketEvent original = CreateCausal("trade-1", EventTime,
            AddSeconds(EventTime, 1), 0);
        JsonObject document = JsonNode.Parse(CoreJsonSerializer.Serialize(original))!.AsObject();

        foreach (string requiredProperty in RequiredCausalMetadataProperties)
        {
            JsonObject missing = JsonNode.Parse(document.ToJsonString())!.AsObject();
            missing.Remove(requiredProperty);

            Assert.Throws<JsonException>(() =>
                CoreJsonSerializer.Deserialize<CausalMarketEvent>(missing.ToJsonString()));
        }
    }

    private static CausalMarketEvent CreateCausal(string eventId, UtcTimestamp eventTime,
        UtcTimestamp availabilityTime, long stableSequence,
        UtcTimestamp? exchangeTime = null)
    {
        return CreateCausal(CreateTrade(eventId, eventTime, 100m),
            AddSeconds(eventTime, -1), availabilityTime, stableSequence, exchangeTime);
    }

    private static CausalMarketEvent CreateCausal(MarketEvent payload, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, long stableSequence, UtcTimestamp? exchangeTime = null)
    {
        return new CausalMarketEvent(payload, receiveTime, availabilityTime, stableSequence,
            exchangeTime);
    }

    private static Trade CreateTrade(string eventId, UtcTimestamp eventTime, decimal price)
    {
        return new Trade(eventId, Instrument, eventTime, new Price(price),
            new Quantity(1m), TradeSide.Buy);
    }

    private static UtcTimestamp AddSeconds(UtcTimestamp timestamp, int seconds)
    {
        return new UtcTimestamp(timestamp.Value.AddSeconds(seconds));
    }
}
