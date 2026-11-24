using System.Globalization;
using System.Text.Json;
using TradingBot.Core;

namespace TradingBot.App;

internal sealed record BybitDemoOrderFact(string Id, string ClientId, string? ParentId, string Symbol,
    Direction Side, decimal Quantity, decimal Filled, decimal? Average, string Status,
    bool ReduceOnly, int PositionIndex, decimal? Trigger, string StopType, string TriggerBy, int TriggerDirection, string OrderType)
{
    public bool Terminal => TradingBot.Infrastructure.SqliteBybitDemoReservationStore.Terminal(Status);
}

internal sealed partial class BybitDemoRestClient
{
    public async Task<BybitDemoOrderFact?> FindOrderAsync(string symbol, string clientId, string? orderId,
        CancellationToken cancellationToken)
    {
        string identity = orderId is null ? "&orderLinkId=" + Uri.EscapeDataString(clientId) : "&orderId=" + Uri.EscapeDataString(orderId);
        string query = "category=linear&symbol=" + Uri.EscapeDataString(symbol) + identity;
        foreach (string path in new[] { "/v5/order/realtime", "/v5/order/history" })
        {
            using JsonDocument document = await ReadSignedAsync(path, query, cancellationToken).ConfigureAwait(false);
            JsonElement list = document.RootElement.GetProperty("result").GetProperty("list");
            if (list.GetArrayLength() == 0) continue;
            if (list.GetArrayLength() != 1) throw new InvalidDataException("The demo order lookup is ambiguous.");
            BybitDemoOrderFact fact = ParseOrder(list[0]);
            if (fact.Symbol != symbol || orderId is not null && fact.Id != orderId
                || orderId is null && fact.ClientId != clientId)
                throw new InvalidDataException("The demo order does not match its lookup.");
            return fact;
        }
        return null;
    }

    public async Task<IReadOnlyList<BybitDemoExecution>> GetOrderExecutionsAsync(BybitDemoOrderFact order,
        DateTimeOffset since, CancellationToken cancellationToken)
    {
        DateTimeOffset until = _clock.GetUtcNow();
        Dictionary<string, BybitDemoExecution> executions = new(StringComparer.Ordinal);
        for (DateTimeOffset start = since; start <= until;)
        {
            DateTimeOffset end = start + TimeSpan.FromDays(7) < until ? start.AddDays(7).AddMilliseconds(-1) : until;
            string cursor = string.Empty;
            HashSet<string> cursors = new(StringComparer.Ordinal);
            bool complete = false;
            for (int page = 0; page < 1000; page++)
            {
                string query = "category=linear&orderId=" + Uri.EscapeDataString(order.Id)
                    + "&execType=Trade&limit=100&startTime=" + start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                    + "&endTime=" + end.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                    + (cursor.Length == 0 ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
                using JsonDocument document = await ReadSignedAsync("/v5/execution/list", query, cancellationToken).ConfigureAwait(false);
                JsonElement result = document.RootElement.GetProperty("result");
                foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
                {
                    string side = RequiredOrderText(item, "side");
                    DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(RequiredOrderText(item, "execTime"), CultureInfo.InvariantCulture));
                    decimal quantity = RequiredNumber(item, "execQty");
                    string? closedText = item.GetProperty("closedSize").GetString();
                    decimal closed = string.IsNullOrEmpty(closedText) ? (order.ReduceOnly ? quantity : 0m)
                        : decimal.Parse(closedText, CultureInfo.InvariantCulture);
                    BybitDemoExecution fill = new(RequiredOrderText(item, "execId"), RequiredOrderText(item, "orderId"),
                        item.GetProperty("orderLinkId").GetString()!, RequiredOrderText(item, "symbol"),
                        side == "Buy" ? Direction.Long : Direction.Short, quantity, RequiredNumber(item, "execPrice"),
                        decimal.Parse(RequiredOrderText(item, "execFee"), CultureInfo.InvariantCulture), closed,
                        item.GetProperty("seq").GetInt64(), at, until, until);
                    if (item.GetProperty("execType").GetString() != "Trade" || side is not ("Buy" or "Sell")
                        || fill.Symbol != order.Symbol || fill.ExchangeOrderId != order.Id || fill.Side != order.Side
                        || fill.Quantity <= 0m || fill.Price <= 0m || at < start || at > end
                        || (order.ReduceOnly ? closed != quantity : closed != 0m))
                        throw new InvalidDataException("The demo execution history does not match its order.");
                    if (executions.TryGetValue(fill.ExecutionId, out BybitDemoExecution previous) && previous != fill)
                        throw new InvalidDataException("The demo execution history conflicts.");
                    executions[fill.ExecutionId] = fill;
                }
                cursor = result.GetProperty("nextPageCursor").GetString()!;
                if (string.IsNullOrEmpty(cursor)) { complete = true; break; }
                if (cursor.Length > 2048 || !cursors.Add(cursor)) throw new InvalidDataException("The demo execution cursor repeats.");
            }
            if (!complete) throw new InvalidDataException("The demo execution history is incomplete.");
            if (end == until) break;
            start = end.AddMilliseconds(1);
        }
        return executions.Values.OrderBy(item => item.ExchangeAt).ThenBy(item => item.ExecutionId, StringComparer.Ordinal).ToArray();
    }

