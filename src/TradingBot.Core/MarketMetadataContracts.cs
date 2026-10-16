using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Preserves one unmodeled metadata field as deterministic raw JSON.</summary>
public sealed record MetadataField
{
    public string Name { get; }

    public string RawValue { get; }

    [JsonConstructor]
    public MetadataField(string name, string rawValue)
    {
        ContractGuard.RequireText(name, nameof(name));
        ContractGuard.RequireText(rawValue, nameof(rawValue));
        Name = name;
        RawValue = rawValue;
    }
}

/// <summary>Preserves one unmodeled field within an exchange symbol filter.</summary>
public sealed record MetadataFilterField
{
    public string Name { get; }

    public string RawValue { get; }

    [JsonConstructor]
    public MetadataFilterField(string name, string rawValue)
    {
        ContractGuard.RequireText(name, nameof(name));
        ContractGuard.RequireText(rawValue, nameof(rawValue));
        Name = name;
        RawValue = rawValue;
    }
}

/// <summary>
/// Exact-decimal exchange filter metadata. It describes exchange rules only; it
/// does not quantize orders or authorize execution.
/// </summary>
public sealed record ExchangeMetadataFilter
{
    public string FilterType { get; }

    public decimal? MinPrice { get; }

    public decimal? MaxPrice { get; }

    public decimal? TickSize { get; }

    public decimal? MinQuantity { get; }

    public decimal? MaxQuantity { get; }

    public decimal? StepSize { get; }

    public decimal? Notional { get; }

    public decimal? MultiplierUp { get; }

    public decimal? MultiplierDown { get; }

    public int? MultiplierDecimal { get; }

    public int? Limit { get; }

    public ImmutableValueList<MetadataFilterField> UnknownFields { get; }

    public ExchangeMetadataFilter(string filterType, decimal? minPrice = null,
        decimal? maxPrice = null, decimal? tickSize = null, decimal? minQuantity = null,
        decimal? maxQuantity = null, decimal? stepSize = null, decimal? notional = null,
        decimal? multiplierUp = null, decimal? multiplierDown = null,
        int? multiplierDecimal = null, int? limit = null,
        IEnumerable<MetadataFilterField>? unknownFields = null)
        : this(filterType, minPrice, maxPrice, tickSize, minQuantity, maxQuantity, stepSize,
            notional, multiplierUp, multiplierDown, multiplierDecimal, limit,
            SnapshotFields(unknownFields))
    {
    }

    [JsonConstructor]
    public ExchangeMetadataFilter(string filterType, decimal? minPrice, decimal? maxPrice,
        decimal? tickSize, decimal? minQuantity, decimal? maxQuantity, decimal? stepSize,
        decimal? notional, decimal? multiplierUp, decimal? multiplierDown,
        int? multiplierDecimal, int? limit, ImmutableValueList<MetadataFilterField> unknownFields)
    {
        ContractGuard.RequireText(filterType, nameof(filterType));
        ArgumentNullException.ThrowIfNull(unknownFields);
        FilterType = filterType;
        MinPrice = minPrice;
        MaxPrice = maxPrice;
        TickSize = tickSize;
        MinQuantity = minQuantity;
        MaxQuantity = maxQuantity;
        StepSize = stepSize;
        Notional = notional;
        MultiplierUp = multiplierUp;
        MultiplierDown = multiplierDown;
        MultiplierDecimal = multiplierDecimal;
        Limit = limit;
        UnknownFields = SortFields(unknownFields);
    }

    private static ImmutableValueList<MetadataFilterField> SnapshotFields(
        IEnumerable<MetadataFilterField>? fields)
    {
        return fields is null
            ? new ImmutableValueList<MetadataFilterField>([])
            : new ImmutableValueList<MetadataFilterField>(fields);
    }

    private static ImmutableValueList<MetadataFilterField> SortFields(
        IEnumerable<MetadataFilterField> fields)
    {
        MetadataFilterField[] snapshot = fields.ToArray();
        if (snapshot.Any(static field => field is null))
        {
            throw new ArgumentException("Metadata filter fields cannot contain null values.",
                nameof(fields));
        }

        if (snapshot.Select(static field => field.Name).Distinct(StringComparer.Ordinal).Count()
            != snapshot.Length)
        {
            throw new ArgumentException("Metadata filter field names must be unique.", nameof(fields));
        }

        return new ImmutableValueList<MetadataFilterField>(snapshot.OrderBy(
            static field => field.Name, StringComparer.Ordinal));
    }
}

/// <summary>
/// Immutable exchange metadata used by the metadata-only universe gate. All
/// timestamps are captured from the local adapter clock; exchangeInfo serverTime
/// is retained by the wire DTO but is not used as provenance.
/// </summary>
public sealed record ExchangeEligibilityMetadata
{
    public InstrumentId InstrumentId { get; }

    public string Pair { get; }

    public string ContractType { get; }

    public string Status { get; }

    public string BaseAsset { get; }

    public string QuoteAsset { get; }

    public string MarginAsset { get; }

    public UtcTimestamp OnboardTime { get; }

    public UtcTimestamp DeliveryTime { get; }

    public int PricePrecision { get; }

    public int QuantityPrecision { get; }

    public int BaseAssetPrecision { get; }

    public int QuotePrecision { get; }

    public decimal MaintMarginPercent { get; }

    public decimal RequiredMarginPercent { get; }

    public ImmutableValueList<ExchangeMetadataFilter> Filters { get; }

