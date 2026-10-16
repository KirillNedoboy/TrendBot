using System.Globalization;
using TradingBot.Core;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class MetadataCandidateFilterTests
{
    [Fact]
    public void EvaluateIncludesOnlyUsdtPerpetualTradingSymbolsAndRetainsUnknownFilters()
    {
        ExchangeEligibilityMetadata[] input =
        [
            Metadata("ZUSDT"),
            Metadata("AUSDT", contractType: "CURRENT_QUARTER"),
            Metadata("BUSDC", quoteAsset: "USDC"),
            Metadata("CUSDT", status: "SETTLING"),
            Metadata("DUSDT", marginAsset: "USDC")
        ];

        MetadataCandidateFilterResult result = MetadataCandidateFilter.Evaluate(input);

        Assert.Equal(["ZUSDT"], result.Candidates.Select(static item => item.InstrumentId.Value));
        Assert.Equal(4, result.Exclusions.Count);
        Assert.Contains(result.Candidates[0].Filters,
            static filter => filter.FilterType == "NEW_OFFICIAL_FILTER");
        Assert.Equal(["contract-type-not-perpetual"],
            result.Exclusions.Single(static item => item.Metadata.InstrumentId.Value == "AUSDT").Reasons);
        Assert.Equal(["quote-asset-not-usdt"],
            result.Exclusions.Single(static item => item.Metadata.InstrumentId.Value == "BUSDC").Reasons);
        Assert.Equal(["status-not-trading"],
            result.Exclusions.Single(static item => item.Metadata.InstrumentId.Value == "CUSDT").Reasons);
        Assert.Equal(["margin-asset-not-usdt"],
            result.Exclusions.Single(static item => item.Metadata.InstrumentId.Value == "DUSDT").Reasons);
    }

    [Fact]
    public void EvaluateRequiresAndValidatesRequiredFiltersWithStableReasons()
    {
        ExchangeEligibilityMetadata metadata = Metadata("BTCUSDT",
            filters:
            [
                new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 2m, maxPrice: 1m,
                    tickSize: 0m),
                new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 3m, maxPrice: 1m,
                    tickSize: 0m),
                new ExchangeMetadataFilter("LOT_SIZE", minQuantity: 0m, maxQuantity: 1m,
                    stepSize: 0m)
            ]);

        MetadataCandidateFilterResult result = MetadataCandidateFilter.Evaluate([metadata]);

        Assert.Empty(result.Candidates);
        Assert.Equal(
            [
                "duplicate-filter:PRICE_FILTER",
                "conflicting-duplicate-filter:PRICE_FILTER",
                "malformed-filter:PRICE_FILTER:maxPrice-before-minPrice",
                "malformed-filter:LOT_SIZE:stepSize-not-positive",
                "missing-filter:MIN_NOTIONAL"
            ], result.Exclusions.Single().Reasons);
    }

    [Fact]
    public void EvaluateAllowsDocumentedZeroPriceFilterValuesAndIsCultureIndependent()
    {
        ExchangeEligibilityMetadata metadata = Metadata("BTCUSDT",
            filters:
            [
                new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 0m, maxPrice: 0m,
                    tickSize: 0m),
                new ExchangeMetadataFilter("LOT_SIZE", minQuantity: 0.001m,
                    maxQuantity: 100m, stepSize: 0.001m),
                new ExchangeMetadataFilter("MIN_NOTIONAL", notional: 5m)
            ]);
        CultureInfo original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            MetadataCandidateFilterResult first = MetadataCandidateFilter.Evaluate([metadata]);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            MetadataCandidateFilterResult second = MetadataCandidateFilter.Evaluate([metadata]);

            Assert.Equal(first, second);
            Assert.Single(first.Candidates);
            Assert.Empty(first.Exclusions);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void EvaluateValidatesPresentRecognizedOptionalFilterFields()
    {
        ExchangeMetadataFilter[] filters =
        [
            new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 0m, maxPrice: 0m, tickSize: 0m),
            new ExchangeMetadataFilter("LOT_SIZE", minQuantity: 0.001m,
                maxQuantity: 100m, stepSize: 0.001m),
            new ExchangeMetadataFilter("MIN_NOTIONAL", notional: 5m),
            new ExchangeMetadataFilter("PERCENT_PRICE", multiplierUp: 0m,
                multiplierDown: 1.1m, multiplierDecimal: -1),
            new ExchangeMetadataFilter("MAX_NUM_ORDERS", limit: 0)
        ];

        MetadataCandidateFilterResult result = MetadataCandidateFilter.Evaluate(
            [Metadata("BTCUSDT", filters: filters)]);

        Assert.Equal(
            [
                "malformed-filter:MAX_NUM_ORDERS:limit-not-positive",
                "malformed-filter:PERCENT_PRICE:multiplierDecimal-negative",
                "malformed-filter:PERCENT_PRICE:multiplierUp-not-positive"
            ], result.Exclusions.Single().Reasons);
    }

    [Fact]
    public void EvaluateIsRepeatableForShuffledInputAndUsesOrdinalSymbolOrdering()
    {
        ExchangeEligibilityMetadata[] ordered = [Metadata("aUSDT"), Metadata("AUSDT"), Metadata("BUSDT")];
        ExchangeEligibilityMetadata[] shuffled = [ordered[2], ordered[0], ordered[1]];

        MetadataCandidateFilterResult first = MetadataCandidateFilter.Evaluate(ordered);
        MetadataCandidateFilterResult second = MetadataCandidateFilter.Evaluate(shuffled);

        Assert.Equal(first, second);
        Assert.Equal(["AUSDT", "BUSDT", "aUSDT"],
            first.Candidates.Select(static item => item.InstrumentId.Value));
    }

    private static ExchangeEligibilityMetadata Metadata(string symbol,
        string contractType = "PERPETUAL", string status = "TRADING",
        string quoteAsset = "USDT", string marginAsset = "USDT",
        IEnumerable<ExchangeMetadataFilter>? filters = null)
    {
        return new ExchangeEligibilityMetadata(new InstrumentId(symbol), symbol, contractType, status,
            symbol[..^4], quoteAsset, marginAsset,
            new UtcTimestamp(DateTimeOffset.UnixEpoch),
            new UtcTimestamp(DateTimeOffset.UnixEpoch.AddDays(1)),
            8, 8, 8, 8, 2.5000m, 5.0000m,
            filters ??
            [
                new ExchangeMetadataFilter("PRICE_FILTER", minPrice: 0.01m, maxPrice: 1_000_000m,
                    tickSize: 0.01m),
                new ExchangeMetadataFilter("LOT_SIZE", minQuantity: 0.001m,
                    maxQuantity: 100_000m, stepSize: 0.001m),
                new ExchangeMetadataFilter("MIN_NOTIONAL", notional: 5m),
                new ExchangeMetadataFilter("NEW_OFFICIAL_FILTER",
                    unknownFields: [new MetadataFilterField("newDecimal", "\"0.00000000000000000001\"")])
            ],
            [], new UtcTimestamp(DateTimeOffset.UnixEpoch.AddSeconds(2)),
            new UtcTimestamp(DateTimeOffset.UnixEpoch.AddSeconds(3)));
    }
}
