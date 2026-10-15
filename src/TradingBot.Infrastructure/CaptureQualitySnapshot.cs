using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>Freshness assessment from an explicit caller threshold.</summary>
public enum CaptureFreshnessStatus
{
    Unknown,
    Fresh,
    Stale
}

/// <summary>Provider-silence assessment from an explicit caller threshold.</summary>
public enum CaptureProviderSilenceStatus
{
    Unknown,
    Active,
    Silent
}

/// <summary>Clock-skew assessment from explicit quality evidence and a threshold.</summary>
public enum CaptureClockSkewStatus
{
    Unknown,
    Clear,
    Signaled
}

/// <summary>Observed event-to-receive latency summary; it does not infer clock skew.</summary>
public sealed record CaptureLatencySummary
{
    [JsonConstructor]
    public CaptureLatencySummary(long sampleCount, TimeSpan? minimum, TimeSpan? maximum,
        TimeSpan? average)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        if (sampleCount == 0 && (minimum is not null || maximum is not null || average is not null)
            || sampleCount > 0 && (minimum is null || maximum is null || average is null))
        {
            throw new ArgumentException(
                "Latency bounds must be present exactly when samples are present.");
        }

        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException("Latency minimum cannot exceed maximum.",
                nameof(minimum));
        }

        SampleCount = sampleCount;
        Minimum = minimum;
        Maximum = maximum;
        Average = average;
    }

    public long SampleCount { get; }

    public TimeSpan? Minimum { get; }

    public TimeSpan? Maximum { get; }

    public TimeSpan? Average { get; }
}

/// <summary>Explicit as-of time, bounded read limits, and caller quality thresholds.</summary>
public sealed record CaptureQualitySnapshotBuildOptions
{
    [JsonConstructor]
    public CaptureQualitySnapshotBuildOptions(DateTimeOffset asOfUtc, long maxSegments,
        long maxRecords, TimeSpan? freshnessThreshold,
        TimeSpan? providerSilenceThreshold, TimeSpan? clockSkewThreshold)
    {
        if (asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Quality snapshots require a UTC asOf value.",
                nameof(asOfUtc));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);
        ValidateThreshold(freshnessThreshold, nameof(freshnessThreshold));
        ValidateThreshold(providerSilenceThreshold, nameof(providerSilenceThreshold));
        ValidateThreshold(clockSkewThreshold, nameof(clockSkewThreshold));

        AsOfUtc = asOfUtc;
        MaxSegments = maxSegments;
        MaxRecords = maxRecords;
        FreshnessThreshold = freshnessThreshold;
        ProviderSilenceThreshold = providerSilenceThreshold;
        ClockSkewThreshold = clockSkewThreshold;
    }

    public DateTimeOffset AsOfUtc { get; }

    public long MaxSegments { get; }

    public long MaxRecords { get; }

    public TimeSpan? FreshnessThreshold { get; }

    public TimeSpan? ProviderSilenceThreshold { get; }

    public TimeSpan? ClockSkewThreshold { get; }

    private static void ValidateThreshold(TimeSpan? threshold, string parameterName)
    {
        if (threshold is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, threshold,
                "A known quality threshold must be positive.");
        }
    }
}

/// <summary>Immutable quality evidence for one manifest checkpoint.</summary>
public sealed record CaptureQualitySnapshot
{
    public const int CurrentFormatVersion = 1;

