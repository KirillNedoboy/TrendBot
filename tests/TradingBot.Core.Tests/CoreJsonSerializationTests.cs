using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class CoreJsonSerializationTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    private static readonly UtcTimestamp EventTime =
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3)));

    [Fact]
    public void RoundTripsAllCurrentCoreContracts()
    {
        Assert.Equal(new InstrumentId("BTCUSDT"), RoundTrip(new InstrumentId("BTCUSDT")));
        Assert.Equal(new Price(12.345678901234567890123456789m),
            RoundTrip(new Price(12.345678901234567890123456789m)));
        Assert.Equal(new Quantity(0m), RoundTrip(new Quantity(0m)));
        Assert.Equal(new Money(-12.345678901234567890123456789m, "USDT"),
            RoundTrip(new Money(-12.345678901234567890123456789m, "USDT")));
        Assert.Equal(EventTime, RoundTrip(EventTime));

        Trade trade = new("trade-1", Instrument, EventTime, new Price(123.456789m),
            new Quantity(0.00123456789m), TradeSide.Buy);
        Assert.Equal(trade, RoundTrip(trade));

        Bar bar = new("bar-1", Instrument, EventTime,
            new UtcTimestamp(EventTime.Value.AddMinutes(-1)), EventTime,
            new Price(100m), new Price(105m), new Price(95m), new Price(101m),
            new Quantity(12.3456789m));
        Assert.Equal(bar, RoundTrip(bar));

        BookLevel level = new(new Price(100m), new Quantity(2m));
        Assert.Equal(level, RoundTrip(level));

        BookDelta delta = new("book-1", Instrument, EventTime,
            [new BookLevel(new Price(100m), new Quantity(2m)),
                new BookLevel(new Price(99m), new Quantity(3m))],
            [new BookLevel(new Price(101m), new Quantity(4m))]);
        BookDelta restoredDelta = RoundTrip(delta);
        Assert.Equal(delta, restoredDelta);
        Assert.Equal([100m, 99m], restoredDelta.Bids.Select(static item => item.Price.Value));

        FundingUpdate funding = new("funding-1", Instrument, EventTime, 0.000123456789m,
            new UtcTimestamp(EventTime.Value.AddHours(8)));
        Assert.Equal(funding, RoundTrip(funding));
        Assert.Equal(new FundingUpdate("funding-2", Instrument, EventTime, -0.0001m),
            RoundTrip(new FundingUpdate("funding-2", Instrument, EventTime, -0.0001m)));

        OIUpdate oi = new("oi-1", Instrument, EventTime, 123456.7890123456789m);
        Assert.Equal(oi, RoundTrip(oi));

        Setup setup = new("setup-1", Instrument, SetupType.BoundaryRejection,
            Direction.Long, SetupState.Observing, "anchor-1", EventTime,
            new Price(100m), new Price(101m), new Price(110m), new Price(90m),
            "context-v1");
        Assert.Equal(setup, RoundTrip(setup));

        Decision decision = new("decision-1", "setup-1", DecisionOutcome.Allow,
            ["price-confirmed", "volume-confirmed"]);
        Assert.Equal(decision, RoundTrip(decision));

        RiskDecision risk = new("risk-1", "decision-1", RiskDecisionOutcome.Blocked,
            ["kill-switch"]);
        Assert.Equal(risk, RoundTrip(risk));

        OrderIntent intent = new("intent-1", "run-1", "setup-1", "client-1", Instrument,
            Direction.Short, OrderRole.Entry, 1, new Quantity(0.01m), new Price(99.5m),
            EventTime, true);
        Assert.Equal(intent, RoundTrip(intent));

        Position position = new("position-1", Instrument, Direction.Long, new Quantity(1.25m),
            EventTime, new UtcTimestamp(EventTime.Value.AddMinutes(2)), new Price(100m),
            new Money(1.5m, "USDT"));
        Assert.Equal(position, RoundTrip(position));
    }

    [Fact]
    public void MarketEventRoundTripUsesStableDiscriminatorForEveryEventKind()
    {
        MarketEvent[] events =
        [
            new Trade("trade-1", Instrument, EventTime, new Price(100m), new Quantity(1m), TradeSide.Buy),
            new Bar("bar-1", Instrument, EventTime, new UtcTimestamp(EventTime.Value.AddMinutes(-1)),
                EventTime, new Price(99m), new Price(101m), new Price(98m), new Price(100m), new Quantity(10m)),
            new BookDelta("book-1", Instrument, EventTime,
                [new BookLevel(new Price(99m), new Quantity(1m))],
                [new BookLevel(new Price(101m), new Quantity(2m))]),
            new FundingUpdate("funding-1", Instrument, EventTime, 0.0001m),
            new OIUpdate("oi-1", Instrument, EventTime, 100m)
        ];
        string[] discriminators = ["trade", "bar", "bookDelta", "fundingUpdate", "oiUpdate"];

        for (int index = 0; index < events.Length; index++)
        {
            string json = CoreJsonSerializer.Serialize<MarketEvent>(events[index]);
            Assert.Contains($"\"$type\":\"{discriminators[index]}\"", json);
            Assert.Equal(events[index], CoreJsonSerializer.Deserialize<MarketEvent>(json));

            // Runtime-inferred serialization keeps the same wire contract.
            Assert.Contains($"\"$type\":\"{discriminators[index]}\"",
                SerializeRuntime(events[index]));
        }
    }

    [Fact]
    public void SerializationIsCompactDeterministicCultureIndependentAndStrict()
    {
        Trade trade = new("trade-1", Instrument, EventTime,
            new Price(123456789.01234567890123456789m),
            new Quantity(0.000000000123456789m), TradeSide.Sell);
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            string enUsJson = CoreJsonSerializer.Serialize<MarketEvent>(trade);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            string frJson = CoreJsonSerializer.Serialize<MarketEvent>(trade);
            string repeatedFrJson = CoreJsonSerializer.Serialize<MarketEvent>(trade);

            Assert.Equal(enUsJson, frJson);
            Assert.Equal(frJson, repeatedFrJson);
            Assert.DoesNotContain(" ", frJson);
            Assert.Contains("\"side\":\"sell\"", frJson);
            Assert.Contains("123456789.01234567890123456789", frJson);
            Assert.Equal(trade, CoreJsonSerializer.Deserialize<MarketEvent>(frJson));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void InvalidJsonUnknownMembersMissingRequiredValuesAndDomainViolationsAreJsonExceptions()
    {
        const string validTrade = "{\"eventId\":\"trade-1\",\"instrumentId\":{\"value\":\"BTCUSDT\"},\"eventTime\":{\"value\":\"2026-01-02T00:00:00+00:00\"},\"price\":{\"value\":100},\"quantity\":{\"value\":1},\"side\":\"buy\"}";

        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Trade>(null!));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Trade>(
            validTrade[..^1] + ",\"unexpected\":true}"));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Trade>(
            validTrade.Replace(",\"side\":\"buy\"", string.Empty, StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Trade>(
            validTrade.Replace("\"value\":100", "\"value\":\"100\"", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Trade>(
            validTrade.Replace("\"value\":100", "\"value\":0", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Trade>(
            validTrade.Replace("\"side\":\"buy\"", "\"side\":1", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Trade>(
            validTrade.Replace("\"side\":\"buy\"", "\"side\":\"hold\"", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<MarketEvent>(
            validTrade[..^1] + ",\"$type\":\"unknown\"}"));

        string tradeJson = CoreJsonSerializer.Serialize<MarketEvent>(
            new Trade("trade-2", Instrument, EventTime, new Price(100m), new Quantity(1m), TradeSide.Buy));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Bar>(tradeJson));
    }

    [Fact]
    public void ImmutableCollectionsPreserveOrderAndDefensiveCopies()
    {
        List<BookLevel> bids =
        [new BookLevel(new Price(100m), new Quantity(1m)),
            new BookLevel(new Price(99m), new Quantity(2m))];
        List<BookLevel> asks =
        [new BookLevel(new Price(101m), new Quantity(3m)),
            new BookLevel(new Price(102m), new Quantity(4m))];
        BookDelta original = new("book-1", Instrument, EventTime, bids, asks);
        string json = CoreJsonSerializer.Serialize(original);
        BookDelta restored = CoreJsonSerializer.Deserialize<BookDelta>(json);

        bids.Clear();
        asks.Clear();

        Assert.Equal(2, restored.Bids.Count);
        Assert.Equal(100m, restored.Bids[0].Price.Value);
        Assert.Equal(99m, restored.Bids[1].Price.Value);
        Assert.Equal(2, restored.Asks.Count);
        Assert.Equal(101m, restored.Asks[0].Price.Value);
        Assert.Equal(102m, restored.Asks[1].Price.Value);
        Assert.False((object)restored.Bids is BookLevel[]);

        Decision decision = new("decision-1", "setup-1", DecisionOutcome.Allow,
            ["first", "second"]);
        Decision restoredDecision = RoundTrip(decision);
        Assert.Equal(["first", "second"], restoredDecision.Reasons);

        RiskDecision risk = new("risk-1", "decision-1", RiskDecisionOutcome.Blocked,
            ["risk-first", "risk-second"]);
        RiskDecision restoredRisk = RoundTrip(risk);
        Assert.Equal(["risk-first", "risk-second"], restoredRisk.Reasons);
    }

    [Fact]
    public void NullableFundingTimeRoundTripsAsExplicitNull()
    {
        FundingUpdate funding = new("funding-1", Instrument, EventTime, 0.0001m);

        FundingUpdate restored = RoundTrip(funding);

        Assert.Null(restored.NextFundingTime);
        Assert.Contains("\"nextFundingTime\":null", CoreJsonSerializer.Serialize(funding));
    }

    [Fact]
    public void InvalidBookDeltaNullListEntriesAndBarOrSetupInvariantsAreJsonExceptions()
    {
        BookDelta book = new("book-1", Instrument, EventTime,
            [new BookLevel(new Price(100m), new Quantity(1m))],
            [new BookLevel(new Price(101m), new Quantity(2m))]);
        string bookJson = CoreJsonSerializer.Serialize<BookDelta>(book);
        JsonObject nullEntryDocument = JsonNode.Parse(bookJson)!.AsObject();
        nullEntryDocument["bids"]!.AsArray().Insert(0, null);
        JsonObject nullListDocument = JsonNode.Parse(bookJson)!.AsObject();
        nullListDocument["bids"] = null;

        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<BookDelta>(
            nullEntryDocument.ToJsonString()));
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<BookDelta>(
            nullListDocument.ToJsonString()));

        Bar bar = new("bar-1", Instrument, EventTime,
            new UtcTimestamp(EventTime.Value.AddMinutes(-1)), EventTime,
            new Price(100m), new Price(105m), new Price(95m), new Price(101m),
            new Quantity(1m));
        string barJson = CoreJsonSerializer.Serialize<Bar>(bar);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Bar>(
            barJson.Replace("\"high\":{\"value\":105}", "\"high\":{\"value\":99}", StringComparison.Ordinal)));

        Setup setup = new("setup-1", Instrument, SetupType.BoundaryRejection,
            Direction.Long, SetupState.Observing, "anchor-1", EventTime,
            new Price(100m), new Price(101m), new Price(110m), new Price(90m),
            "context-v1");
        string setupJson = CoreJsonSerializer.Serialize(setup);
        Assert.Throws<JsonException>(() => CoreJsonSerializer.Deserialize<Setup>(
            setupJson.Replace("\"highSinceAnchor\":{\"value\":110}",
                "\"highSinceAnchor\":{\"value\":99}", StringComparison.Ordinal)));
    }

    private static T RoundTrip<T>(T value)
    {
        return CoreJsonSerializer.Deserialize<T>(CoreJsonSerializer.Serialize(value));
    }

    private static string SerializeRuntime(MarketEvent value)
    {
        return value switch
        {
            Trade trade => CoreJsonSerializer.Serialize(trade),
            Bar bar => CoreJsonSerializer.Serialize(bar),
            BookDelta delta => CoreJsonSerializer.Serialize(delta),
            FundingUpdate funding => CoreJsonSerializer.Serialize(funding),
            OIUpdate oi => CoreJsonSerializer.Serialize(oi),
            _ => throw new InvalidOperationException()
        };
    }
}