    public ImmutableValueList<MetadataField> UnknownFields { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public string? UnderlyingType { get; }

    public ImmutableValueList<string> UnderlyingSubType { get; }

    public int? SettlePlan { get; }

    public decimal? TriggerProtect { get; }

    public decimal? LiquidationFee { get; }

    public decimal? MarketTakeBound { get; }

    public ExchangeEligibilityMetadata(InstrumentId instrumentId, string pair, string contractType,
        string status, string baseAsset, string quoteAsset, string marginAsset,
        UtcTimestamp onboardTime, UtcTimestamp deliveryTime, int pricePrecision,
        int quantityPrecision, int baseAssetPrecision, int quotePrecision,
        decimal maintMarginPercent, decimal requiredMarginPercent,
        IEnumerable<ExchangeMetadataFilter>? filters, IEnumerable<MetadataField>? unknownFields,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime, string? underlyingType = null,
        IEnumerable<string>? underlyingSubType = null, int? settlePlan = null,
        decimal? triggerProtect = null, decimal? liquidationFee = null,
        decimal? marketTakeBound = null)
        : this(instrumentId, pair, contractType, status, baseAsset, quoteAsset, marginAsset,
            onboardTime, deliveryTime, pricePrecision, quantityPrecision, baseAssetPrecision,
            quotePrecision, maintMarginPercent, requiredMarginPercent,
            SnapshotFilters(filters), SnapshotFields(unknownFields), receiveTime, availabilityTime,
            underlyingType, SnapshotStrings(underlyingSubType), settlePlan, triggerProtect,
            liquidationFee, marketTakeBound)
    {
    }

    [JsonConstructor]
    public ExchangeEligibilityMetadata(InstrumentId instrumentId, string pair, string contractType,
        string status, string baseAsset, string quoteAsset, string marginAsset,
        UtcTimestamp onboardTime, UtcTimestamp deliveryTime, int pricePrecision,
        int quantityPrecision, int baseAssetPrecision, int quotePrecision,
        decimal maintMarginPercent, decimal requiredMarginPercent,
        ImmutableValueList<ExchangeMetadataFilter> filters,
        ImmutableValueList<MetadataField> unknownFields, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, string? underlyingType,
        ImmutableValueList<string> underlyingSubType, int? settlePlan,
        decimal? triggerProtect, decimal? liquidationFee, decimal? marketTakeBound)
    {
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(pair, nameof(pair));
        ContractGuard.RequireText(contractType, nameof(contractType));
        ContractGuard.RequireText(status, nameof(status));
        ContractGuard.RequireText(baseAsset, nameof(baseAsset));
        ContractGuard.RequireText(quoteAsset, nameof(quoteAsset));
        ContractGuard.RequireText(marginAsset, nameof(marginAsset));
        ArgumentNullException.ThrowIfNull(onboardTime);
        ArgumentNullException.ThrowIfNull(deliveryTime);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(unknownFields);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ArgumentNullException.ThrowIfNull(underlyingSubType);
        if (pricePrecision < 0 || quantityPrecision < 0 || baseAssetPrecision < 0
            || quotePrecision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pricePrecision));
        }

        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Metadata availability cannot precede receive time.",
                nameof(availabilityTime));
        }

        InstrumentId = instrumentId;
        Pair = pair;
        ContractType = contractType;
        Status = status;
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
        MarginAsset = marginAsset;
        OnboardTime = onboardTime;
        DeliveryTime = deliveryTime;
        PricePrecision = pricePrecision;
        QuantityPrecision = quantityPrecision;
        BaseAssetPrecision = baseAssetPrecision;
        QuotePrecision = quotePrecision;
        MaintMarginPercent = maintMarginPercent;
        RequiredMarginPercent = requiredMarginPercent;
        Filters = new ImmutableValueList<ExchangeMetadataFilter>(filters);
        UnknownFields = SortFields(unknownFields);
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        UnderlyingType = string.IsNullOrWhiteSpace(underlyingType) ? null : underlyingType;
        UnderlyingSubType = new ImmutableValueList<string>(underlyingSubType);
        SettlePlan = settlePlan;
        TriggerProtect = triggerProtect;
        LiquidationFee = liquidationFee;
        MarketTakeBound = marketTakeBound;
    }

    private static ImmutableValueList<ExchangeMetadataFilter> SnapshotFilters(
        IEnumerable<ExchangeMetadataFilter>? filters)
    {
        return filters is null
            ? new ImmutableValueList<ExchangeMetadataFilter>([])
            : new ImmutableValueList<ExchangeMetadataFilter>(filters);
    }

    private static ImmutableValueList<MetadataField> SnapshotFields(IEnumerable<MetadataField>? fields)
    {
        return fields is null
            ? new ImmutableValueList<MetadataField>([])
            : new ImmutableValueList<MetadataField>(fields);
    }

    private static ImmutableValueList<string> SnapshotStrings(IEnumerable<string>? values)
    {
        return values is null
            ? new ImmutableValueList<string>([])
            : new ImmutableValueList<string>(values);
    }

    private static ImmutableValueList<MetadataField> SortFields(IEnumerable<MetadataField> fields)
    {
        MetadataField[] snapshot = fields.ToArray();
        if (snapshot.Any(static field => field is null))
        {
            throw new ArgumentException("Metadata fields cannot contain null values.", nameof(fields));
        }

        if (snapshot.Select(static field => field.Name).Distinct(StringComparer.Ordinal).Count()
            != snapshot.Length)
        {
            throw new ArgumentException("Metadata field names must be unique.", nameof(fields));
        }

        return new ImmutableValueList<MetadataField>(snapshot.OrderBy(
            static field => field.Name, StringComparer.Ordinal));
    }
}
