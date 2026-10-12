using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Stores causal market events in a versioned SQLite file.
/// </summary>
/// <remarks>
/// The journal is deliberately a small recording boundary. It assigns no
/// sequence values, applies no replay filtering, and has no order or execution
/// authorization state.
/// </remarks>
public sealed class SqliteEventJournal : IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private const string JournalTable = "event_journal";

    private static readonly JournalColumn[] RequiredColumns =
    [
        new("stream_id", "TEXT", 1, 1),
        new("stable_sequence", "INTEGER", 1, 2),
        new("payload_json", "TEXT", 1, 0),
        new("event_time_ticks", "INTEGER", 1, 0),
        new("receive_time_ticks", "INTEGER", 1, 0),
        new("availability_time_ticks", "INTEGER", 1, 0),
        new("exchange_time_ticks", "INTEGER", 0, 0),
        new("envelope_version", "INTEGER", 1, 0)
    ];

    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteEventJournal(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>
    /// Opens a file-backed journal and applies supported transactional schema
    /// migrations when the file is empty or on a prior supported version.
    /// </summary>
    public static SqliteEventJournal Open(string databasePath, int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (StringComparer.Ordinal.Equals(databasePath, ":memory:"))
        {
            throw new ArgumentException("The event journal requires a file-backed SQLite database.",
                nameof(databasePath));
        }

        if (commandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds),
                commandTimeoutSeconds, "The SQLite command timeout must be positive.");
        }

        string fullPath = Path.GetFullPath(databasePath);
        SqliteEventJournal journal = new(fullPath, commandTimeoutSeconds);
        journal.Initialize();
        return journal;
    }

    /// <summary>
    /// Appends one event to a stream. The caller supplies its stable sequence.
    /// </summary>
    /// <returns><see langword="false"/> when the same row is already present.</returns>
    public bool Append(string streamId, CausalMarketEvent causalEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(causalEvent);

        return Append(new ReplayEnvelope(streamId, causalEvent));
    }

    /// <summary>Appends a complete replay envelope to its declared stream.</summary>
    /// <returns><see langword="false"/> when the same row is already present.</returns>
    public bool Append(ReplayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        JournalRow? existing = FindRow(connection, transaction, envelope.StreamId,
            envelope.CausalEvent.StableSequence);
        if (existing is not null)
        {
            ReplayEnvelope stored = DeserializeRow(envelope.StreamId, existing);
            if (stored == envelope)
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The stream sequence already contains a different replay envelope.");
        }

        long? maximumSequence = FindMaximumSequence(connection, transaction, envelope.StreamId);
        if (maximumSequence is not null
            && envelope.CausalEvent.StableSequence <= maximumSequence.Value)
        {
            throw new InvalidOperationException(
                "A new stream event must have a stable sequence greater than the current maximum.");
        }

        InsertRow(connection, transaction, envelope);
        transaction.Commit();
        return true;
    }

    /// <summary>Reads one stream as an immutable snapshot ordered by stable sequence.</summary>
    public IReadOnlyList<CausalMarketEvent> ReadStream(string streamId)
    {
        return new ReadOnlyCollection<CausalMarketEvent>(
            ReadEnvelopes(streamId).Select(static envelope => envelope.CausalEvent).ToList());
    }

    /// <summary>Reads one stream as immutable replay envelopes ordered by stable sequence.</summary>
    public IReadOnlyList<ReplayEnvelope> ReadEnvelopes(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stable_sequence, envelope_version, payload_json, event_time_ticks,
                   receive_time_ticks, availability_time_ticks, exchange_time_ticks
            FROM event_journal
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY stable_sequence ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);

        List<ReplayEnvelope> envelopes = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            envelopes.Add(DeserializeRow(streamId, new JournalRow(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6))));
        }

        return new ReadOnlyCollection<ReplayEnvelope>(envelopes);
    }

    /// <summary>Alias for reading the immutable replay-envelope stream snapshot.</summary>
    public IReadOnlyList<ReplayEnvelope> ReadEnvelopeStream(string streamId)
    {
        return ReadEnvelopes(streamId);
    }

    /// <summary>Releases no shared connection; each operation owns its connection.</summary>
    public void Dispose()
    {
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        long version = ReadUserVersion(connection);

        if (version == 0)
        {
            if (HasUserObjects(connection))
            {
                throw new InvalidDataException(
                    "A non-empty SQLite database without a supported schema version cannot be migrated.");
            }

            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            long lockedVersion = ReadUserVersion(connection, transaction);
            if (lockedVersion == 0 && !HasUserObjects(connection, transaction))
            {
                CreateSchemaV1(connection, transaction);
                SetUserVersion(connection, transaction, 1);
                MigrateV1ToV2(connection, transaction);
                SetUserVersion(connection, transaction, 2);
                MigrateV2ToV3(connection, transaction);
                SetUserVersion(connection, transaction, CurrentSchemaVersion);
            }
            else if (lockedVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"SQLite schema version {lockedVersion} is not supported.");
            }

            transaction.Commit();
            version = CurrentSchemaVersion;
        }
        else if (version is 1 or 2)
        {
            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            long lockedVersion = ReadUserVersion(connection, transaction);
            if (lockedVersion == 1)
            {
                MigrateV1ToV2(connection, transaction);
                SetUserVersion(connection, transaction, 2);
                lockedVersion = 2;
            }

            if (lockedVersion == 2)
            {
                MigrateV2ToV3(connection, transaction);
                SetUserVersion(connection, transaction, CurrentSchemaVersion);
                lockedVersion = CurrentSchemaVersion;
            }

            if (lockedVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"SQLite schema version {lockedVersion} is not supported.");
            }

            transaction.Commit();
            version = CurrentSchemaVersion;
        }

        if (version != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"SQLite schema version {version} is not supported.");
        }

        ValidateSchema(connection);
        ValidateOrderIntentSchema(connection);
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = _commandTimeoutSeconds
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

    private static void CreateSchemaV1(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE event_journal
            (
                stream_id TEXT COLLATE BINARY NOT NULL,
                stable_sequence INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                event_time_ticks INTEGER NOT NULL,
                receive_time_ticks INTEGER NOT NULL,
                availability_time_ticks INTEGER NOT NULL,
                exchange_time_ticks INTEGER NULL,
                PRIMARY KEY (stream_id, stable_sequence)
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV1ToV2(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            ALTER TABLE event_journal
            ADD COLUMN envelope_version INTEGER NOT NULL DEFAULT 1;
            """);
    }

    private static void MigrateV2ToV3(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE order_intents
            (
                intent_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                client_order_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                intent_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(event_journal);";
        using SqliteDataReader reader = command.ExecuteReader();

        List<JournalColumn> actualColumns = [];
        while (reader.Read())
        {
            actualColumns.Add(new JournalColumn(reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(5)));
        }

        if (!RequiredColumns.SequenceEqual(actualColumns))
        {
            throw new InvalidDataException("The event_journal schema does not match version 3.");
        }

        using SqliteCommand sqlCommand = connection.CreateCommand();
        sqlCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'event_journal';
            """;
        string? createSql = sqlCommand.ExecuteScalar() as string;
        if (createSql is null
            || createSql.IndexOf("COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException("The event_journal stream key must use BINARY collation.");
        }
    }

    private static void ValidateOrderIntentSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'order_intents';
            """;
        string? createSql = command.ExecuteScalar() as string;
        if (createSql is null
            || createSql.IndexOf("intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("client_order_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException("The order_intents schema does not match version 3.");
        }
    }

    private static long ReadUserVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        command.Transaction = transaction;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool HasUserObjects(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS
            (
                SELECT 1
                FROM sqlite_master
                WHERE type IN ('table', 'index', 'trigger', 'view')
                  AND name NOT LIKE 'sqlite_%'
            );
            """;
        command.Transaction = transaction;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction transaction,
        int version)
    {
        ExecuteNonQuery(connection, transaction, $"PRAGMA user_version = {version};");
    }

    private static JournalRow? FindRow(SqliteConnection connection, SqliteTransaction transaction,
        string streamId, long stableSequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stable_sequence, envelope_version, payload_json, event_time_ticks,
                   receive_time_ticks, availability_time_ticks, exchange_time_ticks
            FROM event_journal
            WHERE stream_id = $stream_id COLLATE BINARY
              AND stable_sequence = $stable_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$stable_sequence", stableSequence);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new JournalRow(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6))
            : null;
    }

    private static long? FindMaximumSequence(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(stable_sequence)
            FROM event_journal
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        object? value = command.ExecuteScalar();
        return value is DBNull or null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void InsertRow(SqliteConnection connection, SqliteTransaction transaction,
        ReplayEnvelope envelope)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO event_journal
                (stream_id, stable_sequence, envelope_version, payload_json, event_time_ticks,
                 receive_time_ticks, availability_time_ticks, exchange_time_ticks)
            VALUES
                ($stream_id, $stable_sequence, $envelope_version, $payload_json, $event_time_ticks,
                 $receive_time_ticks, $availability_time_ticks, $exchange_time_ticks);
            """;
        command.Parameters.AddWithValue("$stream_id", envelope.StreamId);
        command.Parameters.AddWithValue("$stable_sequence", envelope.CausalEvent.StableSequence);
        command.Parameters.AddWithValue("$envelope_version", envelope.FormatVersion);
        command.Parameters.AddWithValue("$payload_json",
            CoreJsonSerializer.Serialize(envelope.CausalEvent.Payload));
        command.Parameters.AddWithValue("$event_time_ticks",
            ToUtcTicks(envelope.CausalEvent.Payload.EventTime));
        command.Parameters.AddWithValue("$receive_time_ticks",
            ToUtcTicks(envelope.CausalEvent.ReceiveTime));
        command.Parameters.AddWithValue("$availability_time_ticks",
            ToUtcTicks(envelope.CausalEvent.AvailabilityTime));
        command.Parameters.AddWithValue("$exchange_time_ticks",
            envelope.CausalEvent.ExchangeTime is null
                ? DBNull.Value
                : ToUtcTicks(envelope.CausalEvent.ExchangeTime));
        command.ExecuteNonQuery();
    }

    private static ReplayEnvelope DeserializeRow(string streamId, JournalRow row)
    {
        try
        {
            if (row.EnvelopeVersion != ReplayEnvelope.CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"Replay envelope format version {row.EnvelopeVersion} is not supported.");
            }

            MarketEvent payload = CoreJsonSerializer.Deserialize<MarketEvent>(row.PayloadJson);
            UtcTimestamp eventTime = FromUtcTicks(row.EventTimeTicks);
            UtcTimestamp receiveTime = FromUtcTicks(row.ReceiveTimeTicks);
            UtcTimestamp availabilityTime = FromUtcTicks(row.AvailabilityTimeTicks);
            UtcTimestamp? exchangeTime = row.ExchangeTimeTicks is null
                ? null
                : FromUtcTicks(row.ExchangeTimeTicks.Value);

            if (ToUtcTicks(payload.EventTime) != row.EventTimeTicks)
            {
                throw new InvalidDataException("The persisted event time does not match its payload.");
            }

            CausalMarketEvent causalEvent = new(payload, receiveTime, availabilityTime,
                row.StableSequence, exchangeTime);
            if (ToUtcTicks(causalEvent.ReceiveTime) != row.ReceiveTimeTicks
                || ToUtcTicks(causalEvent.AvailabilityTime) != row.AvailabilityTimeTicks
                || (causalEvent.ExchangeTime is null
                    ? row.ExchangeTimeTicks is not null
                    : ToUtcTicks(causalEvent.ExchangeTime) != row.ExchangeTimeTicks))
            {
                throw new InvalidDataException("The persisted causal metadata does not match the row.");
            }

            _ = eventTime;
            return new ReplayEnvelope(row.EnvelopeVersion, streamId, causalEvent);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or FormatException
            or OverflowException
            or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("The persisted event payload or causal metadata is invalid.",
                exception);
        }
    }

    private static long ToUtcTicks(UtcTimestamp timestamp) => timestamp.Value.UtcDateTime.Ticks;

    private static UtcTimestamp FromUtcTicks(long ticks)
    {
        DateTime utcDateTime = new(ticks, DateTimeKind.Utc);
        return new UtcTimestamp(new DateTimeOffset(utcDateTime));
    }

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

    private readonly record struct JournalColumn(string Name, string Type, int NotNull, int PrimaryKey);

    private sealed record JournalRow(long StableSequence, int EnvelopeVersion, string PayloadJson,
        long EventTimeTicks, long ReceiveTimeTicks, long AvailabilityTimeTicks,
        long? ExchangeTimeTicks);
}