    [JsonConstructor]
    public CaptureQualitySnapshot(int formatVersion, string manifestId,
        CaptureManifestCheckpoint checkpoint, DateTimeOffset asOfUtc, long recordCount,
        long gapCount, long duplicateCount, long missingFieldCount, long clockSkewCount,
        long staleCount, long unknownDiagnosticCodeCount, CaptureLatencySummary latency,
        DateTimeOffset? lastReceiveTimeUtc, TimeSpan? freshnessThreshold,
        TimeSpan? providerSilenceThreshold, TimeSpan? clockSkewThreshold,
        CaptureFreshnessStatus freshnessStatus,
        CaptureProviderSilenceStatus providerSilenceStatus,
        CaptureClockSkewStatus clockSkewStatus)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The quality snapshot format version is unsupported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(latency);
        if (asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Quality snapshots require a UTC asOf value.",
                nameof(asOfUtc));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        ValidateCount(gapCount, recordCount, nameof(gapCount));
        ValidateCount(duplicateCount, recordCount, nameof(duplicateCount));
        ValidateCount(missingFieldCount, recordCount, nameof(missingFieldCount));
        ValidateCount(clockSkewCount, recordCount, nameof(clockSkewCount));
        ValidateCount(staleCount, recordCount, nameof(staleCount));
        ValidateCount(unknownDiagnosticCodeCount, recordCount,
            nameof(unknownDiagnosticCodeCount));
        ValidateThreshold(freshnessThreshold, nameof(freshnessThreshold));
        ValidateThreshold(providerSilenceThreshold, nameof(providerSilenceThreshold));
        ValidateThreshold(clockSkewThreshold, nameof(clockSkewThreshold));
        if (!Enum.IsDefined(freshnessStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(freshnessStatus), freshnessStatus,
                "The freshness status is unsupported.");
        }

        if (!Enum.IsDefined(providerSilenceStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(providerSilenceStatus),
                providerSilenceStatus, "The provider-silence status is unsupported.");
        }

        if (!Enum.IsDefined(clockSkewStatus))
        {
            throw new ArgumentOutOfRangeException(nameof(clockSkewStatus), clockSkewStatus,
                "The clock-skew status is unsupported.");
        }

        FormatVersion = formatVersion;
        ManifestId = manifestId;
        Checkpoint = checkpoint;
        AsOfUtc = asOfUtc;
        RecordCount = recordCount;
        GapCount = gapCount;
        DuplicateCount = duplicateCount;
        MissingFieldCount = missingFieldCount;
        ClockSkewCount = clockSkewCount;
        StaleCount = staleCount;
        UnknownDiagnosticCodeCount = unknownDiagnosticCodeCount;
        Latency = latency;
        LastReceiveTimeUtc = lastReceiveTimeUtc?.ToUniversalTime();
        FreshnessThreshold = freshnessThreshold;
        ProviderSilenceThreshold = providerSilenceThreshold;
        ClockSkewThreshold = clockSkewThreshold;
        FreshnessStatus = freshnessStatus;
        ProviderSilenceStatus = providerSilenceStatus;
        ClockSkewStatus = clockSkewStatus;
    }

    public int FormatVersion { get; }

    public string ManifestId { get; }

    public CaptureManifestCheckpoint Checkpoint { get; }

    public DateTimeOffset AsOfUtc { get; }

    public long RecordCount { get; }

    public long GapCount { get; }

    public long DuplicateCount { get; }

    public long MissingFieldCount { get; }

    public long ClockSkewCount { get; }

    public long StaleCount { get; }

    public long UnknownDiagnosticCodeCount { get; }

    public CaptureLatencySummary Latency { get; }

    public DateTimeOffset? LastReceiveTimeUtc { get; }

    public TimeSpan? FreshnessThreshold { get; }

    public TimeSpan? ProviderSilenceThreshold { get; }

    public TimeSpan? ClockSkewThreshold { get; }

    public CaptureFreshnessStatus FreshnessStatus { get; }

    public CaptureProviderSilenceStatus ProviderSilenceStatus { get; }

    public CaptureClockSkewStatus ClockSkewStatus { get; }

    private static void ValidateCount(long value, long recordCount, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (value > recordCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, value,
                "A quality signal count cannot exceed the record count.");
        }
    }

    private static void ValidateThreshold(TimeSpan? threshold, string parameterName)
    {
        if (threshold is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, threshold,
                "A known quality threshold must be positive.");
        }
    }
}

/// <summary>Builds a quality snapshot from durable records at one manifest checkpoint.</summary>
public static class CaptureQualitySnapshotBuilder
{
    private enum QualitySignal
    {
        Gap,
        Duplicate,
        MissingField,
        ClockSkew,
        Stale
    }

