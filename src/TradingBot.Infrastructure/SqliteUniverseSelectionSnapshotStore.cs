using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Persists immutable, replayable staged-selection chains under their
/// canonical SHA-256 identity.
/// </summary>
public sealed class SqliteUniverseSelectionSnapshotStore : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteUniverseSelectionSnapshotStore(string databasePath,
        int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>Opens a file-backed store and applies the shared migrations.</summary>
    public static SqliteUniverseSelectionSnapshotStore Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteDatabase.ValidateTimeout(commandTimeoutSeconds);
        string fullPath = SqliteDatabase.NormalizePath(databasePath);
        SqliteDatabase.Initialize(fullPath, commandTimeoutSeconds);
        return new SqliteUniverseSelectionSnapshotStore(fullPath, commandTimeoutSeconds);
    }

    /// <summary>
    /// Appends a chain after its immediate transaction commits. An identical
    /// retry returns <see langword="false"/>.
    /// </summary>
    public bool Append(StagedSelectionChainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string snapshotJson = CoreJsonSerializer.Serialize(snapshot);
        byte[] snapshotId = ParseIdentity(snapshot.SnapshotIdentity);

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        SnapshotRow? existing = FindRow(connection, transaction, snapshotId);
        if (existing is not null)
        {
            StagedSelectionChainSnapshot stored = DeserializeRow(existing);
            if (StringComparer.Ordinal.Equals(stored.SnapshotIdentity,
                    snapshot.SnapshotIdentity)
                && StringComparer.Ordinal.Equals(existing.SnapshotJson, snapshotJson))
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The staged-selection identity already contains different content.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO universe_selection_snapshots
                (snapshot_id, format_version, algorithm_version, snapshot_json)
            VALUES
                ($snapshot_id, $format_version, $algorithm_version, $snapshot_json);
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId);
        command.Parameters.AddWithValue("$format_version", snapshot.FormatVersion);
        command.Parameters.AddWithValue("$algorithm_version", snapshot.AlgorithmVersion);
        command.Parameters.AddWithValue("$snapshot_json", snapshotJson);
        command.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }

    /// <summary>Finds and validates a chain by its hexadecimal SHA-256 identity.</summary>
    public StagedSelectionChainSnapshot? Find(string snapshotId)
    {
        byte[] binaryIdentity = ParseIdentity(snapshotId);
        return Find(binaryIdentity);
    }

    /// <summary>Finds and validates a chain by its binary SHA-256 identity.</summary>
    public StagedSelectionChainSnapshot? Find(byte[] snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        if (snapshotId.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException(
                "A staged-selection identity must contain 32 bytes.", nameof(snapshotId));
        }

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        SnapshotRow? row = FindRow(connection, transaction: null, snapshotId.ToArray());
        return row is null ? null : DeserializeRow(row);
    }

    /// <summary>Each operation owns its connection; disposal has no shared state.</summary>
    public void Dispose()
    {
    }

    private static byte[] ParseIdentity(string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        try
        {
            byte[] identity = Convert.FromHexString(snapshotId);
            return identity.Length == SHA256.HashSizeInBytes
                ? identity
                : throw new ArgumentException(
                    "A staged-selection identity must contain 32 bytes.", nameof(snapshotId));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A staged-selection identity must be a 64-character hexadecimal SHA-256 value.",
                nameof(snapshotId), exception);
        }
    }

    private static SnapshotRow? FindRow(SqliteConnection connection,
        SqliteTransaction? transaction, byte[] snapshotId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT snapshot_id, format_version, algorithm_version, snapshot_json
            FROM universe_selection_snapshots
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

    private static StagedSelectionChainSnapshot DeserializeRow(SnapshotRow row)
    {
        try
        {
            if (row.SnapshotId.Length != SHA256.HashSizeInBytes)
            {
                throw new InvalidDataException(
                    "The persisted staged-selection identity is not a SHA-256 value.");
            }

            StagedSelectionChainSnapshot snapshot =
                CoreJsonSerializer.Deserialize<StagedSelectionChainSnapshot>(
                    row.SnapshotJson);
            if (snapshot.FormatVersion != row.FormatVersion)
            {
                throw new InvalidDataException(
                    "The persisted staged-selection format does not match its row.");
            }

            if (!StringComparer.Ordinal.Equals(snapshot.AlgorithmVersion,
                row.AlgorithmVersion))
            {
                throw new InvalidDataException(
                    "The persisted staged-selection algorithm does not match its row.");
            }

            byte[] computedIdentity = ParseIdentity(snapshot.SnapshotIdentity);
            if (!CryptographicOperations.FixedTimeEquals(computedIdentity,
                row.SnapshotId))
            {
                throw new InvalidDataException(
                    "The persisted staged-selection identity does not match its content.");
            }

            if (!StringComparer.Ordinal.Equals(
                CoreJsonSerializer.Serialize(snapshot), row.SnapshotJson))
            {
                throw new InvalidDataException(
                    "The persisted staged-selection JSON is not canonical.");
            }

            _ = snapshot.Replay();
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
            throw new InvalidDataException(
                "The persisted staged-selection snapshot is invalid.", exception);
        }
    }

    private sealed record SnapshotRow(byte[] SnapshotId, int FormatVersion,
        string AlgorithmVersion, string SnapshotJson);
}
