using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

public sealed record LaneCQualityReport
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string ReportIdentity { get; }

    public string ManifestId { get; }

    public CaptureManifestCheckpoint Checkpoint { get; }

    public LaneCQualityEvidence Quality { get; }

    public CaptureQualitySnapshot CaptureQuality { get; }

    [JsonConstructor]
    public LaneCQualityReport(int formatVersion, string reportIdentity, string manifestId,
        CaptureManifestCheckpoint checkpoint, LaneCQualityEvidence quality,
        CaptureQualitySnapshot captureQuality)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(captureQuality);
        if (!StringComparer.Ordinal.Equals(manifestId, captureQuality.ManifestId)
            || checkpoint != captureQuality.Checkpoint
            || !StringComparer.Ordinal.Equals(reportIdentity,
                ComputeIdentity(manifestId, checkpoint, quality, captureQuality)))
        {
            throw new ArgumentException("Quality report identity or checkpoint is inconsistent.",
                nameof(reportIdentity));
        }
        FormatVersion = formatVersion;
        ReportIdentity = reportIdentity;
        ManifestId = manifestId;
        Checkpoint = checkpoint;
        Quality = quality;
        CaptureQuality = captureQuality;
    }

    public static string ComputeIdentity(string manifestId,
        CaptureManifestCheckpoint checkpoint, LaneCQualityEvidence quality,
        CaptureQualitySnapshot captureQuality)
    {
        string canonical = CoreJsonSerializer.Serialize(new
        {
            FormatVersion = CurrentFormatVersion,
            ManifestId = manifestId,
            Checkpoint = checkpoint,
            Quality = quality,
            CaptureQuality = captureQuality
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

public static class LaneCQualityReportBuilder
{
    public static async Task<LaneCQualityReport> BuildAsync(ParquetCaptureStore store,
        CaptureDatasetManifest manifest, LaneCFeatureInput input,
        LaneCFeatureConfiguration configuration, LaneCFeatureResult features,
        LaneCQualityPolicy policy, CaptureQualitySnapshotBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        if (!StringComparer.Ordinal.Equals(store.Checkpoint.StreamId, manifest.StreamId)
            || !Matches(store.Checkpoint, manifest.Checkpoint))
        {
            throw new InvalidDataException(
                "Quality report capture store must match the manifest checkpoint.");
        }
        if (input.Cursor.AsOf.Value != options.AsOfUtc)
        {
            throw new InvalidDataException(
                "Quality report options must use the feature causal cursor time.");
        }

        List<CaptureEnvelope> durableRows = [];
        await foreach (CaptureEnvelope row in store.ReadSegmentsAsync(options.MaxSegments,
            options.MaxRecords, cancellationToken))
        {
            durableRows.Add(row);
        }
        if (durableRows.Count != manifest.TotalRecordCount
            || !Matches(store.Checkpoint, manifest.Checkpoint))
        {
            throw new InvalidDataException(
                "Quality report bounded read did not match the durable manifest.");
        }

        Dictionary<long, CaptureEnvelope> bySequence = durableRows.ToDictionary(
            static row => row.CaptureSequence);
        foreach (CaptureEnvelope inputRow in input.Records)
        {
            if (!bySequence.TryGetValue(inputRow.CaptureSequence, out CaptureEnvelope? durable)
                || !StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(inputRow),
                    CoreJsonSerializer.Serialize(durable)))
            {
                throw new InvalidDataException(
                    "Quality report feature input is not present in durable capture history.");
            }
        }

        LaneCQualityEvidence quality = LaneCQualityEvaluator.Evaluate(input, configuration,
            features, policy);
        CaptureQualitySnapshot captureQuality =
            await CaptureQualitySnapshotBuilder.BuildAsync(store, manifest, options,
                cancellationToken);
        string identity = LaneCQualityReport.ComputeIdentity(manifest.ManifestId,
            manifest.Checkpoint, quality, captureQuality);
        return new LaneCQualityReport(LaneCQualityReport.CurrentFormatVersion, identity,
            manifest.ManifestId, manifest.Checkpoint, quality, captureQuality);
    }

    private static bool Matches(CaptureCheckpoint current,
        CaptureManifestCheckpoint expected) =>
        StringComparer.Ordinal.Equals(current.StreamId, expected.StreamId)
        && current.NextCaptureSequence == expected.NextCaptureSequence
        && current.LastDurableSequence == expected.LastDurableSequence
        && StringComparer.Ordinal.Equals(current.LastSegmentId, expected.LastSegmentId);
}

public sealed class LaneCQualityReportStore
{
    public LaneCQualityReportStore(string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ReportPath = Path.GetFullPath(reportPath);
    }

    public string ReportPath { get; }

    public async Task PersistAsync(LaneCQualityReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        string? directory = Path.GetDirectoryName(ReportPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Quality report path has no parent directory.");
        }
        Directory.CreateDirectory(directory);
        string temporaryPath = ReportPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(CoreJsonSerializer.Serialize(report));
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(ReportPath))
            {
                File.Replace(temporaryPath, ReportPath, null);
            }
            else
            {
                File.Move(temporaryPath, ReportPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<LaneCQualityReport> ReadAsync(CaptureDatasetManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!File.Exists(ReportPath))
        {
            throw new FileNotFoundException("Quality report does not exist.", ReportPath);
        }
        string json = await File.ReadAllTextAsync(ReportPath, cancellationToken)
            .ConfigureAwait(false);
        LaneCQualityReport report = CoreJsonSerializer.Deserialize<LaneCQualityReport>(json);
        if (!StringComparer.Ordinal.Equals(report.ManifestId, manifest.ManifestId)
            || report.Checkpoint != manifest.Checkpoint)
        {
            throw new InvalidDataException(
                "Quality report does not match the requested manifest checkpoint.");
        }
        return report;
    }
}
