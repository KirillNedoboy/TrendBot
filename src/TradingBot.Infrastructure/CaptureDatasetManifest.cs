using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>Whether the bounded capture says anything about complete venue history.</summary>
public enum CaptureManifestCoverage
{
    Unknown
}

/// <summary>Retention dry-run result state.</summary>
public enum CaptureRetentionStatus
{
    Unknown,
    NoCandidates,
    Candidates
}

/// <summary>Nullable retention limits. Null means that the project value is unknown.</summary>
public sealed record CaptureManifestRetentionPolicy
{
    [JsonConstructor]
    public CaptureManifestRetentionPolicy(TimeSpan? maxAge, long? maxTotalBytes)
    {
        if (maxAge is { } age && age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge,
                "A configured retention age must be positive when known.");
        }

        if (maxTotalBytes is { } bytes && bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), maxTotalBytes,
                "A configured retention byte limit must be positive when known.");
        }

        MaxAge = maxAge;
        MaxTotalBytes = maxTotalBytes;
    }

    public TimeSpan? MaxAge { get; }

    public long? MaxTotalBytes { get; }
}

/// <summary>Explicit capture and nullable retention bounds recorded in a manifest.</summary>
public sealed record CaptureManifestConfiguredBounds
{
    [JsonConstructor]
    public CaptureManifestConfiguredBounds(int maxRowsPerSegment, int maxBufferedRows,
        long maxSegmentBytes, CaptureManifestRetentionPolicy retentionPolicy)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRowsPerSegment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentBytes);
        ArgumentNullException.ThrowIfNull(retentionPolicy);

        MaxRowsPerSegment = maxRowsPerSegment;
        MaxBufferedRows = maxBufferedRows;
        MaxSegmentBytes = maxSegmentBytes;
        RetentionPolicy = retentionPolicy;
    }

    public int MaxRowsPerSegment { get; }

    public int MaxBufferedRows { get; }

    public long MaxSegmentBytes { get; }

    public CaptureManifestRetentionPolicy RetentionPolicy { get; }
}

/// <summary>Fixed durable checkpoint linked to a manifest.</summary>
public sealed record CaptureManifestCheckpoint
{
    [JsonConstructor]
    public CaptureManifestCheckpoint(string streamId, long nextCaptureSequence,
        long lastDurableSequence, string? lastSegmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegative(nextCaptureSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastDurableSequence, -1);
        if (lastDurableSequence >= nextCaptureSequence)
        {
            throw new ArgumentException(
                "The last durable capture sequence must precede the next sequence.",
                nameof(lastDurableSequence));
        }

        if (lastDurableSequence >= 0 && string.IsNullOrWhiteSpace(lastSegmentId))
        {
            throw new ArgumentException(
                "A durable capture sequence requires its segment identity.",
                nameof(lastSegmentId));
        }

        StreamId = streamId;
        NextCaptureSequence = nextCaptureSequence;
        LastDurableSequence = lastDurableSequence;
        LastSegmentId = lastSegmentId;
    }

    public string StreamId { get; }

    public long NextCaptureSequence { get; }

    public long LastDurableSequence { get; }

    public string? LastSegmentId { get; }

    public static CaptureManifestCheckpoint From(CaptureCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return new CaptureManifestCheckpoint(checkpoint.StreamId,
            checkpoint.NextCaptureSequence, checkpoint.LastDurableSequence,
            checkpoint.LastSegmentId);
    }
}

