using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Explicit, secret-free configuration for the separate demo executor.</summary>
internal sealed record BybitDemoConfiguration
{
    private const int FormatVersion = 1;
    private const string Mode = "bybit-demo";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    private BybitDemoConfiguration(decimal targetNotionalUsdt, int maximumPositions,
        decimal maximumPriceDifferencePercent, decimal maximumSlippagePercent, decimal maximumLeverage,
        string marginMode, int maximumSignalAgeSeconds, int maximumQuoteAgeSeconds,
        int privateStreamFreshnessSeconds, int receiveWindowMilliseconds, string statePath,
        ImmutableValueList<string> signalJournalPaths)
    {
        TargetNotionalUsdt = targetNotionalUsdt;
        MaximumPositions = maximumPositions;
        MaximumPriceDifferencePercent = maximumPriceDifferencePercent;
        MaximumSlippagePercent = maximumSlippagePercent;
        MaximumLeverage = maximumLeverage;
        MarginMode = marginMode;
        MaximumSignalAgeSeconds = maximumSignalAgeSeconds;
        MaximumQuoteAgeSeconds = maximumQuoteAgeSeconds;
        PrivateStreamFreshnessSeconds = privateStreamFreshnessSeconds;
        ReceiveWindowMilliseconds = receiveWindowMilliseconds;
        StatePath = statePath;
        SignalJournalPaths = signalJournalPaths;
    }

    public decimal TargetNotionalUsdt { get; }
    public int MaximumPositions { get; }
    public decimal MaximumPriceDifferencePercent { get; }
    public decimal MaximumSlippagePercent { get; }
    public decimal MaximumLeverage { get; }
    public string MarginMode { get; }
    public int MaximumSignalAgeSeconds { get; }
    public int MaximumQuoteAgeSeconds { get; }
    public int PrivateStreamFreshnessSeconds { get; }
    public int ReceiveWindowMilliseconds { get; }
    public string StatePath { get; }
    public ImmutableValueList<string> SignalJournalPaths { get; }

    public static BybitDemoConfiguration Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }
            SignalOnlyConfiguration.RejectDuplicateProperties(document.RootElement);
            WireConfiguration wire = JsonSerializer.Deserialize<WireConfiguration>(json, Options)
                ?? throw new JsonException();
            if (wire.FormatVersion != FormatVersion || !StringComparer.Ordinal.Equals(wire.Mode, Mode)
                || wire.TargetNotionalUsdt is <= 0m or > 250m
                || wire.MaximumPositions is <= 0 or > 20
                || wire.MaximumPriceDifferencePercent is <= 0m or > 10m
                || wire.MaximumSlippagePercent is < 0.01m or > 10m
                || decimal.Round(wire.MaximumSlippagePercent, 2) != wire.MaximumSlippagePercent
                || wire.MaximumLeverage is < 1m or > 10m
                || !StringComparer.Ordinal.Equals(wire.MarginMode, "isolated")
                || wire.MaximumSignalAgeSeconds <= 0 || wire.MaximumQuoteAgeSeconds <= 0
                || wire.PrivateStreamFreshnessSeconds <= 0 || wire.ReceiveWindowMilliseconds <= 0
                || wire.SignalJournalPaths is null || wire.SignalJournalPaths.Length != 3)
            {
                throw new JsonException();
            }
            string state = NormalizePath(wire.StatePath);
            string[] inputs = wire.SignalJournalPaths.Select(NormalizePath).ToArray();
            StringComparer comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (inputs.Distinct(comparer).Count() != inputs.Length || inputs.Contains(state, comparer))
            {
                throw new JsonException();
            }
            return new BybitDemoConfiguration(wire.TargetNotionalUsdt, wire.MaximumPositions,
                wire.MaximumPriceDifferencePercent, wire.MaximumSlippagePercent, wire.MaximumLeverage,
                wire.MarginMode, wire.MaximumSignalAgeSeconds, wire.MaximumQuoteAgeSeconds,
                wire.PrivateStreamFreshnessSeconds, wire.ReceiveWindowMilliseconds, state,
                new ImmutableValueList<string>(inputs));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or NotSupportedException or IOException or InvalidOperationException)
        {
            throw new JsonException("The Bybit demo configuration is invalid.");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(new WireConfiguration(
        FormatVersion, Mode, TargetNotionalUsdt, MaximumPositions, MaximumPriceDifferencePercent,
        MaximumSlippagePercent, MaximumLeverage, MarginMode, MaximumSignalAgeSeconds,
        MaximumQuoteAgeSeconds, PrivateStreamFreshnessSeconds, ReceiveWindowMilliseconds,
        StatePath, SignalJournalPaths.ToArray()), Options);

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (StringComparer.Ordinal.Equals(path, ":memory:"))
        {
            throw new ArgumentException("The demo journals must be file-backed.", nameof(path));
        }
        return Path.GetFullPath(path);
    }

    private sealed record WireConfiguration(int FormatVersion, string Mode,
        decimal TargetNotionalUsdt, int MaximumPositions, decimal MaximumPriceDifferencePercent,
        decimal MaximumSlippagePercent, decimal MaximumLeverage, string MarginMode,
        int MaximumSignalAgeSeconds, int MaximumQuoteAgeSeconds, int PrivateStreamFreshnessSeconds,
        int ReceiveWindowMilliseconds, string StatePath, string[] SignalJournalPaths);
}
