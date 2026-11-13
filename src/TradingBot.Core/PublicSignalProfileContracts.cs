using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>The public source allowed for native signal-profile bars.</summary>
public enum PublicSignalDataSource
{
    BinanceUsdMNativeKlines
}

/// <summary>The price dimension used by the public profile's price-valued features.</summary>
public enum PublicSignalValueUnit
{
    Price
}

/// <summary>The unit of multiplier parameters in the public profile.</summary>
public enum PublicSignalMultiplierUnit
{
    Dimensionless
}

/// <summary>Configuration-level state; Ready is not runtime or statistical readiness.</summary>
public enum PublicSignalProfileState
{
    UnknownConfiguration,
    Ready
}

/// <summary>
/// Caller-supplied source, history, warmup, and freshness requirements for one
/// native public timeframe. Missing values remain unknown; no clock or data is read.
/// </summary>
public sealed record PublicSignalTimeframeConfiguration
{
    public string? Timeframe { get; }

    public PublicSignalDataSource? Source { get; }

    public string? SourceVersion { get; }

    public DateTimeOffset? HistoryStartUtc { get; }

    public string? HistoryVersion { get; }

    public int? SeedHistoryBars { get; }

    public string? SeedVersion { get; }

    public int? WarmupBars { get; }

    public string? WarmupVersion { get; }

    public string? FreshnessVersion { get; }

    public TimeSpan? MaximumMarketDataAge { get; }

    public TimeSpan? MaximumAvailabilityAge { get; }

    [JsonConstructor]
    public PublicSignalTimeframeConfiguration(string? timeframe = null,
        PublicSignalDataSource? source = null, string? sourceVersion = null,
        DateTimeOffset? historyStartUtc = null, string? historyVersion = null,
        int? seedHistoryBars = null, string? seedVersion = null, int? warmupBars = null,
        string? warmupVersion = null, string? freshnessVersion = null,
        TimeSpan? maximumMarketDataAge = null, TimeSpan? maximumAvailabilityAge = null)
    {
        ValidateOptionalText(sourceVersion, nameof(sourceVersion));
        ValidateOptionalText(historyVersion, nameof(historyVersion));
        ValidateOptionalText(seedVersion, nameof(seedVersion));
        ValidateOptionalText(warmupVersion, nameof(warmupVersion));
        ValidateOptionalText(freshnessVersion, nameof(freshnessVersion));
        if (timeframe is not null && timeframe is not ("15m" or "1h"))
        {
            throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe,
                "Only native 15m and 1h public profiles are supported.");
        }

        if (source is { } sourceValue)
        {
            ContractGuard.RequireDefined(sourceValue, nameof(source));
        }

        ValidatePositive(seedHistoryBars, nameof(seedHistoryBars));
        ValidatePositive(warmupBars, nameof(warmupBars));
        ValidatePositive(maximumMarketDataAge, nameof(maximumMarketDataAge));
        ValidatePositive(maximumAvailabilityAge, nameof(maximumAvailabilityAge));
        if (historyStartUtc is { Offset: not { Ticks: 0 } })
        {
            throw new ArgumentException("History start must be UTC.", nameof(historyStartUtc));
        }

        Timeframe = timeframe;
        Source = source;
        SourceVersion = sourceVersion;
        HistoryStartUtc = historyStartUtc;
        HistoryVersion = historyVersion;
        SeedHistoryBars = seedHistoryBars;
        SeedVersion = seedVersion;
        WarmupBars = warmupBars;
        WarmupVersion = warmupVersion;
        FreshnessVersion = freshnessVersion;
        MaximumMarketDataAge = maximumMarketDataAge;
        MaximumAvailabilityAge = maximumAvailabilityAge;
    }

    private static void ValidateOptionalText(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A declared version cannot be blank.", parameterName);
        }
    }

    private static void ValidatePositive(int? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A declared bar count must be positive.");
        }
    }

    private static void ValidatePositive(TimeSpan? value, string parameterName)
    {
        if (value is { } age && age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A declared age limit must be positive.");
        }
    }
}

/// <summary>
/// Caller-supplied price-context/setup formula versions, periods, methods, and
/// dimensions. Numeric strategy parameters are nullable and have no defaults.
/// </summary>
public sealed record PublicSignalIndicatorConfiguration
{
    public string? PriceContextVersion { get; }

