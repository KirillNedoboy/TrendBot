using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Explicit configuration for the public-only signal consumer.</summary>
public sealed record SignalOnlyConfiguration
{
    public const int CurrentFormatVersion = 1;
    public const int PerSymbolWidthFormatVersion = 2;
    public const int MultiLaneFormatVersion = 3;
    public const int MultiTimeframeFormatVersion = 4;
    public const string SignalOnlyMode = "signal-only-public";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int FormatVersion { get; }

    public string Mode { get; }

    public ImmutableValueList<string> Symbols { get; }

    public string InputLedgerPath { get; }

    public string SignalJournalPath { get; }

    public PublicSignalProfileConfiguration Profile { get; }

    public SignalScenarioPolicySet ScenarioPolicies { get; }

    internal IReadOnlyDictionary<string, decimal>? BoundaryMinimumChannelWidths { get; }

    public SignalMultiLanePolicy? MultiLanePolicy { get; }

    internal IReadOnlyList<SignalOnlyTimeframeProfile> AdditionalProfiles { get; }

    private readonly Dictionary<string, PublicSignalProfileConfiguration>? _profilesBySymbol;

    public SignalOnlyConfiguration(IEnumerable<string> symbols, string inputLedgerPath,
        string signalJournalPath, PublicSignalProfileConfiguration profile,
        SignalScenarioPolicySet scenarioPolicies)
        : this(symbols, inputLedgerPath, signalJournalPath, profile, scenarioPolicies, null)
    {
    }

    public SignalOnlyConfiguration(IEnumerable<string> symbols, string inputLedgerPath,
        string signalJournalPath, PublicSignalProfileConfiguration profile,
        SignalScenarioPolicySet scenarioPolicies,
        IReadOnlyDictionary<string, decimal>? boundaryMinimumChannelWidths,
        SignalMultiLanePolicy? multiLanePolicy = null)
        : this(symbols, inputLedgerPath, signalJournalPath, profile, scenarioPolicies,
            boundaryMinimumChannelWidths, multiLanePolicy, null)
    {
    }

    private SignalOnlyConfiguration(IEnumerable<string> symbols, string inputLedgerPath,
        string signalJournalPath, PublicSignalProfileConfiguration profile,
        SignalScenarioPolicySet scenarioPolicies,
        IReadOnlyDictionary<string, decimal>? boundaryMinimumChannelWidths,
        SignalMultiLanePolicy? multiLanePolicy,
        IReadOnlyList<SignalOnlyTimeframeProfile>? additionalProfiles)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(scenarioPolicies);
        if (!profile.IsReady)
        {
            throw new ArgumentException(
                "A complete explicit public signal profile is required.", nameof(profile));
        }

        if (profile.Setup?.Source != PublicSignalDataSource.BinanceUsdMNativeKlines
            || profile.Context?.Source != PublicSignalDataSource.BinanceUsdMNativeKlines)
        {
            throw new ArgumentException(
                "Both profile timeframes must use Binance USD-M public native klines.",
                nameof(profile));
        }

        if (!scenarioPolicies.IsReady)
        {
            throw new ArgumentException(
                "A complete explicit scenario policy set is required.",
                nameof(scenarioPolicies));
        }

        string[] canonicalSymbols = symbols.Select(CanonicalizeSymbol).ToArray();
        if (canonicalSymbols.Length == 0
            || canonicalSymbols.Distinct(StringComparer.Ordinal).Count() != canonicalSymbols.Length)
        {
            throw new ArgumentException(
                "At least one unique public symbol is required.", nameof(symbols));
        }

        if ((profile.Universe?.MinimumSymbols is { } minimum
                && canonicalSymbols.Length < minimum)
            || (profile.Universe?.MaximumSymbols is { } maximum
                && canonicalSymbols.Length > maximum))
        {
            throw new ArgumentException(
                "The symbol list is outside the declared public universe bounds.",
                nameof(symbols));
        }

