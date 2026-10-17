using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Persists immutable minimum screener snapshots in the shared SQLite schema.
/// </summary>
public sealed class SqliteScreenerSnapshotStore : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteScreenerSnapshotStore(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>Opens a file-backed store and applies the shared migrations.</summary>
    public static SqliteScreenerSnapshotStore Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteDatabase.ValidateTimeout(commandTimeoutSeconds);
        string fullPath = SqliteDatabase.NormalizePath(databasePath);
        SqliteDatabase.Initialize(fullPath, commandTimeoutSeconds);
        return new SqliteScreenerSnapshotStore(fullPath, commandTimeoutSeconds);
    }

    /// <summary>
    /// Appends a snapshot after its SQLite transaction commits. An identical
    /// retry returns <see langword="false"/>.
    /// </summary>
    public bool Append(ScreenerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string snapshotJson = CoreJsonSerializer.Serialize(snapshot);
        byte[] snapshotId = snapshot.IdentityBytes;

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        SnapshotRow? existing = FindRow(connection, transaction, snapshotId);
        if (existing is not null)
        {
            ScreenerSnapshot stored = DeserializeRow(existing);
            if (StringComparer.Ordinal.Equals(stored.SnapshotId, snapshot.SnapshotId)
                && StringComparer.Ordinal.Equals(existing.SnapshotJson, snapshotJson))
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The snapshot identity already contains different snapshot content.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO screener_snapshots
                (snapshot_id, format_version, formula_version, snapshot_json)
            VALUES
                ($snapshot_id, $format_version, $formula_version, $snapshot_json);
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        command.Parameters.AddWithValue("$format_version", snapshot.FormatVersion);
        command.Parameters.AddWithValue("$formula_version", snapshot.FormulaVersion.Value);
        command.Parameters.AddWithValue("$snapshot_json", snapshotJson);
        command.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }

    /// <summary>Finds and validates a snapshot by its SHA-256 identity.</summary>
    public ScreenerSnapshot? Find(string snapshotId)
    {
        byte[] binaryIdentity = ParseIdentity(snapshotId);
        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        SnapshotRow? row = FindRow(connection, transaction: null, binaryIdentity);
        return row is null ? null : DeserializeRow(row);
    }

    /// <summary>Finds and validates a snapshot by its binary SHA-256 identity.</summary>
    public ScreenerSnapshot? Find(byte[] snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        if (snapshotId.Length != 32)
        {
            throw new ArgumentException("A screener snapshot identity must contain 32 bytes.",
                nameof(snapshotId));
        }

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        SnapshotRow? row = FindRow(connection, transaction: null, snapshotId.ToArray());
        return row is null ? null : DeserializeRow(row);
    }

    /// <summary>Releases no shared connection; each operation owns its connection.</summary>
    public void Dispose()
    {
    }

    private static byte[] ParseIdentity(string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        try
        {
            byte[] identity = Convert.FromHexString(snapshotId);
            return identity.Length == 32
                ? identity
                : throw new ArgumentException(
                    "A screener snapshot identity must contain 32 bytes.", nameof(snapshotId));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A screener snapshot identity must be a 64-character hexadecimal SHA-256 value.",
                nameof(snapshotId), exception);
        }
    }

    private static SnapshotRow? FindRow(SqliteConnection connection,
        SqliteTransaction? transaction, byte[] snapshotId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_id, format_version, formula_version, snapshot_json
            FROM screener_snapshots
            WHERE snapshot_id = $snapshot_id;
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SnapshotRow(reader.GetFieldValue<byte[]>(0), reader.GetInt32(1),
            reader.GetString(2), reader.GetString(3));
    }

    private static ScreenerSnapshot DeserializeRow(SnapshotRow row)
    {
        try
        {
            if (row.SnapshotId.Length != 32)
            {
                throw new InvalidDataException(
                    "The persisted screener snapshot identity is not a SHA-256 value.");
            }

            ScreenerSnapshot snapshot = CoreJsonSerializer.Deserialize<ScreenerSnapshot>(
                row.SnapshotJson);
            if (snapshot.FormatVersion != row.FormatVersion)
            {
                throw new InvalidDataException(
                    "The persisted screener snapshot format does not match its row.");
            }

            if (!StringComparer.Ordinal.Equals(snapshot.FormulaVersion.Value,
                row.FormulaVersion))
            {
                throw new InvalidDataException(
                    "The persisted screener snapshot formula does not match its row.");
            }

            if (!CryptographicOperations.FixedTimeEquals(snapshot.IdentityBytes, row.SnapshotId))
            {
                throw new InvalidDataException(
                    "The persisted screener snapshot identity does not match its content.");
            }

            if (!StringComparer.Ordinal.Equals(
                CoreJsonSerializer.Serialize(snapshot), row.SnapshotJson))
            {
                throw new InvalidDataException(
                    "The persisted screener snapshot JSON is not canonical.");
            }

            return snapshot;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            throw new InvalidDataException("The persisted screener snapshot is invalid.",
                exception);
        }
    }

    private sealed record SnapshotRow(byte[] SnapshotId, int FormatVersion,
        string FormulaVersion, string SnapshotJson);
}
