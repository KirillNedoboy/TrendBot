using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>Durable claim, attempt, and receipt journal for committed signal outbox rows.</summary>
public sealed class SqliteSignalOutboxDeliveryStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteSignalOutboxDeliveryStore(string databasePath,
        int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public static SqliteSignalOutboxDeliveryStore Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandTimeoutSeconds);
        if (StringComparer.Ordinal.Equals(databasePath, ":memory:"))
        {
            throw new ArgumentException("The delivery journal must be file-backed.",
                nameof(databasePath));
        }

        string fullPath = Path.GetFullPath(databasePath);
        Initialize(fullPath, commandTimeoutSeconds);
        return new SqliteSignalOutboxDeliveryStore(fullPath, commandTimeoutSeconds);
    }

    /// <summary>Claims one eligible row from the validated, committed signal journal.</summary>
    public SignalOutboxLease? ClaimNext(SqliteSignalJournalStore journal,
        string streamId, SignalOutboxDeliveryPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(policy);
        DateTimeOffset utcNow = now.ToUniversalTime();
        SignalJournalSnapshot committed = journal.Read(streamId);
        Dictionary<string, SignalRecord> signals = committed.Signals.ToDictionary(
            static item => item.Signal.Identity.SignalId,
            static item => item.Signal, StringComparer.Ordinal);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        EnsurePolicy(connection, transaction, policy);
        EnsureDeliveryRows(connection, transaction, streamId, committed.Outbox,
            signals, policy);
        RecoverExpiredLeases(connection, transaction, streamId, committed.Outbox,
            signals, committed.Signals, utcNow);
        ExpireUnsentRows(connection, transaction, streamId, utcNow);

        DateTimeOffset? rateLimitUntil = ReadRateLimit(connection, transaction);
        if (rateLimitUntil > utcNow)
        {
            transaction.Commit();
            return null;
        }

        foreach (SignalJournalOutboxItem item in committed.Outbox)
        {
            if (!signals.TryGetValue(item.ScenarioEvent.SignalId,
                    out SignalRecord? signal) || signal is null)
            {
                throw new InvalidDataException(
                    "A committed Telegram outbox row has no signal record.");
            }

            DeliveryRow? row = FindDelivery(connection, transaction, streamId,
                item.OutboxId);
            if (row is null || row.Status != SignalOutboxDeliveryStatus.Pending
                || row.CreatedAt > utcNow || row.NextAttemptAt > utcNow
                || row.ExpiresAt <= utcNow)
            {
                continue;
            }

            string leaseToken = Guid.NewGuid().ToString("N");
            DateTimeOffset leaseExpiresAt = AddBounded(utcNow, policy.LeaseDuration);
            using SqliteCommand claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE telegram_outbox_delivery
                SET status = $status, lease_token = $lease_token,
                    lease_until_ticks = $lease_until_ticks
                WHERE stream_id = $stream_id COLLATE BINARY
                  AND outbox_id = $outbox_id COLLATE BINARY
                  AND status = $pending;
                """;
            claim.Parameters.AddWithValue("$status", StatusName(SignalOutboxDeliveryStatus.Leased));
            claim.Parameters.AddWithValue("$lease_token", leaseToken);
            claim.Parameters.AddWithValue("$lease_until_ticks", ToTicks(leaseExpiresAt));
            claim.Parameters.AddWithValue("$stream_id", streamId);
            claim.Parameters.AddWithValue("$outbox_id", item.OutboxId);
            claim.Parameters.AddWithValue("$pending", StatusName(SignalOutboxDeliveryStatus.Pending));
            if (claim.ExecuteNonQuery() != 1)
            {
                continue;
            }

            transaction.Commit();
            SignalEvidenceRecord evidence = committed.Signals.First(entry =>
                StringComparer.Ordinal.Equals(entry.Signal.Identity.SignalId, signal.Identity.SignalId)).Evidence;
            return new SignalOutboxLease(streamId, item, signal, evidence, leaseToken,
                leaseExpiresAt, checked(row.AttemptsStarted + 1));
        }

        transaction.Commit();
        return null;
    }

    /// <summary>Durably marks the point after which a crash makes delivery ambiguous.</summary>
    public bool BeginAttempt(SignalOutboxLease lease,
        SignalOutboxDeliveryPolicy policy, DateTimeOffset now)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(policy);
        DateTimeOffset utcNow = now.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        EnsurePolicy(connection, transaction, policy);
        DeliveryRow row = RequireLeaseRow(connection, transaction, lease);
        if (row.Status != SignalOutboxDeliveryStatus.Leased
            || row.LeaseExpiresAt is null || row.LeaseExpiresAt <= utcNow)
        {
            transaction.Commit();
            return false;
        }

        if (row.ExpiresAt <= utcNow)
        {
            SetTerminalState(connection, transaction, lease.StreamId,
                lease.Item.OutboxId, SignalOutboxDeliveryStatus.Expired,
                "message-expired", null);
            transaction.Commit();
            return false;
        }

        int attemptNumber = checked(row.AttemptsStarted + 1);
        if (attemptNumber != lease.NextAttemptNumber)
        {
            throw new InvalidOperationException(
                "The Telegram delivery lease attempt number is stale.");
        }

        SignalDeliveryReceipt receipt = SignalDeliveryReceipt.Create(lease.Signal,
            AttemptId(lease.Item.OutboxId, attemptNumber), SignalDeliveryChannel.Telegram,
            SignalDeliveryStatus.Attempted, new UtcTimestamp(utcNow));
        InsertReceipt(connection, transaction, lease, attemptNumber, receipt);
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE telegram_outbox_delivery
            SET status = $sending, attempts_started = $attempts,
                last_reason_code = NULL, external_receipt_id = NULL
            WHERE stream_id = $stream_id COLLATE BINARY
              AND outbox_id = $outbox_id COLLATE BINARY
              AND status = $leased AND lease_token = $lease_token COLLATE BINARY;
            """;
        update.Parameters.AddWithValue("$sending", StatusName(SignalOutboxDeliveryStatus.Sending));
        update.Parameters.AddWithValue("$attempts", attemptNumber);
        update.Parameters.AddWithValue("$leased", StatusName(SignalOutboxDeliveryStatus.Leased));
        AddLeaseParameters(update, lease);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The Telegram delivery lease changed before the attempt was recorded.");
        }

        transaction.Commit();
        return true;
    }

    public void RecordAcknowledgement(SignalOutboxLease lease,
        SignalOutboxDeliveryPolicy policy, DateTimeOffset now,
        string externalMessageId)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalMessageId);
        DateTimeOffset utcNow = now.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        EnsurePolicy(connection, transaction, policy);
        DeliveryRow row = RequireLeaseRow(connection, transaction, lease);
        if (row.Status == SignalOutboxDeliveryStatus.Acknowledged
            && StringComparer.Ordinal.Equals(row.ExternalReceiptId, externalMessageId))
        {
            transaction.Commit();
            return;
        }
        RequireSendingLease(row, lease);
        SignalDeliveryReceipt receipt = SignalDeliveryReceipt.Create(lease.Signal,
            AttemptId(lease.Item.OutboxId, row.AttemptsStarted),
            SignalDeliveryChannel.Telegram, SignalDeliveryStatus.Acknowledged,
            new UtcTimestamp(utcNow), externalMessageId);
        InsertReceipt(connection, transaction, lease, row.AttemptsStarted, receipt);
        SetTerminalState(connection, transaction, lease.StreamId,
            lease.Item.OutboxId, SignalOutboxDeliveryStatus.Acknowledged,
            null, externalMessageId, lease.LeaseToken);
        transaction.Commit();
    }

    public void RecordUnknownDelivery(SignalOutboxLease lease,
        SignalOutboxDeliveryPolicy policy, DateTimeOffset now)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(policy);
        DateTimeOffset utcNow = now.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        EnsurePolicy(connection, transaction, policy);
        DeliveryRow row = RequireLeaseRow(connection, transaction, lease);
        if (row.Status == SignalOutboxDeliveryStatus.UnknownDelivery)
        {
            transaction.Commit();
            return;
        }
        RequireSendingLease(row, lease);
        SignalDeliveryReceipt receipt = SignalDeliveryReceipt.Create(lease.Signal,
            AttemptId(lease.Item.OutboxId, row.AttemptsStarted),
            SignalDeliveryChannel.Telegram, SignalDeliveryStatus.Unknown,
            new UtcTimestamp(utcNow), reason: "telegram-response-ambiguous");
        InsertReceipt(connection, transaction, lease, row.AttemptsStarted, receipt);
        SetTerminalState(connection, transaction, lease.StreamId,
            lease.Item.OutboxId, SignalOutboxDeliveryStatus.UnknownDelivery,
            "telegram-response-ambiguous", null, lease.LeaseToken);
        transaction.Commit();
    }

    public void RecordFailure(SignalOutboxLease lease,
        SignalOutboxDeliveryPolicy policy, DateTimeOffset now,
        SignalOutboxFailureKind failureKind, int? retryAfterSeconds = null)
    {
        ValidateLease(lease);
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind), failureKind,
                "The Telegram delivery failure kind is unsupported.");
        }
        if (retryAfterSeconds is <= 0)
        {
            retryAfterSeconds = null;
        }

        DateTimeOffset utcNow = now.ToUniversalTime();
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);
        EnsurePolicy(connection, transaction, policy);
        DeliveryRow row = RequireLeaseRow(connection, transaction, lease);
        if (row.Status == SignalOutboxDeliveryStatus.PermanentFailure
            || row.Status == SignalOutboxDeliveryStatus.RetryExhausted
            || row.Status == SignalOutboxDeliveryStatus.Expired)
        {
            transaction.Commit();
            return;
        }
        RequireSendingLease(row, lease);
        string reasonCode = failureKind switch
        {
            SignalOutboxFailureKind.Retryable => "telegram-transient-rejection",
            SignalOutboxFailureKind.RateLimited => "telegram-rate-limited",
            SignalOutboxFailureKind.Permanent => "telegram-permanent-failure",
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
        };
        SignalDeliveryReceipt receipt = SignalDeliveryReceipt.Create(lease.Signal,
            AttemptId(lease.Item.OutboxId, row.AttemptsStarted),
            SignalDeliveryChannel.Telegram, SignalDeliveryStatus.Rejected,
            new UtcTimestamp(utcNow), reason: reasonCode);
        InsertReceipt(connection, transaction, lease, row.AttemptsStarted, receipt);
        if (failureKind == SignalOutboxFailureKind.RateLimited)
        {
            TimeSpan rateLimitDelay = retryAfterSeconds is > 0
                ? TimeSpan.FromSeconds(retryAfterSeconds.Value)
                : GetBackoff(policy, row.AttemptsStarted);
            ExtendRateLimit(connection, transaction,
                AddBounded(utcNow, rateLimitDelay));
        }

        if (failureKind == SignalOutboxFailureKind.Permanent)
        {
            SetTerminalState(connection, transaction, lease.StreamId,
                lease.Item.OutboxId, SignalOutboxDeliveryStatus.PermanentFailure,
                reasonCode, null, lease.LeaseToken);
        }
        else if (row.ExpiresAt <= utcNow)
        {
            SetTerminalState(connection, transaction, lease.StreamId,
                lease.Item.OutboxId, SignalOutboxDeliveryStatus.Expired,
                "message-expired", null, lease.LeaseToken);
        }
        else if (row.AttemptsStarted >= policy.MaxAttempts)
        {
            SetTerminalState(connection, transaction, lease.StreamId,
                lease.Item.OutboxId, SignalOutboxDeliveryStatus.RetryExhausted,
                "retry-limit-reached", null, lease.LeaseToken);
        }
        else
        {
            TimeSpan delay = failureKind == SignalOutboxFailureKind.RateLimited
                && retryAfterSeconds is > 0
                ? TimeSpan.FromSeconds(retryAfterSeconds.Value)
                : GetBackoff(policy, row.AttemptsStarted);

            DateTimeOffset nextAttempt = AddBounded(utcNow, delay);
            if (nextAttempt >= row.ExpiresAt)
            {
                SetTerminalState(connection, transaction, lease.StreamId,
                    lease.Item.OutboxId, SignalOutboxDeliveryStatus.Expired,
                    "message-expired", null, lease.LeaseToken);
            }
            else
            {
                SetPendingRetry(connection, transaction, lease, nextAttempt,
                    reasonCode);
            }
        }

        transaction.Commit();
    }

    public ImmutableValueList<SignalOutboxDeliverySnapshot> Read(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        List<DeliveryRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT outbox_id, payload_hash, signal_id, signal_record_hash, status,
                       attempts_started, created_at_ticks, expires_at_ticks,
                       next_attempt_ticks, lease_token, lease_until_ticks,
                       last_reason_code, external_receipt_id
                FROM telegram_outbox_delivery
                WHERE stream_id = $stream_id COLLATE BINARY
                ORDER BY created_at_ticks, outbox_id COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$stream_id", streamId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new DeliveryRow(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3),
                    ParseStatus(reader.GetString(4)), reader.GetInt32(5),
                    FromTicks(reader.GetInt64(6)), FromTicks(reader.GetInt64(7)),
                    FromTicks(reader.GetInt64(8)),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : FromTicks(reader.GetInt64(10)),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12)));
            }
        }

        List<SignalOutboxDeliverySnapshot> snapshots = [];
        foreach (DeliveryRow row in rows)
        {
            List<SignalDeliveryReceipt> receipts = ReadReceipts(connection,
                transaction, streamId, row.OutboxId, row.SignalId,
                row.SignalRecordHash);
            snapshots.Add(new SignalOutboxDeliverySnapshot(streamId, row.OutboxId,
                row.PayloadHash, row.SignalId, row.SignalRecordHash, row.Status,
                row.AttemptsStarted, row.CreatedAt, row.ExpiresAt,
                row.NextAttemptAt, row.LeaseExpiresAt, row.LastReasonCode,
                row.ExternalReceiptId, receipts));
        }
        transaction.Commit();
        return new ImmutableValueList<SignalOutboxDeliverySnapshot>(snapshots);
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
                    "A non-empty SQLite file cannot be initialized as a Telegram delivery journal.");
            }
            using SqliteTransaction transaction = connection.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            if (ReadUserVersion(connection, transaction) != 0
                || HasUserObjects(connection, transaction))
            {
                throw new InvalidDataException(
                    "The SQLite file changed while the Telegram delivery journal was initialized.");
            }
            CreateSchema(connection, transaction);
            SetUserVersion(connection, transaction, SchemaVersion);
            transaction.Commit();
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidDataException(
                $"SQLite schema version {version} is not a supported Telegram delivery journal.");
        }
        else
        {
            ValidateSchema(connection);
        }
        ConfigureDurability(connection);
    }

    private static void CreateSchema(SqliteConnection connection,
        SqliteTransaction transaction) => ExecuteNonQuery(connection, transaction, """
        CREATE TABLE telegram_delivery_policy (
            singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
            max_attempts INTEGER NOT NULL CHECK (max_attempts > 0),
            max_age_ticks INTEGER NOT NULL CHECK (max_age_ticks > 0),
            initial_backoff_ticks INTEGER NOT NULL CHECK (initial_backoff_ticks > 0),
            max_backoff_ticks INTEGER NOT NULL CHECK (max_backoff_ticks > 0),
            lease_duration_ticks INTEGER NOT NULL CHECK (lease_duration_ticks > 0),
            rate_limit_until_ticks INTEGER
        ) WITHOUT ROWID;
        CREATE TABLE telegram_outbox_delivery (
            stream_id TEXT NOT NULL COLLATE BINARY,
            outbox_id TEXT NOT NULL COLLATE BINARY,
            payload_hash TEXT NOT NULL COLLATE BINARY CHECK (length(payload_hash) = 64),
            signal_id TEXT NOT NULL COLLATE BINARY CHECK (length(signal_id) = 64),
            signal_record_hash TEXT NOT NULL COLLATE BINARY CHECK (length(signal_record_hash) = 64),
            status TEXT NOT NULL COLLATE BINARY,
            attempts_started INTEGER NOT NULL CHECK (attempts_started >= 0),
            created_at_ticks INTEGER NOT NULL,
            expires_at_ticks INTEGER NOT NULL,
            next_attempt_ticks INTEGER NOT NULL,
            lease_token TEXT COLLATE BINARY,
            lease_until_ticks INTEGER,
            last_reason_code TEXT COLLATE BINARY,
            external_receipt_id TEXT COLLATE BINARY,
            PRIMARY KEY (stream_id, outbox_id),
            CHECK ((lease_token IS NULL) = (lease_until_ticks IS NULL))
        ) WITHOUT ROWID;
        CREATE TABLE telegram_delivery_receipts (
            stream_id TEXT NOT NULL COLLATE BINARY,
            outbox_id TEXT NOT NULL COLLATE BINARY,
            attempt_number INTEGER NOT NULL CHECK (attempt_number > 0),
            receipt_id TEXT NOT NULL COLLATE BINARY CHECK (length(receipt_id) = 64),
            receipt_status TEXT NOT NULL COLLATE BINARY,
            receipt_json TEXT NOT NULL,
            PRIMARY KEY (stream_id, receipt_id),
            UNIQUE (stream_id, outbox_id, attempt_number, receipt_status)
        ) WITHOUT ROWID;
        """);

    private static void ValidateSchema(SqliteConnection connection)
    {
        (string Table, string[] Columns)[] expected =
        [
            ("telegram_delivery_policy", ["singleton", "max_attempts", "max_age_ticks",
                "initial_backoff_ticks", "max_backoff_ticks", "lease_duration_ticks",
                "rate_limit_until_ticks"]),
            ("telegram_outbox_delivery", ["stream_id", "outbox_id", "payload_hash",
                "signal_id", "signal_record_hash", "status", "attempts_started",
                "created_at_ticks", "expires_at_ticks", "next_attempt_ticks",
                "lease_token", "lease_until_ticks", "last_reason_code",
                "external_receipt_id"]),
            ("telegram_delivery_receipts", ["stream_id", "outbox_id", "attempt_number",
                "receipt_id", "receipt_status", "receipt_json"])
        ];
        foreach ((string table, string[] columns) in expected)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table});";
            using SqliteDataReader reader = command.ExecuteReader();
            List<string> actual = [];
            while (reader.Read())
            {
                actual.Add(reader.GetString(1));
            }
            if (!columns.SequenceEqual(actual, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    "The Telegram delivery journal schema is invalid.");
            }
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
                "The Telegram delivery journal contains tables outside its isolated schema.");
        }
    }

    private static void EnsurePolicy(SqliteConnection connection,
        SqliteTransaction transaction, SignalOutboxDeliveryPolicy policy)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT max_attempts, max_age_ticks, initial_backoff_ticks,
                   max_backoff_ticks, lease_duration_ticks
            FROM telegram_delivery_policy WHERE singleton = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            bool matches = reader.GetInt32(0) == policy.MaxAttempts
                && reader.GetInt64(1) == policy.MaxAge.Ticks
                && reader.GetInt64(2) == policy.InitialBackoff.Ticks
                && reader.GetInt64(3) == policy.MaxBackoff.Ticks
                && reader.GetInt64(4) == policy.LeaseDuration.Ticks;
            reader.Close();
            if (!matches)
            {
                throw new InvalidOperationException(
                    "The Telegram delivery policy differs from the durable journal policy.");
            }
            return;
        }
        reader.Close();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO telegram_delivery_policy
                (singleton, max_attempts, max_age_ticks, initial_backoff_ticks,
                 max_backoff_ticks, lease_duration_ticks)
            VALUES (1, $max_attempts, $max_age_ticks, $initial_backoff_ticks,
                    $max_backoff_ticks, $lease_duration_ticks);
            """;
        insert.Parameters.AddWithValue("$max_attempts", policy.MaxAttempts);
        insert.Parameters.AddWithValue("$max_age_ticks", policy.MaxAge.Ticks);
        insert.Parameters.AddWithValue("$initial_backoff_ticks", policy.InitialBackoff.Ticks);
        insert.Parameters.AddWithValue("$max_backoff_ticks", policy.MaxBackoff.Ticks);
        insert.Parameters.AddWithValue("$lease_duration_ticks", policy.LeaseDuration.Ticks);
        insert.ExecuteNonQuery();
    }

    private static void EnsureDeliveryRows(SqliteConnection connection,
        SqliteTransaction transaction, string streamId,
        IReadOnlyList<SignalJournalOutboxItem> outbox,
        Dictionary<string, SignalRecord> signals,
        SignalOutboxDeliveryPolicy policy)
    {
        foreach (SignalJournalOutboxItem item in outbox)
        {
            if (!signals.TryGetValue(item.ScenarioEvent.SignalId,
                    out SignalRecord? signal) || signal is null
                || !StringComparer.Ordinal.Equals(signal.RecordHash,
                    item.SignalRecordHash))
            {
                throw new InvalidDataException(
                    "A committed Telegram outbox row has invalid signal provenance.");
            }
            DeliveryRow? existing = FindDelivery(connection, transaction,
                streamId, item.OutboxId);
            if (existing is not null)
            {
                ValidateOutboxIdentity(existing, item, signal);
                continue;
            }

            DateTimeOffset createdAt = item.ScenarioEvent.AvailabilityTime.Value
                .ToUniversalTime();
            DateTimeOffset expiresAt = AddBounded(createdAt, policy.MaxAge);
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO telegram_outbox_delivery
                    (stream_id, outbox_id, payload_hash, signal_id,
                     signal_record_hash, status, attempts_started, created_at_ticks,
                     expires_at_ticks, next_attempt_ticks)
                VALUES ($stream_id, $outbox_id, $payload_hash, $signal_id,
                        $signal_record_hash, $pending, 0, $created_at_ticks,
                        $expires_at_ticks, $created_at_ticks);
                """;
            insert.Parameters.AddWithValue("$stream_id", streamId);
            insert.Parameters.AddWithValue("$outbox_id", item.OutboxId);
            insert.Parameters.AddWithValue("$payload_hash", item.PayloadHash);
            insert.Parameters.AddWithValue("$signal_id", signal.Identity.SignalId);
            insert.Parameters.AddWithValue("$signal_record_hash", signal.RecordHash);
            insert.Parameters.AddWithValue("$pending", StatusName(SignalOutboxDeliveryStatus.Pending));
            insert.Parameters.AddWithValue("$created_at_ticks", ToTicks(createdAt));
            insert.Parameters.AddWithValue("$expires_at_ticks", ToTicks(expiresAt));
            insert.ExecuteNonQuery();
        }
    }

    private static void RecoverExpiredLeases(SqliteConnection connection,
        SqliteTransaction transaction, string streamId,
        IReadOnlyList<SignalJournalOutboxItem> outbox,
        Dictionary<string, SignalRecord> signals, IReadOnlyList<SignalJournalSignal> signalEvidence,
        DateTimeOffset now)
    {
        foreach (SignalJournalOutboxItem item in outbox)
        {
            DeliveryRow? row = FindDelivery(connection, transaction, streamId,
                item.OutboxId);
            if (row is null || row.LeaseExpiresAt is null
                || row.LeaseExpiresAt > now)
            {
                continue;
            }
            if (row.Status == SignalOutboxDeliveryStatus.Leased)
            {
                if (row.ExpiresAt <= now)
                {
                    SetTerminalState(connection, transaction, streamId,
                        item.OutboxId, SignalOutboxDeliveryStatus.Expired,
                        "message-expired", null);
                }
                else
                {
                    SetPendingAfterLease(connection, transaction, streamId,
                        item.OutboxId);
                }
            }
            else if (row.Status == SignalOutboxDeliveryStatus.Sending)
            {
                if (!signals.TryGetValue(item.ScenarioEvent.SignalId,
                        out SignalRecord? signal) || signal is null)
                {
                    throw new InvalidDataException(
                        "An ambiguous Telegram delivery has no committed signal record.");
                }
                SignalDeliveryReceipt receipt = SignalDeliveryReceipt.Create(signal,
                    AttemptId(item.OutboxId, row.AttemptsStarted),
                    SignalDeliveryChannel.Telegram, SignalDeliveryStatus.Unknown,
                    new UtcTimestamp(now), reason: "delivery-lease-expired-after-send-start");
                SignalEvidenceRecord evidence = signalEvidence.First(entry =>
                    StringComparer.Ordinal.Equals(entry.Signal.Identity.SignalId, signal.Identity.SignalId)).Evidence;
                SignalOutboxLease lease = new(streamId, item, signal, evidence,
                    row.LeaseToken!, row.LeaseExpiresAt.Value, row.AttemptsStarted);
                InsertReceipt(connection, transaction, lease, row.AttemptsStarted,
                    receipt);
                SetTerminalState(connection, transaction, streamId,
                    item.OutboxId, SignalOutboxDeliveryStatus.UnknownDelivery,
                    "delivery-lease-expired-after-send-start", null,
                    row.LeaseToken);
            }
        }
    }

    private static void ExpireUnsentRows(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, DateTimeOffset now)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE telegram_outbox_delivery
            SET status = $expired, last_reason_code = 'message-expired',
                lease_token = NULL, lease_until_ticks = NULL
            WHERE stream_id = $stream_id COLLATE BINARY
              AND status = $pending AND expires_at_ticks <= $now_ticks;
            """;
        command.Parameters.AddWithValue("$expired", StatusName(SignalOutboxDeliveryStatus.Expired));
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$pending", StatusName(SignalOutboxDeliveryStatus.Pending));
        command.Parameters.AddWithValue("$now_ticks", ToTicks(now));
        command.ExecuteNonQuery();
    }

    private static DeliveryRow? FindDelivery(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, string outboxId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_hash, signal_id, signal_record_hash, status,
                   attempts_started, created_at_ticks, expires_at_ticks,
                   next_attempt_ticks, lease_token, lease_until_ticks,
                   last_reason_code, external_receipt_id
            FROM telegram_outbox_delivery
            WHERE stream_id = $stream_id COLLATE BINARY
              AND outbox_id = $outbox_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$outbox_id", outboxId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new DeliveryRow(outboxId, reader.GetString(0), reader.GetString(1),
            reader.GetString(2), ParseStatus(reader.GetString(3)),
            reader.GetInt32(4), FromTicks(reader.GetInt64(5)),
            FromTicks(reader.GetInt64(6)), FromTicks(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : FromTicks(reader.GetInt64(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    private static DeliveryRow RequireLeaseRow(SqliteConnection connection,
        SqliteTransaction transaction, SignalOutboxLease lease)
    {
        DeliveryRow row = FindDelivery(connection, transaction, lease.StreamId,
            lease.Item.OutboxId)
            ?? throw new InvalidOperationException(
                "The Telegram delivery lease is not present in the durable journal.");
        ValidateOutboxIdentity(row, lease.Item, lease.Signal);
        if (!StringComparer.Ordinal.Equals(row.LeaseToken, lease.LeaseToken))
        {
            throw new InvalidOperationException(
                "The Telegram delivery lease token is stale.");
        }
        return row;
    }

    private static void RequireSendingLease(DeliveryRow row,
        SignalOutboxLease lease)
    {
        if (row.Status != SignalOutboxDeliveryStatus.Sending
            || !StringComparer.Ordinal.Equals(row.LeaseToken, lease.LeaseToken))
        {
            throw new InvalidOperationException(
                "The Telegram delivery is no longer owned by this sender.");
        }
    }

    private static void ValidateLease(SignalOutboxLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.StreamId);
        ArgumentNullException.ThrowIfNull(lease.Item);
        ArgumentNullException.ThrowIfNull(lease.Signal);
        ArgumentNullException.ThrowIfNull(lease.Evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.LeaseToken);
    }

    private static void ValidateOutboxIdentity(DeliveryRow row,
        SignalJournalOutboxItem item, SignalRecord signal)
    {
        if (!StringComparer.Ordinal.Equals(row.OutboxId, item.OutboxId)
            || !StringComparer.Ordinal.Equals(row.PayloadHash, item.PayloadHash)
            || !StringComparer.Ordinal.Equals(row.SignalId,
                item.ScenarioEvent.SignalId)
            || !StringComparer.Ordinal.Equals(row.SignalId,
                signal.Identity.SignalId)
            || !StringComparer.Ordinal.Equals(row.SignalRecordHash,
                item.SignalRecordHash)
            || !StringComparer.Ordinal.Equals(row.SignalRecordHash,
                signal.RecordHash))
        {
            throw new InvalidDataException(
                "The Telegram delivery row conflicts with its committed outbox item.");
        }
    }

    private static void InsertReceipt(SqliteConnection connection,
        SqliteTransaction transaction, SignalOutboxLease lease, int attemptNumber,
        SignalDeliveryReceipt receipt)
    {
        string json = CoreJsonSerializer.Serialize(receipt);
        using SqliteCommand find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT receipt_json FROM telegram_delivery_receipts
            WHERE stream_id = $stream_id COLLATE BINARY
              AND receipt_id = $receipt_id COLLATE BINARY;
            """;
        find.Parameters.AddWithValue("$stream_id", lease.StreamId);
        find.Parameters.AddWithValue("$receipt_id", receipt.ReceiptId);
        object? existing = find.ExecuteScalar();
        if (existing is string existingJson)
        {
            if (!StringComparer.Ordinal.Equals(existingJson, json))
            {
                throw new InvalidDataException(
                    "A Telegram delivery receipt identity has conflicting content.");
            }
            return;
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO telegram_delivery_receipts
                (stream_id, outbox_id, attempt_number, receipt_id,
                 receipt_status, receipt_json)
            VALUES ($stream_id, $outbox_id, $attempt_number, $receipt_id,
                    $receipt_status, $receipt_json);
            """;
        insert.Parameters.AddWithValue("$stream_id", lease.StreamId);
        insert.Parameters.AddWithValue("$outbox_id", lease.Item.OutboxId);
        insert.Parameters.AddWithValue("$attempt_number", attemptNumber);
        insert.Parameters.AddWithValue("$receipt_id", receipt.ReceiptId);
        insert.Parameters.AddWithValue("$receipt_status", receipt.Status.ToString());
        insert.Parameters.AddWithValue("$receipt_json", json);
        insert.ExecuteNonQuery();
    }

    private static List<SignalDeliveryReceipt> ReadReceipts(
        SqliteConnection connection, SqliteTransaction transaction,
        string streamId, string outboxId, string signalId,
        string signalRecordHash)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT receipt_id, receipt_status, receipt_json
            FROM telegram_delivery_receipts
            WHERE stream_id = $stream_id COLLATE BINARY
              AND outbox_id = $outbox_id COLLATE BINARY
            ORDER BY attempt_number, receipt_status COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$outbox_id", outboxId);
        List<SignalDeliveryReceipt> receipts = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string receiptId = reader.GetString(0);
            string receiptStatus = reader.GetString(1);
            string json = reader.GetString(2);
            try
            {
                SignalDeliveryReceipt receipt =
                    CoreJsonSerializer.Deserialize<SignalDeliveryReceipt>(json);
                if (!StringComparer.Ordinal.Equals(receipt.ReceiptId, receiptId)
                    || !StringComparer.Ordinal.Equals(receipt.Status.ToString(), receiptStatus)
                    || !StringComparer.Ordinal.Equals(receipt.SignalId, signalId)
                    || !StringComparer.Ordinal.Equals(receipt.SignalRecordHash,
                        signalRecordHash)
                    || receipt.Channel != SignalDeliveryChannel.Telegram
                    || !StringComparer.Ordinal.Equals(
                        CoreJsonSerializer.Serialize(receipt), json))
                {
                    throw new InvalidDataException(
                        "A persisted Telegram delivery receipt does not match its row.");
                }
                receipts.Add(receipt);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException
                or ArgumentException or InvalidOperationException or FormatException
                or NotSupportedException or OverflowException)
            {
                throw new InvalidDataException(
                    "A persisted Telegram delivery receipt is invalid.", exception);
            }
        }
        return receipts;
    }

    private static void SetTerminalState(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, string outboxId,
        SignalOutboxDeliveryStatus status, string? reasonCode,
        string? externalReceiptId, string? leaseToken = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE telegram_outbox_delivery
            SET status = $status, last_reason_code = $reason_code,
                external_receipt_id = $external_receipt_id,
                lease_token = NULL, lease_until_ticks = NULL
            WHERE stream_id = $stream_id COLLATE BINARY
              AND outbox_id = $outbox_id COLLATE BINARY
              AND ($lease_token IS NULL OR lease_token = $lease_token COLLATE BINARY);
            """;
        command.Parameters.AddWithValue("$status", StatusName(status));
        command.Parameters.AddWithValue("$reason_code", DbValue(reasonCode));
        command.Parameters.AddWithValue("$external_receipt_id", DbValue(externalReceiptId));
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$outbox_id", outboxId);
        command.Parameters.AddWithValue("$lease_token", DbValue(leaseToken));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The Telegram delivery state changed before completion was persisted.");
        }
    }

    private static void SetPendingAfterLease(SqliteConnection connection,
        SqliteTransaction transaction, string streamId, string outboxId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE telegram_outbox_delivery
            SET status = $pending, lease_token = NULL, lease_until_ticks = NULL
            WHERE stream_id = $stream_id COLLATE BINARY
              AND outbox_id = $outbox_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$pending", StatusName(SignalOutboxDeliveryStatus.Pending));
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$outbox_id", outboxId);
        command.ExecuteNonQuery();
    }

    private static void SetPendingRetry(SqliteConnection connection,
        SqliteTransaction transaction, SignalOutboxLease lease,
        DateTimeOffset nextAttempt, string reasonCode)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE telegram_outbox_delivery
            SET status = $pending, next_attempt_ticks = $next_attempt_ticks,
                last_reason_code = $reason_code, lease_token = NULL,
                lease_until_ticks = NULL
            WHERE stream_id = $stream_id COLLATE BINARY
              AND outbox_id = $outbox_id COLLATE BINARY
              AND status = $sending AND lease_token = $lease_token COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$pending", StatusName(SignalOutboxDeliveryStatus.Pending));
        command.Parameters.AddWithValue("$next_attempt_ticks", ToTicks(nextAttempt));
        command.Parameters.AddWithValue("$reason_code", reasonCode);
        command.Parameters.AddWithValue("$sending", StatusName(SignalOutboxDeliveryStatus.Sending));
        AddLeaseParameters(command, lease);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                "The Telegram retry state changed before it was persisted.");
        }
    }

    private static void AddLeaseParameters(SqliteCommand command,
        SignalOutboxLease lease)
    {
        command.Parameters.AddWithValue("$stream_id", lease.StreamId);
        command.Parameters.AddWithValue("$outbox_id", lease.Item.OutboxId);
        command.Parameters.AddWithValue("$lease_token", lease.LeaseToken);
    }

    private static DateTimeOffset? ReadRateLimit(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rate_limit_until_ticks FROM telegram_delivery_policy
            WHERE singleton = 1;
            """;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null
            : FromTicks(Convert.ToInt64(value,
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void ExtendRateLimit(SqliteConnection connection,
        SqliteTransaction transaction, DateTimeOffset until)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE telegram_delivery_policy
            SET rate_limit_until_ticks = CASE
                WHEN rate_limit_until_ticks IS NULL
                  OR rate_limit_until_ticks < $until_ticks
                THEN $until_ticks ELSE rate_limit_until_ticks END
            WHERE singleton = 1;
            """;
        command.Parameters.AddWithValue("$until_ticks", ToTicks(until));
        command.ExecuteNonQuery();
    }

    private static TimeSpan GetBackoff(SignalOutboxDeliveryPolicy policy,
        int attemptNumber)
    {
        TimeSpan delay = policy.InitialBackoff;
        for (int attempt = 1; attempt < attemptNumber
            && delay < policy.MaxBackoff; attempt++)
        {
            delay = delay.Ticks >= policy.MaxBackoff.Ticks / 2
                ? policy.MaxBackoff
                : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2,
                    policy.MaxBackoff.Ticks));
        }
        return delay;
    }

    private static DateTimeOffset AddBounded(DateTimeOffset value,
        TimeSpan duration)
    {
        try
        {
            return value.Add(duration).ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue.ToUniversalTime();
        }
    }

    private static string AttemptId(string outboxId, int attemptNumber) =>
        $"{outboxId}:{attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static SignalOutboxDeliveryStatus ParseStatus(string value) =>
        Enum.TryParse(value, ignoreCase: false,
            out SignalOutboxDeliveryStatus status) && Enum.IsDefined(status)
            ? status
            : throw new InvalidDataException(
                "A persisted Telegram delivery status is unsupported.");

    private static string StatusName(SignalOutboxDeliveryStatus status) => status.ToString();

    private static long ToTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private static DateTimeOffset FromTicks(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc));

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

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

    private static void ConfigureDurability(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        ExecuteNonQuery(connection, "PRAGMA synchronous = FULL;");
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

    private sealed record DeliveryRow(string OutboxId, string PayloadHash,
        string SignalId, string SignalRecordHash, SignalOutboxDeliveryStatus Status,
        int AttemptsStarted, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
        DateTimeOffset NextAttemptAt, string? LeaseToken,
        DateTimeOffset? LeaseExpiresAt, string? LastReasonCode,
        string? ExternalReceiptId);
}
