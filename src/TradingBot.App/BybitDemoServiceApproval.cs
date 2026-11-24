using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.App;

internal sealed class BybitDemoServiceApproval(string path, string scopeSha256, string credentialSha256)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true
    };

    public static BybitDemoServiceApproval Create(string path, BybitDemoConfiguration configuration,
        IReadOnlyList<SignalOnlyConfiguration> profiles, string key, bool externalSignals, string releaseDirectory)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The service approval path must be absolute.", nameof(path));
        using IncrementalHash scope = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(scope, "bybit-demo-service-scope-v1");
        Append(scope, "https://api-demo.bybit.com|wss://stream-demo.bybit.com|market|" + externalSignals);
        Append(scope, configuration.ToJson());
        foreach (SignalOnlyConfiguration profile in profiles) Append(scope, profile.ToJson());
        Span<byte> digest = stackalloc byte[32];
        foreach (string file in Directory.EnumerateFiles(releaseDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            Append(scope, Path.GetRelativePath(releaseDirectory, file).Replace('\\', '/'));
            using FileStream stream = File.OpenRead(file);
            SHA256.HashData(stream, digest);
            scope.AppendData(digest);
        }
        return new(path, Convert.ToHexStringLower(scope.GetHashAndReset()),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("bybit-demo-api-key-v1:" + key))));
    }

    public string PendingRequestJson() => JsonSerializer.Serialize(new Receipt(1, "bybit-demo-service", "PENDING",
        DateTimeOffset.UnixEpoch, scopeSha256, credentialSha256), Options);

    public bool IsAuthorized(DateTimeOffset now)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long length = stream.Length;
            if (length is <= 0 or > 8192) return false;
            using JsonDocument document = JsonDocument.Parse(ReadBounded(stream, checked((int)length)));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            SignalOnlyConfiguration.RejectDuplicateProperties(document.RootElement);
            Receipt? receipt = document.RootElement.Deserialize<Receipt>(Options);
            return receipt is { FormatVersion: 1, Mode: "bybit-demo-service", Statement: "ENABLE BYBIT DEMO SERVICE" }
                && receipt.AuthorizedAtUtc > DateTimeOffset.UnixEpoch && receipt.AuthorizedAtUtc.Offset == TimeSpan.Zero
                && receipt.AuthorizedAtUtc <= now && receipt.ScopeSha256 == scopeSha256 && receipt.CredentialSha256 == credentialSha256;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[] ReadBounded(FileStream stream, int length)
    {
        byte[] bytes = new byte[length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        return bytes;
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private sealed record Receipt(int FormatVersion, string Mode, string Statement, DateTimeOffset AuthorizedAtUtc,
        string ScopeSha256, string CredentialSha256);
}
