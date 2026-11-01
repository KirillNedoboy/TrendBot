using System.Data;
using Microsoft.Data.Sqlite;

namespace TradingBot.Infrastructure;

public enum ExchangeTestReceiptWriteDisposition
{
    Recorded,
    AlreadyRecorded
}

/// <summary>Non-secret identity linking one isolated exchange-test run.</summary>
public sealed record ExchangeTestRunMetadata
{
    public ExchangeTestRunMetadata(string runId, string configurationId,
        string environmentId, DateTimeOffset startedAt)
    {
        RequireIdentity(runId, nameof(runId));
        RequireIdentity(configurationId, nameof(configurationId));
        if (!StringComparer.Ordinal.Equals(environmentId, "binance-usdm-test"))
        {
            throw new ArgumentException("The exchange-test store requires the Binance USD-M test environment.",
                nameof(environmentId));
        }

        RunId = runId;
        ConfigurationId = configurationId;
        EnvironmentId = environmentId;
        StartedAt = startedAt;
    }

    public string RunId { get; }
    public string ConfigurationId { get; }
    public string EnvironmentId { get; }
    public DateTimeOffset StartedAt { get; }

    private static void RequireIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(char.IsControl)
            || !char.IsLetterOrDigit(value[0]) || !char.IsLetterOrDigit(value[^1])
            || value.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The exchange-test identity is not valid.", parameterName);
        }
    }
}

/// <summary>
/// A normalized private-stream receipt. Raw frames, credentials, signatures,
/// listen keys, and other credential-bearing payloads intentionally have no
/// representation in this contract.
/// </summary>
public sealed record ExchangeTestEventReceipt
{
    public ExchangeTestEventReceipt(string runId, string identity, string intentId,
        string eventType, long stableSequence, DateTimeOffset eventTime,
        DateTimeOffset receiveTime, DateTimeOffset availabilityTime,
        string? exchangeOrderId, decimal? fillQuantity, string lifecycle,
        bool requiresReconciliation, string fingerprint)
    {
        RequireIdentity(runId, nameof(runId));
        RequireIdentity(identity, nameof(identity));
        RequireIdentity(intentId, nameof(intentId));
        RequireIdentity(eventType, nameof(eventType));
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);

        if (availabilityTime < receiveTime)
        {
            throw new ArgumentException("Availability cannot precede receipt time.",
                nameof(availabilityTime));
        }

        if (exchangeOrderId is not null)
        {
            RequireIdentity(exchangeOrderId, nameof(exchangeOrderId));
        }

        if (fillQuantity is <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(fillQuantity));
        }

        RequireIdentity(lifecycle, nameof(lifecycle));
        if (fingerprint.Length != 64
            || fingerprint.Any(static character =>
                !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A normalized event fingerprint must be SHA-256 hex.",
                nameof(fingerprint));
        }

        RunId = runId;
        Identity = identity;
        IntentId = intentId;
        EventType = eventType;
        StableSequence = stableSequence;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        ExchangeOrderId = exchangeOrderId;
        FillQuantity = fillQuantity;
        Lifecycle = lifecycle;
        RequiresReconciliation = requiresReconciliation;
        Fingerprint = fingerprint.ToLowerInvariant();
    }

    public string RunId { get; }
    public string Identity { get; }
    public string IntentId { get; }
    public string EventType { get; }
    public long StableSequence { get; }
    public DateTimeOffset EventTime { get; }
    public DateTimeOffset ReceiveTime { get; }
    public DateTimeOffset AvailabilityTime { get; }
    public string? ExchangeOrderId { get; }
    public decimal? FillQuantity { get; }
    public string Lifecycle { get; }
    public bool RequiresReconciliation { get; }
    public string Fingerprint { get; }

    private static void RequireIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 256 || value.Any(char.IsControl)
            || !char.IsLetterOrDigit(value[0]) || !char.IsLetterOrDigit(value[^1])
            || value.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.'
                    or ':')))
        {
            throw new ArgumentException("The normalized event identity is not valid.",
                parameterName);
        }
    }
}