/// <summary>Allowlisted segment metadata; the local file path is deliberately omitted.</summary>
public sealed record CaptureManifestSegment
{
    [JsonConstructor]
    public CaptureManifestSegment(string segmentId, string streamId, int formatVersion,
        long rowCount, long firstCaptureSequence, long lastCaptureSequence,
        long firstStableSequence, long lastStableSequence, long byteLength,
        DateTimeOffset createdTimeUtc, DateTimeOffset durableTimeUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(formatVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(firstCaptureSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastCaptureSequence,
            firstCaptureSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(firstStableSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastStableSequence, firstStableSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);

        SegmentId = segmentId;
        StreamId = streamId;
        FormatVersion = formatVersion;
        RowCount = rowCount;
        FirstCaptureSequence = firstCaptureSequence;
        LastCaptureSequence = lastCaptureSequence;
        FirstStableSequence = firstStableSequence;
        LastStableSequence = lastStableSequence;
        ByteLength = byteLength;
        CreatedTimeUtc = createdTimeUtc.ToUniversalTime();
        DurableTimeUtc = durableTimeUtc.ToUniversalTime();
        if (DurableTimeUtc < CreatedTimeUtc)
        {
            throw new ArgumentException(
                "A durable segment time cannot precede its creation time.",
                nameof(durableTimeUtc));
        }
    }

    public string SegmentId { get; }

    public string StreamId { get; }

    public int FormatVersion { get; }

    public long RowCount { get; }

    public long FirstCaptureSequence { get; }

    public long LastCaptureSequence { get; }

    public long FirstStableSequence { get; }

    public long LastStableSequence { get; }

    public long ByteLength { get; }

    public DateTimeOffset CreatedTimeUtc { get; }

    public DateTimeOffset DurableTimeUtc { get; }

    public static CaptureManifestSegment From(CaptureSegmentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new CaptureManifestSegment(metadata.SegmentId, metadata.StreamId,
            metadata.FormatVersion, metadata.RowCount, metadata.FirstCaptureSequence,
            metadata.LastCaptureSequence, metadata.FirstStableSequence,
            metadata.LastStableSequence, metadata.ByteLength,
            ToUtc(metadata.CreatedTimeTicks), ToUtc(metadata.DurableTimeTicks));
    }

    private static DateTimeOffset ToUtc(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc));
}

/// <summary>Allowlisted source, instrument, and session provenance.</summary>
public sealed record CaptureManifestProvenance
{
    [JsonConstructor]
    public CaptureManifestProvenance(ImmutableValueList<string> sources,
        ImmutableValueList<string> sourceStreams, ImmutableValueList<string> instruments,
        ImmutableValueList<string> sessions)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(sourceStreams);
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(sessions);
        foreach (string source in sources)
        {
            CaptureManifestMetadataGuard.ValidateSource(source);
        }

        Sources = new ImmutableValueList<string>(sources);
        SourceStreams = new ImmutableValueList<string>(sourceStreams);
        Instruments = new ImmutableValueList<string>(instruments);
        Sessions = new ImmutableValueList<string>(sessions);
    }

    public CaptureManifestProvenance(IEnumerable<string> sources,
        IEnumerable<string> sourceStreams, IEnumerable<string> instruments,
        IEnumerable<string> sessions)
        : this(new ImmutableValueList<string>(sources),
            new ImmutableValueList<string>(sourceStreams),
            new ImmutableValueList<string>(instruments),
            new ImmutableValueList<string>(sessions))
    {
    }

    public ImmutableValueList<string> Sources { get; }

    public ImmutableValueList<string> SourceStreams { get; }

    public ImmutableValueList<string> Instruments { get; }

    public ImmutableValueList<string> Sessions { get; }
}

/// <summary>Observed event, receive, and availability bounds; null means unknown.</summary>
public sealed record CaptureManifestTimeBounds
{
    [JsonConstructor]
    public CaptureManifestTimeBounds(DateTimeOffset? earliestEventTime,
        DateTimeOffset? latestEventTime, DateTimeOffset? earliestReceiveTime,
        DateTimeOffset? latestReceiveTime, DateTimeOffset? earliestAvailabilityTime,
        DateTimeOffset? latestAvailabilityTime)
    {
        ValidatePair(earliestEventTime, latestEventTime, nameof(earliestEventTime));
        ValidatePair(earliestReceiveTime, latestReceiveTime, nameof(earliestReceiveTime));
        ValidatePair(earliestAvailabilityTime, latestAvailabilityTime,
            nameof(earliestAvailabilityTime));
        EarliestEventTime = ToUtc(earliestEventTime);
        LatestEventTime = ToUtc(latestEventTime);
        EarliestReceiveTime = ToUtc(earliestReceiveTime);
        LatestReceiveTime = ToUtc(latestReceiveTime);
        EarliestAvailabilityTime = ToUtc(earliestAvailabilityTime);
        LatestAvailabilityTime = ToUtc(latestAvailabilityTime);
    }

