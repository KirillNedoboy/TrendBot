using System.Text.Json;
using TradingBot.Core;

namespace TradingBot.Exchange.Binance.Tests;

internal static class BinanceAlgoProtocolFixtures
{
    public static readonly string UnknownStatusUpdate =
        "{\"e\":\"ALGO_UPDATE\",\"E\":1700000000320,\"T\":1700000000319," +
        "\"o\":{\"algoId\":7001,\"status\":\"FUTURE_STATUS\",\"futureField\":{\"opaque\":true}}}";

    public static readonly string MissingTransactionTime =
        "{\"e\":\"ALGO_UPDATE\",\"E\":1700000000400,\"o\":{\"algoId\":7001}}";

    public static readonly string WrongEventTimeType =
        "{\"e\":\"ALGO_UPDATE\",\"E\":\"1700000000410\",\"T\":1700000000409," +
        "\"o\":{\"algoId\":7001}}";

    public static readonly string MissingOpaqueOrder =
        "{\"e\":\"ALGO_UPDATE\",\"E\":1700000000420,\"T\":1700000000419}";

    public static string AlgoUpdate(long algoId, string status, long eventTime,
        long transactionTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        return $"{{\"e\":\"ALGO_UPDATE\",\"E\":{eventTime},\"T\":{transactionTime}," +
            $"\"o\":{{\"algoId\":{algoId},\"status\":\"{status}\",\"symbol\":\"BTCUSDT\"}}}}";
    }
}

internal sealed record BinanceAlgoObservationMetadata(
    string SessionId,
    long StableSequence,
    UtcTimestamp ReceiveTime,
    UtcTimestamp AvailabilityTime);

internal sealed record BinanceAlgoUpdateObservation(
    string EventType,
    long EventTimeMilliseconds,
    long TransactionTimeMilliseconds,
    JsonElement OpaqueOrder,
    UtcTimestamp ReceiveTime,
    UtcTimestamp AvailabilityTime,
    string SessionId,
    long StableSequence,
    string RawJson)
{
    public (string SessionId, long StableSequence) EventIdentity => (SessionId, StableSequence);
}

internal static class BinanceAlgoProtocolFixtureParser
{
    public static BinanceAlgoUpdateObservation Parse(string rawJson,
        BinanceAlgoObservationMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.SessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(metadata.StableSequence);
        ArgumentNullException.ThrowIfNull(metadata.ReceiveTime);
        ArgumentNullException.ThrowIfNull(metadata.AvailabilityTime);
        if (metadata.AvailabilityTime.Value < metadata.ReceiveTime.Value)
        {
            throw new ArgumentException("Availability cannot precede receive time.",
                nameof(metadata));
        }

        using JsonDocument document = JsonDocument.Parse(rawJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ALGO_UPDATE envelope must be a JSON object.");
        }

        string eventType = RequiredString(root, "e");
        if (eventType != "ALGO_UPDATE")
        {
            throw new JsonException("The fixture is not an ALGO_UPDATE event.");
        }

        long eventTime = RequiredInt64(root, "E");
        long transactionTime = RequiredInt64(root, "T");
        if (!root.TryGetProperty("o", out JsonElement opaqueOrder)
            || opaqueOrder.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("ALGO_UPDATE requires an opaque object field o.");
        }

        return new BinanceAlgoUpdateObservation(eventType, eventTime, transactionTime,
            opaqueOrder.Clone(), metadata.ReceiveTime, metadata.AvailabilityTime,
            metadata.SessionId, metadata.StableSequence, rawJson);
    }

    public static bool IsDuplicate(BinanceAlgoUpdateObservation first,
        BinanceAlgoUpdateObservation second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.EventIdentity == second.EventIdentity
            && first.RawJson == second.RawJson;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text)
        {
            throw new JsonException($"ALGO_UPDATE requires string field {name}.");
        }

        return text;
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out long number))
        {
            throw new JsonException($"ALGO_UPDATE requires int64 field {name}.");
        }

        return number;
    }
}
