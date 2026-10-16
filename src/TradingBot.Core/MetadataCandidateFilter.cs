using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>One metadata candidate excluded before any feature or clock input is read.</summary>
public sealed record MetadataCandidateExclusion
{
    public ExchangeEligibilityMetadata Metadata { get; }

    public ImmutableValueList<string> Reasons { get; }

    public MetadataCandidateExclusion(ExchangeEligibilityMetadata metadata,
        IEnumerable<string> reasons)
        : this(metadata, SnapshotReasons(reasons))
    {
    }

    [JsonConstructor]
    public MetadataCandidateExclusion(ExchangeEligibilityMetadata metadata,
        ImmutableValueList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons.Count == 0 || reasons.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A metadata exclusion requires one or more reasons.",
                nameof(reasons));
        }

        Metadata = metadata;
        Reasons = new ImmutableValueList<string>(reasons);
    }

    private static ImmutableValueList<string> SnapshotReasons(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return new ImmutableValueList<string>(reasons);
    }
}

/// <summary>Deterministic result of the metadata-only universe gate.</summary>
public sealed record MetadataCandidateFilterResult
{
    public ImmutableValueList<ExchangeEligibilityMetadata> Candidates { get; }

    public ImmutableValueList<MetadataCandidateExclusion> Exclusions { get; }

    public MetadataCandidateFilterResult(IEnumerable<ExchangeEligibilityMetadata> candidates,
        IEnumerable<MetadataCandidateExclusion> exclusions)
        : this(Snapshot(candidates), Snapshot(exclusions))
    {
    }

    [JsonConstructor]
    public MetadataCandidateFilterResult(
        ImmutableValueList<ExchangeEligibilityMetadata> candidates,
        ImmutableValueList<MetadataCandidateExclusion> exclusions)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(exclusions);
        Candidates = new ImmutableValueList<ExchangeEligibilityMetadata>(candidates);
        Exclusions = new ImmutableValueList<MetadataCandidateExclusion>(exclusions);
    }

    private static ImmutableValueList<T> Snapshot<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ImmutableValueList<T>(values);
    }
}

/// <summary>
/// Applies only the immutable exchange metadata eligibility gate. It reads no
/// clock, network, screener, feature, ranking, pool-limit, trade, or execution state.
/// </summary>
public static class MetadataCandidateFilter
{
    private const string Perpetual = "PERPETUAL";
    private const string Trading = "TRADING";
    private const string Usdt = "USDT";

    private static readonly string[] RequiredFilterTypes =
    [
        "PRICE_FILTER",
        "LOT_SIZE",
        "MIN_NOTIONAL"
    ];

    public static MetadataCandidateFilterResult Evaluate(
        IEnumerable<ExchangeEligibilityMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ExchangeEligibilityMetadata[] snapshot = metadata.ToArray();
        if (snapshot.Any(static item => item is null))
        {
            throw new ArgumentException("Metadata candidates cannot contain null values.",
                nameof(metadata));
        }

        List<ExchangeEligibilityMetadata> candidates = [];
        List<MetadataCandidateExclusion> exclusions = [];

        foreach (IGrouping<string, ExchangeEligibilityMetadata> group in snapshot
            .GroupBy(static item => item.InstrumentId.Value, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            ExchangeEligibilityMetadata item = group.Count() == 1
                ? group.Single()
                : group.OrderBy(static candidate => SortKey(candidate), StringComparer.Ordinal).First();
            List<string> reasons = EvaluateReasons(item);
            if (group.Count() > 1)
            {
                reasons.Add("duplicate-symbol");
            }

            string[] orderedReasons = OrderReasons(reasons);
            if (orderedReasons.Length == 0)
            {
                candidates.Add(item);
            }
            else
            {
                exclusions.Add(new MetadataCandidateExclusion(item, orderedReasons));
            }
        }

        return new MetadataCandidateFilterResult(candidates, exclusions);
    }

    private static List<string> EvaluateReasons(ExchangeEligibilityMetadata metadata)
    {
        List<string> reasons = [];
        if (!StringComparer.Ordinal.Equals(metadata.ContractType, Perpetual))
        {
            reasons.Add("contract-type-not-perpetual");
        }

        if (!StringComparer.Ordinal.Equals(metadata.Status, Trading))
        {
            reasons.Add("status-not-trading");
        }

        if (!StringComparer.Ordinal.Equals(metadata.QuoteAsset, Usdt))
        {
            reasons.Add("quote-asset-not-usdt");
        }

        if (!StringComparer.Ordinal.Equals(metadata.MarginAsset, Usdt))
        {
            reasons.Add("margin-asset-not-usdt");
        }

        Dictionary<string, List<ExchangeMetadataFilter>> filters = metadata.Filters
            .GroupBy(static filter => filter.FilterType, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(),
                StringComparer.Ordinal);

        foreach (KeyValuePair<string, List<ExchangeMetadataFilter>> pair in filters
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Value.Count > 1)
            {
                reasons.Add($"duplicate-filter:{pair.Key}");
                if (pair.Value.Skip(1).Any(filter => !filter.Equals(pair.Value[0])))
                {
                    reasons.Add($"conflicting-duplicate-filter:{pair.Key}");
                }
            }

            ValidateFilter(pair.Key, pair.Value[0], reasons);
        }

        foreach (string requiredType in RequiredFilterTypes)
        {
            if (!filters.ContainsKey(requiredType))
            {
                reasons.Add($"missing-filter:{requiredType}");
            }
        }

        return reasons;
    }