    public DateTimeOffset? EarliestEventTime { get; }

    public DateTimeOffset? LatestEventTime { get; }

    public DateTimeOffset? EarliestReceiveTime { get; }

    public DateTimeOffset? LatestReceiveTime { get; }

    public DateTimeOffset? EarliestAvailabilityTime { get; }

    public DateTimeOffset? LatestAvailabilityTime { get; }

    private static DateTimeOffset? ToUtc(DateTimeOffset? value) =>
        value?.ToUniversalTime();

    private static void ValidatePair(DateTimeOffset? earliest, DateTimeOffset? latest,
        string parameterName)
    {
        if (earliest is not null && latest is not null && earliest > latest)
        {
            throw new ArgumentException("An earliest bound cannot exceed its latest bound.",
                parameterName);
        }
    }
}

/// <summary>Sequence bounds and the explicit ordering statement for one manifest.</summary>
public sealed record CaptureManifestOrderingBounds
{
    public const string DefaultRule =
        "captureSequence ascending; stableSequence is retained; equal timestamps do not reorder rows";

    [JsonConstructor]
    public CaptureManifestOrderingBounds(long? firstCaptureSequence,
        long? lastCaptureSequence, long? firstStableSequence, long? lastStableSequence,
        string rule)
    {
        ValidateRange(firstCaptureSequence, lastCaptureSequence,
            nameof(firstCaptureSequence));
        ValidateRange(firstStableSequence, lastStableSequence,
            nameof(firstStableSequence));
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        FirstCaptureSequence = firstCaptureSequence;
        LastCaptureSequence = lastCaptureSequence;
        FirstStableSequence = firstStableSequence;
        LastStableSequence = lastStableSequence;
        Rule = rule;
    }

    public long? FirstCaptureSequence { get; }

    public long? LastCaptureSequence { get; }

    public long? FirstStableSequence { get; }

    public long? LastStableSequence { get; }

    public string Rule { get; }

    private static void ValidateRange(long? first, long? last, string parameterName)
    {
        if (first is < 0 || last is < 0 || first is not null && last is null
            || first is null && last is not null || first is not null && first > last)
        {
            throw new ArgumentException("Sequence bounds are invalid.", parameterName);
        }
    }
}

/// <summary>Explicitly recorded gap evidence, separate from exclusions.</summary>
public sealed record CaptureManifestGap
{
    [JsonConstructor]
    public CaptureManifestGap(string reason, string scope, string evidenceProvenance)
    {
        Reason = CaptureManifestMetadataGuard.ValidateSafeText(reason, nameof(reason));
        Scope = CaptureManifestMetadataGuard.ValidateSafeText(scope, nameof(scope));
        EvidenceProvenance = CaptureManifestMetadataGuard.ValidateSafeText(
            evidenceProvenance, nameof(evidenceProvenance));
    }

    public string Reason { get; }

    public string Scope { get; }

    public string EvidenceProvenance { get; }
}

/// <summary>Explicitly recorded excluded scope, separate from gaps.</summary>
public sealed record CaptureManifestExclusion
{
    [JsonConstructor]
    public CaptureManifestExclusion(string reason, string scope, string evidenceProvenance)
    {
        Reason = CaptureManifestMetadataGuard.ValidateSafeText(reason, nameof(reason));
        Scope = CaptureManifestMetadataGuard.ValidateSafeText(scope, nameof(scope));
        EvidenceProvenance = CaptureManifestMetadataGuard.ValidateSafeText(
            evidenceProvenance, nameof(evidenceProvenance));
    }

    public string Reason { get; }

    public string Scope { get; }

    public string EvidenceProvenance { get; }
}

