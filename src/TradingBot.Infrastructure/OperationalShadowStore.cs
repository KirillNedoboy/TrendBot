using System.Data;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>One versioned, isolated SQLite file for operational shadow evidence.</summary>
/// <remarks>
/// This store deliberately owns a separate schema and migration number. It does
/// not call or alter the shared TradingBot SQLite schema.
/// </remarks>
public sealed class OperationalShadowStore : IDisposable
{
    public const int CurrentSchemaVersion = 2;

    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;
    private bool _disposed;

    private OperationalShadowStore(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public static int SchemaVersion => CurrentSchemaVersion;

    public bool IsHealthy
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                using SqliteConnection connection = OpenConnection(_databasePath,
                    _commandTimeoutSeconds);
                return ReadUserVersion(connection) == CurrentSchemaVersion;
            }
            catch (Exception exception) when (exception is SqliteException
                or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return false;
            }
        }
    }

    public static OperationalShadowStore Open(string databasePath, int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (StringComparer.Ordinal.Equals(databasePath, ":memory:"))
        {
            throw new ArgumentException(
                "Operational shadow persistence requires an isolated file-backed database.",
                nameof(databasePath));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);

        string fullPath = Path.GetFullPath(databasePath);
        OperationalShadowStore store = new(fullPath, commandTimeoutSeconds);
        Initialize(fullPath, commandTimeoutSeconds);
        return store;
    }

    public OperationalShadowAppendResult Append(OperationalShadowJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ThrowIfDisposed();

        using SqliteConnection connection = OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        PersistedRow? existing = ReadRow(connection, transaction, entry.StreamId,
            entry.StableSequence);
        if (existing is not null)
        {
            OperationalShadowJournalEntry stored = ToEntry(existing);
            if (stored == entry)
            {
                transaction.Commit();
                return new OperationalShadowAppendResult(false, true,
                    CreateCheckpoint(entry));
            }

            throw new InvalidOperationException(
                "The shadow stream sequence already contains a different entry.");
        }

        long? maximumSequence = ReadMaximumSequence(connection, transaction, entry.StreamId);
        if (maximumSequence is not null && entry.StableSequence <= maximumSequence.Value)
        {
            throw new InvalidOperationException(
                "A shadow entry must have a stable sequence greater than the current maximum.");
        }

        long? existingEventSequence = ReadEventSequence(connection, transaction, entry.StreamId,
            entry.EventId);
        if (existingEventSequence is not null)
        {
            throw new InvalidOperationException(
                "The shadow event identity is already associated with another sequence.");
        }

        InsertEntry(connection, transaction, entry);
        UpsertCheckpoint(connection, transaction, entry);
        transaction.Commit();
        return new OperationalShadowAppendResult(true, false, CreateCheckpoint(entry));
    }

    /// <summary>Appends one durable health snapshot within a distinct runtime run.</summary>
    public OperationalShadowHealthEvidenceAppendResult AppendHealthEvidence(
        OperationalShadowHealthEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ThrowIfDisposed();

        using SqliteConnection connection = OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        PersistedHealthEvidence? existing = ReadHealthEvidenceRow(connection, transaction,
            evidence.RunId, evidence.HealthSequence);
        if (existing is not null)
        {
            OperationalShadowHealthEvidence stored = ToHealthEvidence(existing);
            if (stored == evidence)
            {
                transaction.Commit();
                return new OperationalShadowHealthEvidenceAppendResult(false, true);
            }

            throw new InvalidOperationException(
                "The health run sequence already contains different evidence.");
        }

        long? maximumSequence = ReadMaximumHealthSequence(connection, transaction,
            evidence.RunId);
        if (maximumSequence is not null && evidence.HealthSequence <= maximumSequence.Value)
        {
            throw new InvalidOperationException(
                "Health evidence sequence must increase within a run.");
        }

        InsertHealthEvidence(connection, transaction, evidence);
        transaction.Commit();
        return new OperationalShadowHealthEvidenceAppendResult(true, false);
    }

    public IReadOnlyList<OperationalShadowHealthEvidence> ReadHealthEvidence(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ThrowIfDisposed();

        using SqliteConnection connection = OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, health_sequence, checked_at_ticks, causal_checkpoint_link,
                   evidence_version, health_json
            FROM shadow_health_evidence
            WHERE run_id = $run_id COLLATE BINARY
            ORDER BY health_sequence ASC;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        return ReadHealthEvidenceRows(command);
    }

    public IReadOnlyList<OperationalShadowHealthEvidence> ReadHealthEvidence() =>
        ReadAllHealthEvidence();

    public IReadOnlyList<OperationalShadowHealthEvidence> ReadAllHealthEvidence()
    {
        ThrowIfDisposed();

        using SqliteConnection connection = OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, health_sequence, checked_at_ticks, causal_checkpoint_link,
                   evidence_version, health_json
            FROM shadow_health_evidence
            ORDER BY run_id COLLATE BINARY, health_sequence ASC;
            """;
        return ReadHealthEvidenceRows(command);
    }

    public IReadOnlyList<OperationalShadowJournalEntry> ReadAll(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ThrowIfDisposed();

        using SqliteConnection connection = OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stream_id, stable_sequence, event_id, event_time_ticks,
                   receive_time_ticks, availability_time_ticks, exchange_time_ticks,
                   causal_event_json, derived_state_json, outcome_json, checkpoint_json
            FROM shadow_entries
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY stable_sequence ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);

        List<OperationalShadowJournalEntry> entries = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(ToEntry(ReadPersistedRow(reader)));
        }

        return entries.AsReadOnly();
    }

    public OperationalShadowCheckpoint? ReadCheckpoint(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ThrowIfDisposed();

        using SqliteConnection connection = OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stream_id, stable_sequence, event_id, derived_state_json,
                   outcome_json, checkpoint_json
            FROM shadow_checkpoints
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new OperationalShadowCheckpoint(reader.GetString(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5))
            : null;
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void Initialize(string databasePath, int commandTimeoutSeconds)
    {
        using SqliteConnection connection = OpenConnection(databasePath, commandTimeoutSeconds);
        long version = ReadUserVersion(connection);
        if (version == 0)
        {
            if (HasUserObjects(connection))
            {
                throw new InvalidDataException(
                    "The shadow database contains objects without a supported schema version.");
            }

            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            if (ReadUserVersion(connection, transaction) != 0
                || HasUserObjects(connection, transaction))
            {
                throw new InvalidDataException(
                    "The shadow database changed while its schema was being initialized.");
            }

            CreateSchema(connection, transaction);
            SetUserVersion(connection, transaction, CurrentSchemaVersion);
            transaction.Commit();
        }
        else if (version == 1)
        {
            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            MigrateV1ToV2(connection, transaction);
            SetUserVersion(connection, transaction, CurrentSchemaVersion);
            transaction.Commit();
        }
        else if (version != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Shadow schema version {version} is not supported.");
        }

        ValidateSchema(connection);
    }

    private static SqliteConnection OpenConnection(string databasePath, int timeoutSeconds)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = timeoutSeconds
        };
        SqliteConnection connection = new(builder.ConnectionString);
        try
        {
            connection.Open();
            ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
            ExecuteNonQuery(connection, "PRAGMA synchronous = FULL;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE shadow_entries
            (
                stream_id TEXT COLLATE BINARY NOT NULL,
                stable_sequence INTEGER NOT NULL,
                event_id TEXT COLLATE BINARY NOT NULL,
                event_time_ticks INTEGER NOT NULL,
                receive_time_ticks INTEGER NOT NULL,
                availability_time_ticks INTEGER NOT NULL,
                exchange_time_ticks INTEGER,
                causal_event_json TEXT NOT NULL,
                derived_state_json TEXT NOT NULL,
                outcome_json TEXT NOT NULL,
                checkpoint_json TEXT NOT NULL,
                PRIMARY KEY (stream_id, stable_sequence),
                UNIQUE (stream_id, event_id)
            ) WITHOUT ROWID;
            CREATE TABLE shadow_checkpoints
            (
                stream_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                stable_sequence INTEGER NOT NULL,
                event_id TEXT COLLATE BINARY NOT NULL,
                derived_state_json TEXT NOT NULL,
                outcome_json TEXT NOT NULL,
                checkpoint_json TEXT NOT NULL
            ) WITHOUT ROWID;
            CREATE TABLE shadow_health_evidence
            (
                run_id TEXT COLLATE BINARY NOT NULL,
                health_sequence INTEGER NOT NULL,
                checked_at_ticks INTEGER NOT NULL,
                causal_checkpoint_link TEXT COLLATE BINARY,
                evidence_version INTEGER NOT NULL,
                health_json TEXT NOT NULL,
                PRIMARY KEY (run_id, health_sequence)
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV1ToV2(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS shadow_health_evidence
            (
                run_id TEXT COLLATE BINARY NOT NULL,
                health_sequence INTEGER NOT NULL,
                checked_at_ticks INTEGER NOT NULL,
                causal_checkpoint_link TEXT COLLATE BINARY,
                evidence_version INTEGER NOT NULL,
                health_json TEXT NOT NULL,
                PRIMARY KEY (run_id, health_sequence)
            ) WITHOUT ROWID;
            """);
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
            AND name IN ('shadow_entries', 'shadow_checkpoints',
                         'shadow_health_evidence')
            ORDER BY name;
            """;
        List<string> names = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        if (!names.SequenceEqual(["shadow_checkpoints", "shadow_entries",
                                  "shadow_health_evidence"],
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The operational shadow schema does not match version 2.");
        }
    }

    private static void InsertEntry(SqliteConnection connection, SqliteTransaction transaction,
        OperationalShadowJournalEntry entry)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO shadow_entries
                (stream_id, stable_sequence, event_id, event_time_ticks,
                 receive_time_ticks, availability_time_ticks, exchange_time_ticks,
                 causal_event_json, derived_state_json, outcome_json, checkpoint_json)
            VALUES
                ($stream_id, $stable_sequence, $event_id, $event_time_ticks,
                 $receive_time_ticks, $availability_time_ticks, $exchange_time_ticks,
                 $causal_event_json, $derived_state_json, $outcome_json, $checkpoint_json);
            """;
        AddParameters(command, entry);
        command.ExecuteNonQuery();
    }

    private static void UpsertCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, OperationalShadowJournalEntry entry)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO shadow_checkpoints
                (stream_id, stable_sequence, event_id, derived_state_json,
                 outcome_json, checkpoint_json)
            VALUES
                ($stream_id, $stable_sequence, $event_id, $derived_state_json,
                 $outcome_json, $checkpoint_json)
            ON CONFLICT(stream_id) DO UPDATE SET
                stable_sequence = excluded.stable_sequence,
                event_id = excluded.event_id,
                derived_state_json = excluded.derived_state_json,
                outcome_json = excluded.outcome_json,
                checkpoint_json = excluded.checkpoint_json;
            """;
        command.Parameters.AddWithValue("$stream_id", entry.StreamId);
        command.Parameters.AddWithValue("$stable_sequence", entry.StableSequence);
        command.Parameters.AddWithValue("$event_id", entry.EventId);
        command.Parameters.AddWithValue("$derived_state_json", entry.DerivedStateJson);
        command.Parameters.AddWithValue("$outcome_json", entry.OutcomeJson);
        command.Parameters.AddWithValue("$checkpoint_json", entry.CheckpointJson);
        command.ExecuteNonQuery();
    }

    private static void InsertHealthEvidence(SqliteConnection connection,
        SqliteTransaction transaction, OperationalShadowHealthEvidence evidence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO shadow_health_evidence
                (run_id, health_sequence, checked_at_ticks, causal_checkpoint_link,
                 evidence_version, health_json)
            VALUES
                ($run_id, $health_sequence, $checked_at_ticks, $causal_checkpoint_link,
                 $evidence_version, $health_json);
            """;
        command.Parameters.AddWithValue("$run_id", evidence.RunId);
        command.Parameters.AddWithValue("$health_sequence", evidence.HealthSequence);
        command.Parameters.AddWithValue("$checked_at_ticks", ToTicks(evidence.CheckedAt));
        command.Parameters.AddWithValue("$causal_checkpoint_link",
            evidence.CausalCheckpointLink is null
                ? DBNull.Value : evidence.CausalCheckpointLink);
        command.Parameters.AddWithValue("$evidence_version", evidence.EvidenceVersion);
        command.Parameters.AddWithValue("$health_json", evidence.HealthJson);
        command.ExecuteNonQuery();
    }

    private static PersistedHealthEvidence? ReadHealthEvidenceRow(
        SqliteConnection connection, SqliteTransaction transaction, string runId,
        long healthSequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, health_sequence, checked_at_ticks, causal_checkpoint_link,
                   evidence_version, health_json
            FROM shadow_health_evidence
            WHERE run_id = $run_id COLLATE BINARY
              AND health_sequence = $health_sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$health_sequence", healthSequence);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadPersistedHealthEvidence(reader) : null;
    }

    private static long? ReadMaximumHealthSequence(SqliteConnection connection,
        SqliteTransaction transaction, string runId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(health_sequence)
            FROM shadow_health_evidence
            WHERE run_id = $run_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<OperationalShadowHealthEvidence>
        ReadHealthEvidenceRows(
        SqliteCommand command)
    {
        List<OperationalShadowHealthEvidence> evidence = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            evidence.Add(ToHealthEvidence(ReadPersistedHealthEvidence(reader)));
        }

        return evidence.AsReadOnly();
    }

    private static void AddParameters(SqliteCommand command, OperationalShadowJournalEntry entry)
    {
        command.Parameters.AddWithValue("$stream_id", entry.StreamId);
        command.Parameters.AddWithValue("$stable_sequence", entry.StableSequence);
        command.Parameters.AddWithValue("$event_id", entry.EventId);
        command.Parameters.AddWithValue("$event_time_ticks", ToTicks(entry.EventTime));
        command.Parameters.AddWithValue("$receive_time_ticks", ToTicks(entry.ReceiveTime));
        command.Parameters.AddWithValue("$availability_time_ticks",
            ToTicks(entry.AvailabilityTime));
        command.Parameters.AddWithValue("$exchange_time_ticks", entry.ExchangeTime is null
            ? DBNull.Value : ToTicks(entry.ExchangeTime));
        command.Parameters.AddWithValue("$causal_event_json", entry.CausalEventJson);
        command.Parameters.AddWithValue("$derived_state_json", entry.DerivedStateJson);
        command.Parameters.AddWithValue("$outcome_json", entry.OutcomeJson);
        command.Parameters.AddWithValue("$checkpoint_json", entry.CheckpointJson);
    }

    private static PersistedRow? ReadRow(SqliteConnection connection, SqliteTransaction transaction,
        string streamId, long stableSequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stream_id, stable_sequence, event_id, event_time_ticks,
                   receive_time_ticks, availability_time_ticks, exchange_time_ticks,
                   causal_event_json, derived_state_json, outcome_json, checkpoint_json
            FROM shadow_entries
            WHERE stream_id = $stream_id COLLATE BINARY
              AND stable_sequence = $stable_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$stable_sequence", stableSequence);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadPersistedRow(reader) : null;
    }

    private static long? ReadMaximumSequence(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(stable_sequence)
            FROM shadow_entries
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long? ReadEventSequence(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, string eventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stable_sequence
            FROM shadow_entries
            WHERE stream_id = $stream_id COLLATE BINARY
              AND event_id = $event_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$event_id", eventId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static PersistedRow ReadPersistedRow(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
        reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10));

    private static PersistedHealthEvidence ReadPersistedHealthEvidence(
        SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
        reader.GetString(5));

    private static OperationalShadowJournalEntry ToEntry(PersistedRow row) =>
        new(row.StreamId, row.EventId, row.StableSequence, FromTicks(row.EventTimeTicks),
            FromTicks(row.ReceiveTimeTicks), FromTicks(row.AvailabilityTimeTicks),
            row.ExchangeTimeTicks is null ? null : FromTicks(row.ExchangeTimeTicks.Value),
            row.CausalEventJson, row.DerivedStateJson, row.OutcomeJson, row.CheckpointJson);

    private static OperationalShadowHealthEvidence ToHealthEvidence(
        PersistedHealthEvidence row) => new(row.RunId, row.HealthSequence,
            FromTicks(row.CheckedAtTicks), row.HealthJson, row.CausalCheckpointLink,
            row.EvidenceVersion);

    private static long ReadUserVersion(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool HasUserObjects(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS
            (
                SELECT 1 FROM sqlite_master
                WHERE type IN ('table', 'index', 'trigger', 'view')
                  AND name NOT LIKE 'sqlite_%'
            );
            """;
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction,
        int version) => ExecuteNonQuery(connection, transaction,
            $"PRAGMA user_version = {version};");

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ToTicks(UtcTimestamp timestamp) => timestamp.Value.UtcDateTime.Ticks;

    private static UtcTimestamp FromTicks(long ticks) => new(
        new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static OperationalShadowCheckpoint CreateCheckpoint(
        OperationalShadowJournalEntry entry) => new(entry.StreamId, entry.StableSequence,
            entry.EventId, entry.DerivedStateJson, entry.OutcomeJson, entry.CheckpointJson);

    private sealed record PersistedRow(string StreamId, long StableSequence, string EventId,
        long EventTimeTicks, long ReceiveTimeTicks, long AvailabilityTimeTicks,
        long? ExchangeTimeTicks, string CausalEventJson, string DerivedStateJson,
        string OutcomeJson, string CheckpointJson);

    private sealed record PersistedHealthEvidence(string RunId, long HealthSequence,
        long CheckedAtTicks, string? CausalCheckpointLink, int EvidenceVersion,
        string HealthJson);
}

public sealed record OperationalShadowJournalEntry
{
    public const int CurrentFormatVersion = 1;

    public string StreamId { get; }
    public string EventId { get; }
    public long StableSequence { get; }
    public UtcTimestamp EventTime { get; }
    public UtcTimestamp ReceiveTime { get; }
    public UtcTimestamp AvailabilityTime { get; }
    public UtcTimestamp? ExchangeTime { get; }
    public string CausalEventJson { get; }
    public string DerivedStateJson { get; }
    public string OutcomeJson { get; }
    public string CheckpointJson { get; }

    public OperationalShadowJournalEntry(string streamId, string eventId, long stableSequence,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        UtcTimestamp? exchangeTime, string causalEventJson, string derivedStateJson,
        string outcomeJson, string checkpointJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException(
                "Shadow availability cannot precede receive time.", nameof(availabilityTime));
        }

        RequireJsonText(causalEventJson, nameof(causalEventJson));
        RequireJsonText(derivedStateJson, nameof(derivedStateJson));
        RequireJsonText(outcomeJson, nameof(outcomeJson));
        RequireJsonText(checkpointJson, nameof(checkpointJson));
        StreamId = streamId;
        EventId = eventId;
        StableSequence = stableSequence;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        ExchangeTime = exchangeTime;
        CausalEventJson = causalEventJson;
        DerivedStateJson = derivedStateJson;
        OutcomeJson = outcomeJson;
        CheckpointJson = checkpointJson;
    }

    private static void RequireJsonText(string value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
}

public sealed record OperationalShadowCheckpoint(
    string StreamId,
    long StableSequence,
    string EventId,
    string DerivedStateJson,
    string OutcomeJson,
    string CheckpointJson);

public sealed record OperationalShadowAppendResult(
    bool Appended,
    bool Duplicate,
    OperationalShadowCheckpoint Checkpoint);

public sealed record OperationalShadowHealthEvidence
{
    public const int CurrentEvidenceVersion = 1;

    public string RunId { get; }
    public long HealthSequence { get; }
    public UtcTimestamp CheckedAt { get; }
    public string HealthJson { get; }
    public string? CausalCheckpointLink { get; }
    public int EvidenceVersion { get; }

    public OperationalShadowHealthEvidence(string runId, long healthSequence,
        UtcTimestamp checkedAt, string healthJson, string? causalCheckpointLink = null,
        int evidenceVersion = CurrentEvidenceVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentOutOfRangeException.ThrowIfNegative(healthSequence);
        ArgumentNullException.ThrowIfNull(checkedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(healthJson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(evidenceVersion);

        RunId = runId;
        HealthSequence = healthSequence;
        CheckedAt = checkedAt;
        HealthJson = healthJson;
        CausalCheckpointLink = causalCheckpointLink;
        EvidenceVersion = evidenceVersion;
    }
}

public sealed record OperationalShadowHealthEvidenceAppendResult(
    bool Appended,
    bool Duplicate);