    public string? FeatureQualityVersion { get; }

    public string? SetupFamilyVersion { get; }

    public int? PivotLeftBars { get; }

    public int? PivotRightBars { get; }

    public int? AtrPeriodBars { get; }

    public PriceContextAtrMode? AtrMode { get; }

    public PublicSignalValueUnit? AtrValueUnit { get; }

    public PriceContextChannelCenterKind? ChannelCenterKind { get; }

    public int? ChannelCenterPeriodBars { get; }

    public PriceContextMovingAverageKind? Hlc3SmoothingKind { get; }

    public PublicSignalValueUnit? ChannelCenterValueUnit { get; }

    public PriceContextChannelWidthKind? ChannelWidthKind { get; }

    public int? ChannelWidthPeriodBars { get; }

    public decimal? ChannelWidthMultiplier { get; }

    public PublicSignalValueUnit? ChannelWidthValueUnit { get; }

    public PublicSignalMultiplierUnit? ChannelWidthMultiplierUnit { get; }

    public decimal? LevelBufferMultiplier { get; }

    public PublicSignalMultiplierUnit? LevelBufferMultiplierUnit { get; }

    public int? HtfFastPeriodBars { get; }

    public int? HtfSlowPeriodBars { get; }

    public PriceContextMovingAverageKind? HtfMovingAverageKind { get; }

    public int? HtfConfirmationBars { get; }

    public PublicSignalValueUnit? HtfMovingAverageValueUnit { get; }

    public decimal? BoundaryMinimumChannelWidth { get; }

    public PublicSignalValueUnit? BoundaryMinimumChannelWidthUnit { get; }

