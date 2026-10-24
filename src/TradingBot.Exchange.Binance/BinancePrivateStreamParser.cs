using System.Text.Json;

namespace TradingBot.Exchange.Binance;

/// <summary>Parses current USD-M private user-data envelopes without guessing opaque fields.</summary>
public static class BinancePrivateStreamParser
{
    public static BinancePrivateStreamEvent Parse(string rawJson,
        BinancePrivateStreamMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);
        ArgumentNullException.ThrowIfNull(metadata);
        using JsonDocument document = JsonDocument.Parse(rawJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A Binance private stream envelope must be an object.");
        }

        string eventType = BinancePrivateRestClient.RequiredString(root, "e");
        long eventTime = BinancePrivateRestClient.RequiredInt64(root, "E");
        long? transactionTime = BinancePrivateRestClient.OptionalInt64(root, "T");
        return eventType switch
        {
            "ORDER_TRADE_UPDATE" => ParseOrderTradeUpdate(root, eventTime,
                RequireTransactionTime(transactionTime, eventType), metadata, rawJson),
            "ACCOUNT_UPDATE" => ParseAccountUpdate(root, eventTime,
                RequireTransactionTime(transactionTime, eventType), metadata, rawJson),
            "ALGO_UPDATE" => ParseAlgoUpdate(root, eventTime,
                RequireTransactionTime(transactionTime, eventType), metadata, rawJson),
            "listenKeyExpired" => ParseListenKeyExpired(root, eventTime, metadata, rawJson),
            "ACCOUNT_CONFIG_UPDATE" or "CONDITIONAL_ORDER_TRIGGER_REJECT" or "GRID_UPDATE"
                or "MARGIN_CALL" or "STRATEGY_UPDATE" or "TRADE_LITE"
                => ParseOpaque(root, eventType, eventTime, transactionTime, metadata, rawJson),
            _ => throw new JsonException($"Unsupported Binance private event type '{eventType}'.")
        };
    }

    private static BinanceOrderTradeUpdateEvent ParseOrderTradeUpdate(JsonElement root,
        long eventTime, long transactionTime, BinancePrivateStreamMetadata metadata,
        string rawJson)
    {
        JsonElement order = RequiredObject(root, "o");
        string statusText = BinancePrivateRestClient.RequiredString(order, "X");
        BinancePrivateOrder parsed = new()
        {
            OrderId = BinancePrivateRestClient.RequiredInt64(order, "i"),
            ClientOrderId = BinancePrivateRestClient.OptionalString(order, "c") ?? string.Empty,
            Symbol = BinancePrivateRestClient.RequiredString(order, "s"),
            Side = BinancePrivateRestClient.ParseWireSide(
                BinancePrivateRestClient.RequiredString(order, "S")),
            PositionSide = BinancePrivateRestClient.ParsePositionSide(
                BinancePrivateRestClient.OptionalString(order, "ps") ?? "BOTH"),
            Type = BinancePrivateRestClient.ParseWireOrderType(
                BinancePrivateRestClient.OptionalString(order, "o") ?? "MARKET"),
            Status = BinancePrivateRestClient.ParseWireStatus(statusText),
            StatusText = statusText,
            TimeInForce = BinancePrivateRestClient.ParseWireTimeInForce(
                BinancePrivateRestClient.OptionalString(order, "f")),
            OriginalQuantity = BinancePrivateRestClient.OptionalDecimal(order, "q") ?? 0m,
            ExecutedQuantity = BinancePrivateRestClient.OptionalDecimal(order, "z") ?? 0m,
            Price = BinancePrivateRestClient.OptionalDecimal(order, "p") ?? 0m,
            AveragePrice = BinancePrivateRestClient.OptionalDecimal(order, "ap") ?? 0m,
            StopPrice = BinancePrivateRestClient.OptionalDecimal(order, "sp"),
            ReduceOnly = BinancePrivateRestClient.OptionalBoolean(order, "R") ?? false,
            ClosePosition = BinancePrivateRestClient.OptionalBoolean(order, "cp") ?? false,
            OrderTimeMilliseconds = BinancePrivateRestClient.OptionalInt64(order, "T") ?? transactionTime,
            UpdateTimeMilliseconds = transactionTime,
            Raw = order.Clone()
        };

        decimal lastQuantity = BinancePrivateRestClient.OptionalDecimal(order, "l") ?? 0m;
        List<BinancePrivateFill> fills = [];
        if (lastQuantity > 0m)
        {
            decimal lastPrice = BinancePrivateRestClient.OptionalDecimal(order, "L")
                ?? parsed.AveragePrice;
            if (lastPrice <= 0m)
            {
                throw new JsonException("A Binance trade update fill requires a positive price.");
            }

            fills.Add(new BinancePrivateFill
            {
                TradeId = BinancePrivateRestClient.OptionalInt64(order, "t") ?? 0L,
                OrderId = parsed.OrderId,
                Symbol = parsed.Symbol,
                Quantity = lastQuantity,
                Price = lastPrice,
                Commission = BinancePrivateRestClient.OptionalDecimal(order, "n"),
                CommissionAsset = BinancePrivateRestClient.OptionalString(order, "N"),
                TradeTimeMilliseconds = BinancePrivateRestClient.OptionalInt64(order, "T")
                    ?? transactionTime
            });
        }

        return new BinanceOrderTradeUpdateEvent(parsed, fills, eventTime, transactionTime,
            metadata, rawJson);
    }

    private static BinanceAccountUpdateEvent ParseAccountUpdate(JsonElement root, long eventTime,
        long transactionTime, BinancePrivateStreamMetadata metadata, string rawJson)
    {
        JsonElement account = RequiredObject(root, "a");
        JsonElement positions = RequiredArray(account, "P");
        List<BinancePrivatePositionUpdate> updates = [];
        foreach (JsonElement position in positions.EnumerateArray())
        {
            updates.Add(new BinancePrivatePositionUpdate
            {
                Symbol = BinancePrivateRestClient.RequiredString(position, "s"),
                PositionAmount = BinancePrivateRestClient.RequiredDecimal(position, "pa"),
                PositionSide = BinancePrivateRestClient.ParsePositionSide(
                    BinancePrivateRestClient.OptionalString(position, "ps") ?? "BOTH"),
                EntryPrice = BinancePrivateRestClient.OptionalDecimal(position, "ep") ?? 0m,
                BreakEvenPrice = BinancePrivateRestClient.OptionalDecimal(position, "bep"),
                UnrealizedProfit = BinancePrivateRestClient.OptionalDecimal(position, "up"),
                MarginType = BinancePrivateRestClient.OptionalString(position, "mt")
            });
        }

        return new BinanceAccountUpdateEvent(updates, eventTime, transactionTime, metadata,
            rawJson);
    }

    private static BinanceAlgoUpdateEvent ParseAlgoUpdate(JsonElement root, long eventTime,
        long transactionTime, BinancePrivateStreamMetadata metadata, string rawJson)
    {
        JsonElement order = RequiredObject(root, "o");
        return new BinanceAlgoUpdateEvent(order.Clone(), eventTime, transactionTime, metadata,
            rawJson);
    }

    private static BinanceListenKeyExpiredEvent ParseListenKeyExpired(JsonElement root,
        long eventTime, BinancePrivateStreamMetadata metadata, string rawJson)
    {
        return new BinanceListenKeyExpiredEvent(
            BinancePrivateRestClient.RequiredString(root, "listenKey"), eventTime, metadata,
            rawJson);
    }

    private static BinanceOpaquePrivateStreamEvent ParseOpaque(JsonElement root, string eventType,
        long eventTime, long? transactionTime, BinancePrivateStreamMetadata metadata,
        string rawJson)
    {
        JsonElement payload = root.Clone();
        return new BinanceOpaquePrivateStreamEvent(eventType, payload, eventTime,
            transactionTime, metadata, rawJson);
    }

    private static JsonElement RequiredObject(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException($"Binance private event requires object field '{name}'.");
        }

        return value;
    }

    private static JsonElement RequiredArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Binance private event requires array field '{name}'.");
        }

        return value;
    }

    private static long RequireTransactionTime(long? value, string eventType) => value
        ?? throw new JsonException($"Binance {eventType} requires transaction time T.");
}
