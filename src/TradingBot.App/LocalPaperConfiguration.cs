using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Backtesting;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Explicit, local-only settings for a research paper simulation.</summary>
public sealed record LocalPaperConfiguration
{
    public const int CurrentFormatVersion = 1;
    public const string LocalPaperMode = "local-paper";

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
    public string ConfigurationId { get; }
    public string Mode { get; }
    public Money InitialEquity { get; }
    public decimal RiskFraction { get; }
    public RiskEvaluationConfiguration RiskConfiguration { get; }
    public SimulatedFillAssumptions FillAssumptions { get; }

    public LocalPaperConfiguration(string configurationId, Money initialEquity,
        decimal riskFraction, RiskEvaluationConfiguration riskConfiguration,
        SimulatedFillAssumptions fillAssumptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        ArgumentNullException.ThrowIfNull(initialEquity);
        ArgumentNullException.ThrowIfNull(riskConfiguration);
        ArgumentNullException.ThrowIfNull(fillAssumptions);
        if (initialEquity.Amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(initialEquity));
        }

        if (riskFraction is <= 0m or > 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(riskFraction));
        }

        FormatVersion = CurrentFormatVersion;
        ConfigurationId = configurationId;
        Mode = LocalPaperMode;
        InitialEquity = initialEquity;
        RiskFraction = riskFraction;
        RiskConfiguration = riskConfiguration;
        FillAssumptions = fillAssumptions;
    }

    public static LocalPaperConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            WireConfiguration wire = JsonSerializer.Deserialize<WireConfiguration>(json,
                WireOptions) ?? throw new JsonException();
            if (wire.FormatVersion != CurrentFormatVersion
                || !StringComparer.Ordinal.Equals(wire.Mode, LocalPaperMode))
            {
                throw new JsonException();
            }

            return new LocalPaperConfiguration(wire.ConfigurationId,
                CoreJsonSerializer.Deserialize<Money>(wire.InitialEquity.GetRawText()),
                wire.RiskFraction,
                CoreJsonSerializer.Deserialize<RiskEvaluationConfiguration>(
                    wire.RiskConfiguration.GetRawText()),
                DeserializeFillAssumptions(wire.FillAssumptions));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or NotSupportedException or OverflowException)
        {
            throw new JsonException("The local paper configuration is invalid.");
        }
    }

    public static LocalPaperConfiguration Load(string path)
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw new IOException("The local paper configuration could not be read.");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new WireConfiguration(
        FormatVersion, ConfigurationId, Mode,
        JsonDocument.Parse(CoreJsonSerializer.Serialize(InitialEquity)).RootElement.Clone(),
        RiskFraction,
        JsonDocument.Parse(CoreJsonSerializer.Serialize(RiskConfiguration)).RootElement.Clone(),
        SerializeFillAssumptions(FillAssumptions)), WireOptions);

    private static WireFillAssumptions SerializeFillAssumptions(
        SimulatedFillAssumptions assumptions) => new(
        assumptions.Version?.Value ?? throw new InvalidOperationException(),
        assumptions.FeePerQuantityUnit, assumptions.SlippagePerQuantityUnit,
        assumptions.Descriptions.Fees, assumptions.Descriptions.Slippage,
        assumptions.Descriptions.Latency, assumptions.Descriptions.LiquidityAndQueue,
        assumptions.Descriptions.PartialFills, assumptions.Descriptions.IntrabarOrdering,
        assumptions.Descriptions.DataLimitations);

    private static SimulatedFillAssumptions DeserializeFillAssumptions(
        WireFillAssumptions assumptions) => new(assumptions.FeePerQuantityUnit,
        assumptions.SlippagePerQuantityUnit,
        new FillSimulationAssumptions(assumptions.Fees, assumptions.Slippage,
            assumptions.Latency, assumptions.LiquidityAndQueue,
            assumptions.PartialFills, assumptions.IntrabarOrdering,
            assumptions.DataLimitations), new FillSimulationVersion(assumptions.Version));

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException();
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

    private sealed record WireConfiguration(int FormatVersion,
        string ConfigurationId, string Mode, JsonElement InitialEquity,
        decimal RiskFraction, JsonElement RiskConfiguration,
        WireFillAssumptions FillAssumptions);

    private sealed record WireFillAssumptions(string Version,
        decimal FeePerQuantityUnit, decimal SlippagePerQuantityUnit,
        string Fees, string Slippage, string Latency, string LiquidityAndQueue,
        string PartialFills, string IntrabarOrdering, string DataLimitations);
}
