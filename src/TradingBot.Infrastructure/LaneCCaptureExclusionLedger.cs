using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

public sealed record LaneCCaptureExclusionEntry
{
    public string? SegmentId { get; }
    public long? FirstCaptureSequence { get; }
    public long? LastCaptureSequence { get; }
    public string? InstrumentId { get; }
    public string Source { get; }
    public DateTimeOffset? EarliestEventTimeUtc { get; }
    public DateTimeOffset? LatestEventTimeUtc { get; }
    public ImmutableValueList<string> ReasonCodes { get; }
    public string Provenance { get; }

    [JsonConstructor]
    public LaneCCaptureExclusionEntry(string? segmentId, long? firstCaptureSequence,
        long? lastCaptureSequence, string? instrumentId, string source,
        DateTimeOffset? earliestEventTimeUtc, DateTimeOffset? latestEventTimeUtc,
        ImmutableValueList<string> reasonCodes, string provenance)
    {
        if (segmentId is not null)
        {
            CaptureManifestMetadataGuard.ValidateSafeText(segmentId, nameof(segmentId));
            if (segmentId.Any(static character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.')))
            {
                throw new ArgumentException("Segment reference must be a safe identifier.",
                    nameof(segmentId));
            }
        }
        if ((firstCaptureSequence is null) != (lastCaptureSequence is null))
        {
            throw new ArgumentException("Capture sequence bounds must both be known or unknown.",
                nameof(firstCaptureSequence));
        }
        if (firstCaptureSequence is < 0 || lastCaptureSequence is < 0
            || firstCaptureSequence > lastCaptureSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(firstCaptureSequence));
        }
        if ((earliestEventTimeUtc is null) != (latestEventTimeUtc is null))
        {
            throw new ArgumentException("Event time bounds must both be known or unknown.",
                nameof(earliestEventTimeUtc));
        }
        if (earliestEventTimeUtc is { } earliest && latestEventTimeUtc is { } latest
            && earliest > latest)
        {
            throw new ArgumentException("Earliest event time cannot follow latest event time.",
                nameof(earliestEventTimeUtc));
        }
        if (reasonCodes is null || reasonCodes.Count == 0
            || reasonCodes.Any(static reason => string.IsNullOrWhiteSpace(reason)))
        {
            throw new ArgumentException("An exclusion requires non-empty reason codes.",
                nameof(reasonCodes));
        }
        foreach (string reason in reasonCodes)
        {
            CaptureManifestMetadataGuard.ValidateSafeText(reason, nameof(reasonCodes));
        }
        if (instrumentId is not null)
        {
            _ = new InstrumentId(instrumentId);
        }
        string safeSource = CaptureManifestMetadataGuard.ValidateSource(source);
        string safeProvenance = CaptureManifestMetadataGuard.ValidateSource(provenance);
        SegmentId = segmentId;
        FirstCaptureSequence = firstCaptureSequence;
        LastCaptureSequence = lastCaptureSequence;
        InstrumentId = instrumentId;
        Source = safeSource;
        EarliestEventTimeUtc = earliestEventTimeUtc?.ToUniversalTime();
        LatestEventTimeUtc = latestEventTimeUtc?.ToUniversalTime();
        ReasonCodes = new ImmutableValueList<string>(reasonCodes.Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));
        Provenance = safeProvenance;
    }

    public LaneCCaptureExclusionEntry(string? segmentId, long? firstCaptureSequence,
        long? lastCaptureSequence, string? instrumentId, string source,
        DateTimeOffset? earliestEventTimeUtc, DateTimeOffset? latestEventTimeUtc,
        IEnumerable<string> reasonCodes, string provenance)
        : this(segmentId, firstCaptureSequence, lastCaptureSequence, instrumentId, source,
            earliestEventTimeUtc, latestEventTimeUtc,
            new ImmutableValueList<string>(reasonCodes), provenance)
    {
    }
}