    [JsonConstructor]
    public PublicSignalIndicatorConfiguration(string? priceContextVersion = null,
        string? featureQualityVersion = null, string? setupFamilyVersion = null,
        int? pivotLeftBars = null, int? pivotRightBars = null, int? atrPeriodBars = null,
        PriceContextAtrMode? atrMode = null, PublicSignalValueUnit? atrValueUnit = null,
        PriceContextChannelCenterKind? channelCenterKind = null,
        int? channelCenterPeriodBars = null,
        PriceContextMovingAverageKind? hlc3SmoothingKind = null,
        PublicSignalValueUnit? channelCenterValueUnit = null,
        PriceContextChannelWidthKind? channelWidthKind = null,
        int? channelWidthPeriodBars = null, decimal? channelWidthMultiplier = null,
        PublicSignalValueUnit? channelWidthValueUnit = null,
        PublicSignalMultiplierUnit? channelWidthMultiplierUnit = null,
        decimal? levelBufferMultiplier = null,
        PublicSignalMultiplierUnit? levelBufferMultiplierUnit = null,
        int? htfFastPeriodBars = null, int? htfSlowPeriodBars = null,
        PriceContextMovingAverageKind? htfMovingAverageKind = null,
        int? htfConfirmationBars = null,
        PublicSignalValueUnit? htfMovingAverageValueUnit = null,
        decimal? boundaryMinimumChannelWidth = null,
        PublicSignalValueUnit? boundaryMinimumChannelWidthUnit = null)
    {
        ValidateOptionalText(priceContextVersion, nameof(priceContextVersion));
        ValidateOptionalText(featureQualityVersion, nameof(featureQualityVersion));
        ValidateOptionalText(setupFamilyVersion, nameof(setupFamilyVersion));
        ValidatePositive(pivotLeftBars, nameof(pivotLeftBars));
        ValidatePositive(pivotRightBars, nameof(pivotRightBars));
        ValidatePositive(atrPeriodBars, nameof(atrPeriodBars));
        ValidateDefined(atrMode, nameof(atrMode));
        ValidateDefined(atrValueUnit, nameof(atrValueUnit));
        ValidateDefined(channelCenterKind, nameof(channelCenterKind));
        ValidatePositive(channelCenterPeriodBars, nameof(channelCenterPeriodBars));
        ValidateDefined(hlc3SmoothingKind, nameof(hlc3SmoothingKind));
        ValidateDefined(channelCenterValueUnit, nameof(channelCenterValueUnit));
        ValidateDefined(channelWidthKind, nameof(channelWidthKind));
        ValidatePositive(channelWidthPeriodBars, nameof(channelWidthPeriodBars));
        ValidatePositive(channelWidthMultiplier, nameof(channelWidthMultiplier));
        ValidateDefined(channelWidthValueUnit, nameof(channelWidthValueUnit));
        ValidateDefined(channelWidthMultiplierUnit, nameof(channelWidthMultiplierUnit));
        ValidatePositive(levelBufferMultiplier, nameof(levelBufferMultiplier));
        ValidateDefined(levelBufferMultiplierUnit, nameof(levelBufferMultiplierUnit));
        ValidatePositive(htfFastPeriodBars, nameof(htfFastPeriodBars));
        ValidatePositive(htfSlowPeriodBars, nameof(htfSlowPeriodBars));
        ValidateDefined(htfMovingAverageKind, nameof(htfMovingAverageKind));
        ValidatePositive(htfConfirmationBars, nameof(htfConfirmationBars));
        ValidateDefined(htfMovingAverageValueUnit, nameof(htfMovingAverageValueUnit));
        ValidatePositive(boundaryMinimumChannelWidth, nameof(boundaryMinimumChannelWidth));
        ValidateDefined(boundaryMinimumChannelWidthUnit, nameof(boundaryMinimumChannelWidthUnit));

        PriceContextVersion = priceContextVersion;
        FeatureQualityVersion = featureQualityVersion;
        SetupFamilyVersion = setupFamilyVersion;
        PivotLeftBars = pivotLeftBars;
        PivotRightBars = pivotRightBars;
        AtrPeriodBars = atrPeriodBars;
        AtrMode = atrMode;
        AtrValueUnit = atrValueUnit;
        ChannelCenterKind = channelCenterKind;
        ChannelCenterPeriodBars = channelCenterPeriodBars;
        Hlc3SmoothingKind = hlc3SmoothingKind;
        ChannelCenterValueUnit = channelCenterValueUnit;
        ChannelWidthKind = channelWidthKind;
        ChannelWidthPeriodBars = channelWidthPeriodBars;
        ChannelWidthMultiplier = channelWidthMultiplier;
        ChannelWidthValueUnit = channelWidthValueUnit;
        ChannelWidthMultiplierUnit = channelWidthMultiplierUnit;
        LevelBufferMultiplier = levelBufferMultiplier;
        LevelBufferMultiplierUnit = levelBufferMultiplierUnit;
        HtfFastPeriodBars = htfFastPeriodBars;
        HtfSlowPeriodBars = htfSlowPeriodBars;
        HtfMovingAverageKind = htfMovingAverageKind;
        HtfConfirmationBars = htfConfirmationBars;
        HtfMovingAverageValueUnit = htfMovingAverageValueUnit;
        BoundaryMinimumChannelWidth = boundaryMinimumChannelWidth;
        BoundaryMinimumChannelWidthUnit = boundaryMinimumChannelWidthUnit;
    }

    private static void ValidateOptionalText(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A declared version cannot be blank.", parameterName);
        }
    }

    private static void ValidatePositive(int? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A declared bar count must be positive.");
        }
    }

    private static void ValidatePositive(decimal? value, string parameterName)
    {
        if (value is <= 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A declared multiplier must be positive.");
        }
    }

    private static void ValidateDefined<TEnum>(TEnum? value, string parameterName)
        where TEnum : struct, Enum
    {
        if (value is { } enumValue)
        {
            ContractGuard.RequireDefined(enumValue, parameterName);
        }
    }
}

/// <summary>Explicit point-in-time universe definition and symbol-count bounds.</summary>
public sealed record PublicSignalUniverseConfiguration
{
    public string? SelectionVersion { get; }

    public int? MinimumSymbols { get; }

    public int? MaximumSymbols { get; }

    [JsonConstructor]
    public PublicSignalUniverseConfiguration(string? selectionVersion = null,
        int? minimumSymbols = null, int? maximumSymbols = null)
    {
        if (selectionVersion is not null && string.IsNullOrWhiteSpace(selectionVersion))
        {
            throw new ArgumentException("A declared version cannot be blank.", nameof(selectionVersion));
        }

        ValidatePositive(minimumSymbols, nameof(minimumSymbols));
        ValidatePositive(maximumSymbols, nameof(maximumSymbols));
        if (minimumSymbols is { } minimum && maximumSymbols is { } maximum && minimum > maximum)
        {
            throw new ArgumentException("The minimum universe size cannot exceed the maximum.",
                nameof(minimumSymbols));
        }

        SelectionVersion = selectionVersion;
        MinimumSymbols = minimumSymbols;
        MaximumSymbols = maximumSymbols;
    }

