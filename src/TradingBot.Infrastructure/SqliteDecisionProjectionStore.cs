using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>Persists canonical Decision Engine projections in SQLite.</summary>
public sealed class SqliteDecisionProjectionStore : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteDecisionProjectionStore(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>Opens a file-backed store and applies the shared migrations.</summary>
    public static SqliteDecisionProjectionStore Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteDatabase.ValidateTimeout(commandTimeoutSeconds);
        string fullPath = SqliteDatabase.NormalizePath(databasePath);
        SqliteDatabase.Initialize(fullPath, commandTimeoutSeconds);
        return new SqliteDecisionProjectionStore(fullPath, commandTimeoutSeconds);
    }

    /// <summary>
    /// Appends a projection. An exact retry is idempotent and returns
    /// <see langword="false"/>; a conflicting identity fails closed.
    /// </summary>
    public bool Append(DecisionProjectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string snapshotJson = CoreJsonSerializer.Serialize(snapshot);
        byte[] projectionId = snapshot.IdentityBytes;

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        ProjectionRow? existing = FindRow(connection, transaction, projectionId);
        if (existing is not null)
        {
            DecisionProjectionSnapshot stored = DeserializeRow(existing);
            if (StringComparer.Ordinal.Equals(stored.SnapshotId, snapshot.SnapshotId)
                && StringComparer.Ordinal.Equals(existing.SnapshotJson, snapshotJson))
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The decision projection identity already contains different content.");
        }

        if (HasDecisionIdConflict(connection, transaction, snapshot))
        {
            throw new InvalidOperationException(
                "The decision ID already contains a different projection.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO decision_projections
                (projection_id, format_version, reason_codes_version, snapshot_json)
            VALUES
                ($projection_id, $format_version, $reason_codes_version, $snapshot_json);
            """;
        command.Parameters.AddWithValue("$projection_id", projectionId);
        command.Parameters.AddWithValue("$format_version", snapshot.FormatVersion);
        command.Parameters.AddWithValue("$reason_codes_version",
            snapshot.ReasonCodesVersion);
        command.Parameters.AddWithValue("$snapshot_json", snapshotJson);
        command.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }

    /// <summary>Finds and validates a projection by lower-case hexadecimal identity.</summary>
    public DecisionProjectionSnapshot? Find(string snapshotId)
    {
        byte[] binaryIdentity = ParseIdentity(snapshotId);
        return Find(binaryIdentity);
    }

    /// <summary>Finds and validates a projection by its 32-byte identity.</summary>
    public DecisionProjectionSnapshot? Find(byte[] snapshotId)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);
        if (snapshotId.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException(
                "A decision projection identity must contain 32 bytes.", nameof(snapshotId));
        }

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        ProjectionRow? row = FindRow(connection, transaction: null, snapshotId.ToArray());
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
                    "A decision projection identity must contain 32 bytes.", nameof(snapshotId));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "A decision projection identity must be a 64-character hexadecimal SHA-256 value.",
                nameof(snapshotId), exception);
        }
    }

    private static ProjectionRow? FindRow(SqliteConnection connection,
        SqliteTransaction? transaction, byte[] projectionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT projection_id, format_version, reason_codes_version, snapshot_json
            FROM decision_projections
            WHERE projection_id = $projection_id;
            """;
        command.Parameters.AddWithValue("$projection_id", projectionId);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ProjectionRow(reader.GetFieldValue<byte[]>(0), reader.GetInt32(1),
            reader.GetString(2), reader.GetString(3));
    }

    private static bool HasDecisionIdConflict(SqliteConnection connection,
        SqliteTransaction transaction, DecisionProjectionSnapshot snapshot)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT projection_id, format_version, reason_codes_version, snapshot_json
            FROM decision_projections;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ProjectionRow row = new(reader.GetFieldValue<byte[]>(0), reader.GetInt32(1),
                reader.GetString(2), reader.GetString(3));
            DecisionProjectionSnapshot stored = DeserializeRow(row);
            if (StringComparer.Ordinal.Equals(stored.DecisionId, snapshot.DecisionId)
                && !StringComparer.Ordinal.Equals(stored.SnapshotId, snapshot.SnapshotId))
            {
                return true;
            }
        }

        return false;
    }

    private static DecisionProjectionSnapshot DeserializeRow(ProjectionRow row)
    {
        try
        {
            if (row.ProjectionId.Length != SHA256.HashSizeInBytes)
            {
                throw new InvalidDataException(
                    "The persisted decision projection identity is not a SHA-256 value.");
            }

            DecisionProjectionSnapshot snapshot = CoreJsonSerializer.Deserialize<
                DecisionProjectionSnapshot>(row.SnapshotJson);
            if (snapshot.FormatVersion != row.FormatVersion)
            {
                throw new InvalidDataException(
                    "The persisted decision projection format does not match its row.");
            }

            if (!StringComparer.Ordinal.Equals(snapshot.ReasonCodesVersion,
                row.ReasonCodesVersion))
            {
                throw new InvalidDataException(
                    "The persisted decision reason-code version does not match its row.");
            }

            if (!CryptographicOperations.FixedTimeEquals(snapshot.IdentityBytes,
                row.ProjectionId))
            {
                throw new InvalidDataException(
                    "The persisted decision projection identity does not match its content.");
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(snapshot),
                row.SnapshotJson))
            {
                throw new InvalidDataException(
                    "The persisted decision projection JSON is not canonical.");
            }

            _ = snapshot.Reevaluate();
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
            throw new InvalidDataException("The persisted decision projection is invalid.",
                exception);
        }
    }

    private sealed record ProjectionRow(byte[] ProjectionId, int FormatVersion,
        string ReasonCodesVersion, string SnapshotJson);
}