    private static void ValidateFilter(string filterType, ExchangeMetadataFilter filter,
        List<string> reasons)
    {
        switch (filterType)
        {
            case "PRICE_FILTER":
                RequireDecimal(filter.MinPrice, filterType, "minPrice", reasons);
                RequireDecimal(filter.MaxPrice, filterType, "maxPrice", reasons);
                RequireDecimal(filter.TickSize, filterType, "tickSize", reasons);
                ValidateNonNegative(filter.MinPrice, filterType, "minPrice", reasons);
                ValidateNonNegative(filter.MaxPrice, filterType, "maxPrice", reasons);
                ValidateNonNegative(filter.TickSize, filterType, "tickSize", reasons);
                if (filter.MinPrice is > 0m && filter.MaxPrice is > 0m
                    && filter.MaxPrice < filter.MinPrice)
                {
                    reasons.Add($"malformed-filter:{filterType}:maxPrice-before-minPrice");
                }

                break;
            case "LOT_SIZE":
            case "MARKET_LOT_SIZE":
                RequireDecimal(filter.MinQuantity, filterType, "minQty", reasons);
                RequireDecimal(filter.MaxQuantity, filterType, "maxQty", reasons);
                RequireDecimal(filter.StepSize, filterType, "stepSize", reasons);
                ValidateNonNegative(filter.MinQuantity, filterType, "minQty", reasons);
                ValidateNonNegative(filter.MaxQuantity, filterType, "maxQty", reasons);
                if (filter.StepSize is not null && filter.StepSize <= 0m)
                {
                    reasons.Add($"malformed-filter:{filterType}:stepSize-not-positive");
                }

                if (filter.MinQuantity is >= 0m && filter.MaxQuantity is >= 0m
                    && filter.MaxQuantity < filter.MinQuantity)
                {
                    reasons.Add($"malformed-filter:{filterType}:maxQty-before-minQty");
                }

                break;
            case "MIN_NOTIONAL":
            case "NOTIONAL":
                RequireDecimal(filter.Notional, filterType, "notional", reasons);
                if (filter.Notional is not null && filter.Notional <= 0m)
                {
                    reasons.Add($"malformed-filter:{filterType}:notional-not-positive");
                }

                break;
            case "PERCENT_PRICE":
                RequireDecimal(filter.MultiplierUp, filterType, "multiplierUp", reasons);
                RequireDecimal(filter.MultiplierDown, filterType, "multiplierDown", reasons);
                RequireInteger(filter.MultiplierDecimal, filterType, "multiplierDecimal", reasons);
                if (filter.MultiplierUp is not null && filter.MultiplierUp <= 0m)
                {
                    reasons.Add($"malformed-filter:{filterType}:multiplierUp-not-positive");
                }

                if (filter.MultiplierDown is not null && filter.MultiplierDown <= 0m)
                {
                    reasons.Add($"malformed-filter:{filterType}:multiplierDown-not-positive");
                }

                if (filter.MultiplierDecimal is < 0)
                {
                    reasons.Add($"malformed-filter:{filterType}:multiplierDecimal-negative");
                }

                break;
            case "MAX_NUM_ORDERS":
            case "MAX_NUM_ALGO_ORDERS":
            case "MAX_NUM_ICEBERG_ORDERS":
                RequireInteger(filter.Limit, filterType, "limit", reasons);
                if (filter.Limit is not null && filter.Limit <= 0)
                {
                    reasons.Add($"malformed-filter:{filterType}:limit-not-positive");
                }

                break;
        }
    }

    private static void RequireDecimal(decimal? value, string filterType, string field,
        List<string> reasons)
    {
        if (value is null)
        {
            reasons.Add($"malformed-filter:{filterType}:{field}-missing");
        }
    }

    private static void RequireInteger(int? value, string filterType, string field,
        List<string> reasons)
    {
        if (value is null)
        {
            reasons.Add($"malformed-filter:{filterType}:{field}-missing");
        }
    }

    private static void ValidateNonNegative(decimal? value, string filterType, string field,
        List<string> reasons)
    {
        if (value is < 0m)
        {
            reasons.Add($"malformed-filter:{filterType}:{field}-negative");
        }
    }

    private static string[] OrderReasons(IEnumerable<string> reasons)
    {
        return reasons.Distinct(StringComparer.Ordinal)
            .OrderBy(ReasonRank)
            .ThenBy(FilterRank)
            .ThenBy(static reason => reason, StringComparer.Ordinal)
            .ToArray();
    }

    private static int FilterRank(string reason)
    {
        foreach (string filterType in RequiredFilterTypes)
        {
            if (reason.Contains(filterType, StringComparison.Ordinal))
            {
                return Array.IndexOf(RequiredFilterTypes, filterType);
            }
        }

        return RequiredFilterTypes.Length;
    }

    private static int ReasonRank(string reason)
    {
        if (reason.StartsWith("contract-type-", StringComparison.Ordinal))
        {
            return 10;
        }

        if (reason.StartsWith("status-", StringComparison.Ordinal))
        {
            return 20;
        }

        if (reason.StartsWith("quote-asset-", StringComparison.Ordinal))
        {
            return 30;
        }

        if (reason.StartsWith("margin-asset-", StringComparison.Ordinal))
        {
            return 40;
        }

        if (reason == "duplicate-symbol")
        {
            return 45;
        }

        if (reason.StartsWith("duplicate-filter:", StringComparison.Ordinal))
        {
            return 50;
        }

        if (reason.StartsWith("conflicting-duplicate-filter:", StringComparison.Ordinal))
        {
            return 60;
        }

        if (reason.StartsWith("malformed-filter:", StringComparison.Ordinal))
        {
            return 70;
        }

        if (reason.StartsWith("missing-filter:", StringComparison.Ordinal))
        {
            return 80;
        }

        return 90;
    }

    private static string SortKey(ExchangeEligibilityMetadata metadata)
    {
        return CoreJsonSerializer.Serialize(metadata);
    }
}
