using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Isolated, append-only signal journal. It owns a standalone SQLite schema and
/// never initializes or migrates the shared operational database.
/// </summary>
public sealed class SqliteSignalJournalStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteSignalJournalStore(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public static SqliteSignalJournalStore Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);
        if (StringComparer.Ordinal.Equals(databasePath, ":memory:"))
        {
            throw new ArgumentException("The signal journal must be file-backed.",
                nameof(databasePath));
        }

        string fullPath = Path.GetFullPath(databasePath);
        Initialize(fullPath, commandTimeoutSeconds);
        return new SqliteSignalJournalStore(fullPath, commandTimeoutSeconds);
    }

    /// <summary>
    /// Atomically commits an input cursor, immutable signal/scenario evidence,
    /// diagnostics, the scenario checkpoint, and output-only outbox rows.
    /// Exact retries return <see langword="false"/>; identity/hash conflicts fail.
    /// </summary>
    public bool Append(SignalJournalCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        string snapshotJson = CoreJsonSerializer.Serialize(commit.ScenarioSnapshot);
        string snapshotHash = ComputeHash(snapshotJson);
        string commitJson = SerializeCommit(commit, snapshotHash);
        string commitHash = ComputeHash(commitJson);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        InputRow? existingSequence = FindInput(connection, transaction,
            commit.StreamId, commit.InputSequence);
        if (existingSequence is not null)
        {
            ValidateInputRow(existingSequence);
            if (IsExactRetry(existingSequence, commit, snapshotHash, commitJson, commitHash))
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The signal journal input sequence already contains different content.");
        }

        InputRow? existingEvent = FindInput(connection, transaction,
            commit.StreamId, commit.InputEventId);
        if (existingEvent is not null)
        {
            ValidateInputRow(existingEvent);
            if (IsExactRetry(existingEvent, commit, snapshotHash, commitJson, commitHash))
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The signal journal input identity already contains different content.");
        }

        CurrentStateRow? current = ReadCurrentState(connection, transaction,
            commit.StreamId);
        long currentSequence = current?.Checkpoint.InputSequence ?? 0;
        if (commit.ExpectedPreviousSequence != currentSequence)
        {
            throw new InvalidOperationException(
                "The signal journal cursor changed before the projection commit.");
        }

        if (current is not null)
        {
            SignalJournalCheckpoint checkpoint = current.Checkpoint;
            if (commit.InputSequence <= checkpoint.InputSequence
                || commit.StableSequence <= checkpoint.StableSequence
                || commit.AvailabilityTime.Value < checkpoint.AvailabilityTime.Value)
            {
                throw new InvalidOperationException(
                    "The signal journal input does not advance the causal cursor.");
            }
        }

        ValidateScenarioAdvance(current?.ScenarioSnapshot, commit);

        if (commit.SignalRecord is not null)
        {
            InsertSignal(connection, transaction, commit.StreamId,
                commit.SignalRecord, commit.EvidenceRecord!);
        }

        if (commit.Diagnostic is not null)
        {
            InsertDiagnostic(connection, transaction, commit, commit.Diagnostic);
        }

        InsertInput(connection, transaction, commit, snapshotHash, commitJson,
            commitHash);
        InsertScenarioEvents(connection, transaction, commit);
        InsertOutbox(connection, transaction, commit);
        WriteCheckpoint(connection, transaction, commit, snapshotJson, snapshotHash,
            commitHash);

        transaction.Commit();
        return true;
    }

    /// <summary>Reads only the durable cursor and scenario snapshot for restart.</summary>
    public SignalJournalCurrentState ReadCurrentState(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        CurrentStateRow? state = ReadCurrentState(connection, transaction, streamId);
        transaction.Commit();
        return state is null
            ? new SignalJournalCurrentState(null, null)
            : new SignalJournalCurrentState(state.Checkpoint, state.ScenarioSnapshot);
    }

    /// <summary>Reads and validates the committed journal contents for one stream.</summary>
    public SignalJournalSnapshot Read(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        CurrentStateRow? current = ReadCurrentState(connection, transaction, streamId);
        List<SignalJournalSignal> signals = ReadSignals(connection, transaction, streamId);
        List<SignalScenarioEvent> events = ReadScenarioEvents(connection, transaction,
            streamId);
        List<SignalJournalDiagnostic> diagnostics = ReadDiagnostics(connection,
            transaction, streamId);
        List<SignalJournalOutboxItem> outbox = ReadOutbox(connection, transaction,
            streamId, signals, events);
        ValidateInputHistory(connection, transaction, streamId,
            current?.Checkpoint);
        ValidateScenarioEvents(current?.ScenarioSnapshot, events);
        ValidateNoOrphanRows(connection, transaction, streamId, current is not null);
        transaction.Commit();
        return new SignalJournalSnapshot(current?.Checkpoint,
            current?.ScenarioSnapshot, new ImmutableValueList<SignalJournalSignal>(signals),
            new ImmutableValueList<SignalScenarioEvent>(events),
            new ImmutableValueList<SignalJournalDiagnostic>(diagnostics),
            new ImmutableValueList<SignalJournalOutboxItem>(outbox));
    }

    public void Dispose()
    {
    }

    private static bool IsExactRetry(InputRow existing, SignalJournalCommit commit,
        string snapshotHash, string commitJson, string commitHash) =>
        existing.InputSequence == commit.InputSequence
        && StringComparer.Ordinal.Equals(existing.InputEventId, commit.InputEventId)
        && StringComparer.Ordinal.Equals(existing.InputHash, commit.InputHash)
        && existing.EventTimeTicks == ToTicks(commit.EventTime)
        && existing.AvailabilityTimeTicks == ToTicks(commit.AvailabilityTime)
        && existing.StableSequence == commit.StableSequence
        && StringComparer.Ordinal.Equals(existing.ScenarioSnapshotHash, snapshotHash)
        && StringComparer.Ordinal.Equals(existing.CommitHash, commitHash)
        && StringComparer.Ordinal.Equals(existing.CommitJson, commitJson);

    private static void ValidateScenarioAdvance(
        SignalScenarioProjectionSnapshot? previous, SignalJournalCommit commit)
    {
        SignalScenarioProjectionSnapshot next = commit.ScenarioSnapshot;
        if (previous is null)
        {
            if (commit.ExpectedPreviousSequence != 0
                || commit.ProcessedScenarioInputHash is null
                    && (next.ProcessedInputs.Count != 0 || next.Events.Count != 0
                        || next.Episodes.Count != 0)
                || commit.ProcessedScenarioInputHash is not null
                    && next.ProcessedInputs.Count != 1)
            {
                throw new InvalidOperationException(
                    "The initial signal journal checkpoint must start from the supplied scenario transition.");
            }

            ValidateScenarioTail(previousInputCount: 0, previousEventCount: 0,
                next, commit);
            return;
        }

        if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(previous.Policies),
                CoreJsonSerializer.Serialize(next.Policies))
            || !StringComparer.Ordinal.Equals(previous.ProjectionVersion,
                next.ProjectionVersion)
            || previous.FormatVersion != next.FormatVersion
            || next.ProcessedInputs.Count < previous.ProcessedInputs.Count
            || next.Events.Count < previous.Events.Count)
        {
            throw new InvalidOperationException(
                "A signal journal commit cannot replace its scenario policy or history.");
        }

        for (int index = 0; index < previous.ProcessedInputs.Count; index++)
        {
            if (previous.ProcessedInputs[index] != next.ProcessedInputs[index])
            {
                throw new InvalidOperationException(
                    "A signal journal commit cannot rewrite a processed scenario input.");
            }
        }

        for (int index = 0; index < previous.Events.Count; index++)
        {
            if (!StringComparer.Ordinal.Equals(previous.Events[index].EventId,
                    next.Events[index].EventId))
            {
                throw new InvalidOperationException(
                    "A signal journal commit cannot rewrite a scenario event.");
            }
        }

        ValidateScenarioTail(previous.ProcessedInputs.Count, previous.Events.Count,
            next, commit);
    }

    private static void ValidateScenarioTail(int previousInputCount,
        int previousEventCount, SignalScenarioProjectionSnapshot next,
        SignalJournalCommit commit)
    {
        int addedInputs = next.ProcessedInputs.Count - previousInputCount;
        if (commit.ProcessedScenarioInputHash is null)
        {
            if (addedInputs != 0)
            {
                throw new InvalidOperationException(
                    "A scenario checkpoint advanced without a committed input hash.");
            }
        }
        else
        {
            if (addedInputs != 1)
            {
                throw new InvalidOperationException(
                    "A signal journal commit must add exactly one processed scenario input.");
            }

            SignalScenarioProcessedInput processed = next.ProcessedInputs[^1];
            if (!StringComparer.Ordinal.Equals(processed.StreamId, commit.StreamId)
                || !StringComparer.Ordinal.Equals(processed.EventId, commit.InputEventId)
                || !StringComparer.Ordinal.Equals(processed.InputHash,
                    commit.ProcessedScenarioInputHash)
                || processed.EventTime != commit.EventTime
                || processed.AvailabilityTime != commit.AvailabilityTime
                || processed.StableSequence != commit.StableSequence)
            {
                throw new InvalidOperationException(
                    "The processed scenario input does not match the committed causal cursor.");
            }
        }

        int addedEvents = next.Events.Count - previousEventCount;
        if (addedEvents != commit.NewScenarioEvents.Count)
        {
            throw new InvalidOperationException(
                "The scenario event suffix does not match the committed transition.");
        }

        for (int index = 0; index < addedEvents; index++)
        {
            if (!StringComparer.Ordinal.Equals(
                    next.Events[previousEventCount + index].EventId,
                    commit.NewScenarioEvents[index].EventId))
            {
                throw new InvalidOperationException(
                    "The scenario event suffix does not match the committed transition.");
            }
        }
    }

    private static void Initialize(string databasePath, int commandTimeoutSeconds)
    {
        using SqliteConnection connection = OpenRawConnection(databasePath,
            commandTimeoutSeconds);
        connection.Open();
        long version = ReadUserVersion(connection);
        if (version == 0)
        {
            if (HasUserObjects(connection))
            {
                throw new InvalidDataException(
                    "A non-empty SQLite file cannot be initialized as a signal journal.");
            }

            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            if (ReadUserVersion(connection, transaction) != 0
                || HasUserObjects(connection, transaction))
            {
                throw new InvalidDataException(
                    "The SQLite file changed while the signal journal was initialized.");
            }

            CreateSchema(connection, transaction);
            SetUserVersion(connection, transaction, SchemaVersion);
            transaction.Commit();
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidDataException(
                $"SQLite schema version {version} is not a supported signal journal schema.");
        }
        else
        {
            ValidateSchema(connection);
        }

        ConfigureDurability(connection);
    }

    private static SqliteConnection OpenRawConnection(string databasePath,
        int commandTimeoutSeconds)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = commandTimeoutSeconds
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = OpenRawConnection(_databasePath,
            _commandTimeoutSeconds);
        try
        {
            connection.Open();
            ConfigureDurability(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ConfigureDurability(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous = FULL;");
    }

    private static void CreateSchema(SqliteConnection connection,
        SqliteTransaction transaction) => ExecuteNonQuery(connection, transaction, """
        CREATE TABLE signal_journal_inputs (
            stream_id TEXT NOT NULL COLLATE BINARY,
            input_sequence INTEGER NOT NULL CHECK (input_sequence > 0),
            input_event_id TEXT NOT NULL COLLATE BINARY,
            input_hash TEXT NOT NULL COLLATE BINARY CHECK (length(input_hash) = 64),
            event_time_ticks INTEGER NOT NULL,
            availability_time_ticks INTEGER NOT NULL,
            stable_sequence INTEGER NOT NULL CHECK (stable_sequence >= 0),
            scenario_snapshot_hash TEXT NOT NULL COLLATE BINARY CHECK (length(scenario_snapshot_hash) = 64),
            commit_hash TEXT NOT NULL COLLATE BINARY CHECK (length(commit_hash) = 64),
            commit_json TEXT NOT NULL,
            PRIMARY KEY (stream_id, input_sequence),
            UNIQUE (stream_id, input_event_id)
        ) WITHOUT ROWID;
        CREATE TABLE signal_journal_checkpoints (
            stream_id TEXT PRIMARY KEY COLLATE BINARY,
            input_sequence INTEGER NOT NULL CHECK (input_sequence > 0),
            input_event_id TEXT NOT NULL COLLATE BINARY,
            input_hash TEXT NOT NULL COLLATE BINARY CHECK (length(input_hash) = 64),
            event_time_ticks INTEGER NOT NULL,
            availability_time_ticks INTEGER NOT NULL,
            stable_sequence INTEGER NOT NULL CHECK (stable_sequence >= 0),
            commit_hash TEXT NOT NULL COLLATE BINARY CHECK (length(commit_hash) = 64),
            scenario_snapshot_hash TEXT NOT NULL COLLATE BINARY CHECK (length(scenario_snapshot_hash) = 64),
            scenario_snapshot_json TEXT NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE signal_journal_signals (
            stream_id TEXT NOT NULL COLLATE BINARY,
            signal_id TEXT NOT NULL COLLATE BINARY,
            record_hash TEXT NOT NULL COLLATE BINARY CHECK (length(record_hash) = 64),
            evidence_hash TEXT NOT NULL COLLATE BINARY CHECK (length(evidence_hash) = 64),
            signal_json TEXT NOT NULL,
            evidence_json TEXT NOT NULL,
            PRIMARY KEY (stream_id, signal_id)
        ) WITHOUT ROWID;
        CREATE TABLE signal_journal_scenario_events (
            stream_id TEXT NOT NULL COLLATE BINARY,
            input_sequence INTEGER NOT NULL CHECK (input_sequence > 0),
            event_ordinal INTEGER NOT NULL CHECK (event_ordinal >= 0),
            event_id TEXT NOT NULL COLLATE BINARY,
            payload_hash TEXT NOT NULL COLLATE BINARY CHECK (length(payload_hash) = 64),
            event_json TEXT NOT NULL,
            PRIMARY KEY (stream_id, input_sequence, event_ordinal),
            UNIQUE (stream_id, event_id)
        ) WITHOUT ROWID;
        CREATE TABLE signal_journal_diagnostics (
            stream_id TEXT NOT NULL COLLATE BINARY,
            diagnostic_id TEXT NOT NULL COLLATE BINARY,
            input_sequence INTEGER NOT NULL CHECK (input_sequence > 0),
            payload_hash TEXT NOT NULL COLLATE BINARY CHECK (length(payload_hash) = 64),
            diagnostic_json TEXT NOT NULL,
            PRIMARY KEY (stream_id, diagnostic_id)
        ) WITHOUT ROWID;
        CREATE TABLE signal_journal_outbox (
            stream_id TEXT NOT NULL COLLATE BINARY,
            outbox_id TEXT NOT NULL COLLATE BINARY,
            input_sequence INTEGER NOT NULL CHECK (input_sequence > 0),
            event_ordinal INTEGER NOT NULL CHECK (event_ordinal >= 0),
            event_id TEXT NOT NULL COLLATE BINARY,
            signal_id TEXT NOT NULL COLLATE BINARY,
            related_signal_id TEXT COLLATE BINARY,
            signal_record_hash TEXT NOT NULL COLLATE BINARY CHECK (length(signal_record_hash) = 64),
            related_signal_record_hash TEXT COLLATE BINARY,
            payload_hash TEXT NOT NULL COLLATE BINARY CHECK (length(payload_hash) = 64),
            payload_json TEXT NOT NULL,
            PRIMARY KEY (stream_id, outbox_id),
            UNIQUE (stream_id, event_id),
            CHECK ((related_signal_id IS NULL) = (related_signal_record_hash IS NULL)),
            CHECK (related_signal_record_hash IS NULL OR length(related_signal_record_hash) = 64)
        ) WITHOUT ROWID;
        """);

    private static void ValidateSchema(SqliteConnection connection)
    {
        (string Table, string[] Columns)[] expected =
        [
            ("signal_journal_inputs", ["stream_id", "input_sequence", "input_event_id",
                "input_hash", "event_time_ticks", "availability_time_ticks",
                "stable_sequence", "scenario_snapshot_hash", "commit_hash", "commit_json"]),
            ("signal_journal_checkpoints", ["stream_id", "input_sequence", "input_event_id",
                "input_hash", "event_time_ticks", "availability_time_ticks",
                "stable_sequence", "commit_hash", "scenario_snapshot_hash",
                "scenario_snapshot_json"]),
            ("signal_journal_signals", ["stream_id", "signal_id", "record_hash",
                "evidence_hash", "signal_json", "evidence_json"]),
            ("signal_journal_scenario_events", ["stream_id", "input_sequence",
                "event_ordinal", "event_id", "payload_hash", "event_json"]),
            ("signal_journal_diagnostics", ["stream_id", "diagnostic_id",
                "input_sequence", "payload_hash", "diagnostic_json"]),
            ("signal_journal_outbox", ["stream_id", "outbox_id", "input_sequence",
                "event_ordinal", "event_id", "signal_id", "related_signal_id",
                "signal_record_hash", "related_signal_record_hash", "payload_hash",
                "payload_json"])
        ];

        foreach ((string table, string[] columns) in expected)
        {
            if (!HasTable(connection, table))
            {
                throw new InvalidDataException("The signal journal schema is incomplete.");
            }

            ValidateColumns(connection, table, columns);
        }

        using SqliteCommand tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name COLLATE BINARY;
            """;
        using SqliteDataReader tablesReader = tablesCommand.ExecuteReader();
        List<string> actualTables = [];
        while (tablesReader.Read())
        {
            actualTables.Add(tablesReader.GetString(0));
        }

        string[] expectedTables = expected.Select(static item => item.Table)
            .OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        if (!expectedTables.SequenceEqual(actualTables, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The signal journal database contains tables outside its isolated schema.");
        }
    }

    private static void ValidateColumns(SqliteConnection connection, string table,
        IReadOnlyList<string> expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> actual = [];
        while (reader.Read())
        {
            actual.Add(reader.GetString(1));
        }

        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"The signal journal table '{table}' is invalid.");
        }
    }

    private static bool HasUserObjects(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type IN ('table', 'index', 'view', 'trigger')
                  AND name NOT LIKE 'sqlite_%'
            );
            """;
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasTable(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type = 'table' AND name = $name COLLATE BINARY
            );
            """;
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static long ReadUserVersion(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection connection,
        SqliteTransaction transaction, int version) =>
        ExecuteNonQuery(connection, transaction, $"PRAGMA user_version = {version};");

    private static CurrentStateRow? ReadCurrentState(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT input_sequence, input_event_id, input_hash, event_time_ticks,
                   availability_time_ticks, stable_sequence, commit_hash,
                   scenario_snapshot_hash, scenario_snapshot_json
            FROM signal_journal_checkpoints
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        SignalJournalCheckpoint? checkpoint = null;
        string? snapshotJson = null;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                checkpoint = new SignalJournalCheckpoint(reader.GetInt64(0),
                    reader.GetString(1), reader.GetString(2),
                    FromTicks(reader.GetInt64(3)), FromTicks(reader.GetInt64(4)),
                    reader.GetInt64(5), reader.GetString(6), reader.GetString(7));
                snapshotJson = reader.GetString(8);
            }
        }

        if (checkpoint is null)
        {
            if (HasStreamRows(connection, transaction, streamId))
            {
                throw new InvalidDataException(
                    "Signal journal rows exist without a durable checkpoint.");
            }

            return null;
        }

        ValidateCheckpointMetadata(checkpoint, snapshotJson!);
        SignalScenarioProjectionSnapshot snapshot =
            DeserializeScenarioSnapshot(snapshotJson!);
        ValidateLatestInput(connection, transaction, streamId, checkpoint);
        return new CurrentStateRow(checkpoint, snapshot);
    }

    private static void ValidateCheckpointMetadata(SignalJournalCheckpoint checkpoint,
        string snapshotJson)
    {
        if (checkpoint.InputSequence <= 0 || checkpoint.StableSequence < 0)
        {
            throw new InvalidDataException("The signal journal checkpoint cursor is invalid.");
        }

        RequireSha256(checkpoint.InputHash, "checkpoint input hash");
        RequireSha256(checkpoint.CommitHash, "checkpoint commit hash");
        RequireSha256(checkpoint.ScenarioSnapshotHash, "checkpoint snapshot hash");
        if (!StringComparer.Ordinal.Equals(ComputeHash(snapshotJson),
                checkpoint.ScenarioSnapshotHash))
        {
            throw new InvalidDataException(
                "The signal journal checkpoint snapshot hash is invalid.");
        }
    }

    private static void ValidateLatestInput(SqliteConnection connection,
        SqliteTransaction transaction, string streamId,
        SignalJournalCheckpoint checkpoint)
    {
        InputRow? latest = FindInput(connection, transaction, streamId,
            checkpoint.InputSequence);
        if (latest is null)
        {
            throw new InvalidDataException(
                "The signal journal checkpoint has no matching input receipt.");
        }

        ValidateInputRow(latest);
        if (!StringComparer.Ordinal.Equals(latest.InputEventId,
                checkpoint.InputEventId)
            || !StringComparer.Ordinal.Equals(latest.InputHash, checkpoint.InputHash)
            || latest.EventTimeTicks != ToTicks(checkpoint.EventTime)
            || latest.AvailabilityTimeTicks != ToTicks(checkpoint.AvailabilityTime)
            || latest.StableSequence != checkpoint.StableSequence
            || !StringComparer.Ordinal.Equals(latest.CommitHash, checkpoint.CommitHash)
            || !StringComparer.Ordinal.Equals(latest.ScenarioSnapshotHash,
                checkpoint.ScenarioSnapshotHash))
        {
            throw new InvalidDataException(
                "The signal journal checkpoint does not match its immutable input receipt.");
        }
    }

    private static SignalScenarioProjectionSnapshot DeserializeScenarioSnapshot(string json)
    {
        try
        {
            SignalScenarioProjectionSnapshot snapshot =
                CoreJsonSerializer.Deserialize<SignalScenarioProjectionSnapshot>(json);
            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(snapshot), json))
            {
                throw new InvalidDataException(
                    "The signal journal scenario snapshot is not canonical JSON.");
            }

            return snapshot;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException or InvalidOperationException or FormatException
            or OverflowException)
        {
            throw new InvalidDataException(
                "The persisted signal journal scenario snapshot is invalid.", exception);
        }
    }

    private static bool HasStreamRows(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM signal_journal_inputs WHERE stream_id = $stream_id COLLATE BINARY
                UNION ALL SELECT 1 FROM signal_journal_signals WHERE stream_id = $stream_id COLLATE BINARY
                UNION ALL SELECT 1 FROM signal_journal_scenario_events WHERE stream_id = $stream_id COLLATE BINARY
                UNION ALL SELECT 1 FROM signal_journal_diagnostics WHERE stream_id = $stream_id COLLATE BINARY
                UNION ALL SELECT 1 FROM signal_journal_outbox WHERE stream_id = $stream_id COLLATE BINARY
            );
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static InputRow? FindInput(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, long inputSequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT input_sequence, input_event_id, input_hash, event_time_ticks,
                   availability_time_ticks, stable_sequence, scenario_snapshot_hash,
                   commit_hash, commit_json
            FROM signal_journal_inputs
            WHERE stream_id = $stream_id COLLATE BINARY
              AND input_sequence = $input_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$input_sequence", inputSequence);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadInputRow(reader) : null;
    }

    private static InputRow? FindInput(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, string inputEventId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT input_sequence, input_event_id, input_hash, event_time_ticks,
                   availability_time_ticks, stable_sequence, scenario_snapshot_hash,
                   commit_hash, commit_json
            FROM signal_journal_inputs
            WHERE stream_id = $stream_id COLLATE BINARY
              AND input_event_id = $input_event_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$input_event_id", inputEventId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadInputRow(reader) : null;
    }

    private static InputRow ReadInputRow(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
        reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
        reader.GetString(8));

    private static void ValidateInputRow(InputRow row)
    {
        if (row.InputSequence <= 0 || row.StableSequence < 0)
        {
            throw new InvalidDataException("A signal journal input cursor is invalid.");
        }

        RequireSha256(row.InputHash, "input hash");
        RequireSha256(row.ScenarioSnapshotHash, "scenario snapshot hash");
        RequireSha256(row.CommitHash, "commit hash");
        if (!StringComparer.Ordinal.Equals(ComputeHash(row.CommitJson), row.CommitHash))
        {
            throw new InvalidDataException("A signal journal commit hash is invalid.");
        }
    }

    private static List<SignalJournalSignal> ReadSignals(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT signal_id, record_hash, evidence_hash, signal_json, evidence_json
            FROM signal_journal_signals
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY signal_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        List<SignalJournalSignal> signals = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string signalId = reader.GetString(0);
            string recordHash = reader.GetString(1);
            string evidenceHash = reader.GetString(2);
            string signalJson = reader.GetString(3);
            string evidenceJson = reader.GetString(4);
            try
            {
                SignalRecord signal = CoreJsonSerializer.Deserialize<SignalRecord>(signalJson);
                SignalEvidenceRecord evidence =
                    CoreJsonSerializer.Deserialize<SignalEvidenceRecord>(evidenceJson);
                signal.ValidateEvidence(evidence);
                if (!StringComparer.Ordinal.Equals(signal.Identity.SignalId, signalId)
                    || !StringComparer.Ordinal.Equals(signal.RecordHash, recordHash)
                    || !StringComparer.Ordinal.Equals(evidence.RecordHash, evidenceHash)
                    || !StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(signal),
                        signalJson)
                    || !StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(evidence),
                        evidenceJson))
                {
                    throw new InvalidDataException(
                        "A signal journal record does not match its canonical row metadata.");
                }

                signals.Add(new SignalJournalSignal(signal, evidence));
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException
                or ArgumentException or InvalidOperationException or FormatException
                or OverflowException)
            {
                throw new InvalidDataException(
                    "A persisted signal journal record is invalid.", exception);
            }
        }

        return signals;
    }

    private static List<SignalScenarioEvent> ReadScenarioEvents(
        SqliteConnection connection, SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, payload_hash, event_json
            FROM signal_journal_scenario_events
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY input_sequence, event_ordinal;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        List<SignalScenarioEvent> events = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string eventId = reader.GetString(0);
            string payloadHash = reader.GetString(1);
            string eventJson = reader.GetString(2);
            SignalScenarioEvent scenarioEvent = DeserializeScenarioEvent(eventJson);
            if (!StringComparer.Ordinal.Equals(scenarioEvent.EventId, eventId)
                || !StringComparer.Ordinal.Equals(ComputeHash(eventJson), payloadHash)
                || !StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(scenarioEvent),
                    eventJson))
            {
                throw new InvalidDataException(
                    "A persisted scenario event does not match its immutable row.");
            }

            events.Add(scenarioEvent);
        }

        return events;
    }

    private static SignalScenarioEvent DeserializeScenarioEvent(string json)
    {
        try
        {
            return CoreJsonSerializer.Deserialize<SignalScenarioEvent>(json);
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException or InvalidOperationException or FormatException
            or OverflowException)
        {
            throw new InvalidDataException(
                "A persisted signal journal scenario event is invalid.", exception);
        }
    }

    private static List<SignalJournalDiagnostic> ReadDiagnostics(
        SqliteConnection connection, SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT diagnostic_id, payload_hash, diagnostic_json
            FROM signal_journal_diagnostics
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY input_sequence, diagnostic_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        List<SignalJournalDiagnostic> diagnostics = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string diagnosticId = reader.GetString(0);
            string payloadHash = reader.GetString(1);
            string json = reader.GetString(2);
            try
            {
                SignalJournalDiagnostic diagnostic =
                    CoreJsonSerializer.Deserialize<SignalJournalDiagnostic>(json);
                if (!StringComparer.Ordinal.Equals(diagnostic.DiagnosticId, diagnosticId)
                    || !StringComparer.Ordinal.Equals(ComputeHash(json), payloadHash)
                    || !StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(diagnostic),
                        json))
                {
                    throw new InvalidDataException(
                        "A persisted signal journal diagnostic does not match its row.");
                }

                diagnostics.Add(diagnostic);
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException
                or ArgumentException or InvalidOperationException or FormatException
                or OverflowException)
            {
                throw new InvalidDataException(
                    "A persisted signal journal diagnostic is invalid.", exception);
            }
        }

        return diagnostics;
    }

    private static List<SignalJournalOutboxItem> ReadOutbox(
        SqliteConnection connection, SqliteTransaction transaction, string streamId,
        IReadOnlyList<SignalJournalSignal> signals,
        IReadOnlyList<SignalScenarioEvent> events)
    {
        Dictionary<string, SignalRecord> signalsById = signals.ToDictionary(
            static item => item.Signal.Identity.SignalId, static item => item.Signal,
            StringComparer.Ordinal);
        Dictionary<string, SignalScenarioEvent> eventsById = events.ToDictionary(
            static item => item.EventId, StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT outbox_id, event_id, signal_id, related_signal_id,
                   signal_record_hash, related_signal_record_hash, payload_hash, payload_json
            FROM signal_journal_outbox
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY input_sequence, event_ordinal;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        List<SignalJournalOutboxItem> items = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string outboxId = reader.GetString(0);
            string eventId = reader.GetString(1);
            string signalId = reader.GetString(2);
            string? relatedSignalId = reader.IsDBNull(3) ? null : reader.GetString(3);
            string signalRecordHash = reader.GetString(4);
            string? relatedSignalRecordHash = reader.IsDBNull(5)
                ? null : reader.GetString(5);
            string payloadHash = reader.GetString(6);
            string payloadJson = reader.GetString(7);
            SignalScenarioEvent scenarioEvent = DeserializeScenarioEvent(payloadJson);
            if (!StringComparer.Ordinal.Equals(outboxId, eventId)
                || !StringComparer.Ordinal.Equals(scenarioEvent.EventId, eventId)
                || !StringComparer.Ordinal.Equals(scenarioEvent.SignalId, signalId)
                || !StringComparer.Ordinal.Equals(scenarioEvent.RelatedSignalId,
                    relatedSignalId)
                || !StringComparer.Ordinal.Equals(ComputeHash(payloadJson), payloadHash)
                || !StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(scenarioEvent),
                    payloadJson))
            {
                throw new InvalidDataException(
                    "A persisted signal outbox item does not match its immutable event.");
            }

            if (!signalsById.TryGetValue(signalId, out SignalRecord? signal)
                || signal is null
                || !StringComparer.Ordinal.Equals(signal.RecordHash, signalRecordHash))
            {
                throw new InvalidDataException(
                    "A persisted signal outbox item has no matching signal record.");
            }

            if (relatedSignalId is not null)
            {
                if (!signalsById.TryGetValue(relatedSignalId,
                        out SignalRecord? related)
                    || related is null
                    || !StringComparer.Ordinal.Equals(related.RecordHash,
                        relatedSignalRecordHash))
                {
                    throw new InvalidDataException(
                        "A persisted signal outbox item has an invalid related record link.");
                }
            }

            if (!eventsById.ContainsKey(eventId))
            {
                throw new InvalidDataException(
                    "A persisted signal outbox item has no scenario event.");
            }

            items.Add(new SignalJournalOutboxItem(outboxId, scenarioEvent,
                signalRecordHash, relatedSignalRecordHash, payloadHash));
        }

        return items;
    }

    private static void ValidateInputHistory(SqliteConnection connection,
        SqliteTransaction transaction, string streamId,
        SignalJournalCheckpoint? checkpoint)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT input_sequence, input_event_id, input_hash, event_time_ticks,
                   availability_time_ticks, stable_sequence, scenario_snapshot_hash,
                   commit_hash, commit_json
            FROM signal_journal_inputs
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY input_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        InputRow? previous = null;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            InputRow current = ReadInputRow(reader);
            ValidateInputRow(current);
            if (previous is not null
                && (current.InputSequence <= previous.InputSequence
                    || current.StableSequence <= previous.StableSequence
                    || current.AvailabilityTimeTicks < previous.AvailabilityTimeTicks))
            {
                throw new InvalidDataException(
                    "The signal journal input history is not causally monotonic.");
            }

            previous = current;
        }

        if ((previous is null) != (checkpoint is null)
            || previous is not null && checkpoint is not null
                && (previous.InputSequence != checkpoint.InputSequence
                    || !StringComparer.Ordinal.Equals(previous.InputEventId,
                        checkpoint.InputEventId)
                    || !StringComparer.Ordinal.Equals(previous.InputHash,
                        checkpoint.InputHash)
                    || previous.EventTimeTicks != ToTicks(checkpoint.EventTime)
                    || previous.AvailabilityTimeTicks != ToTicks(checkpoint.AvailabilityTime)
                    || previous.StableSequence != checkpoint.StableSequence
                    || !StringComparer.Ordinal.Equals(previous.CommitHash,
                        checkpoint.CommitHash)
                    || !StringComparer.Ordinal.Equals(previous.ScenarioSnapshotHash,
                        checkpoint.ScenarioSnapshotHash)))
        {
            throw new InvalidDataException(
                "The signal journal checkpoint does not match the latest input history row.");
        }
    }

    private static void ValidateScenarioEvents(
        SignalScenarioProjectionSnapshot? snapshot,
        List<SignalScenarioEvent> events)
    {
        if (snapshot is null)
        {
            if (events.Count != 0)
            {
                throw new InvalidDataException(
                    "Scenario events exist without a durable scenario checkpoint.");
            }

            return;
        }

        if (snapshot.Events.Count != events.Count)
        {
            throw new InvalidDataException(
                "The persisted scenario event log does not match its checkpoint.");
        }

        for (int index = 0; index < events.Count; index++)
        {
            if (!StringComparer.Ordinal.Equals(snapshot.Events[index].EventId,
                    events[index].EventId))
            {
                throw new InvalidDataException(
                    "The persisted scenario event log differs from its checkpoint.");
            }
        }
    }

    private static void ValidateNoOrphanRows(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, bool hasCheckpoint)
    {
        if (!hasCheckpoint && HasStreamRows(connection, transaction, streamId))
        {
            throw new InvalidDataException(
                "Signal journal rows exist without a durable checkpoint.");
        }
    }

    private static void InsertSignal(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, SignalRecord signal,
        SignalEvidenceRecord evidence)
    {
        string signalJson = CoreJsonSerializer.Serialize(signal);
        string evidenceJson = CoreJsonSerializer.Serialize(evidence);
        using SqliteCommand existingCommand = connection.CreateCommand();
        existingCommand.Transaction = transaction;
        existingCommand.CommandText = """
            SELECT record_hash, evidence_hash, signal_json, evidence_json
            FROM signal_journal_signals
            WHERE stream_id = $stream_id COLLATE BINARY
              AND signal_id = $signal_id COLLATE BINARY;
            """;
        existingCommand.Parameters.AddWithValue("$stream_id", streamId);
        existingCommand.Parameters.AddWithValue("$signal_id", signal.Identity.SignalId);
        using (SqliteDataReader reader = existingCommand.ExecuteReader())
        {
            if (reader.Read())
            {
                bool identical = StringComparer.Ordinal.Equals(reader.GetString(0),
                        signal.RecordHash)
                    && StringComparer.Ordinal.Equals(reader.GetString(1),
                        evidence.RecordHash)
                    && StringComparer.Ordinal.Equals(reader.GetString(2), signalJson)
                    && StringComparer.Ordinal.Equals(reader.GetString(3), evidenceJson);
                if (!identical)
                {
                    throw new InvalidOperationException(
                        "The logical signal identity already contains different immutable content.");
                }

                return;
            }
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO signal_journal_signals
                (stream_id, signal_id, record_hash, evidence_hash, signal_json, evidence_json)
            VALUES
                ($stream_id, $signal_id, $record_hash, $evidence_hash, $signal_json, $evidence_json);
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$signal_id", signal.Identity.SignalId);
        command.Parameters.AddWithValue("$record_hash", signal.RecordHash);
        command.Parameters.AddWithValue("$evidence_hash", evidence.RecordHash);
        command.Parameters.AddWithValue("$signal_json", signalJson);
        command.Parameters.AddWithValue("$evidence_json", evidenceJson);
        command.ExecuteNonQuery();
    }

    private static void InsertDiagnostic(SqliteConnection connection,
        SqliteTransaction transaction, SignalJournalCommit commit,
        SignalJournalDiagnostic diagnostic)
    {
        string json = CoreJsonSerializer.Serialize(diagnostic);
        string hash = ComputeHash(json);
        using SqliteCommand existingCommand = connection.CreateCommand();
        existingCommand.Transaction = transaction;
        existingCommand.CommandText = """
            SELECT payload_hash, diagnostic_json
            FROM signal_journal_diagnostics
            WHERE stream_id = $stream_id COLLATE BINARY
              AND diagnostic_id = $diagnostic_id COLLATE BINARY;
            """;
        existingCommand.Parameters.AddWithValue("$stream_id", commit.StreamId);
        existingCommand.Parameters.AddWithValue("$diagnostic_id", diagnostic.DiagnosticId);
        using (SqliteDataReader reader = existingCommand.ExecuteReader())
        {
            if (reader.Read())
            {
                if (StringComparer.Ordinal.Equals(reader.GetString(0), hash)
                    && StringComparer.Ordinal.Equals(reader.GetString(1), json))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "The signal journal diagnostic identity already contains different content.");
            }
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO signal_journal_diagnostics
                (stream_id, diagnostic_id, input_sequence, payload_hash, diagnostic_json)
            VALUES
                ($stream_id, $diagnostic_id, $input_sequence, $payload_hash, $diagnostic_json);
            """;
        command.Parameters.AddWithValue("$stream_id", commit.StreamId);
        command.Parameters.AddWithValue("$diagnostic_id", diagnostic.DiagnosticId);
        command.Parameters.AddWithValue("$input_sequence", commit.InputSequence);
        command.Parameters.AddWithValue("$payload_hash", hash);
        command.Parameters.AddWithValue("$diagnostic_json", json);
        command.ExecuteNonQuery();
    }

    private static void InsertInput(SqliteConnection connection,
        SqliteTransaction transaction, SignalJournalCommit commit,
        string snapshotHash, string commitJson, string commitHash)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO signal_journal_inputs
                (stream_id, input_sequence, input_event_id, input_hash, event_time_ticks,
                 availability_time_ticks, stable_sequence, scenario_snapshot_hash,
                 commit_hash, commit_json)
            VALUES
                ($stream_id, $input_sequence, $input_event_id, $input_hash, $event_time_ticks,
                 $availability_time_ticks, $stable_sequence, $scenario_snapshot_hash,
                 $commit_hash, $commit_json);
            """;
        command.Parameters.AddWithValue("$stream_id", commit.StreamId);
        command.Parameters.AddWithValue("$input_sequence", commit.InputSequence);
        command.Parameters.AddWithValue("$input_event_id", commit.InputEventId);
        command.Parameters.AddWithValue("$input_hash", commit.InputHash);
        command.Parameters.AddWithValue("$event_time_ticks", ToTicks(commit.EventTime));
        command.Parameters.AddWithValue("$availability_time_ticks",
            ToTicks(commit.AvailabilityTime));
        command.Parameters.AddWithValue("$stable_sequence", commit.StableSequence);
        command.Parameters.AddWithValue("$scenario_snapshot_hash", snapshotHash);
        command.Parameters.AddWithValue("$commit_hash", commitHash);
        command.Parameters.AddWithValue("$commit_json", commitJson);
        command.ExecuteNonQuery();
    }

    private static void InsertScenarioEvents(SqliteConnection connection,
        SqliteTransaction transaction, SignalJournalCommit commit)
    {
        for (int index = 0; index < commit.NewScenarioEvents.Count; index++)
        {
            SignalScenarioEvent scenarioEvent = commit.NewScenarioEvents[index];
            string json = CoreJsonSerializer.Serialize(scenarioEvent);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO signal_journal_scenario_events
                    (stream_id, input_sequence, event_ordinal, event_id, payload_hash, event_json)
                VALUES
                    ($stream_id, $input_sequence, $event_ordinal, $event_id, $payload_hash, $event_json);
                """;
            command.Parameters.AddWithValue("$stream_id", commit.StreamId);
            command.Parameters.AddWithValue("$input_sequence", commit.InputSequence);
            command.Parameters.AddWithValue("$event_ordinal", index);
            command.Parameters.AddWithValue("$event_id", scenarioEvent.EventId);
            command.Parameters.AddWithValue("$payload_hash", ComputeHash(json));
            command.Parameters.AddWithValue("$event_json", json);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertOutbox(SqliteConnection connection,
        SqliteTransaction transaction, SignalJournalCommit commit)
    {
        for (int index = 0; index < commit.NewScenarioEvents.Count; index++)
        {
            SignalScenarioEvent scenarioEvent = commit.NewScenarioEvents[index];
            string signalRecordHash = FindSignalRecordHash(connection, transaction,
                commit.StreamId, scenarioEvent.SignalId)
                ?? throw new InvalidOperationException(
                    "A scenario outbox event requires its durable signal record.");
            string? relatedSignalRecordHash = scenarioEvent.RelatedSignalId is { } relatedId
                ? FindSignalRecordHash(connection, transaction, commit.StreamId, relatedId)
                    ?? throw new InvalidOperationException(
                        "A scenario outbox event requires its related durable signal record.")
                : null;
            string json = CoreJsonSerializer.Serialize(scenarioEvent);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO signal_journal_outbox
                    (stream_id, outbox_id, input_sequence, event_ordinal, event_id,
                     signal_id, related_signal_id, signal_record_hash,
                     related_signal_record_hash, payload_hash, payload_json)
                VALUES
                    ($stream_id, $outbox_id, $input_sequence, $event_ordinal, $event_id,
                     $signal_id, $related_signal_id, $signal_record_hash,
                     $related_signal_record_hash, $payload_hash, $payload_json);
                """;
            command.Parameters.AddWithValue("$stream_id", commit.StreamId);
            command.Parameters.AddWithValue("$outbox_id", scenarioEvent.EventId);
            command.Parameters.AddWithValue("$input_sequence", commit.InputSequence);
            command.Parameters.AddWithValue("$event_ordinal", index);
            command.Parameters.AddWithValue("$event_id", scenarioEvent.EventId);
            command.Parameters.AddWithValue("$signal_id", scenarioEvent.SignalId);
            command.Parameters.AddWithValue("$related_signal_id", DbValue(
                scenarioEvent.RelatedSignalId));
            command.Parameters.AddWithValue("$signal_record_hash", signalRecordHash);
            command.Parameters.AddWithValue("$related_signal_record_hash",
                DbValue(relatedSignalRecordHash));
            command.Parameters.AddWithValue("$payload_hash", ComputeHash(json));
            command.Parameters.AddWithValue("$payload_json", json);
            command.ExecuteNonQuery();
        }
    }

    private static string? FindSignalRecordHash(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, string signalId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT record_hash
            FROM signal_journal_signals
            WHERE stream_id = $stream_id COLLATE BINARY
              AND signal_id = $signal_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$signal_id", signalId);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null
            : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, SignalJournalCommit commit,
        string snapshotJson, string snapshotHash, string commitHash)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO signal_journal_checkpoints
                (stream_id, input_sequence, input_event_id, input_hash, event_time_ticks,
                 availability_time_ticks, stable_sequence, commit_hash,
                 scenario_snapshot_hash, scenario_snapshot_json)
            VALUES
                ($stream_id, $input_sequence, $input_event_id, $input_hash, $event_time_ticks,
                 $availability_time_ticks, $stable_sequence, $commit_hash,
                 $scenario_snapshot_hash, $scenario_snapshot_json)
            ON CONFLICT(stream_id) DO UPDATE SET
                input_sequence = excluded.input_sequence,
                input_event_id = excluded.input_event_id,
                input_hash = excluded.input_hash,
                event_time_ticks = excluded.event_time_ticks,
                availability_time_ticks = excluded.availability_time_ticks,
                stable_sequence = excluded.stable_sequence,
                commit_hash = excluded.commit_hash,
                scenario_snapshot_hash = excluded.scenario_snapshot_hash,
                scenario_snapshot_json = excluded.scenario_snapshot_json
            WHERE signal_journal_checkpoints.input_sequence = $expected_previous_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", commit.StreamId);
        command.Parameters.AddWithValue("$input_sequence", commit.InputSequence);
        command.Parameters.AddWithValue("$input_event_id", commit.InputEventId);
        command.Parameters.AddWithValue("$input_hash", commit.InputHash);
        command.Parameters.AddWithValue("$event_time_ticks", ToTicks(commit.EventTime));
        command.Parameters.AddWithValue("$availability_time_ticks",
            ToTicks(commit.AvailabilityTime));
        command.Parameters.AddWithValue("$stable_sequence", commit.StableSequence);
        command.Parameters.AddWithValue("$commit_hash", commitHash);
        command.Parameters.AddWithValue("$scenario_snapshot_hash", snapshotHash);
        command.Parameters.AddWithValue("$scenario_snapshot_json", snapshotJson);
        command.Parameters.AddWithValue("$expected_previous_sequence",
            commit.ExpectedPreviousSequence);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The signal journal checkpoint changed during commit.");
        }
    }

    private static string SerializeCommit(SignalJournalCommit commit,
        string snapshotHash) => CoreJsonSerializer.Serialize(new
        {
            commit.FormatVersion,
            commit.StreamId,
            commit.InputSequence,
            commit.InputEventId,
            commit.InputHash,
            commit.EventTime,
            commit.AvailabilityTime,
            commit.StableSequence,
            commit.ProcessedScenarioInputHash,
            ScenarioSnapshotHash = snapshotHash,
            commit.NewScenarioEvents,
            commit.SignalRecord,
            commit.EvidenceRecord,
            commit.Diagnostic
        });

    private static void RequireSha256(string value, string description)
    {
        try
        {
            if (Convert.FromHexString(value).Length == SHA256.HashSizeInBytes
                && StringComparer.Ordinal.Equals(value, value.ToLowerInvariant()))
            {
                return;
            }
        }
        catch (FormatException)
        {
        }

        throw new InvalidDataException($"The signal journal {description} is not a SHA-256 value.");
    }

    private static string ComputeHash(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    private static long ToTicks(UtcTimestamp timestamp) => timestamp.Value.UtcDateTime.Ticks;

    private static UtcTimestamp FromTicks(long ticks) => new(
        new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)));

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static void ExecuteNonQuery(SqliteConnection connection, string sql) =>
        ExecuteNonQuery(connection, transaction: null, sql);

    private static void ExecuteNonQuery(SqliteConnection connection,
        SqliteTransaction? transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record InputRow(long InputSequence, string InputEventId,
        string InputHash, long EventTimeTicks, long AvailabilityTimeTicks,
        long StableSequence, string ScenarioSnapshotHash, string CommitHash,
        string CommitJson);

    private sealed record CurrentStateRow(SignalJournalCheckpoint Checkpoint,
        SignalScenarioProjectionSnapshot ScenarioSnapshot);
}