/// <summary>Immutable, versioned allowlisted metadata for one durable capture checkpoint.</summary>
public sealed record CaptureDatasetManifest
{
    public const int CurrentFormatVersion = 1;

    [JsonConstructor]
    public CaptureDatasetManifest(int formatVersion, string manifestId, string streamId,
        CaptureManifestCheckpoint checkpoint, ImmutableValueList<CaptureManifestSegment> segments,
        long totalByteLength, long totalRecordCount, CaptureManifestProvenance provenance,
        CaptureManifestTimeBounds observedBounds, CaptureManifestOrderingBounds ordering,
        CaptureManifestConfiguredBounds configuredBounds, CaptureManifestCoverage coverage,
        ImmutableValueList<CaptureManifestGap> gaps,
        ImmutableValueList<CaptureManifestExclusion> exclusions)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The capture manifest format version is unsupported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(observedBounds);
        ArgumentNullException.ThrowIfNull(ordering);
        ArgumentNullException.ThrowIfNull(configuredBounds);
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(exclusions);
        ArgumentOutOfRangeException.ThrowIfNegative(totalByteLength);
        ArgumentOutOfRangeException.ThrowIfNegative(totalRecordCount);
        if (!StringComparer.Ordinal.Equals(streamId, checkpoint.StreamId))
        {
            throw new ArgumentException("The manifest stream and checkpoint stream differ.",
                nameof(checkpoint));
        }

        long expectedSequence = 0;
        long expectedBytes = 0;
        long expectedRows = 0;
        foreach (CaptureManifestSegment segment in segments)
        {
            if (!StringComparer.Ordinal.Equals(streamId, segment.StreamId))
            {
                throw new ArgumentException("A manifest segment belongs to another stream.",
                    nameof(segments));
            }

            if (segment.FirstCaptureSequence != expectedSequence)
            {
                throw new ArgumentException(
                    "Manifest capture segments must be contiguous from sequence zero.",
                    nameof(segments));
            }

            expectedSequence = checked(segment.LastCaptureSequence + 1);
            expectedRows = checked(expectedRows + segment.RowCount);
            expectedBytes = checked(expectedBytes + segment.ByteLength);
        }

        if (expectedRows != totalRecordCount || expectedBytes != totalByteLength
            || checkpoint.NextCaptureSequence != expectedSequence)
        {
            throw new ArgumentException(
                "Manifest totals and checkpoint do not match segment metadata.",
                nameof(checkpoint));
        }

        if (segments.Count == 0
            ? checkpoint.LastDurableSequence != -1 || checkpoint.LastSegmentId is not null
            : checkpoint.LastDurableSequence != segments[^1].LastCaptureSequence
                || !StringComparer.Ordinal.Equals(checkpoint.LastSegmentId,
                    segments[^1].SegmentId))
        {
            throw new ArgumentException(
                "Manifest checkpoint does not identify the final durable segment.",
                nameof(checkpoint));
        }

        if (!Enum.IsDefined(coverage))
        {
            throw new ArgumentOutOfRangeException(nameof(coverage), coverage,
                "The manifest coverage value is unsupported.");
        }

        FormatVersion = formatVersion;
        ManifestId = manifestId;
        StreamId = streamId;
        Checkpoint = checkpoint;
        Segments = new ImmutableValueList<CaptureManifestSegment>(segments);
        TotalByteLength = totalByteLength;
        TotalRecordCount = totalRecordCount;
        Provenance = provenance;
        ObservedBounds = observedBounds;
        Ordering = ordering;
        ConfiguredBounds = configuredBounds;
        Coverage = coverage;
        Gaps = new ImmutableValueList<CaptureManifestGap>(gaps);
        Exclusions = new ImmutableValueList<CaptureManifestExclusion>(exclusions);
    }

    public int FormatVersion { get; }

    public string ManifestId { get; }

    public string StreamId { get; }

    public CaptureManifestCheckpoint Checkpoint { get; }

    public ImmutableValueList<CaptureManifestSegment> Segments { get; }

    public long TotalByteLength { get; }

    public long TotalRecordCount { get; }

    public CaptureManifestProvenance Provenance { get; }

    public CaptureManifestTimeBounds ObservedBounds { get; }

    public CaptureManifestOrderingBounds Ordering { get; }

    public CaptureManifestConfiguredBounds ConfiguredBounds { get; }

    public CaptureManifestCoverage Coverage { get; }

    public ImmutableValueList<CaptureManifestGap> Gaps { get; }

    public ImmutableValueList<CaptureManifestExclusion> Exclusions { get; }
}

