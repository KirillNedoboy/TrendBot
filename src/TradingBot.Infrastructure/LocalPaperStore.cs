using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Durable journal and virtual ledger for the local-paper lane.
/// </summary>
/// <remarks>
/// The first version of this file (schema-less <c>paper_entries</c> and
/// <c>paper_checkpoints</c>) was published before the executor contract.  The
/// current schema keeps those tables byte-for-byte compatible, adds the
/// executor tables in one transaction, and leaves any legacy rows visible but
/// fail-closed for executor recovery.
/// </remarks>
public sealed class LocalPaperStore : IDisposable
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions LedgerJsonOptions = CreateLedgerJsonOptions();

    private readonly string _path;
    private readonly int _commandTimeoutSeconds;
    private bool _disposed;

    private LocalPaperStore(string path, int commandTimeoutSeconds)
    {
        _path = path;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>Opens a file-backed local-paper store and applies supported migrations.</summary>
    public static LocalPaperStore Open(string path, int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (StringComparer.Ordinal.Equals(path, ":memory:"))
        {
            throw new ArgumentException("Paper state requires a file-backed database.",
                nameof(path));
        }

        if (commandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds),
                commandTimeoutSeconds, "The SQLite command timeout must be positive.");
        }

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        Initialize(fullPath, commandTimeoutSeconds);
        return new LocalPaperStore(fullPath, commandTimeoutSeconds);
    }

    /// <summary>
    /// Retains the original append surface used by the bootstrap journal.
    /// These rows remain readable after migration, but make executor recovery
    /// fail closed because they have no typed causal envelope.
    /// </summary>
    public bool Append(string streamId, long sequence, string eventId, string payload,
        string state)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        using SqliteCommand existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT event_id, payload, state
            FROM paper_entries
            WHERE stream_id = $stream_id COLLATE BINARY AND sequence = $sequence;
            """;
        existing.Parameters.AddWithValue("$stream_id", streamId);
        existing.Parameters.AddWithValue("$sequence", sequence);

        using SqliteDataReader reader = existing.ExecuteReader();
        if (reader.Read())
        {
            bool same = StringComparer.Ordinal.Equals(reader.GetString(0), eventId)
                && StringComparer.Ordinal.Equals(reader.GetString(1), payload)
                && StringComparer.Ordinal.Equals(reader.GetString(2), state);
            reader.Close();
            if (!same)
            {
                throw new InvalidOperationException(
                    "Paper sequence conflicts with durable evidence.");
            }

            transaction.Commit();
            return false;
        }

        reader.Close();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO paper_entries (stream_id, sequence, event_id, payload, state)
            VALUES ($stream_id, $sequence, $event_id, $payload, $state);
            INSERT INTO paper_checkpoints (stream_id, sequence, state)
            VALUES ($stream_id, $sequence, $state)
            ON CONFLICT(stream_id) DO UPDATE SET
                sequence = excluded.sequence,
                state = excluded.state
            WHERE excluded.sequence > paper_checkpoints.sequence;
            """;
        insert.Parameters.AddWithValue("$stream_id", streamId);
        insert.Parameters.AddWithValue("$sequence", sequence);
        insert.Parameters.AddWithValue("$event_id", eventId);
        insert.Parameters.AddWithValue("$payload", payload);
        insert.Parameters.AddWithValue("$state", state);
        insert.ExecuteNonQuery();
        SetMetadata(connection, transaction, "legacy_blocked", "1");
        transaction.Commit();
        return true;
    }

    /// <summary>Reads the legacy checkpoint used by the bootstrap append API.</summary>
    public string? ReadCheckpoint(string streamId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT state
            FROM paper_checkpoints
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        string? legacyState = command.ExecuteScalar() as string;
        if (legacyState is not null)
        {
            return legacyState;
        }

        using SqliteCommand executorCheckpoint = connection.CreateCommand();
        executorCheckpoint.CommandText = """
            SELECT state_json
            FROM paper_checkpoints_v2
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        executorCheckpoint.Parameters.AddWithValue("$stream_id", streamId);
        return executorCheckpoint.ExecuteScalar() as string;
    }

    /// <summary>
    /// Reads typed replay envelopes recorded by the executor.  Legacy string
    /// rows are deliberately excluded because their payload has no causal
    /// contract and are reported through the recovery block.
    /// </summary>
    public IReadOnlyList<ReplayEnvelope> ReadEnvelopes(string streamId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, event_id, envelope_json
            FROM paper_observations
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY sequence ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);

        List<ReplayEnvelope> result = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            long sequence = reader.GetInt64(0);
            string eventId = reader.GetString(1);
            string json = reader.GetString(2);
            ReplayEnvelope envelope = DeserializeEnvelope(json);
            if (!StringComparer.Ordinal.Equals(envelope.StreamId, streamId)
                || envelope.CausalEvent.StableSequence != sequence
                || !StringComparer.Ordinal.Equals(envelope.CausalEvent.Payload.EventId, eventId))
            {
                throw new InvalidDataException(
                    "A persisted paper envelope does not match its journal identity.");
            }

            result.Add(envelope);
        }

        return new ReadOnlyCollection<ReplayEnvelope>(result);
    }

    /// <summary>Reads the durable virtual ledger and validates its checkpoint.</summary>
    public LocalPaperLedgerSnapshot? ReadSnapshot(string streamId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: true);
        StreamRow? stream = ReadStream(connection, transaction, streamId);
        if (stream is null)
        {
            transaction.Commit();
            return null;
        }

        CheckpointRow? checkpoint = ReadCheckpointRow(connection, transaction, streamId);
        LocalPaperLedgerSnapshot snapshot = ComputeLedger(connection, transaction, stream,
            checkpoint?.Revision ?? 0, checkpoint?.Sequence, checkpoint?.EventId,
            IsLegacyBlocked(connection, transaction));
        if (checkpoint is not null
            && !JsonEquivalent(checkpoint.StateJson, snapshot.StateJson))
        {
            throw new InvalidDataException(
                "The persisted paper checkpoint does not match the virtual journal.");
        }

        transaction.Commit();
        return snapshot;
    }

    /// <summary>Reads the executor journal rows for diagnostics and restart recovery.</summary>
    public IReadOnlyList<LocalPaperObservationRecord> ReadObservations(string streamId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stream_id, sequence, event_id, envelope_json, application_json,
                   observation_identity, configuration_identity, plan_identity,
                   risk_input_identity, model_identity, status, block_reason,
                   result_identity, result_json
            FROM paper_observations
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY sequence ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);

        List<LocalPaperObservationRecord> result = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadObservation(reader));
        }

        return new ReadOnlyCollection<LocalPaperObservationRecord>(result);
    }

    /// <summary>
    /// Reads the immutable audit view of one run-scoped paper stream.  The
    /// ledger is recomputed from the durable fill journal and validated against
    /// its checkpoint before the view is returned.
    /// </summary>
    public LocalPaperAuditSnapshot? ReadAuditSnapshot(string streamId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        LocalPaperLedgerSnapshot? ledger = ReadSnapshot(streamId);
        if (ledger is null)
        {
            return null;
        }

        IReadOnlyList<LocalPaperObservationRecord> observations = ReadObservations(streamId);
        IReadOnlyList<LocalPaperFillRecord> fills = ReadFills(streamId);
        return new LocalPaperAuditSnapshot(streamId, observations, fills, ledger);
    }

    /// <summary>Reads the durable primitive fills in causal journal order.</summary>
    public IReadOnlyList<LocalPaperFillRecord> ReadFills(string streamId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT fill_id, instrument_id, direction, is_entry, role, quantity, price,
                   fee_amount, slippage_amount, fee_currency, filled_at_ticks,
                   stable_sequence, event_id
            FROM paper_fills
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY sequence ASC, ordinal ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);

        List<LocalPaperFillRecord> result = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LocalPaperFillRecord(reader.GetString(0), reader.GetString(1),
                ReadDirection(reader.GetInt32(2)), reader.GetInt32(3) != 0, reader.GetString(4),
                ParseDecimal(reader.GetString(5)), ParseDecimal(reader.GetString(6)),
                ParseDecimal(reader.GetString(7)), ParseDecimal(reader.GetString(8)),
                reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11),
                reader.GetString(12)));
        }

        return new ReadOnlyCollection<LocalPaperFillRecord>(result);
    }

    /// <summary>
    /// Reads observations whose virtual capacity reservation is still active.
    /// An active row is the durable continuation source for an open paper
    /// position after the original application has completed.
    /// </summary>
    public IReadOnlyList<LocalPaperObservationRecord> ReadReservedObservations(
        string streamId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.stream_id, o.sequence, o.event_id, o.envelope_json, o.application_json,
                   o.observation_identity, o.configuration_identity, o.plan_identity,
                   o.risk_input_identity, o.model_identity, o.status, o.block_reason,
                   o.result_identity, o.result_json
            FROM paper_observations AS o
            INNER JOIN paper_reservations AS r
                ON r.stream_id = o.stream_id AND r.sequence = o.sequence
            WHERE o.stream_id = $stream_id COLLATE BINARY
              AND r.status = $status
            ORDER BY o.sequence ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$status", ReservationState.Reserved);

        List<LocalPaperObservationRecord> result = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadObservation(reader));
        }

        return new ReadOnlyCollection<LocalPaperObservationRecord>(result);
    }

    /// <summary>
    /// Durably records an observation intent and, when an application is
    /// present, reserves virtual portfolio capacity before simulation starts.
    /// </summary>
    public LocalPaperPreparation Prepare(LocalPaperReservationRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateJson(request.EnvelopeJson, "The paper envelope is invalid.");
        if (request.ApplicationJson is not null)
        {
            ValidateJson(request.ApplicationJson, "The paper application is invalid.");
        }

        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        if (IsLegacyBlocked(connection, transaction))
        {
            transaction.Commit();
            return LocalPaperPreparation.Blocked(
                "Legacy paper rows require explicit recovery before executor use.");
        }

        StreamRow stream = ReadStream(connection, transaction, request.StreamId)
            ?? InsertStream(connection, transaction, request);
        if (!stream.Matches(request))
        {
            transaction.Commit();
            return LocalPaperPreparation.Blocked(
                "The paper configuration or virtual equity conflicts with the stream journal.");
        }

        ObservationRow? existing = ReadObservation(connection, transaction, request.StreamId,
            request.Sequence);
        if (existing is not null)
        {
            if (!existing.Matches(request))
            {
                transaction.Commit();
                return LocalPaperPreparation.Blocked(
                    "The paper observation identity conflicts with durable evidence.");
            }

            if (existing.Status == ObservationState.Complete)
            {
                ValidateStoredResult(existing);
                long completedRevision = ReadRevision(connection, transaction, request.StreamId);
                transaction.Commit();
                return LocalPaperPreparation.AlreadyCompleted(completedRevision);
            }

            if (existing.Status == ObservationState.Blocked)
            {
                transaction.Commit();
                return LocalPaperPreparation.Blocked(existing.BlockReason
                    ?? "The paper observation is durably blocked.");
            }

            long pendingRevision = ReadRevision(connection, transaction, request.StreamId);
            transaction.Commit();
            return LocalPaperPreparation.AlreadyPrepared(pendingRevision);
        }

        ObservationRow? eventIdentity = ReadObservationByEventId(connection, transaction,
            request.StreamId, request.EventId);
        if (eventIdentity is not null)
        {
            transaction.Commit();
            return LocalPaperPreparation.Blocked(
                "The paper event identity is already assigned to another sequence.");
        }

        long? maximumSequence = ReadMaximumSequence(connection, transaction, request.StreamId);
        if (maximumSequence is not null && request.Sequence <= maximumSequence.Value)
        {
            transaction.Commit();
            return LocalPaperPreparation.Blocked(
                "Paper observations must advance the durable stable sequence.");
        }

        if (request.ApplicationJson is not null && !HasCapacity(connection, transaction, request))
        {
            transaction.Commit();
            return LocalPaperPreparation.Blocked(
                "The requested paper reservation exceeds known virtual capacity.");
        }

        InsertObservation(connection, transaction, request);
        if (request.ApplicationJson is not null)
        {
            InsertReservation(connection, transaction, request);
            RefreshCheckpointForReservation(connection, transaction, stream.StreamId);
        }

        long revision = ReadRevision(connection, transaction, request.StreamId);
        transaction.Commit();
        return LocalPaperPreparation.Prepared(revision);
    }

    /// <summary>
    /// Atomically commits simulated fills, the virtual journal checkpoint, and
    /// the observation result under an optimistic stream revision.
    /// </summary>
    public LocalPaperCommitResult Commit(LocalPaperCommitRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateJson(request.ResultJson, "The paper result is invalid.");

        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        ObservationRow observation = ReadObservation(connection, transaction, request.StreamId,
            request.Sequence)
            ?? throw new InvalidDataException("The paper observation intent is missing.");

        if (!StringComparer.Ordinal.Equals(observation.EventId, request.EventId)
            || !StringComparer.Ordinal.Equals(observation.ObservationIdentity,
                request.ObservationIdentity))
        {
            throw new InvalidOperationException(
                "The paper completion does not match the durable observation intent.");
        }

        if (observation.Status == ObservationState.Complete)
        {
            ValidateStoredResult(observation);
            if (!StringComparer.Ordinal.Equals(observation.ResultIdentity,
                    request.ResultIdentity)
                || !StringComparer.Ordinal.Equals(observation.ResultJson, request.ResultJson))
            {
                throw new InvalidOperationException(
                    "A completed paper observation cannot be replaced by a different result.");
            }

            LocalPaperLedgerSnapshot existing = ReadSnapshotInTransaction(connection,
                transaction, request.StreamId);
            transaction.Commit();
            return LocalPaperCommitResult.AlreadyCompleted(existing);
        }

        if (observation.Status != ObservationState.Pending)
        {
            transaction.Commit();
            return LocalPaperCommitResult.Blocked("The paper observation is not commit-ready.");
        }

        long currentRevision = ReadRevision(connection, transaction, request.StreamId);
        if (currentRevision != request.ExpectedRevision)
        {
            transaction.Commit();
            return LocalPaperCommitResult.Blocked(
                "The paper stream revision changed before the journal commit.");
        }

        InsertFills(connection, transaction, request);
        UpdateReservation(connection, transaction, request);

        StreamRow stream = ReadStream(connection, transaction, request.StreamId)
            ?? throw new InvalidDataException("The paper stream metadata is missing.");
        long nextRevision = checked(currentRevision + 1);
        LocalPaperLedgerSnapshot snapshot = ComputeLedger(connection, transaction, stream,
            nextRevision, request.Sequence, request.EventId,
            IsLegacyBlocked(connection, transaction));
        UpdateObservation(connection, transaction, request);
        UpsertCheckpoint(connection, transaction, snapshot);

        transaction.Commit();
        return LocalPaperCommitResult.Applied(snapshot);
    }

    /// <summary>Marks a pending observation blocked without writing fills.</summary>
    public bool Block(LocalPaperBlockRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        using SqliteConnection connection = Connect(_path, _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        ObservationRow observation = ReadObservation(connection, transaction, request.StreamId,
            request.Sequence)
            ?? throw new InvalidDataException("The paper observation intent is missing.");
        if (observation.Status == ObservationState.Complete)
        {
            transaction.Commit();
            return false;
        }

        if (!StringComparer.Ordinal.Equals(observation.ObservationIdentity,
                request.ObservationIdentity))
        {
            throw new InvalidOperationException(
                "The paper block does not match the durable observation intent.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE paper_observations
            SET status = $status, block_reason = $block_reason
            WHERE stream_id = $stream_id COLLATE BINARY AND sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$status", ObservationState.Blocked);
        command.Parameters.AddWithValue("$block_reason", request.Reason);
        command.Parameters.AddWithValue("$stream_id", request.StreamId);
        command.Parameters.AddWithValue("$sequence", request.Sequence);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    /// <summary>Releases the store handle; each operation owns its SQLite connection.</summary>
    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void Initialize(string path, int timeoutSeconds)
    {
        using SqliteConnection connection = Connect(path, timeoutSeconds);
        long version = ReadUserVersion(connection);
        if (version == 0)
        {
            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            long lockedVersion = ReadUserVersion(connection, transaction);
            if (lockedVersion != 0)
            {
                throw new InvalidDataException("The paper schema changed during migration.");
            }

            bool hasObjects = HasUserObjects(connection, transaction);
            if (hasObjects && !IsExactLegacySchema(connection, transaction))
            {
                throw new InvalidDataException(
                    "The database is not an empty local-paper store or the legacy schema is invalid.");
            }

            if (!hasObjects)
            {
                CreateLegacySchema(connection, transaction);
            }

            CreateExecutorSchema(connection, transaction);
            SetMetadata(connection, transaction, "legacy_blocked",
                HasLegacyRows(connection, transaction) ? "1" : "0");
            SetUserVersion(connection, transaction, CurrentSchemaVersion);
            transaction.Commit();
            version = CurrentSchemaVersion;
        }

        if (version != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Paper schema version {version} is not supported.");
        }

        using SqliteConnection validation = Connect(path, timeoutSeconds);
        ValidateSchema(validation);
    }

    private static void CreateLegacySchema(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE paper_entries
            (stream_id TEXT NOT NULL, sequence INTEGER NOT NULL, event_id TEXT NOT NULL,
             payload TEXT NOT NULL, state TEXT NOT NULL,
             PRIMARY KEY(stream_id, sequence), UNIQUE(stream_id, event_id));
            CREATE TABLE paper_checkpoints
            (stream_id TEXT PRIMARY KEY, sequence INTEGER NOT NULL, state TEXT NOT NULL);
            """);
    }

    private static void CreateExecutorSchema(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS paper_metadata
            (key TEXT COLLATE BINARY NOT NULL PRIMARY KEY, value TEXT NOT NULL);

            CREATE TABLE IF NOT EXISTS paper_streams
            (
                stream_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                initial_equity TEXT NOT NULL,
                currency TEXT COLLATE BINARY NOT NULL,
                configuration_identity TEXT COLLATE BINARY NOT NULL,
                max_risk TEXT NULL,
                max_notional TEXT NULL,
                max_position_slots INTEGER NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS paper_observations
            (
                stream_id TEXT COLLATE BINARY NOT NULL,
                sequence INTEGER NOT NULL,
                event_id TEXT COLLATE BINARY NOT NULL,
                envelope_json TEXT NOT NULL,
                application_json TEXT NULL,
                observation_identity TEXT COLLATE BINARY NOT NULL,
                configuration_identity TEXT COLLATE BINARY NOT NULL,
                plan_identity TEXT COLLATE BINARY NULL,
                risk_input_identity TEXT COLLATE BINARY NULL,
                model_identity TEXT COLLATE BINARY NULL,
                status TEXT COLLATE BINARY NOT NULL,
                block_reason TEXT NULL,
                result_identity TEXT COLLATE BINARY NULL,
                result_json TEXT NULL,
                PRIMARY KEY(stream_id, sequence),
                UNIQUE(stream_id, event_id)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS paper_reservations
            (
                stream_id TEXT COLLATE BINARY NOT NULL,
                sequence INTEGER NOT NULL,
                reservation_id TEXT COLLATE BINARY NOT NULL,
                risk_amount TEXT NOT NULL,
                notional_amount TEXT NOT NULL,
                position_slots INTEGER NOT NULL,
                currency TEXT COLLATE BINARY NOT NULL,
                status TEXT COLLATE BINARY NOT NULL,
                PRIMARY KEY(stream_id, sequence),
                UNIQUE(stream_id, reservation_id)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS paper_fills
            (
                stream_id TEXT COLLATE BINARY NOT NULL,
                sequence INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                fill_id TEXT COLLATE BINARY NOT NULL,
                instrument_id TEXT COLLATE BINARY NOT NULL,
                direction INTEGER NOT NULL,
                is_entry INTEGER NOT NULL,
                role TEXT COLLATE BINARY NOT NULL,
                quantity TEXT NOT NULL,
                price TEXT NOT NULL,
                fee_amount TEXT NOT NULL,
                fee_currency TEXT COLLATE BINARY NOT NULL,
                slippage_amount TEXT NOT NULL,
                slippage_currency TEXT COLLATE BINARY NOT NULL,
                filled_at_ticks INTEGER NOT NULL,
                stable_sequence INTEGER NOT NULL,
                event_id TEXT COLLATE BINARY NOT NULL,
                PRIMARY KEY(stream_id, sequence, ordinal),
                UNIQUE(stream_id, fill_id)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS paper_checkpoints_v2
            (
                stream_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                sequence INTEGER NOT NULL,
                event_id TEXT COLLATE BINARY NOT NULL,
                revision INTEGER NOT NULL,
                state_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static bool IsExactLegacySchema(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        string[] names = ReadUserObjectNames(connection, transaction);
        if (names.Length != 2 || !names.Contains("paper_entries", StringComparer.Ordinal)
            || !names.Contains("paper_checkpoints", StringComparer.Ordinal))
        {
            return false;
        }

        return ColumnsMatch(connection, transaction, "paper_entries",
                ["stream_id", "sequence", "event_id", "payload", "state"])
            && ColumnsMatch(connection, transaction, "paper_checkpoints",
                ["stream_id", "sequence", "state"]);
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        if (!ColumnsMatch(connection, null, "paper_entries",
                ["stream_id", "sequence", "event_id", "payload", "state"])
            || !ColumnsMatch(connection, null, "paper_checkpoints",
                ["stream_id", "sequence", "state"])
            || !ColumnsMatch(connection, null, "paper_metadata", ["key", "value"])
            || !ColumnsMatch(connection, null, "paper_streams",
                ["stream_id", "initial_equity", "currency", "configuration_identity",
                    "max_risk", "max_notional", "max_position_slots"])
            || !ColumnsMatch(connection, null, "paper_observations",
                ["stream_id", "sequence", "event_id", "envelope_json", "application_json",
                    "observation_identity", "configuration_identity", "plan_identity",
                    "risk_input_identity", "model_identity", "status", "block_reason",
                    "result_identity", "result_json"])
            || !ColumnsMatch(connection, null, "paper_reservations",
                ["stream_id", "sequence", "reservation_id", "risk_amount", "notional_amount",
                    "position_slots", "currency", "status"])
            || !ColumnsMatch(connection, null, "paper_fills",
                ["stream_id", "sequence", "ordinal", "fill_id", "instrument_id", "direction",
                    "is_entry", "role", "quantity", "price", "fee_amount", "fee_currency",
                    "slippage_amount", "slippage_currency", "filled_at_ticks",
                    "stable_sequence", "event_id"])
            || !ColumnsMatch(connection, null, "paper_checkpoints_v2",
                ["stream_id", "sequence", "event_id", "revision", "state_json"]))
        {
            throw new InvalidDataException(
                $"The local-paper schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static LocalPaperLedgerSnapshot ReadSnapshotInTransaction(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        StreamRow stream = ReadStream(connection, transaction, streamId)
            ?? throw new InvalidDataException("The paper stream metadata is missing.");
        CheckpointRow? checkpoint = ReadCheckpointRow(connection, transaction, streamId);
        LocalPaperLedgerSnapshot snapshot = ComputeLedger(connection, transaction, stream,
            checkpoint?.Revision ?? 0, checkpoint?.Sequence, checkpoint?.EventId,
            IsLegacyBlocked(connection, transaction));
        if (checkpoint is not null && !JsonEquivalent(checkpoint.StateJson, snapshot.StateJson))
        {
            throw new InvalidDataException(
                "The persisted paper checkpoint does not match the virtual journal.");
        }

        return snapshot;
    }

    private static LocalPaperLedgerSnapshot ComputeLedger(SqliteConnection connection,
        SqliteTransaction transaction, StreamRow stream, long revision, long? sequence,
        string? eventId, bool legacyBlocked)
    {
        Dictionary<PositionKey, PositionAccumulator> positions = [];
        decimal realizedPnl = 0m;
        decimal totalFees = 0m;
        decimal totalSlippage = 0m;

        using SqliteCommand fills = connection.CreateCommand();
        fills.Transaction = transaction;
        fills.CommandText = """
            SELECT instrument_id, direction, is_entry, quantity, price, fee_amount,
                   fee_currency, slippage_amount, slippage_currency
            FROM paper_fills
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY sequence ASC, ordinal ASC;
            """;
        fills.Parameters.AddWithValue("$stream_id", stream.StreamId);
        using SqliteDataReader reader = fills.ExecuteReader();
        while (reader.Read())
        {
            string instrumentId = reader.GetString(0);
            Direction direction = ReadDirection(reader.GetInt32(1));
            bool isEntry = reader.GetInt32(2) != 0;
            decimal quantity = ParseDecimal(reader.GetString(3));
            decimal price = ParseDecimal(reader.GetString(4));
            decimal fee = ParseDecimal(reader.GetString(5));
            string feeCurrency = reader.GetString(6);
            decimal slippage = ParseDecimal(reader.GetString(7));
            string slippageCurrency = reader.GetString(8);
            if (!StringComparer.Ordinal.Equals(feeCurrency, stream.Currency)
                || !StringComparer.Ordinal.Equals(slippageCurrency, stream.Currency))
            {
                throw new InvalidDataException(
                    "A paper fill currency does not match the stream equity currency.");
            }

            PositionKey key = new(instrumentId, direction);
            PositionAccumulator accumulator = positions.TryGetValue(key, out PositionAccumulator? found)
                ? found
                : new PositionAccumulator();
            if (isEntry)
            {
                accumulator.AddEntry(quantity, price);
            }
            else
            {
                realizedPnl = checked(realizedPnl + accumulator.AddExit(quantity, price,
                    direction));
            }

            // Fees and slippage are both cash costs of the virtual fill. Keep
            // their typed totals separately while deriving equity from the
            // same durable journal rather than from a caller-supplied balance.
            realizedPnl = checked(realizedPnl - fee - slippage);
            totalFees = checked(totalFees + fee);
            totalSlippage = checked(totalSlippage + slippage);
            positions[key] = accumulator;
        }

        decimal reservedRisk = 0m;
        decimal reservedNotional = 0m;
        int reservedSlots = 0;
        using SqliteCommand reservations = connection.CreateCommand();
        reservations.Transaction = transaction;
        reservations.CommandText = """
            SELECT risk_amount, notional_amount, position_slots, currency
            FROM paper_reservations
            WHERE stream_id = $stream_id COLLATE BINARY AND status = $status;
            """;
        reservations.Parameters.AddWithValue("$stream_id", stream.StreamId);
        reservations.Parameters.AddWithValue("$status", ReservationState.Reserved);
        using SqliteDataReader reservationReader = reservations.ExecuteReader();
        while (reservationReader.Read())
        {
            string currency = reservationReader.GetString(3);
            if (!StringComparer.Ordinal.Equals(currency, stream.Currency))
            {
                throw new InvalidDataException(
                    "A paper reservation currency does not match the stream equity currency.");
            }

            reservedRisk = checked(reservedRisk + ParseDecimal(reservationReader.GetString(0)));
            reservedNotional = checked(reservedNotional
                + ParseDecimal(reservationReader.GetString(1)));
            reservedSlots = checked(reservedSlots + reservationReader.GetInt32(2));
        }

        List<LocalPaperPositionSnapshot> snapshots = positions
            .Where(static pair => pair.Value.Quantity > 0m)
            .OrderBy(static pair => pair.Key.InstrumentId, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Key.Direction)
            .Select(static pair => new LocalPaperPositionSnapshot(pair.Key.InstrumentId,
                pair.Key.Direction, pair.Value.Quantity, pair.Value.AverageEntryPrice,
                pair.Value.RealizedPnl))
            .ToList();
        return new LocalPaperLedgerSnapshot(stream.StreamId, revision, sequence, eventId,
            ParseDecimal(stream.InitialEquity), stream.Currency,
            checked(ParseDecimal(stream.InitialEquity) + realizedPnl), realizedPnl, totalFees,
            totalSlippage, snapshots, reservedRisk, reservedNotional, reservedSlots,
            legacyBlocked,
            legacyBlocked ? "Legacy paper rows require explicit recovery." : null);
    }

    private static bool HasCapacity(SqliteConnection connection, SqliteTransaction transaction,
        LocalPaperReservationRequest request)
    {
        if (request.MaxRiskAmount is null || request.MaxNotionalAmount is null
            || request.MaxPositionSlots is null)
        {
            return false;
        }

        decimal currentRisk = 0m;
        decimal currentNotional = 0m;
        int currentSlots = 0;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT risk_amount, notional_amount, position_slots
            FROM paper_reservations
            WHERE stream_id = $stream_id COLLATE BINARY AND status = $status;
            """;
        command.Parameters.AddWithValue("$stream_id", request.StreamId);
        command.Parameters.AddWithValue("$status", ReservationState.Reserved);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            currentRisk = checked(currentRisk + ParseDecimal(reader.GetString(0)));
            currentNotional = checked(currentNotional + ParseDecimal(reader.GetString(1)));
            currentSlots = checked(currentSlots + reader.GetInt32(2));
        }

        return checked(currentRisk + request.RiskAmount) <= request.MaxRiskAmount.Value
            && checked(currentNotional + request.NotionalAmount) <= request.MaxNotionalAmount.Value
            && checked(currentSlots + request.PositionSlots) <= request.MaxPositionSlots.Value;
    }

    private static StreamRow InsertStream(SqliteConnection connection,
        SqliteTransaction transaction, LocalPaperReservationRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO paper_streams
                (stream_id, initial_equity, currency, configuration_identity,
                 max_risk, max_notional, max_position_slots)
            VALUES
                ($stream_id, $initial_equity, $currency, $configuration_identity,
                 $max_risk, $max_notional, $max_position_slots);
            """;
        command.Parameters.AddWithValue("$stream_id", request.StreamId);
        command.Parameters.AddWithValue("$initial_equity", DecimalText(request.InitialEquity));
        command.Parameters.AddWithValue("$currency", request.Currency);
        command.Parameters.AddWithValue("$configuration_identity", request.ConfigurationIdentity);
        command.Parameters.AddWithValue("$max_risk", NullableDecimalText(request.MaxRiskAmount));
        command.Parameters.AddWithValue("$max_notional",
            NullableDecimalText(request.MaxNotionalAmount));
        command.Parameters.AddWithValue("$max_position_slots",
            request.MaxPositionSlots is null ? DBNull.Value : request.MaxPositionSlots.Value);
        command.ExecuteNonQuery();
        return new StreamRow(request.StreamId, DecimalText(request.InitialEquity), request.Currency,
            request.ConfigurationIdentity, NullableDecimalText(request.MaxRiskAmount),
            NullableDecimalText(request.MaxNotionalAmount), request.MaxPositionSlots);
    }

    private static void InsertObservation(SqliteConnection connection,
        SqliteTransaction transaction, LocalPaperReservationRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO paper_observations
                (stream_id, sequence, event_id, envelope_json, application_json,
                 observation_identity, configuration_identity, plan_identity,
                 risk_input_identity, model_identity, status)
            VALUES
                ($stream_id, $sequence, $event_id, $envelope_json, $application_json,
                 $observation_identity, $configuration_identity, $plan_identity,
                 $risk_input_identity, $model_identity, $status);
            """;
        command.Parameters.AddWithValue("$stream_id", request.StreamId);
        command.Parameters.AddWithValue("$sequence", request.Sequence);
        command.Parameters.AddWithValue("$event_id", request.EventId);
        command.Parameters.AddWithValue("$envelope_json", request.EnvelopeJson);
        command.Parameters.AddWithValue("$application_json",
            request.ApplicationJson is null ? DBNull.Value : request.ApplicationJson);
        command.Parameters.AddWithValue("$observation_identity", request.ObservationIdentity);
        command.Parameters.AddWithValue("$configuration_identity", request.ConfigurationIdentity);
        command.Parameters.AddWithValue("$plan_identity",
            request.PlanIdentity is null ? DBNull.Value : request.PlanIdentity);
        command.Parameters.AddWithValue("$risk_input_identity",
            request.RiskInputIdentity is null ? DBNull.Value : request.RiskInputIdentity);
        command.Parameters.AddWithValue("$model_identity",
            request.ModelIdentity is null ? DBNull.Value : request.ModelIdentity);
        command.Parameters.AddWithValue("$status", ObservationState.Pending);
        command.ExecuteNonQuery();
    }

    private static void InsertReservation(SqliteConnection connection,
        SqliteTransaction transaction, LocalPaperReservationRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO paper_reservations
                (stream_id, sequence, reservation_id, risk_amount, notional_amount,
                 position_slots, currency, status)
            VALUES
                ($stream_id, $sequence, $reservation_id, $risk_amount, $notional_amount,
                 $position_slots, $currency, $status);
            """;
        command.Parameters.AddWithValue("$stream_id", request.StreamId);
        command.Parameters.AddWithValue("$sequence", request.Sequence);
        command.Parameters.AddWithValue("$reservation_id",
            $"paper-reservation:{request.ObservationIdentity}");
        command.Parameters.AddWithValue("$risk_amount", DecimalText(request.RiskAmount));
        command.Parameters.AddWithValue("$notional_amount", DecimalText(request.NotionalAmount));
        command.Parameters.AddWithValue("$position_slots", request.PositionSlots);
        command.Parameters.AddWithValue("$currency", request.Currency);
        command.Parameters.AddWithValue("$status", ReservationState.Reserved);
        command.ExecuteNonQuery();
    }

    private static void InsertFills(SqliteConnection connection, SqliteTransaction transaction,
        LocalPaperCommitRequest request)
    {
        for (int ordinal = 0; ordinal < request.Fills.Count; ordinal++)
        {
            LocalPaperFillRecord fill = request.Fills[ordinal];
            using SqliteCommand existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT stream_id, sequence, ordinal, instrument_id, direction, is_entry, role,
                       quantity, price, fee_amount, fee_currency, slippage_amount,
                       slippage_currency, filled_at_ticks, stable_sequence, event_id
                FROM paper_fills
                WHERE stream_id = $stream_id COLLATE BINARY AND fill_id = $fill_id;
                """;
            existing.Parameters.AddWithValue("$stream_id", request.StreamId);
            existing.Parameters.AddWithValue("$fill_id", fill.FillId);
            using SqliteDataReader reader = existing.ExecuteReader();
            if (reader.Read())
            {
                // A continuation replays the deterministic prefix, so an
                // already durable fill keeps its original journal sequence and
                // ordinal.  Fill identity is the immutable evidence below;
                // placement belongs to the first commit that observed it.
                bool same = StringComparer.Ordinal.Equals(reader.GetString(3), fill.InstrumentId)
                    && reader.GetInt32(4) == (int)fill.Direction
                    && reader.GetInt32(5) == (fill.IsEntry ? 1 : 0)
                    && StringComparer.Ordinal.Equals(reader.GetString(6), fill.Role)
                    && StringComparer.Ordinal.Equals(reader.GetString(7), DecimalText(fill.Quantity))
                    && StringComparer.Ordinal.Equals(reader.GetString(8), DecimalText(fill.Price))
                    && StringComparer.Ordinal.Equals(reader.GetString(9), DecimalText(fill.FeeAmount))
                    && StringComparer.Ordinal.Equals(reader.GetString(10), fill.Currency)
                    && StringComparer.Ordinal.Equals(reader.GetString(11),
                        DecimalText(fill.SlippageAmount))
                    && StringComparer.Ordinal.Equals(reader.GetString(12), fill.Currency)
                    && reader.GetInt64(13) == fill.FilledAtTicks
                    && reader.GetInt64(14) == fill.StableSequence
                    && StringComparer.Ordinal.Equals(reader.GetString(15), fill.EventId);
                reader.Close();
                if (!same)
                {
                    throw new InvalidOperationException(
                        "A paper fill identity conflicts with durable evidence.");
                }

                continue;
            }

            reader.Close();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO paper_fills
                    (stream_id, sequence, ordinal, fill_id, instrument_id, direction, is_entry,
                     role, quantity, price, fee_amount, fee_currency, slippage_amount,
                     slippage_currency, filled_at_ticks, stable_sequence, event_id)
                VALUES
                    ($stream_id, $sequence, $ordinal, $fill_id, $instrument_id, $direction,
                     $is_entry, $role, $quantity, $price, $fee_amount, $fee_currency,
                     $slippage_amount, $slippage_currency, $filled_at_ticks, $stable_sequence,
                     $event_id);
                """;
            insert.Parameters.AddWithValue("$stream_id", request.StreamId);
            insert.Parameters.AddWithValue("$sequence", request.Sequence);
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$fill_id", fill.FillId);
            insert.Parameters.AddWithValue("$instrument_id", fill.InstrumentId);
            insert.Parameters.AddWithValue("$direction", (int)fill.Direction);
            insert.Parameters.AddWithValue("$is_entry", fill.IsEntry ? 1 : 0);
            insert.Parameters.AddWithValue("$role", fill.Role);
            insert.Parameters.AddWithValue("$quantity", DecimalText(fill.Quantity));
            insert.Parameters.AddWithValue("$price", DecimalText(fill.Price));
            insert.Parameters.AddWithValue("$fee_amount", DecimalText(fill.FeeAmount));
            insert.Parameters.AddWithValue("$fee_currency", fill.Currency);
            insert.Parameters.AddWithValue("$slippage_amount", DecimalText(fill.SlippageAmount));
            insert.Parameters.AddWithValue("$slippage_currency", fill.Currency);
            insert.Parameters.AddWithValue("$filled_at_ticks", fill.FilledAtTicks);
            insert.Parameters.AddWithValue("$stable_sequence", fill.StableSequence);
            insert.Parameters.AddWithValue("$event_id", fill.EventId);
            insert.ExecuteNonQuery();
        }
    }

    private static void UpdateReservation(SqliteConnection connection,
        SqliteTransaction transaction, LocalPaperCommitRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE paper_reservations
            SET status = $status
            WHERE stream_id = $stream_id COLLATE BINARY AND sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$status", request.ReleaseReservation
            ? ReservationState.Released : ReservationState.Reserved);
        command.Parameters.AddWithValue("$stream_id", request.StreamId);
        command.Parameters.AddWithValue("$sequence", request.ReservationSequence ?? request.Sequence);
        command.ExecuteNonQuery();
    }

    private static void UpdateObservation(SqliteConnection connection,
        SqliteTransaction transaction, LocalPaperCommitRequest request)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE paper_observations
            SET status = $status, result_identity = $result_identity,
                result_json = $result_json, block_reason = NULL
            WHERE stream_id = $stream_id COLLATE BINARY AND sequence = $sequence
              AND status = $pending;
            """;
        command.Parameters.AddWithValue("$status", ObservationState.Complete);
        command.Parameters.AddWithValue("$result_identity", request.ResultIdentity);
        command.Parameters.AddWithValue("$result_json", request.ResultJson);
        command.Parameters.AddWithValue("$stream_id", request.StreamId);
        command.Parameters.AddWithValue("$sequence", request.Sequence);
        command.Parameters.AddWithValue("$pending", ObservationState.Pending);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The paper observation could not be completed.");
        }
    }

    private static void UpsertCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, LocalPaperLedgerSnapshot snapshot)
    {
        long expectedRevision = checked(snapshot.Revision - 1);
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE paper_checkpoints_v2
            SET sequence = $sequence, event_id = $event_id, revision = $revision,
                state_json = $state_json
            WHERE stream_id = $stream_id COLLATE BINARY AND revision = $expected_revision;
            """;
        update.Parameters.AddWithValue("$sequence", snapshot.LastSequence!.Value);
        update.Parameters.AddWithValue("$event_id", snapshot.LastEventId!);
        update.Parameters.AddWithValue("$revision", snapshot.Revision);
        update.Parameters.AddWithValue("$state_json", snapshot.StateJson);
        update.Parameters.AddWithValue("$stream_id", snapshot.StreamId);
        update.Parameters.AddWithValue("$expected_revision", expectedRevision);
        if (update.ExecuteNonQuery() == 1)
        {
            return;
        }

        if (expectedRevision != 0)
        {
            throw new InvalidDataException("The paper checkpoint revision changed concurrently.");
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO paper_checkpoints_v2
                (stream_id, sequence, event_id, revision, state_json)
            VALUES ($stream_id, $sequence, $event_id, $revision, $state_json);
            """;
        insert.Parameters.AddWithValue("$stream_id", snapshot.StreamId);
        insert.Parameters.AddWithValue("$sequence", snapshot.LastSequence!.Value);
        insert.Parameters.AddWithValue("$event_id", snapshot.LastEventId!);
        insert.Parameters.AddWithValue("$revision", snapshot.Revision);
        insert.Parameters.AddWithValue("$state_json", snapshot.StateJson);
        insert.ExecuteNonQuery();
    }

    private static void RefreshCheckpointForReservation(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        CheckpointRow? checkpoint = ReadCheckpointRow(connection, transaction, streamId);
        if (checkpoint is null)
        {
            return;
        }

        StreamRow stream = ReadStream(connection, transaction, streamId)
            ?? throw new InvalidDataException("The paper stream metadata is missing.");
        LocalPaperLedgerSnapshot snapshot = ComputeLedger(connection, transaction, stream,
            checkpoint.Revision, checkpoint.Sequence, checkpoint.EventId,
            IsLegacyBlocked(connection, transaction));
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE paper_checkpoints_v2
            SET state_json = $state_json
            WHERE stream_id = $stream_id COLLATE BINARY AND revision = $revision;
            """;
        update.Parameters.AddWithValue("$state_json", snapshot.StateJson);
        update.Parameters.AddWithValue("$stream_id", streamId);
        update.Parameters.AddWithValue("$revision", checkpoint.Revision);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The paper reservation checkpoint changed concurrently.");
        }
    }

    private static ObservationRow? ReadObservation(SqliteConnection connection,
        SqliteTransaction? transaction, string streamId, long sequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stream_id, sequence, event_id, envelope_json, application_json,
                   observation_identity, configuration_identity, plan_identity,
                   risk_input_identity, model_identity, status, block_reason,
                   result_identity, result_json
            FROM paper_observations
            WHERE stream_id = $stream_id COLLATE BINARY AND sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$sequence", sequence);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadObservationRow(reader) : null;
    }

    private static ObservationRow? ReadObservationByEventId(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, string eventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stream_id, sequence, event_id, envelope_json, application_json,
                   observation_identity, configuration_identity, plan_identity,
                   risk_input_identity, model_identity, status, block_reason,
                   result_identity, result_json
            FROM paper_observations
            WHERE stream_id = $stream_id COLLATE BINARY AND event_id = $event_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$event_id", eventId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadObservationRow(reader) : null;
    }

    private static ObservationRow ReadObservationRow(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    private static LocalPaperObservationRecord ReadObservation(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    private static long ReadRevision(SqliteConnection connection, SqliteTransaction transaction,
        string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM paper_checkpoints_v2
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long? ReadMaximumSequence(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(sequence)
            FROM paper_observations
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static StreamRow? ReadStream(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stream_id, initial_equity, currency, configuration_identity,
                   max_risk, max_notional, max_position_slots
            FROM paper_streams
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new StreamRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6));
    }

    private static CheckpointRow? ReadCheckpointRow(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stream_id, sequence, event_id, revision, state_json
            FROM paper_checkpoints_v2
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new CheckpointRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4))
            : null;
    }

    private static bool IsLegacyBlocked(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT value
            FROM paper_metadata
            WHERE key = 'legacy_blocked' COLLATE BINARY;
            """;
        return StringComparer.Ordinal.Equals(command.ExecuteScalar() as string, "1");
    }

    private static bool HasLegacyRows(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS
            (
                SELECT 1 FROM paper_entries
                UNION ALL
                SELECT 1 FROM paper_checkpoints
            );
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void ValidateStoredResult(ObservationRow observation)
    {
        if (observation.Status == ObservationState.Complete
            && (observation.ResultIdentity is null || observation.ResultJson is null))
        {
            throw new InvalidDataException("A completed paper observation has no result.");
        }

        if (observation.ResultJson is not null)
        {
            ValidateJson(observation.ResultJson, "A persisted paper result is corrupt.");
        }
    }

    private static void ValidateJson(string json, string message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is JsonValueKind.Null
                or JsonValueKind.Undefined)
            {
                throw new JsonException();
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static ReplayEnvelope DeserializeEnvelope(string json)
    {
        try
        {
            return CoreJsonSerializer.Deserialize<ReplayEnvelope>(json);
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException or InvalidOperationException or NotSupportedException
            or OverflowException)
        {
            throw new InvalidDataException("A persisted paper envelope is corrupt.", exception);
        }
    }

    private static bool JsonEquivalent(string left, string right) =>
        StringComparer.Ordinal.Equals(CanonicalizeJson(left), CanonicalizeJson(right));

    private static string CanonicalizeJson(string json)
    {
        ValidateJson(json, "A persisted paper JSON document is corrupt.");
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, LedgerJsonOptions);
    }

    private static string DecimalText(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    private static string? NullableDecimalText(decimal? value) =>
        value is null ? null : DecimalText(value.Value);

    private static decimal ParseDecimal(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture,
                out decimal result))
        {
            throw new InvalidDataException("A persisted paper decimal is corrupt.");
        }

        return result;
    }

    private static Direction ReadDirection(int value) => value switch
    {
        (int)Direction.Long => Direction.Long,
        (int)Direction.Short => Direction.Short,
        _ => throw new InvalidDataException("A persisted paper direction is invalid.")
    };

    private static void SetMetadata(SqliteConnection connection, SqliteTransaction transaction,
        string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO paper_metadata (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string[] ReadUserObjectNames(SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type IN ('table', 'index', 'trigger', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static bool HasUserObjects(SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        return ReadUserObjectNames(connection, transaction).Length != 0;
    }

    private static bool ColumnsMatch(SqliteConnection connection, SqliteTransaction? transaction,
        string table, IReadOnlyList<string> expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> actual = [];
        while (reader.Read())
        {
            actual.Add(reader.GetString(1));
        }

        return actual.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static long ReadUserVersion(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection connection,
        SqliteTransaction transaction, int version)
    {
        Execute(connection, transaction, $"PRAGMA user_version = {version};");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Connect(string path, int timeoutSeconds)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = timeoutSeconds
        }.ToString());
        try
        {
            connection.Open();
            using SqliteCommand journal = connection.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            journal.ExecuteNonQuery();
            using SqliteCommand synchronous = connection.CreateCommand();
            synchronous.CommandText = "PRAGMA synchronous = FULL;";
            synchronous.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static JsonSerializerOptions CreateLedgerJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static class ObservationState
    {
        public const string Pending = "pending";
        public const string Complete = "complete";
        public const string Blocked = "blocked";
    }

    private static class ReservationState
    {
        public const string Reserved = "reserved";
        public const string Released = "released";
    }

    private readonly record struct PositionKey(string InstrumentId, Direction Direction);

    private sealed class PositionAccumulator
    {
        public decimal Quantity { get; private set; }

        public decimal AverageEntryPrice { get; private set; }

        public decimal RealizedPnl { get; private set; }

        public void AddEntry(decimal quantity, decimal price)
        {
            decimal notional = checked(AverageEntryPrice * Quantity
                + price * quantity);
            Quantity = checked(Quantity + quantity);
            AverageEntryPrice = Quantity == 0m ? 0m : notional / Quantity;
        }

        public decimal AddExit(decimal quantity, decimal price, Direction direction)
        {
            if (quantity > Quantity)
            {
                throw new InvalidDataException("A paper exit overfills the virtual position.");
            }

            decimal pnl = direction == Direction.Long
                ? checked((price - AverageEntryPrice) * quantity)
                : checked((AverageEntryPrice - price) * quantity);
            Quantity = checked(Quantity - quantity);
            if (Quantity == 0m)
            {
                AverageEntryPrice = 0m;
            }

            RealizedPnl = checked(RealizedPnl + pnl);
            return pnl;
        }
    }

    private sealed record StreamRow(string StreamId, string InitialEquity, string Currency,
        string ConfigurationIdentity, string? MaxRisk, string? MaxNotional,
        int? MaxPositionSlots)
    {
        public bool Matches(LocalPaperReservationRequest request) =>
            StringComparer.Ordinal.Equals(InitialEquity, DecimalText(request.InitialEquity))
            && StringComparer.Ordinal.Equals(Currency, request.Currency)
            && StringComparer.Ordinal.Equals(ConfigurationIdentity,
                request.ConfigurationIdentity)
            && StringComparer.Ordinal.Equals(MaxRisk,
                NullableDecimalText(request.MaxRiskAmount))
            && StringComparer.Ordinal.Equals(MaxNotional,
                NullableDecimalText(request.MaxNotionalAmount))
            && MaxPositionSlots == request.MaxPositionSlots;
    }

    private sealed record CheckpointRow(string StreamId, long Sequence, string EventId,
        long Revision, string StateJson);

    private sealed record ObservationRow(string StreamId, long Sequence, string EventId,
        string EnvelopeJson, string? ApplicationJson, string ObservationIdentity,
        string ConfigurationIdentity, string? PlanIdentity, string? RiskInputIdentity,
        string? ModelIdentity, string Status, string? BlockReason, string? ResultIdentity,
        string? ResultJson)
    {
        public bool Matches(LocalPaperReservationRequest request) =>
            StringComparer.Ordinal.Equals(StreamId, request.StreamId)
            && Sequence == request.Sequence
            && StringComparer.Ordinal.Equals(EventId, request.EventId)
            && StringComparer.Ordinal.Equals(EnvelopeJson, request.EnvelopeJson)
            && StringComparer.Ordinal.Equals(ApplicationJson, request.ApplicationJson)
            && StringComparer.Ordinal.Equals(ObservationIdentity, request.ObservationIdentity)
            && StringComparer.Ordinal.Equals(ConfigurationIdentity, request.ConfigurationIdentity)
            && StringComparer.Ordinal.Equals(PlanIdentity, request.PlanIdentity)
            && StringComparer.Ordinal.Equals(RiskInputIdentity, request.RiskInputIdentity)
            && StringComparer.Ordinal.Equals(ModelIdentity, request.ModelIdentity);
    }
}

/// <summary>Preparation outcome for a local-paper durable intent.</summary>
public enum LocalPaperPreparationStatus
{
    Prepared,
    AlreadyPrepared,
    AlreadyCompleted,
    Blocked
}

/// <summary>Durable preparation result returned before simulation.</summary>
public sealed record LocalPaperPreparation
{
    public LocalPaperPreparationStatus Status { get; }

    public long ExpectedRevision { get; }

    public string? BlockReason { get; }

    private LocalPaperPreparation(LocalPaperPreparationStatus status, long expectedRevision,
        string? blockReason)
    {
        Status = status;
        ExpectedRevision = expectedRevision;
        BlockReason = blockReason;
    }

    public static LocalPaperPreparation Prepared(long expectedRevision) =>
        new(LocalPaperPreparationStatus.Prepared, expectedRevision, null);

    public static LocalPaperPreparation AlreadyPrepared(long expectedRevision) =>
        new(LocalPaperPreparationStatus.AlreadyPrepared, expectedRevision, null);

    public static LocalPaperPreparation AlreadyCompleted(long expectedRevision) =>
        new(LocalPaperPreparationStatus.AlreadyCompleted, expectedRevision, null);

    public static LocalPaperPreparation Blocked(string reason) =>
        new(LocalPaperPreparationStatus.Blocked, 0, reason);
}

/// <summary>Primitive request used by App to reserve capacity before simulation.</summary>
public sealed record LocalPaperReservationRequest
{
    public string StreamId { get; }
    public long Sequence { get; }
    public string EventId { get; }
    public string EnvelopeJson { get; }
    public string? ApplicationJson { get; }
    public string ObservationIdentity { get; }
    public string ConfigurationIdentity { get; }
    public string? PlanIdentity { get; }
    public string? RiskInputIdentity { get; }
    public string? ModelIdentity { get; }
    public decimal InitialEquity { get; }
    public string Currency { get; }
    public decimal RiskAmount { get; }
    public decimal NotionalAmount { get; }
    public int PositionSlots { get; }
    public decimal? MaxRiskAmount { get; }
    public decimal? MaxNotionalAmount { get; }
    public int? MaxPositionSlots { get; }

    public LocalPaperReservationRequest(string streamId, long sequence, string eventId,
        string envelopeJson, string? applicationJson, string observationIdentity,
        string configurationIdentity, string? planIdentity, string? riskInputIdentity,
        string? modelIdentity, decimal initialEquity, string currency, decimal riskAmount,
        decimal notionalAmount, int positionSlots, decimal? maxRiskAmount,
        decimal? maxNotionalAmount, int? maxPositionSlots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialEquity, 0m);

        if (riskAmount < 0m || notionalAmount < 0m || positionSlots < 0
            || maxRiskAmount is < 0m || maxNotionalAmount is < 0m
            || maxPositionSlots is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(riskAmount));
        }

        StreamId = streamId;
        Sequence = sequence;
        EventId = eventId;
        EnvelopeJson = envelopeJson;
        ApplicationJson = applicationJson;
        ObservationIdentity = observationIdentity;
        ConfigurationIdentity = configurationIdentity;
        PlanIdentity = planIdentity;
        RiskInputIdentity = riskInputIdentity;
        ModelIdentity = modelIdentity;
        InitialEquity = initialEquity;
        Currency = currency;
        RiskAmount = riskAmount;
        NotionalAmount = notionalAmount;
        PositionSlots = positionSlots;
        MaxRiskAmount = maxRiskAmount;
        MaxNotionalAmount = maxNotionalAmount;
        MaxPositionSlots = maxPositionSlots;
    }
}

/// <summary>One primitive simulated fill retained in the virtual journal.</summary>
public sealed record LocalPaperFillRecord
{
    public string FillId { get; }
    public string InstrumentId { get; }
    public Direction Direction { get; }
    public bool IsEntry { get; }
    public string Role { get; }
    public decimal Quantity { get; }
    public decimal Price { get; }
    public decimal FeeAmount { get; }
    public decimal SlippageAmount { get; }
    public string Currency { get; }
    public long FilledAtTicks { get; }
    public long StableSequence { get; }
    public string EventId { get; }

    public LocalPaperFillRecord(string fillId, string instrumentId, Direction direction,
        bool isEntry, string role, decimal quantity, decimal price, decimal feeAmount,
        decimal slippageAmount, string currency, long filledAtTicks, long stableSequence,
        string eventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (!Enum.IsDefined(direction) || quantity <= 0m || price <= 0m
            || feeAmount < 0m || slippageAmount < 0m || filledAtTicks < 0 || stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        FillId = fillId;
        InstrumentId = instrumentId;
        Direction = direction;
        IsEntry = isEntry;
        Role = role;
        Quantity = quantity;
        Price = price;
        FeeAmount = feeAmount;
        SlippageAmount = slippageAmount;
        Currency = currency;
        FilledAtTicks = filledAtTicks;
        StableSequence = stableSequence;
        EventId = eventId;
    }
}

/// <summary>Atomic completion request for one prepared local-paper observation.</summary>
public sealed record LocalPaperCommitRequest
{
    public string StreamId { get; }
    public long Sequence { get; }
    public string EventId { get; }
    public string ObservationIdentity { get; }
    public string ResultIdentity { get; }
    public string ResultJson { get; }
    public IReadOnlyList<LocalPaperFillRecord> Fills { get; }
    public long ExpectedRevision { get; }
    public bool ReleaseReservation { get; }
    public long? ReservationSequence { get; }

    public LocalPaperCommitRequest(string streamId, long sequence, string eventId,
        string observationIdentity, string resultIdentity, string resultJson,
        IEnumerable<LocalPaperFillRecord> fills, long expectedRevision,
        bool releaseReservation, long? reservationSequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        ArgumentNullException.ThrowIfNull(fills);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (reservationSequence is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reservationSequence));
        }
        LocalPaperFillRecord[] snapshot = fills.ToArray();
        if (snapshot.Any(static fill => fill is null))
        {
            throw new ArgumentException("Paper fills cannot contain null values.", nameof(fills));
        }

        StreamId = streamId;
        Sequence = sequence;
        EventId = eventId;
        ObservationIdentity = observationIdentity;
        ResultIdentity = resultIdentity;
        ResultJson = resultJson;
        Fills = new ReadOnlyCollection<LocalPaperFillRecord>(snapshot);
        ExpectedRevision = expectedRevision;
        ReleaseReservation = releaseReservation;
        ReservationSequence = reservationSequence;
    }
}

/// <summary>Request to retain a pending intent as fail-closed.</summary>
public sealed record LocalPaperBlockRequest
{
    public string StreamId { get; }
    public long Sequence { get; }
    public string ObservationIdentity { get; }
    public string Reason { get; }

    public LocalPaperBlockRequest(string streamId, long sequence, string observationIdentity,
        string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        StreamId = streamId;
        Sequence = sequence;
        ObservationIdentity = observationIdentity;
        Reason = reason;
    }
}

/// <summary>Atomic completion result.</summary>
public sealed record LocalPaperCommitResult
{
    public bool WasApplied { get; }
    public bool WasAlreadyCompleted { get; }
    public bool IsBlocked { get; }
    public string? BlockReason { get; }
    public LocalPaperLedgerSnapshot Snapshot { get; }

    private LocalPaperCommitResult(bool applied, bool alreadyCompleted, bool isBlocked,
        string? blockReason, LocalPaperLedgerSnapshot snapshot)
    {
        WasApplied = applied;
        WasAlreadyCompleted = alreadyCompleted;
        IsBlocked = isBlocked;
        BlockReason = blockReason;
        Snapshot = snapshot;
    }

    public static LocalPaperCommitResult Applied(LocalPaperLedgerSnapshot snapshot) =>
        new(true, false, false, null, snapshot);

    public static LocalPaperCommitResult AlreadyCompleted(LocalPaperLedgerSnapshot snapshot) =>
        new(false, true, false, null, snapshot);

    public static LocalPaperCommitResult Blocked(string reason) =>
        new(false, false, true, reason, EmptySnapshot);

    private static LocalPaperLedgerSnapshot EmptySnapshot { get; } =
        new("empty", 0, null, null, 1m, "UNKNOWN", 1m, 0m, 0m, 0m, [], 0m, 0m, 0,
            true, "The paper completion was blocked.");
}

/// <summary>One immutable virtual position derived from paper fills.</summary>
public sealed record LocalPaperPositionSnapshot(string InstrumentId, Direction Direction,
    decimal Quantity, decimal AverageEntryPrice, decimal RealizedPnl);

/// <summary>Complete virtual ledger derived from durable fills and reservations.</summary>
public sealed record LocalPaperLedgerSnapshot
{
    public string StreamId { get; }
    public long Revision { get; }
    public long? LastSequence { get; }
    public string? LastEventId { get; }
    public decimal InitialEquity { get; }
    public string Currency { get; }
    public decimal Equity { get; }
    public decimal RealizedPnl { get; }
    public decimal TotalFees { get; }
    public decimal TotalSlippage { get; }
    public IReadOnlyList<LocalPaperPositionSnapshot> Positions { get; }
    public decimal ReservedRisk { get; }
    public decimal ReservedNotional { get; }
    public int ReservedPositionSlots { get; }
    public bool RecoveryBlocked { get; }
    public string? RecoveryBlockReason { get; }

    [JsonIgnore]
    public string StateJson => JsonSerializer.Serialize(this, CreateStateJsonOptions());

    public LocalPaperLedgerSnapshot(string streamId, long revision, long? lastSequence,
        string? lastEventId, decimal initialEquity, string currency, decimal equity,
        decimal realizedPnl, decimal totalFees, decimal totalSlippage,
        IEnumerable<LocalPaperPositionSnapshot> positions, decimal reservedRisk,
        decimal reservedNotional, int reservedPositionSlots, bool recoveryBlocked,
        string? recoveryBlockReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(positions);
        StreamId = streamId;
        Revision = revision;
        LastSequence = lastSequence;
        LastEventId = lastEventId;
        InitialEquity = initialEquity;
        Currency = currency;
        Equity = equity;
        RealizedPnl = realizedPnl;
        TotalFees = totalFees;
        TotalSlippage = totalSlippage;
        Positions = new ReadOnlyCollection<LocalPaperPositionSnapshot>(positions.ToArray());
        ReservedRisk = reservedRisk;
        ReservedNotional = reservedNotional;
        ReservedPositionSlots = reservedPositionSlots;
        RecoveryBlocked = recoveryBlocked;
        RecoveryBlockReason = recoveryBlockReason;
    }

    private static JsonSerializerOptions CreateStateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}

/// <summary>Typed view of one durable observation row.</summary>
public sealed record LocalPaperObservationRecord(string StreamId, long Sequence, string EventId,
    string EnvelopeJson, string? ApplicationJson, string ObservationIdentity,
    string ConfigurationIdentity, string? PlanIdentity, string? RiskInputIdentity,
    string? ModelIdentity, string Status, string? BlockReason, string? ResultIdentity,
    string? ResultJson);

/// <summary>Read-only, run-scoped journal plus derived ledger for evidence export.</summary>
public sealed record LocalPaperAuditSnapshot(string StreamId,
    IReadOnlyList<LocalPaperObservationRecord> Observations,
    IReadOnlyList<LocalPaperFillRecord> Fills, LocalPaperLedgerSnapshot Ledger);
