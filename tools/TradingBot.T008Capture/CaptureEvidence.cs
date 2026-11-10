using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;
using TradingBot.Exchange.Binance;

namespace TradingBot.T008Capture;

internal sealed record CaptureConfiguration(
    string TaskId,
    string Symbol,
    int MaximumInstruments,
    string UniverseSource,
    string DataProfileVersion,
    string SetupTimeframe,
    string ContextTimeframe,
    string IndicatorConfiguration,
    long RestStartMilliseconds,
    long RestEndMilliseconds,
    int ExpectedFifteenMinuteBars,
    int ExpectedOneHourBars,
    int MaximumPagesPerTimeframe,
    int MaximumFifteenMinuteRawRows,
    int MaximumOneHourRawRows,
    int MaximumWebSocketMinutes,
    int MaximumReconnectAttempts,
    int MaximumReconnectDowntimeSeconds,
    long MaximumRunStoreBytes,
    int MaximumLedgerReceipts,
    int MaximumMetadataAndReportBytes,
    int MaximumWebSocketFrameBytes,
    int MaximumWebSocketMessageBytes,
    int DeduplicationCapacity,
    string HistoricalRestAvailabilityModel,
    string ProspectiveWebSocketAvailabilityModel,
    string[] HealthAbortCriteria);

internal sealed record ExchangeInfoArtifact(
    BinanceExchangeInfo Snapshot,
    string SnapshotSha256,
    UtcTimestamp? ReceiveTime,
    UtcTimestamp? AvailabilityTime,
    ExchangeEligibilityMetadata? SelectedMetadata,
    MetadataCandidateExclusion? TargetExclusion);

internal sealed record PointInTimeUniverseEvidence(
    string Symbol,
    int MaximumInstruments,
    string Source,
    bool Eligible,
    long? ExchangeServerTimeMilliseconds,
    UtcTimestamp? ReceiveTime,
    UtcTimestamp? AvailabilityTime,
    string? SnapshotSha256,
    ExchangeEligibilityMetadata? SelectedMetadata,
    ImmutableValueList<string> ExclusionReasons);

internal sealed record CoverageIntervalEvidence(
    string Timeframe,
    string AvailabilityModel,
    long IntervalMilliseconds,
    long[] ExpectedOpenTimeMilliseconds,
    long[] CoveredOpenTimeMilliseconds,
    long[] GapOpenTimeMilliseconds);

internal sealed record LedgerReceiptEvidence(
    long IntakeSequence,
    string? EventId,
    string? RevisionHash,
    string Status,
    string? DiagnosticCode,
    string? DiagnosticMessage);

internal sealed record StreamDiagnosticEvidence(
    string Kind,
    string SessionId,
    string? StreamName,
    string? EventId,
    string Message,
    long StableSequence,
    UtcTimestamp ObservedAt);

internal sealed record RestartReplayEvidence(
    bool RestoreCompleted,
    bool HistoryMatched,
    int InitialRestPageRequests,
    int ReopenRestPageRequests,
    int RecoveredReceiptCount,
    int ReplayedEventCount,
    string? BeforeRestartHistorySha256,
    string? AfterRestartHistorySha256,
    string? BeforeRestartReplaySha256,
    string? AfterRestartReplaySha256,
    string[] ReplayEventIds);

internal sealed record CoverageReport(
    string FormatVersion,
    string RunId,
    string TaskId,
    bool Succeeded,
    string? Failure,
    DateTimeOffset RunStartedAt,
    DateTimeOffset RunEndedAt,
    DateTimeOffset RestHistoryStart,
    DateTimeOffset RestHistoryEndExclusive,
    DateTimeOffset? WebSocketStartedAt,
    DateTimeOffset? WebSocketEndedAt,
    DateTimeOffset? ReconnectStartedAt,
    DateTimeOffset? ReconnectFirstEventAt,
    CaptureConfiguration Configuration,
    string ConfigurationSha256,
    string? SourceSha256,
    string RunSha256,
    string? ExchangeInfoSnapshotPath,
    PointInTimeUniverseEvidence? PointInTimeUniverse,
    CoverageIntervalEvidence[] Coverage,
    int? RestFifteenMinutePagesRequested,
    int? RestOneHourPagesRequested,
    int LedgerReceiptCount,
    int OpenWebSocketUpdateCount,
    int DuplicateReceiptCount,
    LedgerReceiptEvidence[] CorrectionLedger,
    LedgerReceiptEvidence[] UnknownLedger,
    StreamDiagnosticEvidence[] WebSocketDiagnostics,
    RestartReplayEvidence RestartReplay,
    bool FullMarketCoverage,
    string[] NonClaims);

internal sealed record CoverageReportEnvelope(string ReportSha256, CoverageReport Report);

internal sealed record NativeLedgerPayload(
    int FormatVersion,
    string? Symbol,
    BinanceKline? Kline,
    BinanceKlineSourceMetadata? SourceMetadata);

internal static class CaptureJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] SerializeCanonical<T>(T value)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        using JsonDocument document = JsonDocument.Parse(json);
        return Canonicalize(document.RootElement);
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    public static byte[] Canonicalize(JsonElement element)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonical(writer, element);
        }

        return stream.ToArray();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject()
                             .OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind: {value.ValueKind}.");
        }
    }
}