        string inputPath = NormalizeDatabasePath(inputLedgerPath,
            nameof(inputLedgerPath));
        string journalPath = NormalizeDatabasePath(signalJournalPath,
            nameof(signalJournalPath));
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (pathComparer.Equals(inputPath, journalPath))
        {
            throw new ArgumentException(
                "The intake ledger and signal journal must use separate files.",
                nameof(signalJournalPath));
        }

        if (multiLanePolicy is not null && boundaryMinimumChannelWidths is null)
        {
            throw new ArgumentException("Multi-lane profiles require explicit per-symbol widths.",
                nameof(boundaryMinimumChannelWidths));
        }
        SignalOnlyTimeframeProfile[] additional = additionalProfiles?.ToArray() ?? [];
        if (additional.Length > 0 && multiLanePolicy is null)
        {
            throw new ArgumentException("Multi-timeframe profiles require multi-lane data.",
                nameof(additionalProfiles));
        }
        HashSet<string> setupTimeframes = new(StringComparer.Ordinal) { profile.Setup!.Timeframe! };
        HashSet<string> profileVersions = new(StringComparer.Ordinal) { profile.ProfileVersion! };
        foreach (SignalOnlyTimeframeProfile item in additional)
        {
            if (!item.Profile.IsReady || !item.ScenarioPolicies.IsReady
                || item.Profile.FormatVersion != PublicSignalProfileConfiguration.MultiTimeframeFormatVersion
                || item.Profile.Setup?.Source != PublicSignalDataSource.BinanceUsdMNativeKlines
                || item.Profile.Context?.Source != PublicSignalDataSource.BinanceUsdMNativeKlines
                || !setupTimeframes.Add(item.Profile.Setup.Timeframe!)
                || !profileVersions.Add(item.Profile.ProfileVersion!)
                || canonicalSymbols.Length < item.Profile.Universe!.MinimumSymbols
                || canonicalSymbols.Length > item.Profile.Universe.MaximumSymbols)
            {
                throw new ArgumentException("The additional timeframe profile is invalid.",
                    nameof(additionalProfiles));
            }
        }
        FormatVersion = additional.Length > 0 ? MultiTimeframeFormatVersion
            : multiLanePolicy is not null ? MultiLaneFormatVersion
            : boundaryMinimumChannelWidths is null
                ? CurrentFormatVersion : PerSymbolWidthFormatVersion;
        Mode = SignalOnlyMode;
        Symbols = new ImmutableValueList<string>(canonicalSymbols);
        InputLedgerPath = inputPath;
        SignalJournalPath = journalPath;
        Profile = profile;
        ScenarioPolicies = scenarioPolicies;
        MultiLanePolicy = multiLanePolicy;
        AdditionalProfiles = Array.AsReadOnly(additional);
        if (boundaryMinimumChannelWidths is not null)
        {
            SortedDictionary<string, decimal> widths = new(StringComparer.Ordinal);
            foreach ((string symbol, decimal width) in boundaryMinimumChannelWidths)
            {
                string canonical = CanonicalizeSymbol(symbol);
                if (width <= 0m || !widths.TryAdd(canonical, width))
                {
                    throw new ArgumentException(
                        "Every symbol must have one positive minimum channel width.",
                        nameof(boundaryMinimumChannelWidths));
                }
            }
            if (widths.Count != canonicalSymbols.Length
                || canonicalSymbols.Any(symbol => !widths.ContainsKey(symbol)))
            {
                throw new ArgumentException(
                    "Per-symbol channel widths must match the declared symbols.",
                    nameof(boundaryMinimumChannelWidths));
            }
            BoundaryMinimumChannelWidths = new ReadOnlyDictionary<string, decimal>(widths);
            _profilesBySymbol = widths.ToDictionary(static item => item.Key,
                item => ProfileWithWidth(profile, item.Value), StringComparer.Ordinal);
        }
    }

    public static SignalOnlyConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            RejectDuplicateProperties(document.RootElement);
            WireConfiguration wire = JsonSerializer.Deserialize<WireConfiguration>(json,
                WireOptions) ?? throw new JsonException();
            bool validVersion = wire.FormatVersion switch
            {
                CurrentFormatVersion => wire.BoundaryMinimumChannelWidths is null
                    && wire.MultiLanePolicy is null && wire.AdditionalProfiles is null,
                PerSymbolWidthFormatVersion => wire.BoundaryMinimumChannelWidths is not null
                    && wire.MultiLanePolicy is null && wire.AdditionalProfiles is null,
                MultiLaneFormatVersion => wire.BoundaryMinimumChannelWidths is not null
                    && wire.MultiLanePolicy is { ValueKind: JsonValueKind.Object }
                    && wire.AdditionalProfiles is null,
                MultiTimeframeFormatVersion => wire.BoundaryMinimumChannelWidths is not null
                    && wire.MultiLanePolicy is { ValueKind: JsonValueKind.Object }
                    && wire.AdditionalProfiles is { Length: > 0 },
                _ => false
            };
            if (!validVersion
                || !StringComparer.Ordinal.Equals(wire.Mode, SignalOnlyMode)
                || wire.Profile.ValueKind != JsonValueKind.Object
                || wire.ScenarioPolicies.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }

            PublicSignalProfileConfiguration profile =
                CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(
                    wire.Profile.GetRawText());
            SignalScenarioPolicySet policies =
                CoreJsonSerializer.Deserialize<SignalScenarioPolicySet>(
                    wire.ScenarioPolicies.GetRawText());
            SignalMultiLanePolicy? multiLanePolicy = wire.MultiLanePolicy is { } policyJson
                ? CoreJsonSerializer.Deserialize<SignalMultiLanePolicy>(policyJson.GetRawText())
                : null;
            SignalOnlyTimeframeProfile[]? additional = wire.AdditionalProfiles?.Select(item =>
                new SignalOnlyTimeframeProfile(
                    CoreJsonSerializer.Deserialize<PublicSignalProfileConfiguration>(item.Profile.GetRawText()),
                    CoreJsonSerializer.Deserialize<SignalScenarioPolicySet>(item.ScenarioPolicies.GetRawText())))
                .ToArray();
            return new SignalOnlyConfiguration(wire.Symbols, wire.InputLedgerPath,
                wire.SignalJournalPath, profile, policies, wire.BoundaryMinimumChannelWidths,
                multiLanePolicy, additional);
        }
        catch (JsonException)
        {
            throw new JsonException("The signal-only public configuration is invalid.");
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or NotSupportedException or OverflowException)
        {
            throw new JsonException("The signal-only public configuration is invalid.");
        }
    }

    public static SignalOnlyConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            throw new IOException("The signal-only public configuration could not be read.");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new WireConfiguration(
        FormatVersion, SignalOnlyMode, Symbols.ToArray(), InputLedgerPath,
        SignalJournalPath, ParseElement(CoreJsonSerializer.Serialize(Profile)),
        ParseElement(CoreJsonSerializer.Serialize(ScenarioPolicies)),
        BoundaryMinimumChannelWidths, MultiLanePolicy is null ? null
            : ParseElement(CoreJsonSerializer.Serialize(MultiLanePolicy)),
        AdditionalProfiles.Count == 0 ? null : AdditionalProfiles.Select(item =>
            new WireAdditionalProfile(ParseElement(CoreJsonSerializer.Serialize(item.Profile)),
                ParseElement(CoreJsonSerializer.Serialize(item.ScenarioPolicies)))).ToArray()), WireOptions);

    internal PublicSignalProfileConfiguration ProfileForSymbol(string symbol)
    {
        string canonical = CanonicalizeSymbol(symbol);
        if (!Symbols.Contains(canonical))
        {
            throw new ArgumentException("The symbol is not in this public configuration.",
                nameof(symbol));
        }
        return _profilesBySymbol is null ? Profile : _profilesBySymbol[canonical];
    }

    internal IReadOnlyList<SignalOnlyTimeframeProfile> ProfilesForSymbol(string symbol)
    {
        PublicSignalProfileConfiguration primary = ProfileForSymbol(symbol);
        if (AdditionalProfiles.Count == 0)
        {
            return [new SignalOnlyTimeframeProfile(primary, ScenarioPolicies)];
        }
        decimal width = BoundaryMinimumChannelWidths![CanonicalizeSymbol(symbol)];
        return [new SignalOnlyTimeframeProfile(primary, ScenarioPolicies),
            .. AdditionalProfiles.Select(item => new SignalOnlyTimeframeProfile(
                ProfileWithWidth(item.Profile, width), item.ScenarioPolicies))];
    }

    internal IEnumerable<string> StreamIds() => Symbols.SelectMany(symbol =>
        ProfilesForSymbol(symbol).Select(item =>
            SignalOnlyStreamId.For(symbol, item.Profile.Setup!.Timeframe!)));

    private static PublicSignalProfileConfiguration ProfileWithWidth(
        PublicSignalProfileConfiguration profile, decimal width)
    {
        PublicSignalIndicatorConfiguration indicators = profile.Indicators!;
        PublicSignalIndicatorConfiguration adjusted = new(
            indicators.PriceContextVersion, indicators.FeatureQualityVersion,
            indicators.SetupFamilyVersion, indicators.PivotLeftBars, indicators.PivotRightBars,
            indicators.AtrPeriodBars, indicators.AtrMode, indicators.AtrValueUnit,
            indicators.ChannelCenterKind, indicators.ChannelCenterPeriodBars,
            indicators.Hlc3SmoothingKind, indicators.ChannelCenterValueUnit,
            indicators.ChannelWidthKind, indicators.ChannelWidthPeriodBars,
            indicators.ChannelWidthMultiplier, indicators.ChannelWidthValueUnit,
            indicators.ChannelWidthMultiplierUnit, indicators.LevelBufferMultiplier,
            indicators.LevelBufferMultiplierUnit, indicators.HtfFastPeriodBars,
            indicators.HtfSlowPeriodBars, indicators.HtfMovingAverageKind,
            indicators.HtfConfirmationBars, indicators.HtfMovingAverageValueUnit,
            width, indicators.BoundaryMinimumChannelWidthUnit);
        return new PublicSignalProfileConfiguration(profile.FormatVersion,
            profile.ProfileVersion, profile.Setup, profile.Context, adjusted, profile.Universe);
    }

    private static string CanonicalizeSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        string canonical = symbol.ToUpperInvariant();
        if (canonical.Length > 20 || canonical.Any(static character =>
            !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "A public symbol must contain only ASCII letters and digits.",
                nameof(symbol));
        }

        return canonical;
    }

    private static string NormalizeDatabasePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (StringComparer.Ordinal.Equals(path, ":memory:"))
        {
            throw new ArgumentException(
                "Signal-only databases must be file-backed.", parameterName);
        }

        return Path.GetFullPath(path);
    }

    private static JsonElement ParseElement(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Configuration properties must be unique.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private sealed record WireConfiguration(int FormatVersion, string Mode,
        string[] Symbols, string InputLedgerPath, string SignalJournalPath,
        JsonElement Profile, JsonElement ScenarioPolicies,
        IReadOnlyDictionary<string, decimal>? BoundaryMinimumChannelWidths = null,
        JsonElement? MultiLanePolicy = null, WireAdditionalProfile[]? AdditionalProfiles = null);

    private sealed record WireAdditionalProfile(JsonElement Profile, JsonElement ScenarioPolicies);
}
