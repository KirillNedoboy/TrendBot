using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

public sealed partial class SqlitePortfolioReservationStore
{
    /// <summary>
    /// Persists a fill and position observations with the supplied next
    /// portfolio revision in one immediate serializable transaction.
    /// </summary>
    public PortfolioObservationResult RecordObservation(Fill? fill,
        IReadOnlyList<PortfolioPositionSnapshot> positionSnapshots,
        ReconciliationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(positionSnapshots);
        ArgumentNullException.ThrowIfNull(checkpoint);
        List<PortfolioPositionSnapshot> snapshots = positionSnapshots.ToList();
        ValidateSnapshotArguments(snapshots, checkpoint.PortfolioId);

        string? fillJson = fill is null ? null : CoreJsonSerializer.Serialize(fill);
        string checkpointJson = CoreJsonSerializer.Serialize(
            Unreconciled(checkpoint));

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        CheckpointRow? currentRow = FindCheckpoint(connection, transaction,
            checkpoint.PortfolioId.Value);
        ReconciliationCheckpoint? current = currentRow is null
            ? null
            : DeserializeCheckpoint(currentRow);
        long currentRevision = current?.Revision ?? 0;

        if (current is not null && checkpoint.Revision == currentRevision)
        {
            if (MatchesRecordedObservation(connection, transaction, fill, snapshots,
                Unreconciled(checkpoint), current))
            {
                transaction.Commit();
                return PortfolioObservationResult.AlreadyRecorded;
            }

            throw new InvalidOperationException(
                "The observation revision has already been recorded with different state.");
        }

        if (checkpoint.Revision <= currentRevision
            || checkpoint.Revision != checked(currentRevision + 1))
        {
            throw new InvalidOperationException(
                "The observation revision is stale or skips a portfolio revision.");
        }

        if (fill is not null)
        {
            FillRow? existingFill = FindFillById(connection, transaction, fill.FillId);
            if (existingFill is not null)
            {
                Fill storedFill = DeserializeFill(existingFill);
                if (!storedFill.Equals(fill))
                {
                    throw new InvalidOperationException(
                        "The fill identifier already contains a different fill.");
                }

                throw new InvalidOperationException(
                    "The fill identifier belongs to an earlier observation revision.");
            }

            ValidateFillCapacity(connection, transaction, fill, checkpoint.PortfolioId);
        }

        foreach (PortfolioPositionSnapshot snapshot in snapshots)
        {
            PositionSnapshotRow? existingSnapshot = FindPositionSnapshot(connection, transaction,
                snapshot.PortfolioId.Value, snapshot.Position.PositionId, checkpoint.Revision);
            if (existingSnapshot is not null)
            {
                PortfolioPositionSnapshot storedSnapshot = DeserializePositionSnapshot(
                    existingSnapshot);
                if (!storedSnapshot.Equals(snapshot))
                {
                    throw new InvalidOperationException(
                        "The position snapshot identity already contains different state.");
                }

                throw new InvalidOperationException(
                    "The position snapshot belongs to an earlier observation revision.");
            }

            ValidateSnapshotIntent(connection, transaction, snapshot);
        }

        if (fill is not null)
        {
            InsertFill(connection, transaction, fill, fillJson!, checkpoint.Revision);
        }

        foreach (PortfolioPositionSnapshot snapshot in snapshots)
        {
            InsertPositionSnapshot(connection, transaction, snapshot, checkpoint.Revision);
        }

        if (currentRow is null)
        {
            InsertCheckpoint(connection, transaction, Unreconciled(checkpoint), checkpointJson);
        }
        else
        {
            UpdateCheckpoint(connection, transaction, Unreconciled(checkpoint), checkpointJson);
        }

        transaction.Commit();
        return PortfolioObservationResult.Recorded;
    }