    public static async Task<CaptureQualitySnapshot> BuildAsync(
        ParquetCaptureStore store, CaptureDatasetManifest manifest,
        CaptureQualitySnapshotBuildOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        if (!StringComparer.Ordinal.Equals(store.Checkpoint.StreamId, manifest.StreamId)
            || !IsSameCheckpoint(store.Checkpoint, manifest.Checkpoint))
        {
            throw new InvalidDataException(
                "The quality snapshot store is not at the manifest checkpoint.");
        }

        long recordCount = 0;
        long gapCount = 0;
        long duplicateCount = 0;
        long missingFieldCount = 0;
        long clockSkewCount = 0;
        long staleCount = 0;
        long unknownDiagnosticCodeCount = 0;
        DateTimeOffset? lastReceive = null;
        TimeSpan? minimumLatency = null;
        TimeSpan? maximumLatency = null;
        long totalLatencyTicks = 0;

        await foreach (CaptureEnvelope envelope in store.ReadSegmentsAsync(
            options.MaxSegments, options.MaxRecords, cancellationToken))
        {
            recordCount = checked(recordCount + 1);
            HashSet<QualitySignal> signals = [];
            foreach (string flag in envelope.Provenance.QualityFlags)
            {
                if (TryMap(flag, out QualitySignal signal))
                {
                    signals.Add(signal);
                }
            }

            if (envelope.Diagnostic is { } diagnostic)
            {
                if (TryMap(diagnostic.Code, out QualitySignal signal))
                {
                    signals.Add(signal);
                }
                else
                {
                    unknownDiagnosticCodeCount = checked(unknownDiagnosticCodeCount + 1);
                }
            }

            if (signals.Contains(QualitySignal.Gap))
            {
                gapCount = checked(gapCount + 1);
            }

            if (signals.Contains(QualitySignal.Duplicate))
            {
                duplicateCount = checked(duplicateCount + 1);
            }

            if (signals.Contains(QualitySignal.MissingField))
            {
                missingFieldCount = checked(missingFieldCount + 1);
            }

            if (signals.Contains(QualitySignal.ClockSkew))
            {
                clockSkewCount = checked(clockSkewCount + 1);
            }

            if (signals.Contains(QualitySignal.Stale))
            {
                staleCount = checked(staleCount + 1);
            }

            DateTimeOffset receiveTime = envelope.ReceiveTime.Value.ToUniversalTime();
            lastReceive = lastReceive is null || receiveTime > lastReceive
                ? receiveTime
                : lastReceive;
            TimeSpan latency = receiveTime - envelope.EventTime.Value.ToUniversalTime();
            minimumLatency = minimumLatency is null || latency < minimumLatency
                ? latency
                : minimumLatency;
            maximumLatency = maximumLatency is null || latency > maximumLatency
                ? latency
                : maximumLatency;
            totalLatencyTicks = checked(totalLatencyTicks + latency.Ticks);
        }

        if (recordCount != manifest.TotalRecordCount
            || !IsSameCheckpoint(store.Checkpoint, manifest.Checkpoint))
        {
            throw new InvalidDataException(
                "The quality snapshot read did not remain at the manifest checkpoint.");
        }

        CaptureLatencySummary latencySummary = recordCount == 0
            ? new CaptureLatencySummary(0, null, null, null)
            : new CaptureLatencySummary(recordCount, minimumLatency, maximumLatency,
                new TimeSpan(totalLatencyTicks / recordCount));
        CaptureFreshnessStatus freshness = GetFreshness(lastReceive, options.AsOfUtc,
            options.FreshnessThreshold);
        CaptureProviderSilenceStatus silence = GetSilence(lastReceive, options.AsOfUtc,
            options.ProviderSilenceThreshold);
        CaptureClockSkewStatus skew = options.ClockSkewThreshold is null
            ? CaptureClockSkewStatus.Unknown
            : clockSkewCount == 0
                ? CaptureClockSkewStatus.Clear
                : CaptureClockSkewStatus.Signaled;

        return new CaptureQualitySnapshot(CaptureQualitySnapshot.CurrentFormatVersion,
            manifest.ManifestId, manifest.Checkpoint, options.AsOfUtc, recordCount,
            gapCount, duplicateCount, missingFieldCount, clockSkewCount, staleCount,
            unknownDiagnosticCodeCount, latencySummary, lastReceive,
            options.FreshnessThreshold, options.ProviderSilenceThreshold,
            options.ClockSkewThreshold, freshness, silence, skew);
    }

