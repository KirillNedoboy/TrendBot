using System.Data;
using Microsoft.Data.Sqlite;

namespace TradingBot.Infrastructure;

/// <summary>
/// Owns the shared SQLite connection policy, schema migrations, and schema
/// validation used by each durable Infrastructure store.
/// </summary>
internal static class SqliteDatabase
{
    public const int CurrentSchemaVersion = 14;

    private static readonly JournalColumn[] RequiredEventColumns =
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

    public static string NormalizePath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (StringComparer.Ordinal.Equals(databasePath, ":memory:"))
        {
            throw new ArgumentException("Durable SQLite stores require a file-backed database.",
                nameof(databasePath));
        }

        return Path.GetFullPath(databasePath);
    }

    public static SqliteConnection OpenConnection(string databasePath, int commandTimeoutSeconds)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = commandTimeoutSeconds
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

    public static void ValidateTimeout(int commandTimeoutSeconds)
    {
        if (commandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds),
                commandTimeoutSeconds, "The SQLite command timeout must be positive.");
        }
    }

    public static void Initialize(string databasePath, int commandTimeoutSeconds)
    {
        using SqliteConnection connection = OpenConnection(databasePath, commandTimeoutSeconds);
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
                SetUserVersion(connection, transaction, 3);
                MigrateV3ToV4(connection, transaction);
                SetUserVersion(connection, transaction, 4);
                MigrateV4ToV5(connection, transaction);
                SetUserVersion(connection, transaction, 5);
                MigrateV5ToV6(connection, transaction);
                SetUserVersion(connection, transaction, 6);
                MigrateV6ToV7(connection, transaction);
                SetUserVersion(connection, transaction, 7);
                MigrateV7ToV8(connection, transaction);
                SetUserVersion(connection, transaction, 8);
                MigrateV8ToV9(connection, transaction);
                SetUserVersion(connection, transaction, 9);
                MigrateV9ToV10(connection, transaction);
                SetUserVersion(connection, transaction, 10);
                MigrateV10ToV11(connection, transaction);
                SetUserVersion(connection, transaction, 11);
                MigrateV11ToV12(connection, transaction);
                SetUserVersion(connection, transaction, 12);
                MigrateV12ToV13(connection, transaction);
                SetUserVersion(connection, transaction, 13);
                MigrateV13ToV14(connection, transaction);
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
        else if (version is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13)
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
                SetUserVersion(connection, transaction, 3);
                lockedVersion = 3;
            }

            if (lockedVersion == 3)
            {
                MigrateV3ToV4(connection, transaction);
                SetUserVersion(connection, transaction, 4);
                lockedVersion = 4;
            }

            if (lockedVersion == 4)
            {
                MigrateV4ToV5(connection, transaction);
                SetUserVersion(connection, transaction, 5);
                lockedVersion = 5;
            }

            if (lockedVersion == 5)
            {
                MigrateV5ToV6(connection, transaction);
                SetUserVersion(connection, transaction, 6);
                lockedVersion = 6;
            }

            if (lockedVersion == 6)
            {
                MigrateV6ToV7(connection, transaction);
                SetUserVersion(connection, transaction, 7);
                lockedVersion = 7;
            }

            if (lockedVersion == 7)
            {
                MigrateV7ToV8(connection, transaction);
                SetUserVersion(connection, transaction, 8);
                lockedVersion = 8;
            }

            if (lockedVersion == 8)
            {
                MigrateV8ToV9(connection, transaction);
                SetUserVersion(connection, transaction, 9);
                lockedVersion = 9;
            }

            if (lockedVersion == 9)
            {
                MigrateV9ToV10(connection, transaction);
                SetUserVersion(connection, transaction, 10);
                lockedVersion = 10;
            }

            if (lockedVersion == 10)
            {
                MigrateV10ToV11(connection, transaction);
                SetUserVersion(connection, transaction, 11);
                lockedVersion = 11;
            }

            if (lockedVersion == 11)
            {
                MigrateV11ToV12(connection, transaction);
                SetUserVersion(connection, transaction, 12);
                lockedVersion = 12;
            }

            if (lockedVersion == 12)
            {
                MigrateV12ToV13(connection, transaction);
                SetUserVersion(connection, transaction, 13);
                lockedVersion = 13;
            }

            if (lockedVersion == 13)
            {
                MigrateV13ToV14(connection, transaction);
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

        ValidateEventSchema(connection);
        ValidateOrderIntentSchema(connection);
        ValidatePortfolioSchema(connection);
        ValidateOperationalSchema(connection);
        ValidateExecutionSchema(connection);
        ValidateCaptureSchema(connection);
        ValidateScreenerSnapshotSchema(connection);
        ValidateUniverseSelectionSnapshotSchema(connection);
        ValidateFeatureQualitySnapshotSchema(connection);
        ValidateDecisionProjectionSchema(connection);
        ValidateRiskEvaluationSchema(connection);
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

    private static void MigrateV3ToV4(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE portfolios
            (
                portfolio_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                limits_json TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE reservations
            (
                reservation_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                portfolio_id TEXT COLLATE BINARY NOT NULL,
                intent_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                reservation_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void ValidateEventSchema(SqliteConnection connection)
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

        if (!RequiredEventColumns.SequenceEqual(actualColumns))
        {
            throw new InvalidDataException(
                $"The event_journal schema does not match version {CurrentSchemaVersion}.");
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

    private static void MigrateV4ToV5(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            ALTER TABLE reservations
            ADD COLUMN released INTEGER NOT NULL DEFAULT 0;

            CREATE TABLE fills
            (
                fill_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                intent_id TEXT COLLATE BINARY NOT NULL,
                observation_revision INTEGER NOT NULL,
                fill_json TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE position_snapshots
            (
                portfolio_id TEXT COLLATE BINARY NOT NULL,
                position_id TEXT COLLATE BINARY NOT NULL,
                revision INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL,
                PRIMARY KEY (portfolio_id, position_id, revision)
            ) WITHOUT ROWID;

            CREATE TABLE reconciliation_checkpoints
            (
                portfolio_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                revision INTEGER NOT NULL,
                checkpoint_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV5ToV6(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE capture_segments
            (
                segment_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                stream_id TEXT COLLATE BINARY NOT NULL,
                file_path TEXT COLLATE BINARY NOT NULL,
                format_version INTEGER NOT NULL,
                row_count INTEGER NOT NULL,
                first_capture_sequence INTEGER NOT NULL,
                last_capture_sequence INTEGER NOT NULL,
                first_stable_sequence INTEGER NOT NULL,
                last_stable_sequence INTEGER NOT NULL,
                byte_length INTEGER NOT NULL,
                created_time_ticks INTEGER NOT NULL,
                durable_time_ticks INTEGER NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE capture_checkpoints
            (
                stream_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                next_capture_sequence INTEGER NOT NULL,
                last_durable_sequence INTEGER NOT NULL,
                last_segment_id TEXT COLLATE BINARY NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV6ToV7(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE screener_snapshots
            (
                snapshot_id BLOB NOT NULL PRIMARY KEY,
                format_version INTEGER NOT NULL,
                formula_version TEXT COLLATE BINARY NOT NULL,
                snapshot_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV7ToV8(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE universe_selection_snapshots
            (
                snapshot_id BLOB NOT NULL PRIMARY KEY,
                format_version INTEGER NOT NULL,
                algorithm_version TEXT COLLATE BINARY NOT NULL,
                snapshot_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV8ToV9(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS feature_quality_snapshots
            (
                snapshot_id BLOB NOT NULL PRIMARY KEY,
                format_version INTEGER NOT NULL,
                algorithm_version TEXT COLLATE BINARY NOT NULL,
                snapshot_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV9ToV10(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS decision_projections
            (
                projection_id BLOB NOT NULL PRIMARY KEY,
                format_version INTEGER NOT NULL,
                reason_codes_version TEXT COLLATE BINARY NOT NULL,
                snapshot_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV10ToV11(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS risk_evaluations
            (
                evaluation_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                decision_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                intent_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                reservation_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                portfolio_id TEXT COLLATE BINARY NOT NULL,
                evidence_json TEXT NOT NULL
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV11ToV12(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS execution_states
            (
                intent_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                client_order_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                state_json TEXT NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS execution_attempts
            (
                attempt_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                intent_id TEXT COLLATE BINARY NOT NULL,
                attempt INTEGER NOT NULL,
                reconciliation_revision INTEGER NOT NULL,
                attempt_json TEXT NOT NULL,
                UNIQUE (intent_id, attempt)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS execution_receipts
            (
                intent_id TEXT COLLATE BINARY NOT NULL,
                receipt_id TEXT COLLATE BINARY NOT NULL,
                receipt_json TEXT NOT NULL,
                PRIMARY KEY (intent_id, receipt_id)
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV12ToV13(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS development_dispatch_claims
            (
                attempt_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                intent_id TEXT COLLATE BINARY NOT NULL,
                claim_json TEXT NOT NULL,
                UNIQUE (intent_id, attempt_id)
            ) WITHOUT ROWID;
            """);
    }

    private static void MigrateV13ToV14(SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ExecuteNonQuery(connection, transaction, """
            CREATE TABLE IF NOT EXISTS protective_dispatch_claims
            (
                intent_id TEXT COLLATE BINARY NOT NULL PRIMARY KEY,
                attempt_id TEXT COLLATE BINARY NOT NULL UNIQUE,
                portfolio_id TEXT COLLATE BINARY NOT NULL,
                position_id TEXT COLLATE BINARY NOT NULL,
                source_intent_id TEXT COLLATE BINARY NOT NULL,
                reconciliation_revision INTEGER NOT NULL,
                request_json TEXT NOT NULL,
                UNIQUE (portfolio_id, position_id, source_intent_id)
            ) WITHOUT ROWID;
            """);
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
            throw new InvalidDataException(
                $"The order_intents schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidatePortfolioSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, sql
            FROM sqlite_master
            WHERE type = 'table' AND name IN ('portfolios', 'reservations')
            ORDER BY name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> definitions = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        if (!definitions.TryGetValue("portfolios", out string? portfolioSql)
            || portfolioSql.IndexOf("portfolio_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || portfolioSql.IndexOf("limits_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("reservations", out string? reservationSql)
            || reservationSql.IndexOf("reservation_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || reservationSql.IndexOf("portfolio_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || reservationSql.IndexOf("intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || reservationSql.IndexOf("reservation_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || reservationSql.IndexOf("released INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || reservationSql.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The portfolio reservation schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateOperationalSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, sql
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('fills', 'position_snapshots', 'reconciliation_checkpoints')
            ORDER BY name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> definitions = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        if (!definitions.TryGetValue("fills", out string? fillsSql)
            || fillsSql.IndexOf("fill_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || fillsSql.IndexOf("intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || fillsSql.IndexOf("observation_revision INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || fillsSql.IndexOf("fill_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("position_snapshots", out string? snapshotsSql)
            || snapshotsSql.IndexOf("portfolio_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || snapshotsSql.IndexOf("position_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || snapshotsSql.IndexOf("revision INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || snapshotsSql.IndexOf("snapshot_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("reconciliation_checkpoints", out string? checkpointsSql)
            || checkpointsSql.IndexOf("portfolio_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || checkpointsSql.IndexOf("revision INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || checkpointsSql.IndexOf("checkpoint_json TEXT", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The operational schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateExecutionSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, sql
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('execution_states', 'execution_attempts', 'execution_receipts',
                           'development_dispatch_claims', 'protective_dispatch_claims')
            ORDER BY name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> definitions = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        if (!definitions.TryGetValue("execution_states", out string? statesSql)
            || statesSql.IndexOf("intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || statesSql.IndexOf("client_order_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || statesSql.IndexOf("state_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("execution_attempts", out string? attemptsSql)
            || attemptsSql.IndexOf("attempt_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || attemptsSql.IndexOf("reconciliation_revision INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || attemptsSql.IndexOf("attempt_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("execution_receipts", out string? receiptsSql)
            || receiptsSql.IndexOf("intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || receiptsSql.IndexOf("receipt_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || receiptsSql.IndexOf("receipt_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("development_dispatch_claims", out string? claimsSql)
            || claimsSql.IndexOf("attempt_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || claimsSql.IndexOf("intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || claimsSql.IndexOf("claim_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("protective_dispatch_claims", out string? protectiveSql)
            || protectiveSql.IndexOf("intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || protectiveSql.IndexOf("attempt_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || protectiveSql.IndexOf("portfolio_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || protectiveSql.IndexOf("position_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || protectiveSql.IndexOf("source_intent_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || protectiveSql.IndexOf("reconciliation_revision INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || protectiveSql.IndexOf("request_json TEXT", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The execution schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateCaptureSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, sql
            FROM sqlite_master
            WHERE type = 'table' AND name IN ('capture_segments', 'capture_checkpoints')
            ORDER BY name;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, string> definitions = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(0), reader.GetString(1));
        }

        if (!definitions.TryGetValue("capture_segments", out string? segmentsSql)
            || segmentsSql.IndexOf("segment_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || segmentsSql.IndexOf("stream_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || segmentsSql.IndexOf("file_path TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || segmentsSql.IndexOf("row_count INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || segmentsSql.IndexOf("first_capture_sequence INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || segmentsSql.IndexOf("last_capture_sequence INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || !definitions.TryGetValue("capture_checkpoints", out string? checkpointsSql)
            || checkpointsSql.IndexOf("stream_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0
            || checkpointsSql.IndexOf("next_capture_sequence INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || checkpointsSql.IndexOf("last_durable_sequence INTEGER", StringComparison.OrdinalIgnoreCase) < 0
            || checkpointsSql.IndexOf("last_segment_id TEXT COLLATE BINARY", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The capture schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateScreenerSnapshotSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(screener_snapshots);";
        using SqliteDataReader reader = command.ExecuteReader();

        List<JournalColumn> actualColumns = [];
        while (reader.Read())
        {
            actualColumns.Add(new JournalColumn(reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(5)));
        }

        JournalColumn[] requiredColumns =
        [
            new("snapshot_id", "BLOB", 1, 1),
            new("format_version", "INTEGER", 1, 0),
            new("formula_version", "TEXT", 1, 0),
            new("snapshot_json", "TEXT", 1, 0)
        ];
        if (!requiredColumns.SequenceEqual(actualColumns))
        {
            throw new InvalidDataException(
                $"The screener_snapshots schema does not match version {CurrentSchemaVersion}.");
        }

        using SqliteCommand sqlCommand = connection.CreateCommand();
        sqlCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'screener_snapshots';
            """;
        string? createSql = sqlCommand.ExecuteScalar() as string;
        if (createSql is null
            || createSql.IndexOf("snapshot_id BLOB", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("formula_version TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The screener_snapshots schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateUniverseSelectionSnapshotSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(universe_selection_snapshots);";
        using SqliteDataReader reader = command.ExecuteReader();

        List<JournalColumn> actualColumns = [];
        while (reader.Read())
        {
            actualColumns.Add(new JournalColumn(reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(5)));
        }

        JournalColumn[] requiredColumns =
        [
            new("snapshot_id", "BLOB", 1, 1),
            new("format_version", "INTEGER", 1, 0),
            new("algorithm_version", "TEXT", 1, 0),
            new("snapshot_json", "TEXT", 1, 0)
        ];
        if (!requiredColumns.SequenceEqual(actualColumns))
        {
            throw new InvalidDataException(
                $"The universe_selection_snapshots schema does not match version {CurrentSchemaVersion}.");
        }

        using SqliteCommand sqlCommand = connection.CreateCommand();
        sqlCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'universe_selection_snapshots';
            """;
        string? createSql = sqlCommand.ExecuteScalar() as string;
        if (createSql is null
            || createSql.IndexOf("snapshot_id BLOB", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("algorithm_version TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The universe_selection_snapshots schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateFeatureQualitySnapshotSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(feature_quality_snapshots);";
        using SqliteDataReader reader = command.ExecuteReader();

        List<JournalColumn> actualColumns = [];
        while (reader.Read())
        {
            actualColumns.Add(new JournalColumn(reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(5)));
        }

        JournalColumn[] requiredColumns =
        [
            new("snapshot_id", "BLOB", 1, 1),
            new("format_version", "INTEGER", 1, 0),
            new("algorithm_version", "TEXT", 1, 0),
            new("snapshot_json", "TEXT", 1, 0)
        ];
        if (!requiredColumns.SequenceEqual(actualColumns))
        {
            throw new InvalidDataException(
                $"The feature_quality_snapshots schema does not match version {CurrentSchemaVersion}.");
        }

        using SqliteCommand sqlCommand = connection.CreateCommand();
        sqlCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'feature_quality_snapshots';
            """;
        string? createSql = sqlCommand.ExecuteScalar() as string;
        if (createSql is null
            || createSql.IndexOf("snapshot_id BLOB", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("algorithm_version TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The feature_quality_snapshots schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateDecisionProjectionSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(decision_projections);";
        using SqliteDataReader reader = command.ExecuteReader();

        List<JournalColumn> actualColumns = [];
        while (reader.Read())
        {
            actualColumns.Add(new JournalColumn(reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(5)));
        }

        JournalColumn[] requiredColumns =
        [
            new("projection_id", "BLOB", 1, 1),
            new("format_version", "INTEGER", 1, 0),
            new("reason_codes_version", "TEXT", 1, 0),
            new("snapshot_json", "TEXT", 1, 0)
        ];
        if (!requiredColumns.SequenceEqual(actualColumns))
        {
            throw new InvalidDataException(
                $"The decision_projections schema does not match version {CurrentSchemaVersion}.");
        }

        using SqliteCommand sqlCommand = connection.CreateCommand();
        sqlCommand.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'decision_projections';
            """;
        string? createSql = sqlCommand.ExecuteScalar() as string;
        if (createSql is null
            || createSql.IndexOf("projection_id BLOB", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("reason_codes_version TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The decision_projections schema does not match version {CurrentSchemaVersion}.");
        }
    }

    private static void ValidateRiskEvaluationSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT sql
            FROM sqlite_master
            WHERE type = 'table' AND name = 'risk_evaluations';
            """;
        string? createSql = command.ExecuteScalar() as string;
        if (createSql is null
            || createSql.IndexOf("evaluation_id TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("decision_id TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("intent_id TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("reservation_id TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("portfolio_id TEXT COLLATE BINARY",
                StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("evidence_json TEXT", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) < 0
            || createSql.IndexOf("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidDataException(
                $"The risk_evaluations schema does not match version {CurrentSchemaVersion}.");
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
}
