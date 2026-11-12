using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Backtesting;

public enum SignalStudyOutOfSampleViewState
{
    Unknown,
    Unseen,
    ViewedByThisStudy,
    ViewedByDifferentStudy
}

/// <summary>First durable view of a frozen OOS dataset under a study identity.</summary>
public sealed class SignalStudyOutOfSampleViewRecord
{
    public const string CurrentFormatVersion = "signal-study-oos-view-v1";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private SignalStudyOutOfSampleViewRecord(string outOfSampleDatasetHash,
        string manifestIdentity, UtcTimestamp firstViewedAt)
    {
        OutOfSampleDatasetHash = outOfSampleDatasetHash;
        ManifestIdentity = manifestIdentity;
        FirstViewedAt = firstViewedAt;
        CanonicalJson = JsonSerializer.Serialize(new SignalStudyOutOfSampleViewWire(
            CurrentFormatVersion, OutOfSampleDatasetHash, ManifestIdentity,
            FirstViewedAt.Value), SerializerOptions);
    }

    public string OutOfSampleDatasetHash { get; }

    public string ManifestIdentity { get; }

    public UtcTimestamp FirstViewedAt { get; }

    public string CanonicalJson { get; }

    internal static SignalStudyOutOfSampleViewRecord Create(
        string outOfSampleDatasetHash, string manifestIdentity,
        UtcTimestamp firstViewedAt)
    {
        ArgumentNullException.ThrowIfNull(firstViewedAt);
        SignalStudyHashDeclaration.RequireHash(outOfSampleDatasetHash,
            nameof(outOfSampleDatasetHash));
        SignalStudyHashDeclaration.RequireHash(manifestIdentity,
            nameof(manifestIdentity));
        return new SignalStudyOutOfSampleViewRecord(outOfSampleDatasetHash,
            manifestIdentity, firstViewedAt);
    }

    internal static SignalStudyOutOfSampleViewRecord Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SignalStudyOutOfSampleViewWire? wire = JsonSerializer.Deserialize<
            SignalStudyOutOfSampleViewWire>(json, SerializerOptions);
        if (wire is null || !StringComparer.Ordinal.Equals(
                CurrentFormatVersion, wire.FormatVersion))
        {
            throw new JsonException("The OOS-view journal version is unsupported.");
        }

        try
        {
            SignalStudyOutOfSampleViewRecord record = Create(
                wire.OutOfSampleDatasetHash, wire.ManifestIdentity,
                new UtcTimestamp(wire.FirstViewedAt));
            if (!StringComparer.Ordinal.Equals(record.CanonicalJson, json))
            {
                throw new JsonException("The OOS-view journal is not canonical.");
            }

            return record;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            throw new JsonException("The OOS-view journal is malformed.", exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            WriteIndented = false
        };
        return options;
    }

    private sealed record SignalStudyOutOfSampleViewWire(
        string FormatVersion,
        string OutOfSampleDatasetHash,
        string ManifestIdentity,
        DateTimeOffset FirstViewedAt);
}

/// <summary>
/// Write-once guard for reusing an OOS dataset after its first evaluation view.
/// It records access intent only; it does not run or authorize an evaluation.
/// </summary>
public sealed class SignalStudyOutOfSampleViewJournal
{
    private readonly string _directory;

    public SignalStudyOutOfSampleViewJournal(string directory)
    {
        _directory = Path.GetFullPath(
            SignalStudyHashDeclaration.RequireText(directory, nameof(directory)));
    }

    public SignalStudyOutOfSampleViewState GetState(SignalStudyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!TryGetOutOfSampleHash(manifest, out string hash))
        {
            return SignalStudyOutOfSampleViewState.Unknown;
        }

        string path = GetPath(hash);
        if (!File.Exists(path))
        {
            return SignalStudyOutOfSampleViewState.Unseen;
        }

        SignalStudyOutOfSampleViewRecord record = Read(path);
        if (!StringComparer.Ordinal.Equals(record.OutOfSampleDatasetHash, hash))
        {
            throw new JsonException("The OOS-view journal does not match its dataset key.");
        }

        return StringComparer.Ordinal.Equals(record.ManifestIdentity, manifest.Identity)
            ? SignalStudyOutOfSampleViewState.ViewedByThisStudy
            : SignalStudyOutOfSampleViewState.ViewedByDifferentStudy;
    }

    public SignalStudyOutOfSampleViewRecord MarkViewed(SignalStudyManifest manifest,
        UtcTimestamp viewedAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(viewedAt);
        if (!TryGetOutOfSampleHash(manifest, out string hash))
        {
            throw new InvalidOperationException(
                "An unknown signal-study manifest cannot view out-of-sample data.");
        }

        string path = GetPath(hash);
        if (File.Exists(path))
        {
            return RequireSameStudy(hash, manifest, Read(path));
        }

        Directory.CreateDirectory(_directory);
        string temporaryPath = Path.Combine(_directory,
            $".{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
            {
                byte[] payload = Encoding.UTF8.GetBytes(
                    SignalStudyOutOfSampleViewRecord.Create(hash, manifest.Identity,
                        viewedAt).CanonicalJson);
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                return RequireSameStudy(hash, manifest, Read(path));
            }

            return Read(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SignalStudyOutOfSampleViewRecord RequireSameStudy(string hash,
        SignalStudyManifest manifest, SignalStudyOutOfSampleViewRecord record)
    {
        if (!StringComparer.Ordinal.Equals(record.OutOfSampleDatasetHash, hash))
        {
            throw new JsonException("The OOS-view journal does not match its dataset key.");
        }

        if (!StringComparer.Ordinal.Equals(record.ManifestIdentity, manifest.Identity))
        {
            throw new InvalidOperationException(
                "This OOS dataset was already viewed under a different study identity.");
        }

        return record;
    }

    private static bool TryGetOutOfSampleHash(SignalStudyManifest manifest,
        out string hash)
    {
        hash = manifest.OutOfSampleDatasetHash!;
        return manifest.State == SignalStudyDefinitionState.Ready
            && manifest.SplitPlan.OutOfSample is not null
            && hash is not null;
    }

    private string GetPath(string hash) => Path.Combine(_directory, $"{hash}.json");

    private static SignalStudyOutOfSampleViewRecord Read(string path) =>
        SignalStudyOutOfSampleViewRecord.Deserialize(File.ReadAllText(path,
            Encoding.UTF8));
}
