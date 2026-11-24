using System.Text;

namespace TradingBot.App;

internal sealed class BybitDemoCredentials
{
    private BybitDemoCredentials(string key, string secret) { Key = key; Secret = secret; }
    public string Key { get; }
    public string Secret { get; }

    public static BybitDemoCredentials Load(string? directory, string? environmentKey, string? environmentSecret)
    {
        try
        {
            string? key = environmentKey;
            string? secret = environmentSecret;
            if (directory is not null)
            {
                if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException();
                key = ReadCredential(Path.Combine(directory, "bybit-demo-api-key"));
                secret = ReadCredential(Path.Combine(directory, "bybit-demo-api-secret"));
            }
            if (!Valid(key) || !Valid(secret)) throw new InvalidDataException();
            return new(key!, secret!);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("The selected Demo credential source must contain a complete valid pair; no fallback is used after a file error.");
        }
    }

    private static string ReadCredential(string path)
    {
        Span<byte> buffer = stackalloc byte[4097];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (count is 0 or > 4096) throw new InvalidDataException();
        foreach (byte value in buffer[..count])
            if (value is < 33 or > 126) throw new InvalidDataException();
        return Encoding.ASCII.GetString(buffer[..count]);
    }

    private static bool Valid(string? value)
    {
        if (value is null || value.Length is 0 or > 4096) return false;
        foreach (char character in value)
            if (character is < '!' or > '~') return false;
        return true;
    }
}
