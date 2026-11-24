using System.Globalization;
using System.Text.Json;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Monitors the Binance 55-bar exit independently of a public scenario's lifetime.</summary>
internal sealed class BybitDemoChannelMonitor(HttpClient http, TimeProvider clock)
{
    public async Task<bool> HasExitAsync(string symbol, string timeframe, Direction direction,
        DateTimeOffset enteredAt, CancellationToken cancellationToken)
    {
        int minutes = timeframe switch { "30m" => 30, "1h" => 60, _ => throw new ArgumentException("The channel timeframe is invalid.") };
        long duration = minutes * 60_000L;
        long first = enteredAt.ToUnixTimeMilliseconds() / duration * duration - 55 * duration;
        long until = clock.GetUtcNow().ToUnixTimeMilliseconds();
        Queue<(long Open, decimal High, decimal Low)> history = new(55);
        long? previous = null;
        while (first < until)
        {
            string url = "https://fapi.binance.com/fapi/v1/klines?symbol=" + Uri.EscapeDataString(symbol)
                + "&interval=" + timeframe + "&startTime=" + first.ToString(CultureInfo.InvariantCulture)
                + "&endTime=" + until.ToString(CultureInfo.InvariantCulture) + "&limit=1000";
            using HttpResponseMessage response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            JsonElement rows = document.RootElement;
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                throw new InvalidDataException("The Binance exit channel history is unavailable.");
            long next = first;
            foreach (JsonElement row in rows.EnumerateArray())
            {
                long open = row[0].GetInt64();
                long close = row[6].GetInt64();
                if (open < first || open % duration != 0 || close != open + duration - 1
                    || previous is { } prior && open != prior + duration)
                    throw new InvalidDataException("The Binance exit channel history has a gap.");
                if (close >= until)
                {
                    if (history.Count != 55) throw new InvalidDataException("The Binance exit channel has incomplete warmup.");
                    return false;
                }
                decimal high = decimal.Parse(row[2].GetString()!, CultureInfo.InvariantCulture);
                decimal low = decimal.Parse(row[3].GetString()!, CultureInfo.InvariantCulture);
                if (high < low || low <= 0m) throw new InvalidDataException("The Binance exit candle is invalid.");
                if (history.Count == 55 && close > enteredAt.ToUnixTimeMilliseconds())
                {
                    decimal lower = decimal.MaxValue;
                    decimal upper = decimal.MinValue;
                    foreach (var bar in history) { lower = Math.Min(lower, bar.Low); upper = Math.Max(upper, bar.High); }
                    if (direction == Direction.Long ? low < lower : high > upper) return true;
                }
                if (history.Count == 55) history.Dequeue();
                history.Enqueue((open, high, low));
                previous = open;
                next = open + duration;
            }
            if (next <= first) throw new InvalidDataException("The Binance exit channel did not advance.");
            first = next;
            if (rows.GetArrayLength() < 1000) break;
        }
        if (history.Count < 55 || previous is null || until - (previous.Value + duration) >= duration)
            throw new InvalidDataException("The Binance exit channel is incomplete or stale.");
        return false;
    }
}