    /// <summary>Reads the current checkpoint, defaulting to revision zero.</summary>
    public ReconciliationCheckpoint ReadCheckpoint(PortfolioId portfolioId)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);

        using SqliteConnection connection = OpenConnection();
        CheckpointRow? row = FindCheckpoint(connection, transaction: null, portfolioId.Value);
        return row is null
            ? EmptyCheckpoint(portfolioId)
            : DeserializeCheckpoint(row);
    }

    /// <summary>Reads one persisted fill by its immutable identity.</summary>
    public Fill? FindFill(string fillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fillId);

        using SqliteConnection connection = OpenConnection();
        FillRow? row = FindFillById(connection, transaction: null, fillId);
        return row is null ? null : DeserializeFill(row);
    }

    /// <summary>Reads immutable position snapshots in revision and identity order.</summary>
    public IReadOnlyList<PortfolioPositionSnapshot> ReadPositionSnapshots(PortfolioId portfolioId)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT portfolio_id, position_id, revision, snapshot_json
            FROM position_snapshots
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
            ORDER BY revision ASC, position_id ASC;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId.Value);

        List<PortfolioPositionSnapshot> snapshots = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshots.Add(DeserializePositionSnapshot(new PositionSnapshotRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3))));
        }

        return new ReadOnlyCollection<PortfolioPositionSnapshot>(snapshots);
    }

    /// <summary>
    /// Marks the current revision as explicitly acknowledged. Acknowledgement
    /// never removes unknown or manual markers; the retry guard still blocks
    /// while either remains unresolved.
    /// </summary>
    public ReconciliationCheckpoint AcknowledgeReconciliation(PortfolioId portfolioId,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision,
                "A reconciliation revision cannot be negative.");
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        CheckpointRow? row = FindCheckpoint(connection, transaction, portfolioId.Value);
        if (row is null)
        {
            if (revision != 0)
            {
                throw new InvalidOperationException(
                    "The requested reconciliation revision is stale or missing.");
            }

            ReconciliationCheckpoint emptyAcknowledged = new(portfolioId, 0,
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), ReconciliationStatus.Acknowledged);
            InsertCheckpoint(connection, transaction, emptyAcknowledged,
                CoreJsonSerializer.Serialize(emptyAcknowledged));
            transaction.Commit();
            return emptyAcknowledged;
        }

        ReconciliationCheckpoint current = DeserializeCheckpoint(row);
        if (current.Revision != revision)
        {
            throw new InvalidOperationException(
                "The requested reconciliation revision is stale or missing.");
        }

        if (current.Status == ReconciliationStatus.Acknowledged)
        {
            transaction.Commit();
            return current;
        }

        ReconciliationCheckpoint acknowledged = new(current.PortfolioId, current.Revision,
            current.OpenIntentIds, current.OpenPositionIds, current.UnknownMarkers,
            current.ManualPositionIds, ReconciliationStatus.Acknowledged);
        UpdateCheckpoint(connection, transaction, acknowledged,
            CoreJsonSerializer.Serialize(acknowledged));
        transaction.Commit();
        return acknowledged;
    }

    /// <summary>
    /// Returns whether the current revision is acknowledged and contains no
    /// unresolved unknown or manual state.
    /// </summary>
    public bool CanRetry(PortfolioId portfolioId)
    {
        ReconciliationCheckpoint checkpoint = ReadCheckpoint(portfolioId);
        return checkpoint.Status == ReconciliationStatus.Acknowledged
            && checkpoint.UnknownMarkers.Count == 0
            && checkpoint.ManualPositionIds.Count == 0;
    }

    /// <summary>
    /// Releases the complete reservation after the acknowledged checkpoint
    /// confirms no open order and no associated position remains open.
    /// </summary>
    public bool ReleaseAfterReconciliation(PortfolioId portfolioId, string intentId,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ContractGuardProxy.RequireText(intentId, nameof(intentId));
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision,
                "A reconciliation revision cannot be negative.");
        }

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        CheckpointRow? checkpointRow = FindCheckpoint(connection, transaction, portfolioId.Value);
        if (checkpointRow is null)
        {
            throw new InvalidOperationException("No reconciliation checkpoint exists.");
        }

        ReconciliationCheckpoint checkpoint = DeserializeCheckpoint(checkpointRow);
        if (checkpoint.Revision != revision)
        {
            throw new InvalidOperationException(
                "The requested reconciliation revision is stale or missing.");
        }

        if (checkpoint.Status != ReconciliationStatus.Acknowledged
            || checkpoint.UnknownMarkers.Count != 0
            || checkpoint.ManualPositionIds.Count != 0)
        {
            throw new InvalidOperationException(
                "A reservation can be released only after acknowledged reconciliation.");
        }

        if (checkpoint.OpenIntentIds.Contains(intentId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The order intent remains open.");
        }

        foreach (PortfolioPositionSnapshot snapshot in ReadLatestSnapshots(connection, transaction,
            portfolioId.Value))
        {
            if (!StringComparer.Ordinal.Equals(snapshot.IntentId, intentId))
            {
                continue;
            }

            if (snapshot.Position.Quantity.Value > 0m
                || checkpoint.OpenPositionIds.Contains(snapshot.Position.PositionId,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The associated position remains open.");
            }
        }

        ReservationRow? reservation = FindReservationByIntentId(connection, transaction, intentId);
        if (reservation is null
            || !StringComparer.Ordinal.Equals(reservation.PortfolioId, portfolioId.Value))
        {
            throw new InvalidOperationException("No reservation exists for the order intent.");
        }

        PortfolioReservation storedReservation = DeserializeReservation(reservation);
        if (reservation.Released)
        {
            transaction.Commit();
            return false;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reservations
            SET released = 1
            WHERE reservation_id = $reservation_id COLLATE BINARY
              AND released = 0;
            """;
        command.Parameters.AddWithValue("$reservation_id", storedReservation.ReservationId);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    private static ReconciliationCheckpoint EmptyCheckpoint(PortfolioId portfolioId) =>
        new(portfolioId, 0, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), ReconciliationStatus.Unreconciled);

    private static ReconciliationCheckpoint Unreconciled(ReconciliationCheckpoint checkpoint) =>
        new(checkpoint.PortfolioId, checkpoint.Revision, checkpoint.OpenIntentIds,
            checkpoint.OpenPositionIds, checkpoint.UnknownMarkers, checkpoint.ManualPositionIds,
            ReconciliationStatus.Unreconciled);

    private static void ValidateSnapshotArguments(List<PortfolioPositionSnapshot> snapshots,
        PortfolioId portfolioId)
    {
        HashSet<string> positionIds = new(StringComparer.Ordinal);
        foreach (PortfolioPositionSnapshot snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.PortfolioId != portfolioId)
            {
                throw new ArgumentException(
                    "Every position snapshot must belong to the checkpoint portfolio.",
                    nameof(snapshots));
            }

            if (!positionIds.Add(snapshot.Position.PositionId))
            {
                throw new ArgumentException(
                    "A position can have only one snapshot per observation revision.",
                    nameof(snapshots));
            }
        }
    }

    private static bool MatchesRecordedObservation(SqliteConnection connection,
        SqliteTransaction transaction, Fill? fill,
        List<PortfolioPositionSnapshot> snapshots,
        ReconciliationCheckpoint expected, ReconciliationCheckpoint current)
    {
        if (!Unreconciled(current).Equals(expected))
        {
            return false;
        }

        if (fill is null)
        {
            using SqliteCommand countCommand = connection.CreateCommand();
            countCommand.Transaction = transaction;
            countCommand.CommandText = """
                SELECT COUNT(*)
                FROM fills AS f
                INNER JOIN reservations AS r ON r.intent_id = f.intent_id COLLATE BINARY
                WHERE r.portfolio_id = $portfolio_id COLLATE BINARY
                  AND f.observation_revision = $revision;
                """;
            countCommand.Parameters.AddWithValue("$portfolio_id", current.PortfolioId.Value);
            countCommand.Parameters.AddWithValue("$revision", current.Revision);
            if (Convert.ToInt64(countCommand.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                return false;
            }
        }
        else
        {
            FillRow? row = FindFillById(connection, transaction, fill.FillId);
            if (row is null || row.ObservationRevision != current.Revision
                || !DeserializeFill(row).Equals(fill))
            {
                return false;
            }
        }

        List<PortfolioPositionSnapshot> storedSnapshots = ReadSnapshotsAtRevision(connection,
            transaction, current.PortfolioId.Value, current.Revision);
        return storedSnapshots.Count == snapshots.Count
            && storedSnapshots.OrderBy(item => item.Position.PositionId,
                StringComparer.Ordinal).SequenceEqual(snapshots.OrderBy(
                    item => item.Position.PositionId, StringComparer.Ordinal));
    }

    private static void ValidateFillCapacity(SqliteConnection connection,
        SqliteTransaction transaction, Fill fill, PortfolioId portfolioId)
    {
        IntentRow? intentRow = FindIntentById(connection, transaction, fill.IntentId);
        if (intentRow is null)
        {
            throw new InvalidOperationException("The fill intent must already be persisted.");
        }

        OrderIntent intent = DeserializeIntent(intentRow);
        if (intent.InstrumentId != fill.InstrumentId)
        {
            throw new InvalidOperationException("The fill instrument does not match its intent.");
        }

        ReservationRow? reservation = FindReservationByIntentId(connection, transaction,
            fill.IntentId);
        if (reservation is not null
            && !StringComparer.Ordinal.Equals(reservation.PortfolioId, portfolioId.Value))
        {
            throw new InvalidOperationException(
                "The fill intent reservation belongs to a different portfolio.");
        }

        decimal total = 0m;
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fill_id, intent_id, observation_revision, fill_json
            FROM fills
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", fill.IntentId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            Fill storedFill = DeserializeFill(new FillRow(reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetString(3)));
            total = checked(total + storedFill.Quantity.Value);
        }

        if (checked(total + fill.Quantity.Value) > intent.Quantity.Value)
        {
            throw new InvalidOperationException(
                "Aggregate fill quantity cannot exceed the order intent quantity.");
        }
    }

    private static void ValidateSnapshotIntent(SqliteConnection connection,
        SqliteTransaction transaction, PortfolioPositionSnapshot snapshot)
    {
        if (snapshot.IntentId is null)
        {
            return;
        }

        IntentRow? intentRow = FindIntentById(connection, transaction, snapshot.IntentId);
        if (intentRow is null)
        {
            throw new InvalidOperationException(
                "A known position snapshot must refer to a persisted order intent.");
        }

        OrderIntent intent = DeserializeIntent(intentRow);
        if (intent.InstrumentId != snapshot.Position.InstrumentId)
        {
            throw new InvalidOperationException(
                "The position snapshot instrument does not match its intent.");
        }
    }

    private static FillRow? FindFillById(SqliteConnection connection,
        SqliteTransaction? transaction, string fillId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT fill_id, intent_id, observation_revision, fill_json
            FROM fills
            WHERE fill_id = $fill_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$fill_id", fillId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new FillRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3))
            : null;
    }

    private static CheckpointRow? FindCheckpoint(SqliteConnection connection,
        SqliteTransaction? transaction, string portfolioId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT portfolio_id, revision, checkpoint_json
            FROM reconciliation_checkpoints
            WHERE portfolio_id = $portfolio_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new CheckpointRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2))
            : null;
    }

    private static PositionSnapshotRow? FindPositionSnapshot(SqliteConnection connection,
        SqliteTransaction transaction, string portfolioId, string positionId, long revision)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT portfolio_id, position_id, revision, snapshot_json
            FROM position_snapshots
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
              AND position_id = $position_id COLLATE BINARY
              AND revision = $revision;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        command.Parameters.AddWithValue("$position_id", positionId);
        command.Parameters.AddWithValue("$revision", revision);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new PositionSnapshotRow(reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetString(3))
            : null;
    }

    private static List<PortfolioPositionSnapshot> ReadSnapshotsAtRevision(
        SqliteConnection connection, SqliteTransaction transaction, string portfolioId,
        long revision)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT portfolio_id, position_id, revision, snapshot_json
            FROM position_snapshots
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
              AND revision = $revision
            ORDER BY position_id ASC;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        command.Parameters.AddWithValue("$revision", revision);
        return ReadSnapshots(command);
    }

    private static List<PortfolioPositionSnapshot> ReadLatestSnapshots(
        SqliteConnection connection, SqliteTransaction transaction, string portfolioId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT portfolio_id, position_id, revision, snapshot_json
            FROM position_snapshots
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
            ORDER BY revision DESC, position_id ASC;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        List<PortfolioPositionSnapshot> snapshots = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (seen.Add(reader.GetString(1)))
            {
                snapshots.Add(DeserializePositionSnapshot(new PositionSnapshotRow(
                    reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    reader.GetString(3))));
            }
        }

        return snapshots;
    }

    private static List<PortfolioPositionSnapshot> ReadSnapshots(SqliteCommand command)
    {
        List<PortfolioPositionSnapshot> snapshots = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshots.Add(DeserializePositionSnapshot(new PositionSnapshotRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3))));
        }

        return snapshots;
    }

    private static void InsertFill(SqliteConnection connection, SqliteTransaction transaction,
        Fill fill, string fillJson, long revision)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO fills (fill_id, intent_id, observation_revision, fill_json)
            VALUES ($fill_id, $intent_id, $revision, $fill_json);
            """;
        command.Parameters.AddWithValue("$fill_id", fill.FillId);
        command.Parameters.AddWithValue("$intent_id", fill.IntentId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$fill_json", fillJson);
        command.ExecuteNonQuery();
    }

    private static void InsertPositionSnapshot(SqliteConnection connection,
        SqliteTransaction transaction, PortfolioPositionSnapshot snapshot, long revision)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO position_snapshots
                (portfolio_id, position_id, revision, snapshot_json)
            VALUES ($portfolio_id, $position_id, $revision, $snapshot_json);
            """;
        command.Parameters.AddWithValue("$portfolio_id", snapshot.PortfolioId.Value);
        command.Parameters.AddWithValue("$position_id", snapshot.Position.PositionId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$snapshot_json", CoreJsonSerializer.Serialize(snapshot));
        command.ExecuteNonQuery();
    }

    private static void InsertCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, ReconciliationCheckpoint checkpoint, string checkpointJson)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reconciliation_checkpoints
                (portfolio_id, revision, checkpoint_json)
            VALUES ($portfolio_id, $revision, $checkpoint_json);
            """;
        command.Parameters.AddWithValue("$portfolio_id", checkpoint.PortfolioId.Value);
        command.Parameters.AddWithValue("$revision", checkpoint.Revision);
        command.Parameters.AddWithValue("$checkpoint_json", checkpointJson);
        command.ExecuteNonQuery();
    }

    private static void UpdateCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, ReconciliationCheckpoint checkpoint, string checkpointJson)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reconciliation_checkpoints
            SET revision = $revision, checkpoint_json = $checkpoint_json
            WHERE portfolio_id = $portfolio_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$portfolio_id", checkpoint.PortfolioId.Value);
        command.Parameters.AddWithValue("$revision", checkpoint.Revision);
        command.Parameters.AddWithValue("$checkpoint_json", checkpointJson);
        command.ExecuteNonQuery();
    }

    private static Fill DeserializeFill(FillRow row)
    {
        try
        {
            Fill fill = CoreJsonSerializer.Deserialize<Fill>(row.FillJson);
            if (!StringComparer.Ordinal.Equals(fill.FillId, row.FillId)
                || !StringComparer.Ordinal.Equals(fill.IntentId, row.IntentId))
            {
                throw new InvalidDataException(
                    "The persisted fill identity does not match its SQLite row.");
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(fill), row.FillJson))
            {
                throw new InvalidDataException("The persisted fill JSON is not canonical.");
            }

            return fill;
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
            throw new InvalidDataException("The persisted fill JSON is invalid.", exception);
        }
    }

    private static PortfolioPositionSnapshot DeserializePositionSnapshot(PositionSnapshotRow row)
    {
        try
        {
            PortfolioPositionSnapshot snapshot =
                CoreJsonSerializer.Deserialize<PortfolioPositionSnapshot>(row.SnapshotJson);
            if (!StringComparer.Ordinal.Equals(snapshot.PortfolioId.Value, row.PortfolioId)
                || !StringComparer.Ordinal.Equals(snapshot.Position.PositionId, row.PositionId))
            {
                throw new InvalidDataException(
                    "The persisted position snapshot identity does not match its SQLite row.");
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(snapshot),
                row.SnapshotJson))
            {
                throw new InvalidDataException(
                    "The persisted position snapshot JSON is not canonical.");
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
            or OverflowException
            or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(
                "The persisted position snapshot JSON is invalid.", exception);
        }
    }

    private static ReconciliationCheckpoint DeserializeCheckpoint(CheckpointRow row)
    {
        try
        {
            ReconciliationCheckpoint checkpoint =
                CoreJsonSerializer.Deserialize<ReconciliationCheckpoint>(row.CheckpointJson);
            if (!StringComparer.Ordinal.Equals(checkpoint.PortfolioId.Value, row.PortfolioId)
                || checkpoint.Revision != row.Revision)
            {
                throw new InvalidDataException(
                    "The persisted reconciliation checkpoint identity does not match its SQLite row.");
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(checkpoint),
                row.CheckpointJson))
            {
                throw new InvalidDataException(
                    "The persisted reconciliation checkpoint JSON is not canonical.");
            }

            return checkpoint;
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
            throw new InvalidDataException(
                "The persisted reconciliation checkpoint JSON is invalid.", exception);
        }
    }

    private sealed record FillRow(string FillId, string IntentId, long ObservationRevision,
        string FillJson);

    private sealed record PositionSnapshotRow(string PortfolioId, string PositionId, long Revision,
        string SnapshotJson);

    private sealed record CheckpointRow(string PortfolioId, long Revision, string CheckpointJson);

    private static class ContractGuardProxy
    {
        public static void RequireText(string value, string parameterName) =>
            ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
    }
}