/// <summary>Explicit bounded read and metadata options for manifest construction.</summary>
public sealed record CaptureDatasetManifestBuildOptions
{
    [JsonConstructor]
    public CaptureDatasetManifestBuildOptions(string manifestId, long maxSegments,
        long maxRecords, CaptureManifestRetentionPolicy retentionPolicy,
        ImmutableValueList<CaptureManifestGap> gaps,
        ImmutableValueList<CaptureManifestExclusion> exclusions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);
        ArgumentNullException.ThrowIfNull(retentionPolicy);
        ArgumentNullException.ThrowIfNull(gaps);
        ArgumentNullException.ThrowIfNull(exclusions);
        ManifestId = manifestId;
        MaxSegments = maxSegments;
        MaxRecords = maxRecords;
        RetentionPolicy = retentionPolicy;
        Gaps = new ImmutableValueList<CaptureManifestGap>(gaps);
        Exclusions = new ImmutableValueList<CaptureManifestExclusion>(exclusions);
    }

    public CaptureDatasetManifestBuildOptions(string manifestId, long maxSegments,
        long maxRecords, CaptureManifestRetentionPolicy retentionPolicy,
        IEnumerable<CaptureManifestGap> gaps, IEnumerable<CaptureManifestExclusion> exclusions)
        : this(manifestId, maxSegments, maxRecords, retentionPolicy,
            new ImmutableValueList<CaptureManifestGap>(gaps),
            new ImmutableValueList<CaptureManifestExclusion>(exclusions))
    {
    }

    public string ManifestId { get; }

    public long MaxSegments { get; }

    public long MaxRecords { get; }

    public CaptureManifestRetentionPolicy RetentionPolicy { get; }

    public ImmutableValueList<CaptureManifestGap> Gaps { get; }

    public ImmutableValueList<CaptureManifestExclusion> Exclusions { get; }
}