public sealed record LaneCCaptureExclusionLedger
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public string LedgerIdentity { get; }
    public string QualityReportIdentity { get; }
    public string ManifestId { get; }
    public CaptureManifestCheckpoint Checkpoint { get; }
    public CaptureManifestCoverage Coverage { get; }
    public ImmutableValueList<LaneCCaptureExclusionEntry> Exclusions { get; }
    public CaptureRetentionDryRunResult RetentionDryRun { get; }

    [JsonConstructor]
    public LaneCCaptureExclusionLedger(int formatVersion, string ledgerIdentity,
        string qualityReportIdentity, string manifestId, CaptureManifestCheckpoint checkpoint,
        CaptureManifestCoverage coverage,
        ImmutableValueList<LaneCCaptureExclusionEntry> exclusions,
        CaptureRetentionDryRunResult retentionDryRun)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualityReportIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(retentionDryRun);
        if (!Enum.IsDefined(coverage)
            || !StringComparer.Ordinal.Equals(ledgerIdentity,
                ComputeIdentity(qualityReportIdentity, manifestId, checkpoint, coverage,
                    exclusions, retentionDryRun)))
        {
            throw new ArgumentException("Exclusion ledger identity or coverage is invalid.",
                nameof(ledgerIdentity));
        }
        FormatVersion = formatVersion;
        LedgerIdentity = ledgerIdentity;
        QualityReportIdentity = qualityReportIdentity;
        ManifestId = manifestId;
        Checkpoint = checkpoint;
        Coverage = coverage;
        Exclusions = exclusions;
        RetentionDryRun = retentionDryRun;
    }

    public static string ComputeIdentity(string qualityReportIdentity, string manifestId,
        CaptureManifestCheckpoint checkpoint, CaptureManifestCoverage coverage,
        ImmutableValueList<LaneCCaptureExclusionEntry> exclusions,
        CaptureRetentionDryRunResult retentionDryRun)
    {
        string canonical = CoreJsonSerializer.Serialize(new
        {
            FormatVersion = CurrentFormatVersion,
            QualityReportIdentity = qualityReportIdentity,
            ManifestId = manifestId,
            Checkpoint = checkpoint,
            Coverage = coverage,
            Exclusions = exclusions,
            RetentionDryRun = retentionDryRun
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

public static class LaneCCaptureExclusionLedgerBuilder
{
    public static LaneCCaptureExclusionLedger Build(CaptureDatasetManifest manifest,
        LaneCQualityReport qualityReport, IEnumerable<LaneCCaptureExclusionEntry> exclusions,
        CaptureManifestRetentionPolicy retentionPolicy, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(qualityReport);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentNullException.ThrowIfNull(retentionPolicy);
        if (asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Retention evidence requires a UTC asOf value.",
                nameof(asOfUtc));
        }
        if (!StringComparer.Ordinal.Equals(manifest.ManifestId, qualityReport.ManifestId)
            || manifest.Checkpoint != qualityReport.Checkpoint)
        {
            throw new InvalidDataException(
                "Exclusion ledger report must match the manifest checkpoint.");
        }

        List<LaneCCaptureExclusionEntry> normalized = exclusions.Distinct()
            .OrderBy(static entry => entry.SegmentId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static entry => entry.FirstCaptureSequence ?? long.MinValue)
            .ThenBy(static entry => entry.LastCaptureSequence ?? long.MinValue)
            .ThenBy(static entry => entry.InstrumentId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Source, StringComparer.Ordinal)
            .ThenBy(static entry => entry.EarliestEventTimeUtc)
            .ThenBy(static entry => entry.LatestEventTimeUtc)
            .ThenBy(static entry => string.Join("|", entry.ReasonCodes), StringComparer.Ordinal)
            .ThenBy(static entry => entry.Provenance, StringComparer.Ordinal)
            .ToList();
        ValidateReferences(manifest, normalized);

        ImmutableValueList<LaneCCaptureExclusionEntry> entryList = new(normalized);
        CaptureRetentionDryRunResult dryRun = CaptureRetentionPlanner.DryRun(manifest,
            retentionPolicy, asOfUtc);
        string identity = LaneCCaptureExclusionLedger.ComputeIdentity(
            qualityReport.ReportIdentity, manifest.ManifestId, manifest.Checkpoint,
            manifest.Coverage, entryList, dryRun);
        return new LaneCCaptureExclusionLedger(
            LaneCCaptureExclusionLedger.CurrentFormatVersion, identity,
            qualityReport.ReportIdentity, manifest.ManifestId, manifest.Checkpoint,
            manifest.Coverage, entryList, dryRun);
    }

    internal static void ValidateReferences(CaptureDatasetManifest manifest,
        IEnumerable<LaneCCaptureExclusionEntry> entries)
    {
        HashSet<string> segmentIds = manifest.Segments.Select(static segment => segment.SegmentId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (LaneCCaptureExclusionEntry entry in entries)
        {
            if (entry.SegmentId is not null && !segmentIds.Contains(entry.SegmentId))
            {
                throw new InvalidDataException(
                    "Exclusion ledger contains a segment reference outside the manifest.");
            }
            if (entry.FirstCaptureSequence is long first
                && (manifest.Ordering.FirstCaptureSequence is not long manifestFirst
                    || manifest.Ordering.LastCaptureSequence is not long manifestLast
                    || first < manifestFirst || entry.LastCaptureSequence > manifestLast))
            {
                throw new InvalidDataException(
                    "Exclusion ledger capture sequence bounds fall outside the manifest.");
            }
            if (entry.SegmentId is not null && entry.FirstCaptureSequence is long segmentFirst)
            {
                CaptureManifestSegment segment = manifest.Segments.Single(item =>
                    StringComparer.Ordinal.Equals(item.SegmentId, entry.SegmentId));
                if (segmentFirst < segment.FirstCaptureSequence
                    || entry.LastCaptureSequence > segment.LastCaptureSequence)
                {
                    throw new InvalidDataException(
                        "Exclusion ledger sequence bounds fall outside the referenced segment.");
                }
            }
        }
    }
}

public sealed class LaneCCaptureExclusionLedgerStore
{
    public LaneCCaptureExclusionLedgerStore(string ledgerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerPath);
        LedgerPath = Path.GetFullPath(ledgerPath);
    }

    public string LedgerPath { get; }

    public async Task PersistAsync(LaneCCaptureExclusionLedger ledger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        string? directory = Path.GetDirectoryName(LedgerPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Ledger path has no parent directory.");
        }
        Directory.CreateDirectory(directory);
        string temporaryPath = LedgerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(CoreJsonSerializer.Serialize(ledger));
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(LedgerPath))
            {
                File.Replace(temporaryPath, LedgerPath, null);
            }
            else
            {
                File.Move(temporaryPath, LedgerPath);
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

    public async Task<LaneCCaptureExclusionLedger> ReadAsync(CaptureDatasetManifest manifest,
        LaneCQualityReport qualityReport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(qualityReport);
        if (!File.Exists(LedgerPath))
        {
            throw new FileNotFoundException("Exclusion ledger does not exist.", LedgerPath);
        }
        string json = await File.ReadAllTextAsync(LedgerPath, cancellationToken)
            .ConfigureAwait(false);
        LaneCCaptureExclusionLedger ledger =
            CoreJsonSerializer.Deserialize<LaneCCaptureExclusionLedger>(json);
        if (ledger.Coverage != manifest.Coverage)
        {
            throw new InvalidDataException(
                "Exclusion ledger coverage does not match the requested manifest.");
        }
        LaneCCaptureExclusionLedgerBuilder.ValidateReferences(manifest, ledger.Exclusions);
        if (!StringComparer.Ordinal.Equals(ledger.ManifestId, manifest.ManifestId)
            || ledger.Checkpoint != manifest.Checkpoint
            || !StringComparer.Ordinal.Equals(ledger.QualityReportIdentity,
                qualityReport.ReportIdentity)
            || !StringComparer.Ordinal.Equals(qualityReport.ManifestId, manifest.ManifestId)
            || qualityReport.Checkpoint != manifest.Checkpoint)
        {
            throw new InvalidDataException(
                "Exclusion ledger links do not match the requested manifest and report.");
        }
        return ledger;
    }
}
