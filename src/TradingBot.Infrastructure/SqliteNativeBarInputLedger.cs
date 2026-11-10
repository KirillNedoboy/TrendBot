using System.Collections.ObjectModel;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

public enum NativeBarInputReceiptStatus
{
    Accepted,
    Duplicate,
    Correction,
    Invalid
}

public sealed record NativeBarInputLedgerReceipt(
    long IntakeSequence,
    string? EventId,
    string? RevisionHash,
    NativeBarInputReceiptStatus Status,
    string PayloadJson,
    string? DiagnosticCode,
    string? DiagnosticMessage,
    string? TriggerEventId,
    UtcTimestamp? TriggerEventTime);

public sealed record NativeBarInputLedgerCheckpoint(
    long LastIntakeSequence,
    string? LastTriggerEventId,
    UtcTimestamp? LastTriggerEventTime);

public sealed record NativeBarInputLedgerSnapshot(
    NativeBarInputLedgerCheckpoint Checkpoint,
    IReadOnlyList<NativeBarInputLedgerReceipt> Receipts);

/// <summary>
/// Append-only native bar intake history in a separate SQLite file. This store
/// never uses or migrates the shared trading database schema.
/// </summary>
public sealed class SqliteNativeBarInputLedger : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteNativeBarInputLedger(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public static SqliteNativeBarInputLedger Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);
        if (StringComparer.Ordinal.Equals(databasePath, ":memory:"))
        {
            throw new ArgumentException("The native bar ledger must be file-backed.",
                nameof(databasePath));
        }

        string fullPath = Path.GetFullPath(databasePath);
        Initialize(fullPath, commandTimeoutSeconds);
        return new SqliteNativeBarInputLedger(fullPath, commandTimeoutSeconds);
    }

    /// <summary>Reads and validates the complete append-only stream snapshot.</summary>
    public NativeBarInputLedgerSnapshot Read(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        NativeBarInputLedgerCheckpoint checkpoint = ReadCheckpoint(connection, transaction,
            streamId);
        List<NativeBarInputLedgerReceipt> receipts = ReadReceipts(connection, transaction,
            streamId);
        ValidateSnapshot(checkpoint, receipts);
        transaction.Commit();
        return new NativeBarInputLedgerSnapshot(checkpoint,
            new ReadOnlyCollection<NativeBarInputLedgerReceipt>(receipts));
    }

    /// <summary>
    /// Appends one receipt and advances its sequence/checkpoint atomically.
    /// Identical retries at the same sequence return false.
    /// </summary>
    public bool Append(string streamId, NativeBarInputLedgerReceipt receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateReceipt(receipt);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        NativeBarInputLedgerReceipt? existing = FindReceipt(connection, transaction, streamId,
            receipt.IntakeSequence);
        if (existing is not null)
        {
            if (existing == receipt)
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The native bar intake sequence already contains a different receipt.");
        }

        NativeBarInputLedgerCheckpoint checkpoint = ReadCheckpoint(connection, transaction,
            streamId);
        if (receipt.IntakeSequence != checked(checkpoint.LastIntakeSequence + 1))
        {
            throw new InvalidOperationException(
                "A new native bar receipt must use the next intake sequence.");
        }

        ValidateRevisionTransition(connection, transaction, streamId, receipt);
        NativeBarInputLedgerCheckpoint nextCheckpoint = AdvanceCheckpoint(checkpoint, receipt);
        InsertReceipt(connection, transaction, streamId, receipt);
        WriteCheckpoint(connection, transaction, streamId, nextCheckpoint);
        transaction.Commit();
        return true;
    }

    public void Dispose()
    {
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
                    "A non-empty SQLite file cannot be initialized as a native bar ledger.");
            }

            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            if (ReadUserVersion(connection, transaction) != 0
                || HasUserObjects(connection, transaction))
            {
                throw new InvalidDataException(
                    "The SQLite file changed while the native bar ledger was initialized.");
            }

            CreateSchema(connection, transaction);
            SetUserVersion(connection, transaction, SchemaVersion);
            transaction.Commit();
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidDataException(
                $"SQLite schema version {version} is not a supported native bar ledger schema.");
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
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE native_bar_input_receipts (
                stream_id TEXT NOT NULL COLLATE BINARY,
                intake_sequence INTEGER NOT NULL CHECK (intake_sequence > 0),
                event_id TEXT COLLATE BINARY,
                revision_hash TEXT COLLATE BINARY,
                status TEXT NOT NULL CHECK (status IN ('accepted', 'duplicate', 'correction', 'invalid')),
                payload_json TEXT NOT NULL,
                payload_hash BLOB NOT NULL CHECK (length(payload_hash) = 32),
                diagnostic_code TEXT,
                diagnostic_message TEXT,
                trigger_event_id TEXT COLLATE BINARY,
                trigger_event_time_ticks INTEGER,
                PRIMARY KEY (stream_id, intake_sequence),
                CHECK ((trigger_event_id IS NULL) = (trigger_event_time_ticks IS NULL))
            ) WITHOUT ROWID;
            CREATE UNIQUE INDEX native_bar_input_accepted_identity
                ON native_bar_input_receipts(stream_id, event_id COLLATE BINARY)
                WHERE status = 'accepted';
            CREATE UNIQUE INDEX native_bar_input_trigger_identity
                ON native_bar_input_receipts(stream_id, trigger_event_id COLLATE BINARY)
                WHERE trigger_event_id IS NOT NULL;
            CREATE TABLE native_bar_input_checkpoints (
                stream_id TEXT PRIMARY KEY COLLATE BINARY,
                last_intake_sequence INTEGER NOT NULL CHECK (last_intake_sequence >= 0),
                last_trigger_event_id TEXT COLLATE BINARY,
                last_trigger_event_time_ticks INTEGER,
                CHECK ((last_trigger_event_id IS NULL) = (last_trigger_event_time_ticks IS NULL))
            ) WITHOUT ROWID;
            """);
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        if (!HasTable(connection, "native_bar_input_receipts")
            || !HasTable(connection, "native_bar_input_checkpoints")
            || !HasIndex(connection, "native_bar_input_accepted_identity")
            || !HasIndex(connection, "native_bar_input_trigger_identity"))
        {
            throw new InvalidDataException("The native bar ledger schema is incomplete.");
        }

        ValidateColumns(connection, "native_bar_input_receipts",
        [
            "stream_id", "intake_sequence", "event_id", "revision_hash", "status",
            "payload_json", "payload_hash", "diagnostic_code", "diagnostic_message",
            "trigger_event_id", "trigger_event_time_ticks"
        ]);
        ValidateColumns(connection, "native_bar_input_checkpoints",
        [
            "stream_id", "last_intake_sequence", "last_trigger_event_id",
            "last_trigger_event_time_ticks"
        ]);
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
            throw new InvalidDataException($"The native bar ledger table '{table}' is invalid.");
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
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            != 0;
    }

    private static bool HasTable(SqliteConnection connection, string name) =>
        HasSchemaObject(connection, "table", name);

    private static bool HasIndex(SqliteConnection connection, string name) =>
        HasSchemaObject(connection, "index", name);

    private static bool HasSchemaObject(SqliteConnection connection, string type, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type = $type AND name = $name COLLATE BINARY
            );
            """;
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
            != 0;
    }

    private static long ReadUserVersion(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(SqliteConnection connection,
        SqliteTransaction transaction, int version) =>
        ExecuteNonQuery(connection, transaction, $"PRAGMA user_version = {version};");

    private static NativeBarInputLedgerCheckpoint ReadCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT last_intake_sequence, last_trigger_event_id,
                   last_trigger_event_time_ticks
            FROM native_bar_input_checkpoints
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new NativeBarInputLedgerCheckpoint(0, null, null);
        }

        string? eventId = reader.IsDBNull(1) ? null : reader.GetString(1);
        UtcTimestamp? eventTime = reader.IsDBNull(2) ? null : FromTicks(reader.GetInt64(2));
        NativeBarInputLedgerCheckpoint checkpoint = new(reader.GetInt64(0), eventId, eventTime);
        if ((eventId is null) != (eventTime is null) || checkpoint.LastIntakeSequence < 0)
        {
            throw new InvalidDataException("The native bar ledger checkpoint is invalid.");
        }

        return checkpoint;
    }

    private static List<NativeBarInputLedgerReceipt> ReadReceipts(
        SqliteConnection connection, SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intake_sequence, event_id, revision_hash, status, payload_json,
                   payload_hash, diagnostic_code, diagnostic_message, trigger_event_id,
                   trigger_event_time_ticks
            FROM native_bar_input_receipts
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY intake_sequence ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        List<NativeBarInputLedgerReceipt> receipts = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            receipts.Add(ReadReceipt(reader));
        }

        return receipts;
    }

    private static NativeBarInputLedgerReceipt? FindReceipt(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, long sequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intake_sequence, event_id, revision_hash, status, payload_json,
                   payload_hash, diagnostic_code, diagnostic_message, trigger_event_id,
                   trigger_event_time_ticks
            FROM native_bar_input_receipts
            WHERE stream_id = $stream_id COLLATE BINARY
              AND intake_sequence = $intake_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$intake_sequence", sequence);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadReceipt(reader) : null;
    }

    private static NativeBarInputLedgerReceipt ReadReceipt(SqliteDataReader reader)
    {
        string statusText = reader.GetString(3);
        if (!Enum.TryParse(statusText, ignoreCase: true,
                out NativeBarInputReceiptStatus status) || !Enum.IsDefined(status))
        {
            throw new InvalidDataException("The native bar ledger contains an unknown receipt status.");
        }

        string payloadJson = reader.GetString(4);
        byte[] storedHash = reader.GetFieldValue<byte[]>(5);
        byte[] actualHash = ComputePayloadHash(payloadJson);
        if (storedHash.Length != actualHash.Length
            || !CryptographicOperations.FixedTimeEquals(storedHash, actualHash))
        {
            throw new InvalidDataException("The native bar ledger payload hash is invalid.");
        }

        NativeBarInputLedgerReceipt receipt = new(reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), status, payloadJson,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : FromTicks(reader.GetInt64(9)));
        ValidateReceipt(receipt);
        return receipt;
    }

    private static void ValidateSnapshot(NativeBarInputLedgerCheckpoint checkpoint,
        IReadOnlyList<NativeBarInputLedgerReceipt> receipts)
    {
        Dictionary<string, string> acceptedHashes = new(StringComparer.Ordinal);
        string? lastTriggerId = null;
        UtcTimestamp? lastTriggerTime = null;
        long expectedSequence = 1;
        foreach (NativeBarInputLedgerReceipt receipt in receipts)
        {
            if (receipt.IntakeSequence != expectedSequence)
            {
                throw new InvalidDataException("The native bar ledger intake sequence has a gap.");
            }

            expectedSequence = checked(expectedSequence + 1);
            ValidateReceipt(receipt);
            if (receipt.EventId is { } eventId && receipt.RevisionHash is { } revisionHash)
            {
                switch (receipt.Status)
                {
                    case NativeBarInputReceiptStatus.Accepted:
                        if (!acceptedHashes.TryAdd(eventId, revisionHash))
                        {
                            throw new InvalidDataException(
                                "The native bar ledger accepts one revision per event identity.");
                        }
                        break;
                    case NativeBarInputReceiptStatus.Duplicate:
                        if (!acceptedHashes.TryGetValue(eventId, out string? acceptedHash)
                            || !StringComparer.Ordinal.Equals(acceptedHash, revisionHash))
                        {
                            throw new InvalidDataException(
                                "The native bar duplicate receipt does not match an accepted revision.");
                        }
                        break;
                    case NativeBarInputReceiptStatus.Correction:
                        if (!acceptedHashes.TryGetValue(eventId, out acceptedHash)
                            || StringComparer.Ordinal.Equals(acceptedHash, revisionHash))
                        {
                            throw new InvalidDataException(
                                "The native bar correction receipt does not conflict with an accepted revision.");
                        }
                        break;
                }
            }

            if (receipt.TriggerEventId is { } triggerId)
            {
                if (receipt.Status != NativeBarInputReceiptStatus.Accepted
                    || !StringComparer.Ordinal.Equals(triggerId, receipt.EventId)
                    || receipt.TriggerEventTime is not { } triggerTime
                    || lastTriggerTime is { } previousTime
                        && triggerTime.Value <= previousTime.Value)
                {
                    throw new InvalidDataException("The native bar trigger checkpoint is not monotonic.");
                }

                lastTriggerId = triggerId;
                lastTriggerTime = triggerTime;
            }
        }

        long lastSequence = expectedSequence - 1;
        if (checkpoint.LastIntakeSequence != lastSequence
            || !StringComparer.Ordinal.Equals(checkpoint.LastTriggerEventId, lastTriggerId)
            || checkpoint.LastTriggerEventTime != lastTriggerTime)
        {
            throw new InvalidDataException("The native bar ledger checkpoint does not match its receipts.");
        }
    }

    private static void ValidateReceipt(NativeBarInputLedgerReceipt receipt)
    {
        if (receipt.IntakeSequence <= 0 || !Enum.IsDefined(receipt.Status)
            || string.IsNullOrWhiteSpace(receipt.PayloadJson))
        {
            throw new ArgumentException("The native bar receipt is incomplete.", nameof(receipt));
        }

        bool revisionRequired = receipt.Status is NativeBarInputReceiptStatus.Accepted
            or NativeBarInputReceiptStatus.Duplicate or NativeBarInputReceiptStatus.Correction;
        if (revisionRequired && (string.IsNullOrWhiteSpace(receipt.EventId)
            || receipt.RevisionHash is null))
        {
            throw new ArgumentException(
                "Accepted, duplicate, and correction receipts require event and revision identities.",
                nameof(receipt));
        }

        if (receipt.RevisionHash is { } revisionHash)
        {
            byte[] hash;
            try
            {
                hash = Convert.FromHexString(revisionHash);
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("A revision hash must be a hexadecimal SHA-256 value.",
                    nameof(receipt), exception);
            }

            if (hash.Length != 32
                || !StringComparer.Ordinal.Equals(revisionHash, revisionHash.ToLowerInvariant()))
            {
                throw new ArgumentException(
                    "A revision hash must be a lowercase hexadecimal SHA-256 value.",
                    nameof(receipt));
            }
        }

        if ((receipt.TriggerEventId is null) != (receipt.TriggerEventTime is null)
            || receipt.TriggerEventId is not null
                && (receipt.Status != NativeBarInputReceiptStatus.Accepted
                    || !StringComparer.Ordinal.Equals(receipt.TriggerEventId, receipt.EventId)))
        {
            throw new ArgumentException(
                "A committed trigger must identify its accepted bar and event time.",
                nameof(receipt));
        }
    }

    private static void ValidateRevisionTransition(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, NativeBarInputLedgerReceipt receipt)
    {
        if (receipt.EventId is null || receipt.Status == NativeBarInputReceiptStatus.Invalid)
        {
            return;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_hash
            FROM native_bar_input_receipts
            WHERE stream_id = $stream_id COLLATE BINARY
              AND event_id = $event_id COLLATE BINARY
              AND status = 'accepted'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$event_id", receipt.EventId);
        object? value = command.ExecuteScalar();
        string? acceptedHash = value is null or DBNull ? null : Convert.ToString(value,
            System.Globalization.CultureInfo.InvariantCulture);
        bool valid = receipt.Status switch
        {
            NativeBarInputReceiptStatus.Accepted => acceptedHash is null,
            NativeBarInputReceiptStatus.Duplicate => StringComparer.Ordinal.Equals(acceptedHash,
                receipt.RevisionHash),
            NativeBarInputReceiptStatus.Correction => acceptedHash is not null
                && !StringComparer.Ordinal.Equals(acceptedHash, receipt.RevisionHash),
            _ => true
        };
        if (!valid)
        {
            throw new InvalidOperationException(
                "The native bar receipt conflicts with the durable accepted revision.");
        }
    }

    private static NativeBarInputLedgerCheckpoint AdvanceCheckpoint(
        NativeBarInputLedgerCheckpoint checkpoint, NativeBarInputLedgerReceipt receipt)
    {
        if (receipt.TriggerEventTime is not { } triggerTime)
        {
            return checkpoint with { LastIntakeSequence = receipt.IntakeSequence };
        }

        if (checkpoint.LastTriggerEventTime is { } previousTime
            && triggerTime.Value <= previousTime.Value)
        {
            throw new InvalidOperationException(
                "A committed primary trigger must advance event time monotonically.");
        }

        return new NativeBarInputLedgerCheckpoint(receipt.IntakeSequence,
            receipt.TriggerEventId, triggerTime);
    }

    private static void InsertReceipt(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, NativeBarInputLedgerReceipt receipt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO native_bar_input_receipts
                (stream_id, intake_sequence, event_id, revision_hash, status, payload_json,
                 payload_hash, diagnostic_code, diagnostic_message, trigger_event_id,
                 trigger_event_time_ticks)
            VALUES
                ($stream_id, $intake_sequence, $event_id, $revision_hash, $status,
                 $payload_json, $payload_hash, $diagnostic_code, $diagnostic_message,
                 $trigger_event_id, $trigger_event_time_ticks);
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$intake_sequence", receipt.IntakeSequence);
        command.Parameters.AddWithValue("$event_id", DbValue(receipt.EventId));
        command.Parameters.AddWithValue("$revision_hash", DbValue(receipt.RevisionHash));
        command.Parameters.AddWithValue("$status", StatusName(receipt.Status));
        command.Parameters.AddWithValue("$payload_json", receipt.PayloadJson);
        command.Parameters.AddWithValue("$payload_hash", ComputePayloadHash(receipt.PayloadJson));
        command.Parameters.AddWithValue("$diagnostic_code", DbValue(receipt.DiagnosticCode));
        command.Parameters.AddWithValue("$diagnostic_message", DbValue(receipt.DiagnosticMessage));
        command.Parameters.AddWithValue("$trigger_event_id", DbValue(receipt.TriggerEventId));
        command.Parameters.AddWithValue("$trigger_event_time_ticks",
            receipt.TriggerEventTime is { } triggerTime
                ? ToTicks(triggerTime) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void WriteCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, string streamId,
        NativeBarInputLedgerCheckpoint checkpoint)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO native_bar_input_checkpoints
                (stream_id, last_intake_sequence, last_trigger_event_id,
                 last_trigger_event_time_ticks)
            VALUES
                ($stream_id, $last_intake_sequence, $last_trigger_event_id,
                 $last_trigger_event_time_ticks)
            ON CONFLICT(stream_id) DO UPDATE SET
                last_intake_sequence = excluded.last_intake_sequence,
                last_trigger_event_id = excluded.last_trigger_event_id,
                last_trigger_event_time_ticks = excluded.last_trigger_event_time_ticks;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$last_intake_sequence", checkpoint.LastIntakeSequence);
        command.Parameters.AddWithValue("$last_trigger_event_id",
            DbValue(checkpoint.LastTriggerEventId));
        command.Parameters.AddWithValue("$last_trigger_event_time_ticks",
            checkpoint.LastTriggerEventTime is { } eventTime
                ? ToTicks(eventTime) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string StatusName(NativeBarInputReceiptStatus status) =>
        status.ToString().ToLowerInvariant();

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static byte[] ComputePayloadHash(string payloadJson) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));

    private static long ToTicks(UtcTimestamp timestamp) => timestamp.Value.UtcDateTime.Ticks;

    private static UtcTimestamp FromTicks(long ticks) => new(
        new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)));

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
}
