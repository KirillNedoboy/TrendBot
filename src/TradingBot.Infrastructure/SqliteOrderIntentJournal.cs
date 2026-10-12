using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Persists immutable order intents before any future execution side effect.
/// </summary>
/// <remarks>
/// This journal owns only durable intent state. It does not submit orders,
/// reserve portfolio state, record fills, or reconcile positions.
/// </remarks>
public sealed class SqliteOrderIntentJournal : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteOrderIntentJournal(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>Opens a file-backed order-intent journal and applies shared migrations.</summary>
    public static SqliteOrderIntentJournal Open(string databasePath, int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (StringComparer.Ordinal.Equals(databasePath, ":memory:"))
        {
            throw new ArgumentException("The order-intent journal requires a file-backed SQLite database.",
                nameof(databasePath));
        }

        if (commandTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeoutSeconds),
                commandTimeoutSeconds, "The SQLite command timeout must be positive.");
        }

        string fullPath = Path.GetFullPath(databasePath);
        using (SqliteEventJournal.Open(fullPath, commandTimeoutSeconds))
        {
        }

        return new SqliteOrderIntentJournal(fullPath, commandTimeoutSeconds);
    }

    /// <summary>
    /// Appends an intent after the transaction commits.
    /// </summary>
    /// <returns><see langword="false"/> when the identical intent is already present.</returns>
    public bool Append(OrderIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        string intentJson = CoreJsonSerializer.Serialize(intent);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        IntentRow? existingByIntentId = FindRowByIntentId(connection, transaction, intent.IntentId);
        if (existingByIntentId is not null)
        {
            OrderIntent existing = DeserializeRow(existingByIntentId);
            if (existing.Equals(intent))
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The intent identifier already contains a different order intent.");
        }

        IntentRow? existingByClientOrderId = FindRowByClientOrderId(connection, transaction,
            intent.ClientOrderId);
        if (existingByClientOrderId is not null)
        {
            _ = DeserializeRow(existingByClientOrderId);
            throw new InvalidOperationException(
                "The client order identifier already belongs to a different order intent.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO order_intents (intent_id, client_order_id, intent_json)
            VALUES ($intent_id, $client_order_id, $intent_json);
            """;
        command.Parameters.AddWithValue("$intent_id", intent.IntentId);
        command.Parameters.AddWithValue("$client_order_id", intent.ClientOrderId);
        command.Parameters.AddWithValue("$intent_json", intentJson);
        command.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }

    /// <summary>Finds an intent by its immutable intent identifier.</summary>
    public OrderIntent? Find(string intentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);

        using SqliteConnection connection = OpenConnection();
        IntentRow? row = FindRowByIntentId(connection, transaction: null, intentId);
        return row is null ? null : DeserializeRow(row);
    }

    /// <summary>Releases no shared connection; each operation owns its connection.</summary>
    public void Dispose()
    {
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

    private static IntentRow? FindRowByIntentId(SqliteConnection connection,
        SqliteTransaction? transaction, string intentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, client_order_id, intent_json
            FROM order_intents
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        return ReadRow(command);
    }

    private static IntentRow? FindRowByClientOrderId(SqliteConnection connection,
        SqliteTransaction transaction, string clientOrderId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, client_order_id, intent_json
            FROM order_intents
            WHERE client_order_id = $client_order_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$client_order_id", clientOrderId);
        return ReadRow(command);
    }

    private static IntentRow? ReadRow(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new IntentRow(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static OrderIntent DeserializeRow(IntentRow row)
    {
        try
        {
            OrderIntent intent = CoreJsonSerializer.Deserialize<OrderIntent>(row.IntentJson);
            if (!StringComparer.Ordinal.Equals(intent.IntentId, row.IntentId)
                || !StringComparer.Ordinal.Equals(intent.ClientOrderId, row.ClientOrderId))
            {
                throw new InvalidDataException(
                    "The persisted order intent identity does not match its SQLite row.");
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(intent), row.IntentJson))
            {
                throw new InvalidDataException("The persisted order intent JSON is not canonical.");
            }

            return intent;
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
            throw new InvalidDataException("The persisted order intent JSON is invalid.", exception);
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record IntentRow(string IntentId, string ClientOrderId, string IntentJson);
}
