using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ExchangeEligibilityMetadataTests
{
    [Fact]
    public void MetadataRetainsExactFiltersUnknownFieldsAndCausalProvenance()
    {
        UtcTimestamp onboard = new(DateTimeOffset.FromUnixTimeMilliseconds(1_500));
        UtcTimestamp delivery = new(DateTimeOffset.FromUnixTimeMilliseconds(4_500));
        UtcTimestamp receive = new(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        UtcTimestamp availability = new(DateTimeOffset.FromUnixTimeMilliseconds(10_125));
        ExchangeMetadataFilter priceFilter = new("PRICE_FILTER", 0.000000010000m,
            1000000.000000000000m, 0.000000010000m, unknownFields:
            [new MetadataFilterField("unmodeled", "\"0.00000000000000000001\"")]);

        ExchangeEligibilityMetadata metadata = new(new InstrumentId("BTCUSDT"), "BTCUSDT",
            "PERPETUAL", "TRADING", "BTC", "USDT", "USDT", onboard, delivery,
            2, 3, 8, 8, 2.5000m, 5.0000m, [priceFilter],
            [new MetadataField("newSymbolField", "[\"A\",\"B\"]")], receive, availability);

        Assert.Equal(0.000000010000m, metadata.Filters[0].MinPrice);
        Assert.Equal(0.000000010000m, metadata.Filters[0].TickSize);
        Assert.Equal("\"0.00000000000000000001\"", metadata.Filters[0].UnknownFields[0].RawValue);
        Assert.Equal("[\"A\",\"B\"]", metadata.UnknownFields[0].RawValue);
        Assert.Equal(receive, metadata.ReceiveTime);
        Assert.Equal(availability, metadata.AvailabilityTime);
    }

    [Fact]
    public void MetadataJsonRoundTripPreservesDecimalsAndUnknownFieldOrder()
    {
        ExchangeEligibilityMetadata metadata = new(new InstrumentId("ETHUSDT"), "ETHUSDT",
            "PERPETUAL", "TRADING", "ETH", "USDT", "USDT",
            new UtcTimestamp(DateTimeOffset.UnixEpoch),
            new UtcTimestamp(DateTimeOffset.UnixEpoch.AddDays(1)),
            2, 3, 8, 8, 2.5000m, 5.0000m,
            [new ExchangeMetadataFilter("MIN_NOTIONAL", notional: 5.00000000000000000001m)],
            [new MetadataField("zeta", "2"), new MetadataField("alpha", "1")],
            new UtcTimestamp(DateTimeOffset.UnixEpoch.AddSeconds(2)),
            new UtcTimestamp(DateTimeOffset.UnixEpoch.AddSeconds(3)));

        ExchangeEligibilityMetadata restored = CoreJsonSerializer.Deserialize<ExchangeEligibilityMetadata>(
            CoreJsonSerializer.Serialize(metadata));

        Assert.Equal(metadata, restored);
        Assert.Equal(["alpha", "zeta"], restored.UnknownFields.Select(static field => field.Name));
        Assert.Equal(5.00000000000000000001m, restored.Filters[0].Notional);
    }
}