/// <summary>Incremental manifest builder over durable Parquet segments.</summary>
public static class CaptureDatasetManifestBuilder
{
    public static async Task<CaptureDatasetManifest> BuildAsync(
        ParquetCaptureStore store, CaptureDatasetManifestBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        CaptureCheckpoint checkpoint = store.Checkpoint;
        IReadOnlyList<CaptureSegmentMetadata> metadata = store.Segments;
        if (metadata.Count > options.MaxSegments)
        {
            throw new InvalidOperationException(
                "The manifest segment read bound is smaller than the durable segment set.");
        }

        long metadataRows = 0;
        long totalBytes = 0;
        List<CaptureManifestSegment> segments = [];
        foreach (CaptureSegmentMetadata item in metadata)
        {
            metadataRows = checked(metadataRows + item.RowCount);
            totalBytes = checked(totalBytes + item.ByteLength);
            segments.Add(CaptureManifestSegment.From(item));
        }

        if (metadataRows > options.MaxRecords)
        {
            throw new InvalidOperationException(
                "The manifest record read bound is smaller than the durable record set.");
        }

        if (checkpoint.NextCaptureSequence != metadataRows)
        {
            throw new InvalidDataException(
                "The durable checkpoint does not match the segment record count.");
        }

        SortedSet<string> sources = new(StringComparer.Ordinal);
        SortedSet<string> sourceStreams = new(StringComparer.Ordinal);
        SortedSet<string> instruments = new(StringComparer.Ordinal);
        SortedSet<string> sessions = new(StringComparer.Ordinal);
        DateTimeOffset? earliestEvent = null;
        DateTimeOffset? latestEvent = null;
        DateTimeOffset? earliestReceive = null;
        DateTimeOffset? latestReceive = null;
        DateTimeOffset? earliestAvailability = null;
        DateTimeOffset? latestAvailability = null;
        long processedRows = 0;
        long? firstStable = null;
        long? lastStable = null;

        await foreach (CaptureEnvelope envelope in store.ReadSegmentsAsync(
            options.MaxSegments, options.MaxRecords, cancellationToken))
        {
            processedRows = checked(processedRows + 1);
            CaptureProvenance provenance = envelope.Provenance;
            sources.Add(CaptureManifestMetadataGuard.ValidateSource(provenance.Source));
            AddOptional(sourceStreams, provenance.StreamName);
            AddOptional(sessions, provenance.SessionId);
            if (envelope.Payload is not null)
            {
                instruments.Add(envelope.Payload.InstrumentId.Value);
            }

            DateTimeOffset eventTime = envelope.EventTime.Value.ToUniversalTime();
            DateTimeOffset receiveTime = envelope.ReceiveTime.Value.ToUniversalTime();
            DateTimeOffset availabilityTime = envelope.AvailabilityTime.Value.ToUniversalTime();
            earliestEvent = Min(earliestEvent, eventTime);
            latestEvent = Max(latestEvent, eventTime);
            earliestReceive = Min(earliestReceive, receiveTime);
            latestReceive = Max(latestReceive, receiveTime);
            earliestAvailability = Min(earliestAvailability, availabilityTime);
            latestAvailability = Max(latestAvailability, availabilityTime);
            firstStable = firstStable is null
                ? envelope.StableSequence
                : Math.Min(firstStable.Value, envelope.StableSequence);
            lastStable = lastStable is null
                ? envelope.StableSequence
                : Math.Max(lastStable.Value, envelope.StableSequence);
        }

        if (processedRows != metadataRows)
        {
            throw new InvalidDataException(
                "The incremental manifest read did not reach the durable record count.");
        }

        if (store.Checkpoint != checkpoint)
        {
            throw new InvalidDataException(
                "The durable checkpoint changed while the manifest was being built.");
        }

        CaptureManifestOrderingBounds ordering = new(
            segments.Count == 0 ? null : segments[0].FirstCaptureSequence,
            segments.Count == 0 ? null : segments[^1].LastCaptureSequence,
            firstStable, lastStable, CaptureManifestOrderingBounds.DefaultRule);
        CaptureManifestTimeBounds observed = new(earliestEvent, latestEvent,
            earliestReceive, latestReceive, earliestAvailability, latestAvailability);
        CaptureManifestConfiguredBounds configured = new(store.Options.MaxRowsPerSegment,
            store.Options.MaxBufferedRows, store.Options.MaxSegmentBytes,
            options.RetentionPolicy);
        return new CaptureDatasetManifest(CaptureDatasetManifest.CurrentFormatVersion,
            options.ManifestId, checkpoint.StreamId, CaptureManifestCheckpoint.From(checkpoint),
            new ImmutableValueList<CaptureManifestSegment>(segments), totalBytes, metadataRows,
            new CaptureManifestProvenance(sources, sourceStreams, instruments, sessions),
            observed, ordering, configured, CaptureManifestCoverage.Unknown, options.Gaps,
            options.Exclusions);
    }

