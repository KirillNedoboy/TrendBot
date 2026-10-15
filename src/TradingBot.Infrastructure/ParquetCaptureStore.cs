using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Serialization;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>Explicit bounds and durability policy for one capture stream.</summary>
public sealed record CapturePersistenceOptions
{
    public string? StreamId { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    public int MaxRowsPerSegment { get; init; } = 10_000;

    public int MaxBufferedRows { get; init; } = 50_000;

    public long MaxSegmentBytes { get; init; } = 64 * 1024 * 1024;

    public bool FlushOnDispose { get; init; } = true;
}

/// <summary>Durable SQLite checkpoint for the next capture sequence.</summary>
public sealed record CaptureCheckpoint
{
    public CaptureCheckpoint(string streamId, long nextCaptureSequence,
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
}

/// <summary>SQLite metadata describing one atomically published Parquet segment.</summary>
public sealed record CaptureSegmentMetadata
{
    public CaptureSegmentMetadata(string segmentId, string streamId, string path,
        int formatVersion, long rowCount, long firstCaptureSequence,
        long lastCaptureSequence, long firstStableSequence, long lastStableSequence,
        long byteLength, long createdTimeTicks, long durableTimeTicks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(formatVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(firstCaptureSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastCaptureSequence,
            firstCaptureSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(firstStableSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(lastStableSequence, firstStableSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        ArgumentOutOfRangeException.ThrowIfNegative(createdTimeTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(durableTimeTicks, createdTimeTicks);

        SegmentId = segmentId;
        StreamId = streamId;
        Path = path;
        FormatVersion = formatVersion;
        RowCount = rowCount;
        FirstCaptureSequence = firstCaptureSequence;
        LastCaptureSequence = lastCaptureSequence;
        FirstStableSequence = firstStableSequence;
        LastStableSequence = lastStableSequence;
        ByteLength = byteLength;
        CreatedTimeTicks = createdTimeTicks;
        DurableTimeTicks = durableTimeTicks;
    }

    public string SegmentId { get; }

    public string StreamId { get; }

    public string Path { get; }

    public int FormatVersion { get; }

    public long RowCount { get; }

    public long FirstCaptureSequence { get; }

    public long LastCaptureSequence { get; }

    public long FirstStableSequence { get; }

    public long LastStableSequence { get; }

    public long ByteLength { get; }

    public long CreatedTimeTicks { get; }

    public long DurableTimeTicks { get; }
}

/// <summary>
/// Flat Parquet row shape for capture. EnvelopeJson is the canonical v1 Core
/// envelope; the scalar columns support bounded scans without deserializing it.
/// </summary>
public sealed class CaptureParquetRow
{
    public int FormatVersion { get; set; }

    public string StreamId { get; set; } = string.Empty;

    public long CaptureSequence { get; set; }

    public long StableSequence { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? InstrumentId { get; set; }

    public string? SourceEventId { get; set; }

    public long EventTimeTicks { get; set; }

    public long ReceiveTimeTicks { get; set; }

    public long AvailabilityTimeTicks { get; set; }

    public long? ExchangeTimeTicks { get; set; }

    public string Source { get; set; } = string.Empty;

    public string? SourceStreamName { get; set; }

    public string? SessionId { get; set; }

    public string? RawPayload { get; set; }

    public string QualityFlagsJson { get; set; } = "[]";

    public string EnvelopeJson { get; set; } = string.Empty;
}

/// <summary>
/// Bounded append-only capture persistence. A flushed batch is first fsynced to
/// a temporary Parquet file, atomically renamed, and then committed with its
/// SQLite segment metadata and sequence checkpoint in one transaction.
/// </summary>
public sealed class ParquetCaptureStore : IDisposable, IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _segmentDirectory;
    private readonly CapturePersistenceOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<CaptureParquetRow> _buffer = [];
    private readonly List<CaptureSegmentMetadata> _segments = [];
    private string _streamId;
    private CaptureCheckpoint _checkpoint;
    private bool _disposed;

    public ParquetCaptureStore(string databasePath, string segmentDirectory,
        CapturePersistenceOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(segmentDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        _databasePath = SqliteDatabase.NormalizePath(databasePath);
        _segmentDirectory = Path.GetFullPath(segmentDirectory);
        _options = options;
        _streamId = options.StreamId!;
        Directory.CreateDirectory(_segmentDirectory);
        SqliteDatabase.Initialize(_databasePath, options.CommandTimeoutSeconds);
        _checkpoint = ReadCheckpoint(_databasePath, _streamId, options.CommandTimeoutSeconds);
        LoadSegments();
    }

    public ParquetCaptureStore(string databasePath, string segmentDirectory, string streamId,
        CapturePersistenceOptions? options = null)
        : this(databasePath, segmentDirectory, WithStreamId(streamId, options))
    {
    }

    public static ParquetCaptureStore Open(string databasePath, string segmentDirectory,
        CapturePersistenceOptions options)
    {
        return new ParquetCaptureStore(databasePath, segmentDirectory, options);
    }

    public static ParquetCaptureStore Open(string databasePath, string segmentDirectory,
        string streamId, CapturePersistenceOptions? options = null)
    {
        return new ParquetCaptureStore(databasePath, segmentDirectory, streamId, options);
    }

    public string DatabasePath => _databasePath;

    public string SegmentDirectory => _segmentDirectory;

    public int BufferedRowCount => _buffer.Count;

    public CaptureCheckpoint Checkpoint => _checkpoint;

    public IReadOnlyList<CaptureSegmentMetadata> Segments =>
        new ReadOnlyCollection<CaptureSegmentMetadata>(_segments.ToArray());

    /// <summary>Appends a row to the bounded in-memory batch.</summary>
    public void Append(CaptureEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _gate.Wait();
        try
        {
            ThrowIfDisposed();
            if (!StringComparer.Ordinal.Equals(envelope.StreamId, _streamId))
            {
                throw new ArgumentException(
                    "The capture envelope stream does not match the store stream.",
                    nameof(envelope));
            }

            if (_buffer.Count >= _options.MaxBufferedRows)
            {
                throw new InvalidOperationException(
                    "The configured capture buffer bound has been reached; flush before appending.");
            }

            if (envelope.CaptureSequence != _checkpoint.NextCaptureSequence
                + _buffer.Count)
            {
                throw new ArgumentException(
                    $"Expected capture sequence {_checkpoint.NextCaptureSequence + _buffer.Count}, "
                    + $"received {envelope.CaptureSequence}.", nameof(envelope));
            }

            _buffer.Add(ToParquetRow(envelope));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Appends and flushes automatically at the segment row bound.</summary>
    public async Task<IReadOnlyList<CaptureSegmentMetadata>> AppendAsync(
        CaptureEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Append(envelope);
        return _buffer.Count >= _options.MaxRowsPerSegment
            ? await FlushAsync(cancellationToken).ConfigureAwait(false)
            : [];
    }

    /// <summary>Durably publishes all buffered rows as bounded segments.</summary>
    public async Task<IReadOnlyList<CaptureSegmentMetadata>> FlushAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            List<CaptureSegmentMetadata> flushed = [];
            while (_buffer.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(_options.MaxRowsPerSegment, _buffer.Count);
                CaptureParquetRow[] batch = _buffer.Take(count).ToArray();
                CaptureSegmentMetadata metadata = await PublishSegmentAsync(batch,
                    cancellationToken).ConfigureAwait(false);
                _buffer.RemoveRange(0, count);
                _segments.Add(metadata);
                _checkpoint = new CaptureCheckpoint(_streamId,
                    checked(metadata.LastCaptureSequence + 1), metadata.LastCaptureSequence,
                    metadata.SegmentId);
                flushed.Add(metadata);
            }

            return new ReadOnlyCollection<CaptureSegmentMetadata>(flushed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Synchronously durably publishes all buffered rows.</summary>
    public IReadOnlyList<CaptureSegmentMetadata> Flush() =>
        FlushAsync().GetAwaiter().GetResult();

    /// <summary>Reads and validates all durable capture envelopes in sequence order.</summary>
    public async Task<IReadOnlyList<CaptureEnvelope>> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            List<CaptureEnvelope> result = [];
            foreach (CaptureSegmentMetadata segment in _segments.OrderBy(
                static item => item.FirstCaptureSequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using FileStream source = new(segment.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                Parquet.Serialization.DeserializationResult<CaptureParquetRow> deserialized =
                    await ParquetSerializer.DeserializeAsync<CaptureParquetRow>(source,
                        new ParquetOptions(), cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                CaptureParquetRow[] rows = deserialized.Data.ToArray();
                if (rows.Length != segment.RowCount)
                {
                    throw new InvalidDataException(
                        $"Capture segment {segment.SegmentId} row count does not match metadata.");
                }

                long expectedSequence = segment.FirstCaptureSequence;
                foreach (CaptureParquetRow row in rows)
                {
                    CaptureEnvelope envelope = CoreJsonSerializer.Deserialize<CaptureEnvelope>(
                        row.EnvelopeJson);
                    ValidateRow(envelope, row);
                    if (envelope.CaptureSequence != expectedSequence)
                    {
                        throw new InvalidDataException(
                            "Capture Parquet rows are not contiguous within the segment.");
                    }

                    expectedSequence = checked(expectedSequence + 1);
                    result.Add(envelope);
                }
            }

            return new ReadOnlyCollection<CaptureEnvelope>(result);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_options.FlushOnDispose && _buffer.Count > 0)
            {
                FlushAsync().GetAwaiter().GetResult();
            }
        }
        finally
        {
            _disposed = true;
            _gate.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_options.FlushOnDispose && _buffer.Count > 0)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            _gate.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task<CaptureSegmentMetadata> PublishSegmentAsync(
        CaptureParquetRow[] rows, CancellationToken cancellationToken)
    {
        string segmentId = Guid.NewGuid().ToString("N");
        string finalPath = Path.Combine(_segmentDirectory, segmentId + ".parquet");
        string temporaryPath = finalPath + ".tmp";
        DateTimeOffset created = DateTimeOffset.UtcNow;
        bool committed = false;
        try
        {
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                await ParquetSerializer.SerializeAsync(rows, output, new ParquetOptions(),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["captureFormatVersion"] = CaptureEnvelope.CurrentFormatVersion
                            .ToString(CultureInfo.InvariantCulture),
                        ["streamId"] = _streamId
                    }, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath);
            long byteLength = new FileInfo(finalPath).Length;
            if (byteLength <= 0 || byteLength > _options.MaxSegmentBytes)
            {
                throw new InvalidOperationException(
                    "The flushed capture segment exceeded the configured byte bound.");
            }

            DateTimeOffset durable = DateTimeOffset.UtcNow;
            CaptureSegmentMetadata metadata = new(segmentId, _streamId, finalPath,
                CaptureEnvelope.CurrentFormatVersion, rows.Length, rows[0].CaptureSequence,
                rows[^1].CaptureSequence, rows.Min(static row => row.StableSequence),
                rows.Max(static row => row.StableSequence), byteLength,
                created.UtcDateTime.Ticks, durable.UtcDateTime.Ticks);
            CommitSegment(metadata);
            committed = true;
            return metadata;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (!committed && File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
        }
    }

    private void CommitSegment(CaptureSegmentMetadata metadata)
    {
        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _options.CommandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO capture_segments
                    (segment_id, stream_id, file_path, format_version, row_count,
                     first_capture_sequence, last_capture_sequence, first_stable_sequence,
                     last_stable_sequence, byte_length, created_time_ticks, durable_time_ticks)
                VALUES ($segment_id, $stream_id, $file_path, $format_version, $row_count,
                        $first_capture_sequence, $last_capture_sequence, $first_stable_sequence,
                        $last_stable_sequence, $byte_length, $created_time_ticks,
                        $durable_time_ticks);
                """;
            AddSegmentParameters(insert, metadata);
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO capture_checkpoints
                    (stream_id, next_capture_sequence, last_durable_sequence, last_segment_id)
                VALUES ($stream_id, $next_capture_sequence, $last_durable_sequence,
                        $last_segment_id)
                ON CONFLICT(stream_id) DO UPDATE SET
                    next_capture_sequence = excluded.next_capture_sequence,
                    last_durable_sequence = excluded.last_durable_sequence,
                    last_segment_id = excluded.last_segment_id;
                """;
            checkpoint.Parameters.AddWithValue("$stream_id", _streamId);
            checkpoint.Parameters.AddWithValue("$next_capture_sequence",
                checked(metadata.LastCaptureSequence + 1));
            checkpoint.Parameters.AddWithValue("$last_durable_sequence",
                metadata.LastCaptureSequence);
            checkpoint.Parameters.AddWithValue("$last_segment_id", metadata.SegmentId);
            checkpoint.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void LoadSegments()
    {
        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _options.CommandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, stream_id, file_path, format_version, row_count,
                   first_capture_sequence, last_capture_sequence, first_stable_sequence,
                   last_stable_sequence, byte_length, created_time_ticks, durable_time_ticks
            FROM capture_segments
            WHERE stream_id = $stream_id
            ORDER BY first_capture_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", _streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            CaptureSegmentMetadata metadata = new(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt32(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
                reader.GetInt64(11));
            if (!File.Exists(metadata.Path))
            {
                throw new InvalidDataException(
                    $"Capture segment {metadata.SegmentId} is missing its Parquet file.");
            }

            _segments.Add(metadata);
        }

        long expectedSequence = 0;
        foreach (CaptureSegmentMetadata segment in _segments)
        {
            if (segment.FirstCaptureSequence != expectedSequence)
            {
                throw new InvalidDataException(
                    "Capture segment metadata contains a sequence gap or overlap.");
            }

            expectedSequence = checked(segment.LastCaptureSequence + 1);
        }

        if (_checkpoint.NextCaptureSequence != expectedSequence
            || (_segments.Count == 0
                ? _checkpoint.LastDurableSequence != -1
                : _checkpoint.LastDurableSequence != _segments[^1].LastCaptureSequence
                    || !StringComparer.Ordinal.Equals(_checkpoint.LastSegmentId,
                        _segments[^1].SegmentId)))
        {
            throw new InvalidDataException(
                "The capture checkpoint does not match the durable segment metadata.");
        }
    }

    private static CaptureCheckpoint ReadCheckpoint(string databasePath, string streamId,
        int commandTimeoutSeconds)
    {
        using SqliteConnection connection = SqliteDatabase.OpenConnection(databasePath,
            commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT next_capture_sequence, last_durable_sequence, last_segment_id
            FROM capture_checkpoints
            WHERE stream_id = $stream_id;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new CaptureCheckpoint(streamId, reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2))
            : new CaptureCheckpoint(streamId, 0, -1, null);
    }

    private static CaptureParquetRow ToParquetRow(CaptureEnvelope envelope)
    {
        return new CaptureParquetRow
        {
            FormatVersion = envelope.FormatVersion,
            StreamId = envelope.StreamId,
            CaptureSequence = envelope.CaptureSequence,
            StableSequence = envelope.StableSequence,
            Kind = envelope.Kind.ToString(),
            InstrumentId = envelope.Payload?.InstrumentId.Value,
            SourceEventId = envelope.Payload?.EventId ?? envelope.Provenance.SourceEventId,
            EventTimeTicks = envelope.EventTime.Value.UtcDateTime.Ticks,
            ReceiveTimeTicks = envelope.ReceiveTime.Value.UtcDateTime.Ticks,
            AvailabilityTimeTicks = envelope.AvailabilityTime.Value.UtcDateTime.Ticks,
            ExchangeTimeTicks = envelope.ExchangeTime?.Value.UtcDateTime.Ticks,
            Source = envelope.Provenance.Source,
            SourceStreamName = envelope.Provenance.StreamName,
            SessionId = envelope.Provenance.SessionId,
            RawPayload = envelope.Provenance.RawPayload,
            QualityFlagsJson = CoreJsonSerializer.Serialize(envelope.Provenance.QualityFlags),
            EnvelopeJson = CoreJsonSerializer.Serialize(envelope)
        };
    }

    private static void ValidateRow(CaptureEnvelope envelope, CaptureParquetRow row)
    {
        if (envelope.FormatVersion != row.FormatVersion
            || !StringComparer.Ordinal.Equals(envelope.StreamId, row.StreamId)
            || envelope.CaptureSequence != row.CaptureSequence
            || envelope.StableSequence != row.StableSequence
            || !StringComparer.Ordinal.Equals(envelope.Kind.ToString(), row.Kind)
            || envelope.EventTime.Value.UtcDateTime.Ticks != row.EventTimeTicks
            || envelope.ReceiveTime.Value.UtcDateTime.Ticks != row.ReceiveTimeTicks
            || envelope.AvailabilityTime.Value.UtcDateTime.Ticks != row.AvailabilityTimeTicks
            || envelope.ExchangeTime?.Value.UtcDateTime.Ticks != row.ExchangeTimeTicks
            || !StringComparer.Ordinal.Equals(envelope.Provenance.Source, row.Source)
            || !StringComparer.Ordinal.Equals(envelope.Provenance.StreamName,
                row.SourceStreamName)
            || !StringComparer.Ordinal.Equals(envelope.Provenance.SessionId, row.SessionId)
            || !StringComparer.Ordinal.Equals(envelope.Provenance.RawPayload, row.RawPayload)
            || !StringComparer.Ordinal.Equals(
                CoreJsonSerializer.Serialize(envelope.Provenance.QualityFlags),
                row.QualityFlagsJson))
        {
            throw new InvalidDataException("Capture Parquet scalar columns do not match the envelope.");
        }
    }

    private static void AddSegmentParameters(SqliteCommand command,
        CaptureSegmentMetadata metadata)
    {
        command.Parameters.AddWithValue("$segment_id", metadata.SegmentId);
        command.Parameters.AddWithValue("$stream_id", metadata.StreamId);
        command.Parameters.AddWithValue("$file_path", metadata.Path);
        command.Parameters.AddWithValue("$format_version", metadata.FormatVersion);
        command.Parameters.AddWithValue("$row_count", metadata.RowCount);
        command.Parameters.AddWithValue("$first_capture_sequence", metadata.FirstCaptureSequence);
        command.Parameters.AddWithValue("$last_capture_sequence", metadata.LastCaptureSequence);
        command.Parameters.AddWithValue("$first_stable_sequence", metadata.FirstStableSequence);
        command.Parameters.AddWithValue("$last_stable_sequence", metadata.LastStableSequence);
        command.Parameters.AddWithValue("$byte_length", metadata.ByteLength);
        command.Parameters.AddWithValue("$created_time_ticks", metadata.CreatedTimeTicks);
        command.Parameters.AddWithValue("$durable_time_ticks", metadata.DurableTimeTicks);
    }

    private static void ValidateOptions(CapturePersistenceOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StreamId);
        SqliteDatabase.ValidateTimeout(options.CommandTimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxRowsPerSegment);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxBufferedRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxSegmentBytes);
        if (options.MaxBufferedRows < options.MaxRowsPerSegment)
        {
            throw new ArgumentException(
                "The capture buffer bound must cover at least one segment.", nameof(options));
        }
    }

    private static CapturePersistenceOptions WithStreamId(string streamId,
        CapturePersistenceOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        if (options is null)
        {
            return new CapturePersistenceOptions { StreamId = streamId };
        }

        if (options.StreamId is not null
            && !StringComparer.Ordinal.Equals(options.StreamId, streamId))
        {
            throw new ArgumentException(
                "The explicit stream ID does not match the persistence options.", nameof(streamId));
        }

        return options with { StreamId = streamId };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
