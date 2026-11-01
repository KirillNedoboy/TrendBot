using System.Security.Cryptography;
using System.Text;

namespace TradingBot.Exchange.Binance;

/// <summary>
/// Signs the exact Binance query/form payload with the runtime-held API secret.
/// The secret is never exposed as a property, serialized, or included in a
/// request payload.
/// </summary>
public sealed class HmacSha256BinanceRequestSigner : IBinancePrivateRequestSigner,
    IDisposable
{
    private byte[]? _secret;

    public HmacSha256BinanceRequestSigner(string apiSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        _secret = Encoding.UTF8.GetBytes(apiSecret);
    }

    public string Sign(string canonicalQuery)
    {
        ArgumentNullException.ThrowIfNull(canonicalQuery);
        byte[]? secret = _secret;
        ObjectDisposedException.ThrowIf(secret is null, this);
        byte[] payload = Encoding.UTF8.GetBytes(canonicalQuery);
        byte[] digest = HMACSHA256.HashData(secret, payload);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public void Dispose()
    {
        byte[]? secret = Interlocked.Exchange(ref _secret, null);
        if (secret is not null)
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}