    private static DateTimeOffset Min(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is null || candidate < current ? candidate : current.Value;

    private static DateTimeOffset Max(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is null || candidate > current ? candidate : current.Value;

    private static void AddOptional(SortedSet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }
}

/// <summary>Atomic JSON persistence for a manifest file; no database schema is changed.</summary>
public sealed class CaptureDatasetManifestStore
{
    public CaptureDatasetManifestStore(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ManifestPath = Path.GetFullPath(manifestPath);
    }

    public string ManifestPath { get; }

    public async Task PersistAsync(CaptureDatasetManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string? directory = Path.GetDirectoryName(ManifestPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The manifest path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = CoreJsonSerializer.Serialize(manifest);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(ManifestPath))
            {
                File.Replace(temporaryPath, ManifestPath, null);
            }
            else
            {
                File.Move(temporaryPath, ManifestPath);
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

    public async Task<CaptureDatasetManifest> ReadAsync(ParquetCaptureStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        return await ReadAsync(CaptureManifestCheckpoint.From(store.Checkpoint),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CaptureDatasetManifest> ReadAsync(CaptureManifestCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!File.Exists(ManifestPath))
        {
            throw new FileNotFoundException("The capture manifest file does not exist.",
                ManifestPath);
        }

        string json = await File.ReadAllTextAsync(ManifestPath, cancellationToken)
            .ConfigureAwait(false);
        CaptureDatasetManifest manifest = CoreJsonSerializer.Deserialize<CaptureDatasetManifest>(
            json);
        if (!StringComparer.Ordinal.Equals(manifest.StreamId, checkpoint.StreamId)
            || manifest.Checkpoint != checkpoint)
        {
            throw new InvalidDataException(
                "The manifest is not pinned to the requested durable checkpoint.");
        }

        return manifest;
    }
}

/// <summary>One retention candidate and the reasons it would be selected.</summary>
public sealed record CaptureRetentionCandidate
{
    [JsonConstructor]
    public CaptureRetentionCandidate(string segmentId, long firstCaptureSequence,
        DateTimeOffset durableTimeUtc, long byteLength, ImmutableValueList<string> reasons)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentOutOfRangeException.ThrowIfNegative(firstCaptureSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons.Count == 0)
        {
            throw new ArgumentException("A retention candidate needs a reason.", nameof(reasons));
        }

        SegmentId = segmentId;
        FirstCaptureSequence = firstCaptureSequence;
        DurableTimeUtc = durableTimeUtc.ToUniversalTime();
        ByteLength = byteLength;
        Reasons = new ImmutableValueList<string>(reasons);
    }

    public string SegmentId { get; }

    public long FirstCaptureSequence { get; }

    public DateTimeOffset DurableTimeUtc { get; }

    public long ByteLength { get; }

    public ImmutableValueList<string> Reasons { get; }
}

/// <summary>Result of a retention dry-run; it never deletes files or changes checkpoints.</summary>
public sealed record CaptureRetentionDryRunResult
{
    [JsonConstructor]
    public CaptureRetentionDryRunResult(CaptureRetentionStatus status,
        DateTimeOffset asOfUtc, CaptureManifestRetentionPolicy policy,
        ImmutableValueList<CaptureRetentionCandidate> candidates, string explanation)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        Status = status;
        AsOfUtc = asOfUtc.ToUniversalTime();
        Policy = policy;
        Candidates = new ImmutableValueList<CaptureRetentionCandidate>(candidates);
        Explanation = explanation;
    }

    public CaptureRetentionStatus Status { get; }

    public DateTimeOffset AsOfUtc { get; }

    public CaptureManifestRetentionPolicy Policy { get; }

    public ImmutableValueList<CaptureRetentionCandidate> Candidates { get; }

    public string Explanation { get; }
}

/// <summary>Pure bounded retention planning. Deletion and checkpoint mutation are absent.</summary>
public static class CaptureRetentionPlanner
{
    public static CaptureRetentionDryRunResult DryRun(CaptureDatasetManifest manifest,
        CaptureManifestRetentionPolicy policy, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(policy);
        if (asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Retention dry-runs require a UTC asOf value.",
                nameof(asOfUtc));
        }

        if (policy.MaxAge is null && policy.MaxTotalBytes is null)
        {
            return new CaptureRetentionDryRunResult(CaptureRetentionStatus.Unknown, asOfUtc,
                policy, new ImmutableValueList<CaptureRetentionCandidate>([]),
                "Retention age and total-byte limits are both unknown.");
        }

        Dictionary<string, List<string>> reasons = new(StringComparer.Ordinal);
        if (policy.MaxAge is { } maxAge)
        {
            DateTimeOffset cutoff = asOfUtc - maxAge;
            foreach (CaptureManifestSegment segment in manifest.Segments)
            {
                if (segment.DurableTimeUtc <= cutoff)
                {
                    AddReason(reasons, segment.SegmentId, "max-age");
                }
            }
        }

        if (policy.MaxTotalBytes is { } maxTotalBytes
            && manifest.TotalByteLength > maxTotalBytes)
        {
            long remaining = manifest.TotalByteLength;
            IEnumerable<CaptureManifestSegment> oldest = manifest.Segments
                .OrderBy(static segment => segment.DurableTimeUtc)
                .ThenBy(static segment => segment.FirstCaptureSequence);
            foreach (CaptureManifestSegment segment in oldest)
            {
                if (remaining <= maxTotalBytes)
                {
                    break;
                }

                AddReason(reasons, segment.SegmentId, "max-total-bytes");
                remaining -= segment.ByteLength;
            }
        }

        List<CaptureRetentionCandidate> candidates = manifest.Segments
            .Where(segment => reasons.ContainsKey(segment.SegmentId))
            .OrderBy(static segment => segment.DurableTimeUtc)
            .ThenBy(static segment => segment.FirstCaptureSequence)
            .Select(segment => new CaptureRetentionCandidate(segment.SegmentId,
                segment.FirstCaptureSequence, segment.DurableTimeUtc, segment.ByteLength,
                new ImmutableValueList<string>(reasons[segment.SegmentId])))
            .ToList();
        CaptureRetentionStatus status = candidates.Count == 0
            ? CaptureRetentionStatus.NoCandidates
            : CaptureRetentionStatus.Candidates;
        string explanation = candidates.Count == 0
            ? "Known retention limits selected no segments."
            : "Dry-run selected candidates and performed no deletion or checkpoint mutation.";
        return new CaptureRetentionDryRunResult(status, asOfUtc, policy,
            new ImmutableValueList<CaptureRetentionCandidate>(candidates), explanation);
    }

    private static void AddReason(Dictionary<string, List<string>> reasons,
        string segmentId, string reason)
    {
        if (!reasons.TryGetValue(segmentId, out List<string>? values))
        {
            values = [];
            reasons.Add(segmentId, values);
        }

        if (!values.Contains(reason, StringComparer.Ordinal))
        {
            values.Add(reason);
        }
    }
}

/// <summary>Validates source and evidence strings before manifest export.</summary>
public static class CaptureManifestMetadataGuard
{
    private static readonly HashSet<string> SensitiveQueryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "authorization", "access_token", "key", "listenkey",
        "password", "secret", "signature", "token"
    };

    public static string ValidateSource(string source)
    {
        string value = ValidateSafeText(source, nameof(source));
        if (Path.IsPathFullyQualified(value) || value.StartsWith("file:",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Absolute local paths are not allowed in manifest source metadata.",
                nameof(source));
        }

        if (value.Contains("listenKey", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "listenKey-like private values are not allowed in manifest source metadata.",
                nameof(source));
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            if (uri.UserInfo.Length > 0)
            {
                throw new ArgumentException(
                    "URI user information is not allowed in manifest source metadata.",
                    nameof(source));
            }

            foreach (string key in ReadQueryKeys(uri.Query))
            {
                if (SensitiveQueryKeys.Contains(key))
                {
                    throw new ArgumentException(
                        "Sensitive URI query keys are not allowed in manifest source metadata.",
                        nameof(source));
                }
            }
        }

        return value;
    }

    public static string ValidateSafeText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n') || value.Contains('{')
            || value.Contains('}') || value.Contains('[') || value.Contains(']'))
        {
            throw new ArgumentException(
                "Manifest metadata must be a bounded non-JSON text value.", parameterName);
        }

        return value;
    }

    private static IEnumerable<string> ReadQueryKeys(string query)
    {
        string trimmed = query.TrimStart('?');
        if (trimmed.Length == 0)
        {
            yield break;
        }

        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = pair.Split('=', 2)[0];
            yield return Uri.UnescapeDataString(key);
        }
    }
}