    private static CaptureFreshnessStatus GetFreshness(DateTimeOffset? lastReceive,
        DateTimeOffset asOfUtc, TimeSpan? threshold)
    {
        if (threshold is null || lastReceive is null)
        {
            return CaptureFreshnessStatus.Unknown;
        }

        return asOfUtc - lastReceive.Value > threshold.Value
            ? CaptureFreshnessStatus.Stale
            : CaptureFreshnessStatus.Fresh;
    }

    private static CaptureProviderSilenceStatus GetSilence(DateTimeOffset? lastReceive,
        DateTimeOffset asOfUtc, TimeSpan? threshold)
    {
        if (threshold is null)
        {
            return CaptureProviderSilenceStatus.Unknown;
        }

        if (lastReceive is null || asOfUtc - lastReceive.Value > threshold.Value)
        {
            return CaptureProviderSilenceStatus.Silent;
        }

        return CaptureProviderSilenceStatus.Active;
    }

    private static bool IsSameCheckpoint(CaptureCheckpoint current,
        CaptureManifestCheckpoint expected) =>
        StringComparer.Ordinal.Equals(current.StreamId, expected.StreamId)
        && current.NextCaptureSequence == expected.NextCaptureSequence
        && current.LastDurableSequence == expected.LastDurableSequence
        && StringComparer.Ordinal.Equals(current.LastSegmentId, expected.LastSegmentId);

    private static bool TryMap(string value, out QualitySignal signal)
    {
        if (value.Equals("gap", StringComparison.OrdinalIgnoreCase))
        {
            signal = QualitySignal.Gap;
            return true;
        }

        if (value.Equals("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            signal = QualitySignal.Duplicate;
            return true;
        }

        if (value.Equals("missing-field", StringComparison.OrdinalIgnoreCase))
        {
            signal = QualitySignal.MissingField;
            return true;
        }

        if (value.Equals("clock-skew", StringComparison.OrdinalIgnoreCase))
        {
            signal = QualitySignal.ClockSkew;
            return true;
        }

        if (value.Equals("stale", StringComparison.OrdinalIgnoreCase))
        {
            signal = QualitySignal.Stale;
            return true;
        }

        signal = default;
        return false;
    }
}

/// <summary>Atomic JSON persistence for quality evidence linked to a manifest checkpoint.</summary>
public sealed class CaptureQualitySnapshotStore
{
    public CaptureQualitySnapshotStore(string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        SnapshotPath = Path.GetFullPath(snapshotPath);
    }

    public string SnapshotPath { get; }

    public async Task PersistAsync(CaptureQualitySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? directory = Path.GetDirectoryName(SnapshotPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The quality snapshot path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = SnapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(CoreJsonSerializer.Serialize(snapshot));
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(SnapshotPath))
            {
                File.Replace(temporaryPath, SnapshotPath, null);
            }
            else
            {
                File.Move(temporaryPath, SnapshotPath);
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

    public async Task<CaptureQualitySnapshot> ReadAsync(
        CaptureDatasetManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!File.Exists(SnapshotPath))
        {
            throw new FileNotFoundException("The quality snapshot file does not exist.",
                SnapshotPath);
        }

        string json = await File.ReadAllTextAsync(SnapshotPath, cancellationToken)
            .ConfigureAwait(false);
        CaptureQualitySnapshot snapshot = CoreJsonSerializer.Deserialize<CaptureQualitySnapshot>(
            json);
        if (!StringComparer.Ordinal.Equals(snapshot.ManifestId, manifest.ManifestId)
            || snapshot.Checkpoint != manifest.Checkpoint)
        {
            throw new InvalidDataException(
                "The quality snapshot is not linked to the requested manifest checkpoint.");
        }

        return snapshot;
    }
}

/// <summary>Idempotent BCL metrics publisher for one quality snapshot.</summary>
public sealed class CaptureQualityMetricsPublisher : IDisposable
{
    public const string MeterName = "TradingBot.Infrastructure.CaptureQuality";