/// <summary>
/// Persists only normalized exchange-test run metadata and private-stream
/// receipts in an isolated SQLite file. It does not store raw frames or
/// credential-bearing values.
/// </summary>
public sealed class SqliteExchangeTestStore : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;
    private readonly ExchangeTestRunMetadata _metadata;
    private bool _disposed;

    private SqliteExchangeTestStore(string databasePath, int commandTimeoutSeconds,
        ExchangeTestRunMetadata metadata)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _metadata = metadata;
    }

    public static SqliteExchangeTestStore Open(string databasePath,
        ExchangeTestRunMetadata metadata, int commandTimeoutSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        SqliteDatabase.ValidateTimeout(commandTimeoutSeconds);
        string path = SqliteDatabase.NormalizePath(databasePath);
        SqliteExchangeTestStore store = new(path, commandTimeoutSeconds, metadata);
        store.Initialize();
        return store;
    }

    /// <summary>Gets the normalized SQLite file used by this exchange-test run.</summary>
    public string DatabasePath => _databasePath;

    public ExchangeTestRunMetadata ReadRunMetadata()
    {
        ThrowIfDisposed();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, configuration_id, environment_id, started_at_ticks,
                   started_at_offset_minutes
            FROM exchange_test_runs
            WHERE run_id = $run_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$run_id", _metadata.RunId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("The exchange-test run metadata is missing.");
        }

        return ReadMetadata(reader);
    }

    public ExchangeTestReceiptWriteDisposition AppendPrivateEvent(
        ExchangeTestEventReceipt receipt)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(receipt);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        ExchangeTestReceiptWriteDisposition disposition = AppendPrivateEventInTransaction(
            connection, transaction, receipt);
        transaction.Commit();
        return disposition;
    }

    /// <summary>
    /// Appends normalized receipts using a caller-owned SQLite transaction.
    /// No receipt is committed until the caller commits its wider lifecycle
    /// and portfolio observation transaction.
    /// </summary>
    internal IReadOnlyList<ExchangeTestReceiptWriteDisposition> AppendPrivateEventsInTransaction(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<ExchangeTestEventReceipt> receipts)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(receipts);
        List<ExchangeTestReceiptWriteDisposition> dispositions = [];
        foreach (ExchangeTestEventReceipt receipt in receipts)
        {
            dispositions.Add(AppendPrivateEventInTransaction(connection, transaction, receipt));
        }

        return dispositions.AsReadOnly();
    }

    private ExchangeTestReceiptWriteDisposition AppendPrivateEventInTransaction(
        SqliteConnection connection, SqliteTransaction transaction,
        ExchangeTestEventReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!StringComparer.Ordinal.Equals(receipt.RunId, _metadata.RunId))
        {
            throw new InvalidOperationException("The private event belongs to a different test run.");
        }

        ExistingReceipt? existing = FindReceipt(connection, transaction, receipt.RunId,
            receipt.Identity);
        if (existing is not null)
        {
            if (StringComparer.Ordinal.Equals(existing.Fingerprint, receipt.Fingerprint)
                && existing.Equals(receipt))
            {
                return ExchangeTestReceiptWriteDisposition.AlreadyRecorded;
            }

            throw new InvalidOperationException(
                "The private event identity already contains different normalized evidence.");
        }

        using SqliteCommand sequenceCommand = connection.CreateCommand();
        sequenceCommand.Transaction = transaction;
        sequenceCommand.CommandText = """
            SELECT identity
            FROM exchange_test_receipts
            WHERE run_id = $run_id COLLATE BINARY AND stable_sequence = $stable_sequence;
            """;
        sequenceCommand.Parameters.AddWithValue("$run_id", receipt.RunId);
        sequenceCommand.Parameters.AddWithValue("$stable_sequence", receipt.StableSequence);
        if (sequenceCommand.ExecuteScalar() is string)
        {
            throw new InvalidOperationException(
                "The private event sequence already belongs to a different identity.");
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO exchange_test_receipts
            (run_id, identity, intent_id, event_type, stable_sequence,
             event_time_ticks, event_time_offset_minutes, receive_time_ticks,
             receive_time_offset_minutes, availability_time_ticks,
             availability_time_offset_minutes, exchange_order_id, fill_quantity,
             lifecycle, requires_reconciliation, fingerprint)
            VALUES ($run_id, $identity, $intent_id, $event_type, $stable_sequence,
                    $event_time_ticks, $event_time_offset_minutes, $receive_time_ticks,
                    $receive_time_offset_minutes, $availability_time_ticks,
                    $availability_time_offset_minutes, $exchange_order_id, $fill_quantity,
                    $lifecycle, $requires_reconciliation, $fingerprint);
            """;
        AddReceiptParameters(insert, receipt);
        insert.ExecuteNonQuery();
        return ExchangeTestReceiptWriteDisposition.Recorded;
    }

    public IReadOnlyList<ExchangeTestEventReceipt> ReadPrivateEvents()
    {
        ThrowIfDisposed();
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, identity, intent_id, event_type, stable_sequence,
                   event_time_ticks, event_time_offset_minutes, receive_time_ticks,
                   receive_time_offset_minutes, availability_time_ticks,
                   availability_time_offset_minutes, exchange_order_id, fill_quantity,
                   lifecycle, requires_reconciliation, fingerprint
            FROM exchange_test_receipts
            WHERE run_id = $run_id COLLATE BINARY
            ORDER BY stable_sequence ASC, identity ASC;
            """;
        command.Parameters.AddWithValue("$run_id", _metadata.RunId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<ExchangeTestEventReceipt> receipts = [];
        while (reader.Read())
        {
            receipts.Add(ReadReceipt(reader));
        }

        return receipts.AsReadOnly();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS exchange_test_runs
            (
                run_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                configuration_id TEXT COLLATE BINARY NOT NULL,
                environment_id TEXT COLLATE BINARY NOT NULL,
                started_at_ticks INTEGER NOT NULL,
                started_at_offset_minutes INTEGER NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS exchange_test_receipts
            (
                run_id TEXT COLLATE BINARY NOT NULL,
                identity TEXT COLLATE BINARY NOT NULL,
                intent_id TEXT COLLATE BINARY NOT NULL,
                event_type TEXT COLLATE BINARY NOT NULL,
                stable_sequence INTEGER NOT NULL,
                event_time_ticks INTEGER NOT NULL,
                event_time_offset_minutes INTEGER NOT NULL,
                receive_time_ticks INTEGER NOT NULL,
                receive_time_offset_minutes INTEGER NOT NULL,
                availability_time_ticks INTEGER NOT NULL,
                availability_time_offset_minutes INTEGER NOT NULL,
                exchange_order_id TEXT COLLATE BINARY NULL,
                fill_quantity TEXT NULL,
                lifecycle TEXT COLLATE BINARY NOT NULL,
                requires_reconciliation INTEGER NOT NULL,
                fingerprint TEXT COLLATE BINARY NOT NULL,
                PRIMARY KEY (run_id, identity),
                UNIQUE (run_id, stable_sequence),
                FOREIGN KEY (run_id) REFERENCES exchange_test_runs(run_id)
            ) WITHOUT ROWID;
            """);

        using SqliteCommand lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = """
            SELECT run_id, configuration_id, environment_id, started_at_ticks,
                   started_at_offset_minutes
            FROM exchange_test_runs
            WHERE run_id = $run_id COLLATE BINARY;
            """;
        lookup.Parameters.AddWithValue("$run_id", _metadata.RunId);
        ExchangeTestRunMetadata? existing = null;
        using (SqliteDataReader reader = lookup.ExecuteReader())
        {
            if (reader.Read())
            {
                existing = ReadMetadata(reader);
            }
        }

        if (existing is not null)
        {
            if (!existing.Equals(_metadata))
            {
                throw new InvalidDataException(
                    "The exchange-test run identity is already bound to different metadata.");
            }
        }
        else
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO exchange_test_runs
                (run_id, configuration_id, environment_id, started_at_ticks,
                 started_at_offset_minutes)
                VALUES ($run_id, $configuration_id, $environment_id, $started_at_ticks,
                        $started_at_offset_minutes);
                """;
            insert.Parameters.AddWithValue("$run_id", _metadata.RunId);
            insert.Parameters.AddWithValue("$configuration_id", _metadata.ConfigurationId);
            insert.Parameters.AddWithValue("$environment_id", _metadata.EnvironmentId);
            insert.Parameters.AddWithValue("$started_at_ticks", _metadata.StartedAt.Ticks);
            insert.Parameters.AddWithValue("$started_at_offset_minutes",
                (int)_metadata.StartedAt.Offset.TotalMinutes);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection OpenConnection() =>
        SqliteDatabase.OpenConnection(_databasePath, _commandTimeoutSeconds);

    private static ExistingReceipt? FindReceipt(SqliteConnection connection,
        SqliteTransaction transaction, string runId, string identity)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, identity, intent_id, event_type, stable_sequence,
                   event_time_ticks, event_time_offset_minutes, receive_time_ticks,
                   receive_time_offset_minutes, availability_time_ticks,
                   availability_time_offset_minutes, exchange_order_id, fill_quantity,
                   lifecycle, requires_reconciliation, fingerprint
            FROM exchange_test_receipts
            WHERE run_id = $run_id COLLATE BINARY AND identity = $identity COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$identity", identity);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new ExistingReceipt(ReadReceipt(reader)) : null;
    }

    private static void AddReceiptParameters(SqliteCommand command,
        ExchangeTestEventReceipt receipt)
    {
        command.Parameters.AddWithValue("$run_id", receipt.RunId);
        command.Parameters.AddWithValue("$identity", receipt.Identity);
        command.Parameters.AddWithValue("$intent_id", receipt.IntentId);
        command.Parameters.AddWithValue("$event_type", receipt.EventType);
        command.Parameters.AddWithValue("$stable_sequence", receipt.StableSequence);
        AddTimestamp(command, "$event_time", receipt.EventTime);
        AddTimestamp(command, "$receive_time", receipt.ReceiveTime);
        AddTimestamp(command, "$availability_time", receipt.AvailabilityTime);
        command.Parameters.AddWithValue("$exchange_order_id",
            (object?)receipt.ExchangeOrderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fill_quantity",
            receipt.FillQuantity?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lifecycle", receipt.Lifecycle);
        command.Parameters.AddWithValue("$requires_reconciliation",
            receipt.RequiresReconciliation ? 1 : 0);
        command.Parameters.AddWithValue("$fingerprint", receipt.Fingerprint);
    }

    private static void AddTimestamp(SqliteCommand command, string prefix, DateTimeOffset value)
    {
        command.Parameters.AddWithValue(prefix + "_ticks", value.Ticks);
        command.Parameters.AddWithValue(prefix + "_offset_minutes", (int)value.Offset.TotalMinutes);
    }

    private static ExchangeTestRunMetadata ReadMetadata(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            FromTimestamp(reader.GetInt64(3), reader.GetInt32(4)));

    private static ExchangeTestEventReceipt ReadReceipt(SqliteDataReader reader)
    {
        decimal? fillQuantity = reader.IsDBNull(12)
            ? null
            : decimal.Parse(reader.GetString(12), System.Globalization.CultureInfo.InvariantCulture);
        return new ExchangeTestEventReceipt(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
            FromTimestamp(reader.GetInt64(5), reader.GetInt32(6)),
            FromTimestamp(reader.GetInt64(7), reader.GetInt32(8)),
            FromTimestamp(reader.GetInt64(9), reader.GetInt32(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11), fillQuantity,
            reader.GetString(13), reader.GetInt64(14) != 0, reader.GetString(15));
    }

    private static DateTimeOffset FromTimestamp(long ticks, int offsetMinutes) =>
        new(new DateTime(ticks, DateTimeKind.Unspecified), TimeSpan.FromMinutes(offsetMinutes));

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void RequireIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 256 || value.Any(char.IsControl)
            || !char.IsLetterOrDigit(value[0]) || !char.IsLetterOrDigit(value[^1])
            || value.Any(static character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.'
                    or ':')))
        {
            throw new ArgumentException("The normalized identity is not valid.", parameterName);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ExistingReceipt(ExchangeTestEventReceipt Receipt)
    {
        public string Fingerprint => Receipt.Fingerprint;

        public bool Equals(ExchangeTestEventReceipt other) => Receipt.Equals(other);
    }
}