    private static void ValidatePositive(int? value, string parameterName)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A declared universe bound must be positive.");
        }
    }
}

/// <summary>
/// Versioned, public-only profile configuration. It validates declarations but
/// does not evaluate signals, read secrets, access data, or authorize runtime use.
/// </summary>
public sealed record PublicSignalProfileConfiguration
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string? ProfileVersion { get; }

    public PublicSignalTimeframeConfiguration? Setup { get; }

    public PublicSignalTimeframeConfiguration? Context { get; }

    public PublicSignalIndicatorConfiguration? Indicators { get; }

    public PublicSignalUniverseConfiguration? Universe { get; }
    [JsonIgnore]
    public PublicSignalProfileState State { get; }

    /// <summary>A reusable explicit unknown configuration for an absent profile.</summary>
    public static PublicSignalProfileConfiguration Missing { get; } =
        new(CurrentFormatVersion);

    [JsonIgnore]
    public bool IsReady => State == PublicSignalProfileState.Ready;

    [JsonIgnore]
    public ImmutableValueList<string> UnknownReasons { get; }

    [JsonConstructor]
    public PublicSignalProfileConfiguration(int formatVersion, string? profileVersion = null,
        PublicSignalTimeframeConfiguration? setup = null,
        PublicSignalTimeframeConfiguration? context = null,
        PublicSignalIndicatorConfiguration? indicators = null,
        PublicSignalUniverseConfiguration? universe = null)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The public signal profile format version is unsupported.");
        }

        if (profileVersion is not null && string.IsNullOrWhiteSpace(profileVersion))
        {
            throw new ArgumentException("A declared profile version cannot be blank.", nameof(profileVersion));
        }

        ValidateRoleTimeframe(setup, "15m", nameof(setup));
        ValidateRoleTimeframe(context, "1h", nameof(context));
        FormatVersion = formatVersion;
        ProfileVersion = profileVersion;
        Setup = setup;
        Context = context;
        Indicators = indicators;
        Universe = universe;
        UnknownReasons = new ImmutableValueList<string>(BuildUnknownReasons());
        State = UnknownReasons.Count == 0
            ? PublicSignalProfileState.Ready
            : PublicSignalProfileState.UnknownConfiguration;
    }

    private List<string> BuildUnknownReasons()
    {
        List<string> reasons = [];
        AddMissing(reasons, "profile-version", ProfileVersion is not null);
        AddTimeframeReasons(reasons, "setup", "15m", Setup);
        AddTimeframeReasons(reasons, "context", "1h", Context);
        AddIndicatorReasons(reasons, Indicators);
        AddUniverseReasons(reasons, Universe);
        return reasons;
    }

    private static void AddTimeframeReasons(List<string> reasons, string prefix,
        string expectedTimeframe, PublicSignalTimeframeConfiguration? configuration)
    {
        AddMissing(reasons, $"{prefix}-timeframe",
            StringComparer.Ordinal.Equals(configuration?.Timeframe, expectedTimeframe));
        AddMissing(reasons, $"{prefix}-source", configuration?.Source is not null);
        AddMissing(reasons, $"{prefix}-source-version", configuration?.SourceVersion is not null);
        AddMissing(reasons, $"{prefix}-history-start", configuration?.HistoryStartUtc is not null);
        AddMissing(reasons, $"{prefix}-history-version", configuration?.HistoryVersion is not null);
        AddMissing(reasons, $"{prefix}-seed-history-bars", configuration?.SeedHistoryBars is not null);
        AddMissing(reasons, $"{prefix}-seed-version", configuration?.SeedVersion is not null);
        AddMissing(reasons, $"{prefix}-warmup-bars", configuration?.WarmupBars is not null);
        AddMissing(reasons, $"{prefix}-warmup-version", configuration?.WarmupVersion is not null);
        AddMissing(reasons, $"{prefix}-freshness-version", configuration?.FreshnessVersion is not null);
        AddMissing(reasons, $"{prefix}-maximum-market-data-age",
            configuration?.MaximumMarketDataAge is not null);
        AddMissing(reasons, $"{prefix}-maximum-availability-age",
            configuration?.MaximumAvailabilityAge is not null);
    }

    private static void AddIndicatorReasons(List<string> reasons,
        PublicSignalIndicatorConfiguration? configuration)
    {
        if (configuration is null)
        {
            reasons.Add("indicator-configuration-unknown");
            return;
        }

        AddMissing(reasons, "price-context-version", configuration.PriceContextVersion is not null);
        AddMissing(reasons, "feature-quality-version", configuration.FeatureQualityVersion is not null);
        AddMissing(reasons, "setup-family-version", configuration.SetupFamilyVersion is not null);
        AddMissing(reasons, "pivot-left-bars", configuration.PivotLeftBars is not null);
        AddMissing(reasons, "pivot-right-bars", configuration.PivotRightBars is not null);
        AddMissing(reasons, "atr-period-bars", configuration.AtrPeriodBars is not null);
        AddMissing(reasons, "atr-mode", configuration.AtrMode is not null);
        AddMissing(reasons, "atr-value-unit", configuration.AtrValueUnit is not null);
        AddMissing(reasons, "channel-center-kind", configuration.ChannelCenterKind is not null);
        AddMissing(reasons, "channel-center-period-bars", configuration.ChannelCenterPeriodBars is not null);
        AddMissing(reasons, "hlc3-smoothing-kind", configuration.Hlc3SmoothingKind is not null);
        AddMissing(reasons, "channel-center-value-unit", configuration.ChannelCenterValueUnit is not null);
        AddMissing(reasons, "channel-width-kind", configuration.ChannelWidthKind is not null);
        AddMissing(reasons, "channel-width-period-bars", configuration.ChannelWidthPeriodBars is not null);
        AddMissing(reasons, "channel-width-multiplier", configuration.ChannelWidthMultiplier is not null);
        AddMissing(reasons, "channel-width-value-unit", configuration.ChannelWidthValueUnit is not null);
        AddMissing(reasons, "channel-width-multiplier-unit",
            configuration.ChannelWidthMultiplierUnit is not null);
        AddMissing(reasons, "level-buffer-multiplier", configuration.LevelBufferMultiplier is not null);
        AddMissing(reasons, "level-buffer-multiplier-unit",
            configuration.LevelBufferMultiplierUnit is not null);
        AddMissing(reasons, "htf-fast-period-bars", configuration.HtfFastPeriodBars is not null);
        AddMissing(reasons, "htf-slow-period-bars", configuration.HtfSlowPeriodBars is not null);
        AddMissing(reasons, "htf-moving-average-kind", configuration.HtfMovingAverageKind is not null);
        AddMissing(reasons, "htf-confirmation-bars", configuration.HtfConfirmationBars is not null);
        AddMissing(reasons, "htf-moving-average-value-unit",
            configuration.HtfMovingAverageValueUnit is not null);
        AddMissing(reasons, "boundary-minimum-channel-width",
            configuration.BoundaryMinimumChannelWidth is not null);
        AddMissing(reasons, "boundary-minimum-channel-width-unit",
            configuration.BoundaryMinimumChannelWidthUnit is not null);
    }

    private static void AddUniverseReasons(List<string> reasons,
        PublicSignalUniverseConfiguration? configuration)
    {
        if (configuration is null)
        {
            reasons.Add("universe-configuration-unknown");
            return;
        }

        AddMissing(reasons, "universe-selection-version", configuration.SelectionVersion is not null);
        AddMissing(reasons, "universe-minimum-symbols", configuration.MinimumSymbols is not null);
        AddMissing(reasons, "universe-maximum-symbols", configuration.MaximumSymbols is not null);
    }

    private static void AddMissing(List<string> reasons, string field, bool known)
    {
        if (!known)
        {
            reasons.Add(field + "-unknown");
        }
    }

    private static void ValidateRoleTimeframe(PublicSignalTimeframeConfiguration? configuration,
        string expectedTimeframe, string parameterName)
    {
        if (configuration?.Timeframe is { } timeframe
            && !StringComparer.Ordinal.Equals(timeframe, expectedTimeframe))
        {
            throw new ArgumentException(
                $"The {parameterName} profile must use native {expectedTimeframe} bars.",
                parameterName);
        }
    }
}
