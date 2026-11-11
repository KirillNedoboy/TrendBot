using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Explicit configuration for the public-only signal consumer.</summary>
public sealed record SignalOnlyConfiguration
{
    public const int CurrentFormatVersion = 1;
    public const string SignalOnlyMode = "signal-only-public";

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    public int FormatVersion { get; }

    public string Mode { get; }

    public ImmutableValueList<string> Symbols { get; }

    public string InputLedgerPath { get; }

    public string SignalJournalPath { get; }

    public PublicSignalProfileConfiguration Profile { get; }

    public SignalScenarioPolicySet ScenarioPolicies { get; }

    public SignalOnlyConfiguration(IEnumerable<string> symbols, string inputLedgerPath,
        string signalJournalPath, PublicSignalProfileConfiguration profile,
        SignalScenarioPolicySet scenarioPolicies)
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

        FormatVersion = CurrentFormatVersion;
        Mode = SignalOnlyMode;

        Symbols = new ImmutableValueList<string>(canonicalSymbols);
        InputLedgerPath = inputPath;
        SignalJournalPath = journalPath;
        Profile = profile;
        ScenarioPolicies = scenarioPolicies;
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
            if (wire.FormatVersion != CurrentFormatVersion
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
            return new SignalOnlyConfiguration(wire.Symbols, wire.InputLedgerPath,
                wire.SignalJournalPath, profile, policies);
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
        CurrentFormatVersion, SignalOnlyMode, Symbols.ToArray(), InputLedgerPath,
        SignalJournalPath, ParseElement(CoreJsonSerializer.Serialize(Profile)),
        ParseElement(CoreJsonSerializer.Serialize(ScenarioPolicies))), WireOptions);

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

    private static void RejectDuplicateProperties(JsonElement element)
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
        JsonElement Profile, JsonElement ScenarioPolicies);
}