    private readonly Meter _meter = new(MeterName, "1.0");
    private readonly Counter<long> _recordCounter;
    private readonly Counter<long> _gapCounter;
    private readonly Counter<long> _duplicateCounter;
    private readonly Counter<long> _missingFieldCounter;
    private readonly Counter<long> _clockSkewCounter;
    private readonly Counter<long> _staleCounter;
    private readonly Counter<long> _unknownDiagnosticCodeCounter;
    private readonly ObservableGauge<long> _lastReceiveGauge;
    private readonly ObservableGauge<long> _freshnessGauge;
    private readonly ObservableGauge<long> _providerSilenceGauge;
    private readonly ObservableGauge<long> _clockSkewStatusGauge;
    private readonly HashSet<string> _publishedSnapshots = new(StringComparer.Ordinal);
    private CaptureQualitySnapshot? _latest;
    private bool _disposed;

    public CaptureQualityMetricsPublisher()
    {
        _recordCounter = _meter.CreateCounter<long>("capture.records");
        _gapCounter = _meter.CreateCounter<long>("capture.gaps");
        _duplicateCounter = _meter.CreateCounter<long>("capture.duplicates");
        _missingFieldCounter = _meter.CreateCounter<long>("capture.missing_fields");
        _clockSkewCounter = _meter.CreateCounter<long>("capture.clock_skew");
        _staleCounter = _meter.CreateCounter<long>("capture.stale");
        _unknownDiagnosticCodeCounter = _meter.CreateCounter<long>(
            "capture.unknown_diagnostic_codes");
        _lastReceiveGauge = _meter.CreateObservableGauge<long>(
            "capture.last_receive_unix_ms", () => _latest?.LastReceiveTimeUtc?.ToUnixTimeMilliseconds()
                ?? 0);
        _freshnessGauge = _meter.CreateObservableGauge<long>("capture.freshness_status",
            () => StatusCode(_latest?.FreshnessStatus));
        _providerSilenceGauge = _meter.CreateObservableGauge<long>(
            "capture.provider_silence_status",
            () => StatusCode(_latest?.ProviderSilenceStatus));
        _clockSkewStatusGauge = _meter.CreateObservableGauge<long>(
            "capture.clock_skew_status", () => StatusCode(_latest?.ClockSkewStatus));
    }

    public Meter Meter => _meter;

    public void Publish(CaptureQualitySnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        _latest = snapshot;
        string fingerprint = CoreJsonSerializer.Serialize(snapshot);
        if (!_publishedSnapshots.Add(fingerprint))
        {
            return;
        }

        _recordCounter.Add(snapshot.RecordCount);
        _gapCounter.Add(snapshot.GapCount);
        _duplicateCounter.Add(snapshot.DuplicateCount);
        _missingFieldCounter.Add(snapshot.MissingFieldCount);
        _clockSkewCounter.Add(snapshot.ClockSkewCount);
        _staleCounter.Add(snapshot.StaleCount);
        _unknownDiagnosticCodeCounter.Add(snapshot.UnknownDiagnosticCodeCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    private static long StatusCode(CaptureFreshnessStatus? status) => status switch
    {
        CaptureFreshnessStatus.Fresh => 1,
        CaptureFreshnessStatus.Stale => 2,
        _ => 0
    };

    private static long StatusCode(CaptureProviderSilenceStatus? status) => status switch
    {
        CaptureProviderSilenceStatus.Active => 1,
        CaptureProviderSilenceStatus.Silent => 2,
        _ => 0
    };

    private static long StatusCode(CaptureClockSkewStatus? status) => status switch
    {
        CaptureClockSkewStatus.Clear => 1,
        CaptureClockSkewStatus.Signaled => 2,
        _ => 0
    };
}
