using System.Globalization;
using System.Text.Json;

namespace TradingBot.App;

/// <summary>UTC estimate calibrated to Demo server time; the OS clock is never changed.</summary>
internal sealed class BybitDemoClock : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _anchor;
    private long _timestamp;
    public TimeSpan Uncertainty { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            if (_anchor == default) throw new InvalidOperationException("The demo clock has not been calibrated.");
            return _anchor + GetElapsedTime(_timestamp);
        }
    }

    public async Task RefreshAsync(HttpClient http, int receiveWindowMilliseconds, CancellationToken token)
    {
        long before = GetTimestamp();
        using HttpResponseMessage response = await http.GetAsync("https://api-demo.bybit.com/v5/market/time", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        long after = GetTimestamp();
        TimeSpan roundTrip = GetElapsedTime(before, after);
        JsonElement root = document.RootElement;
        if (root.GetProperty("retCode").GetInt32() != 0 || roundTrip.TotalMilliseconds > receiveWindowMilliseconds)
            throw new InvalidDataException("The demo time calibration is unavailable or too uncertain.");
        long milliseconds = long.Parse(root.GetProperty("result").GetProperty("timeNano").GetString()!, CultureInfo.InvariantCulture) / 1_000_000;
        lock (_sync)
        {
            _anchor = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) + roundTrip / 2;
            _timestamp = after;
            Uncertainty = roundTrip / 2;
        }
    }

    public DateTimeOffset ClaimLowerBound() => GetUtcNow() - Uncertainty;
}