    public async Task CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new { category = "linear", symbol, orderId });
        try
        {
            using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Post, new Uri("https://api-demo.bybit.com/v5/order/cancel"), body);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.GetProperty("retCode").GetInt32() != 0) throw new BybitDemoSubmissionUnknownException();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            throw new BybitDemoSubmissionUnknownException();
        }
    }

    public async Task<IReadOnlyList<BybitDemoOrderFact>> GetRelatedOrdersAsync(string symbol, DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        Dictionary<string, BybitDemoOrderFact> orders = new(StringComparer.Ordinal);
        DateTimeOffset until = _clock.GetUtcNow();
        for (DateTimeOffset start = since; start <= until;)
        {
            DateTimeOffset end = start.AddDays(7) < until ? start.AddDays(7).AddMilliseconds(-1) : until;
            string cursor = string.Empty;
            HashSet<string> cursors = new(StringComparer.Ordinal);
            bool complete = false;
            for (int page = 0; page < 1000; page++)
            {
                string query = "category=linear&symbol=" + Uri.EscapeDataString(symbol) + "&limit=50&startTime="
                    + start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "&endTime="
                    + end.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                    + (cursor.Length == 0 ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
                using JsonDocument document = await ReadSignedAsync("/v5/order/history", query, cancellationToken).ConfigureAwait(false);
                JsonElement result = document.RootElement.GetProperty("result");
                foreach (JsonElement item in result.GetProperty("list").EnumerateArray())
                {
                    BybitDemoOrderFact fact = ParseOrder(item);
                    if (fact.Symbol != symbol) throw new InvalidDataException("The demo history symbol conflicts.");
                    orders[fact.Id] = fact;
                }
                cursor = result.GetProperty("nextPageCursor").GetString()!;
                if (string.IsNullOrEmpty(cursor)) { complete = true; break; }
                if (cursor.Length > 2048 || !cursors.Add(cursor)) throw new InvalidDataException("The demo order cursor repeats.");
            }
            if (!complete) throw new InvalidDataException("The demo order history is incomplete.");
            if (end == until) break;
            start = end.AddMilliseconds(1);
        }
        return orders.Values.ToArray();
    }

    private static BybitDemoOrderFact ParseOrder(JsonElement item)
    {
        string side = RequiredOrderText(item, "side");
        BybitDemoOrderFact fact = new(RequiredOrderText(item, "orderId"), item.GetProperty("orderLinkId").GetString()!,
            item.TryGetProperty("parentOrderLinkId", out JsonElement parent) ? parent.GetString() : null,
            RequiredOrderText(item, "symbol"), side == "Buy" ? Direction.Long : Direction.Short,
            RequiredNumber(item, "qty"), RequiredNumber(item, "cumExecQty"), OptionalNumber(item, "avgPrice"),
            RequiredOrderText(item, "orderStatus"), item.GetProperty("reduceOnly").GetBoolean(),
            item.GetProperty("positionIdx").GetInt32(), OptionalNumber(item, "triggerPrice"),
            item.GetProperty("stopOrderType").GetString() ?? string.Empty,
            item.GetProperty("triggerBy").GetString() ?? string.Empty,
            item.GetProperty("triggerDirection").GetInt32(), RequiredOrderText(item, "orderType"));
        if (side is not ("Buy" or "Sell") || fact.PositionIndex != 0 || fact.Quantity <= 0m
            || fact.Filled < 0m || fact.Filled > fact.Quantity
            || fact.Status is not ("New" or "PartiallyFilled" or "Untriggered" or "Triggered" or "Filled"
                or "Cancelled" or "PartiallyFilledCanceled" or "Rejected" or "Deactivated"))
            throw new InvalidDataException("The demo order history is invalid.");
        return fact;
    }

    private async Task<JsonDocument> ReadSignedAsync(string path, string query, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Get, new Uri("https://api-demo.bybit.com" + path + "?" + query), query);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            try
            {
                SignalOnlyConfiguration.RejectDuplicateProperties(document.RootElement);
                if (document.RootElement.GetProperty("retCode").GetInt32() != 0
                    || document.RootElement.GetProperty("result").GetProperty("category").GetString() != "linear")
                    throw new InvalidDataException("The demo history is unavailable.");
                return document;
            }
            catch { document.Dispose(); throw; }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The demo history is unavailable.");
        }
    }
}
