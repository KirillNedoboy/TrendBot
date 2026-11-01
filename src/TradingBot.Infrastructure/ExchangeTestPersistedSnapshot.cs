using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>One immutable read transaction snapshot used by exchange-test evidence.</summary>
public sealed class ExchangeTestPersistedSnapshot
{
    public ExchangeTestPersistedSnapshot(ExchangeTestRunMetadata runMetadata,
        IEnumerable<ExchangeTestEventReceipt> privateEvents,
        IEnumerable<OrderIntent> intents,
        IEnumerable<ExecutionStateSnapshot> lifecycles,
        IEnumerable<ExecutionDispatchAttempt> attempts,
        IEnumerable<ExecutionObservation> executionReceipts,
        IEnumerable<Fill> fills,
        IEnumerable<PortfolioPositionSnapshot> positions,
        IEnumerable<ReconciliationCheckpoint> checkpoints,
        IEnumerable<ExchangeTestPersistedReservation> reservations,
        IEnumerable<ExchangeTestPersistedProtectiveLink> protectiveLinks)
    {
        RunMetadata = runMetadata ?? throw new ArgumentNullException(nameof(runMetadata));
        PrivateEvents = Snapshot(privateEvents, nameof(privateEvents));
        Intents = Snapshot(intents, nameof(intents));
        Lifecycles = Snapshot(lifecycles, nameof(lifecycles));
        Attempts = Snapshot(attempts, nameof(attempts));
        ExecutionReceipts = Snapshot(executionReceipts, nameof(executionReceipts));
        Fills = Snapshot(fills, nameof(fills));
        Positions = Snapshot(positions, nameof(positions));
        Checkpoints = Snapshot(checkpoints, nameof(checkpoints));
        Reservations = Snapshot(reservations, nameof(reservations));
        ProtectiveLinks = Snapshot(protectiveLinks, nameof(protectiveLinks));
    }

    public ExchangeTestRunMetadata RunMetadata { get; }
    public IReadOnlyList<ExchangeTestEventReceipt> PrivateEvents { get; }
    public IReadOnlyList<OrderIntent> Intents { get; }
    public IReadOnlyList<ExecutionStateSnapshot> Lifecycles { get; }
    public IReadOnlyList<ExecutionDispatchAttempt> Attempts { get; }
    public IReadOnlyList<ExecutionObservation> ExecutionReceipts { get; }
    public IReadOnlyList<Fill> Fills { get; }
    public IReadOnlyList<PortfolioPositionSnapshot> Positions { get; }
    public IReadOnlyList<ReconciliationCheckpoint> Checkpoints { get; }
    public IReadOnlyList<ExchangeTestPersistedReservation> Reservations { get; }
    public IReadOnlyList<ExchangeTestPersistedProtectiveLink> ProtectiveLinks { get; }

    private static ReadOnlyCollection<T> Snapshot<T>(IEnumerable<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        T[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("A persisted evidence row cannot be null.",
                parameterName);
        }

        return new ReadOnlyCollection<T>(snapshot);
    }
}

/// <summary>Normalized reservation row captured without its SQLite JSON envelope.</summary>
public sealed record ExchangeTestPersistedReservation(string ReservationId,
    string PortfolioId, string IntentId, bool Released, PortfolioReservation Reservation);

/// <summary>Protective dispatch identity captured without credential-bearing request JSON.</summary>
public sealed record ExchangeTestPersistedProtectiveLink(string IntentId, string AttemptId,
    string PortfolioId, string PositionId, string SourceIntentId,
    long ReconciliationRevision);

/// <summary>Read-only, single-transaction access to one exchange-test run snapshot.</summary>
public static class SqliteExchangeTestStoreSnapshotReader
{
    public static ExchangeTestPersistedSnapshot ReadPersistedSnapshot(
        this SqliteExchangeTestStore exchangeStore,
        SqlitePortfolioOperationalStore portfolioStore, PortfolioId portfolioId,
        IEnumerable<string>? intentIds = null)
    {
        ArgumentNullException.ThrowIfNull(exchangeStore);
        ArgumentNullException.ThrowIfNull(portfolioStore);
        ArgumentNullException.ThrowIfNull(portfolioId);
        if (!StringComparer.OrdinalIgnoreCase.Equals(exchangeStore.DatabasePath,
                portfolioStore.DatabasePath))
        {
            throw new InvalidOperationException(
                "Exchange-test evidence requires one shared SQLite database.");
        }

        string[] requestedIntentIds = NormalizeIds(intentIds);
        ExchangeTestRunMetadata expectedMetadata = exchangeStore.ReadRunMetadata();
        using SqliteConnection connection = SqliteDatabase.OpenConnection(
            exchangeStore.DatabasePath, commandTimeoutSeconds: 30);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: true);

        ExchangeTestRunMetadata metadata = ReadMetadata(connection, transaction,
            expectedMetadata.RunId);
        if (!metadata.Equals(expectedMetadata))
        {
            throw new InvalidDataException(
                "The exchange-test run metadata changed during evidence capture.");
        }

        List<ExchangeTestEventReceipt> events = ReadPrivateEvents(connection, transaction,
            metadata.RunId);
        HashSet<string> selectedIntentIds = new(requestedIntentIds,
            StringComparer.Ordinal);
        foreach (ExchangeTestEventReceipt receipt in events)
        {
            selectedIntentIds.Add(receipt.IntentId);
        }

        List<OrderIntent> intents = ReadIntents(connection, transaction, selectedIntentIds);
        List<ExecutionStateSnapshot> lifecycles = ReadLifecycles(connection, transaction,
            selectedIntentIds);
        List<ExecutionDispatchAttempt> attempts = ReadAttempts(connection, transaction,
            selectedIntentIds);
        List<ExecutionObservation> executionReceipts = ReadExecutionReceipts(connection,
            transaction, selectedIntentIds);
        List<Fill> fills = ReadFills(connection, transaction, selectedIntentIds);
        List<PortfolioPositionSnapshot> positions = ReadPositions(connection, transaction,
            portfolioId.Value);
        List<ReconciliationCheckpoint> checkpoints = ReadCheckpoints(connection, transaction,
            portfolioId.Value);
        List<ExchangeTestPersistedReservation> reservations = ReadReservations(connection,
            transaction, portfolioId.Value);
        List<ExchangeTestPersistedProtectiveLink> protectiveLinks = ReadProtectiveLinks(
            connection, transaction, portfolioId.Value, selectedIntentIds);

        transaction.Commit();
        return new ExchangeTestPersistedSnapshot(metadata, events, intents, lifecycles,
            attempts, executionReceipts, fills, positions, checkpoints, reservations,
            protectiveLinks);
    }

    private static ExchangeTestRunMetadata ReadMetadata(SqliteConnection connection,
        SqliteTransaction transaction, string runId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT run_id, configuration_id, environment_id, started_at_ticks,
                   started_at_offset_minutes
            FROM exchange_test_runs
            WHERE run_id = $run_id COLLATE BINARY;
            """);
        command.Parameters.AddWithValue("$run_id", runId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("The exchange-test run metadata is missing.");
        }

        return new ExchangeTestRunMetadata(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), FromTimestamp(reader.GetInt64(3), reader.GetInt32(4)));
    }

    private static List<ExchangeTestEventReceipt> ReadPrivateEvents(SqliteConnection connection,
        SqliteTransaction transaction, string runId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT run_id, identity, intent_id, event_type, stable_sequence,
                   event_time_ticks, event_time_offset_minutes, receive_time_ticks,
                   receive_time_offset_minutes, availability_time_ticks,
                   availability_time_offset_minutes, exchange_order_id, fill_quantity,
                   lifecycle, requires_reconciliation, fingerprint
            FROM exchange_test_receipts
            WHERE run_id = $run_id COLLATE BINARY
            ORDER BY stable_sequence ASC, identity ASC;
            """);
        command.Parameters.AddWithValue("$run_id", runId);
        List<ExchangeTestEventReceipt> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            decimal? fillQuantity = reader.IsDBNull(12) ? null
                : decimal.Parse(reader.GetString(12), System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(new ExchangeTestEventReceipt(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                FromTimestamp(reader.GetInt64(5), reader.GetInt32(6)),
                FromTimestamp(reader.GetInt64(7), reader.GetInt32(8)),
                FromTimestamp(reader.GetInt64(9), reader.GetInt32(10)),
                reader.IsDBNull(11) ? null : reader.GetString(11), fillQuantity,
                reader.GetString(13), reader.GetInt64(14) != 0, reader.GetString(15)));
        }

        return rows;
    }

    private static List<OrderIntent> ReadIntents(SqliteConnection connection,
        SqliteTransaction transaction, HashSet<string> intentIds)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT intent_id, client_order_id, intent_json
            FROM order_intents
            ORDER BY intent_id ASC;
            """);
        List<OrderIntent> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string rowId = reader.GetString(0);
            if (!intentIds.Contains(rowId))
            {
                continue;
            }

            string rowClientId = reader.GetString(1);
            rows.Add(ReadCanonical<OrderIntent>(reader.GetString(2),
                value => StringComparer.Ordinal.Equals(value.IntentId, rowId)
                    && StringComparer.Ordinal.Equals(value.ClientOrderId, rowClientId),
                "The persisted order intent is inconsistent."));
        }

        return rows;
    }

    private static List<ExecutionStateSnapshot> ReadLifecycles(SqliteConnection connection,
        SqliteTransaction transaction, HashSet<string> intentIds)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT intent_id, client_order_id, state_json
            FROM execution_states
            ORDER BY intent_id ASC;
            """);
        List<ExecutionStateSnapshot> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string rowId = reader.GetString(0);
            if (!intentIds.Contains(rowId))
            {
                continue;
            }

            string rowClientId = reader.GetString(1);
            rows.Add(ReadCanonical<ExecutionStateSnapshot>(reader.GetString(2),
                value => StringComparer.Ordinal.Equals(value.IntentId, rowId)
                    && StringComparer.Ordinal.Equals(value.ClientOrderId, rowClientId),
                "The persisted execution lifecycle is inconsistent."));
        }

        return rows;
    }

    private static List<ExecutionDispatchAttempt> ReadAttempts(SqliteConnection connection,
        SqliteTransaction transaction, HashSet<string> intentIds)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT attempt_id, intent_id, attempt, reconciliation_revision, attempt_json
            FROM execution_attempts
            ORDER BY intent_id ASC, attempt ASC;
            """);
        List<ExecutionDispatchAttempt> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string attemptId = reader.GetString(0);
            string intentId = reader.GetString(1);
            if (!intentIds.Contains(intentId))
            {
                continue;
            }

            int attempt = reader.GetInt32(2);
            long revision = reader.GetInt64(3);
            rows.Add(ReadCanonical<ExecutionDispatchAttempt>(reader.GetString(4),
                value => StringComparer.Ordinal.Equals(value.AttemptId, attemptId)
                    && StringComparer.Ordinal.Equals(value.IntentId, intentId)
                    && value.Attempt == attempt
                    && value.ReconciliationRevision == revision,
                "The persisted execution attempt is inconsistent."));
        }

        return rows;
    }

    private static List<ExecutionObservation> ReadExecutionReceipts(
        SqliteConnection connection, SqliteTransaction transaction, HashSet<string> intentIds)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT intent_id, receipt_id, receipt_json
            FROM execution_receipts
            ORDER BY intent_id ASC, receipt_id ASC;
            """);
        List<ExecutionObservation> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string intentId = reader.GetString(0);
            if (!intentIds.Contains(intentId))
            {
                continue;
            }

            string receiptId = reader.GetString(1);
            rows.Add(ReadCanonical<ExecutionObservation>(reader.GetString(2),
                value => StringComparer.Ordinal.Equals(value.IntentId, intentId)
                    && StringComparer.Ordinal.Equals(value.ReceiptId, receiptId),
                "The persisted execution receipt is inconsistent."));
        }

        return rows;
    }

    private static List<Fill> ReadFills(SqliteConnection connection,
        SqliteTransaction transaction, HashSet<string> intentIds)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT fill_id, intent_id, fill_json
            FROM fills
            ORDER BY intent_id ASC, fill_id ASC;
            """);
        List<Fill> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string fillId = reader.GetString(0);
            string intentId = reader.GetString(1);
            if (!intentIds.Contains(intentId))
            {
                continue;
            }

            rows.Add(ReadCanonical<Fill>(reader.GetString(2),
                value => StringComparer.Ordinal.Equals(value.FillId, fillId)
                    && StringComparer.Ordinal.Equals(value.IntentId, intentId),
                "The persisted fill is inconsistent."));
        }

        return rows;
    }

    private static List<PortfolioPositionSnapshot> ReadPositions(SqliteConnection connection,
        SqliteTransaction transaction, string portfolioId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT portfolio_id, position_id, revision, snapshot_json
            FROM position_snapshots
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
            ORDER BY revision ASC, position_id ASC;
            """);
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        List<PortfolioPositionSnapshot> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string rowPortfolioId = reader.GetString(0);
            string positionId = reader.GetString(1);
            rows.Add(ReadCanonical<PortfolioPositionSnapshot>(reader.GetString(3),
                value => StringComparer.Ordinal.Equals(value.PortfolioId.Value, rowPortfolioId)
                    && StringComparer.Ordinal.Equals(value.Position.PositionId, positionId),
                "The persisted position snapshot is inconsistent."));
        }

        return rows;
    }

    private static List<ReconciliationCheckpoint> ReadCheckpoints(SqliteConnection connection,
        SqliteTransaction transaction, string portfolioId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT portfolio_id, revision, checkpoint_json
            FROM reconciliation_checkpoints
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
            ORDER BY revision ASC;
            """);
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        List<ReconciliationCheckpoint> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string rowPortfolioId = reader.GetString(0);
            rows.Add(ReadCanonical<ReconciliationCheckpoint>(reader.GetString(2),
                value => StringComparer.Ordinal.Equals(value.PortfolioId.Value, rowPortfolioId),
                "The persisted reconciliation checkpoint is inconsistent."));
        }

        return rows;
    }

    private static List<ExchangeTestPersistedReservation> ReadReservations(
        SqliteConnection connection, SqliteTransaction transaction, string portfolioId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT reservation_id, portfolio_id, intent_id, reservation_json, released
            FROM reservations
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
            ORDER BY reservation_id ASC;
            """);
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        List<ExchangeTestPersistedReservation> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string reservationId = reader.GetString(0);
            string rowPortfolioId = reader.GetString(1);
            string intentId = reader.GetString(2);
            PortfolioReservation reservation = ReadCanonical<PortfolioReservation>(reader.GetString(3),
                value => StringComparer.Ordinal.Equals(value.ReservationId, reservationId)
                    && StringComparer.Ordinal.Equals(value.PortfolioId.Value, rowPortfolioId)
                    && StringComparer.Ordinal.Equals(value.IntentId, intentId),
                "The persisted reservation is inconsistent.");
            rows.Add(new ExchangeTestPersistedReservation(reservationId, rowPortfolioId,
                intentId, reader.GetInt64(4) != 0, reservation));
        }

        return rows;
    }

    private static List<ExchangeTestPersistedProtectiveLink> ReadProtectiveLinks(
        SqliteConnection connection, SqliteTransaction transaction, string portfolioId,
        HashSet<string> intentIds)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT intent_id, attempt_id, portfolio_id, position_id, source_intent_id,
                   reconciliation_revision
            FROM protective_dispatch_claims
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
            ORDER BY intent_id ASC;
            """);
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        List<ExchangeTestPersistedProtectiveLink> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string intentId = reader.GetString(0);
            string sourceIntentId = reader.GetString(4);
            if (!intentIds.Contains(intentId) && !intentIds.Contains(sourceIntentId))
            {
                continue;
            }

            rows.Add(new ExchangeTestPersistedProtectiveLink(intentId, reader.GetString(1),
                reader.GetString(2), reader.GetString(3), sourceIntentId, reader.GetInt64(5)));
        }

        return rows;
    }

    private static T ReadCanonical<T>(string json, Func<T, bool> identityCheck,
        string message)
    {
        try
        {
            T value = CoreJsonSerializer.Deserialize<T>(json);
            if (!identityCheck(value)
                || !StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(value), json))
            {
                throw new InvalidDataException(message);
            }

            return value;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private static SqliteCommand Command(SqliteConnection connection,
        SqliteTransaction transaction, string text)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static string[] NormalizeIds(IEnumerable<string>? values) => (values ?? [])
        .Select(static value =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            return value;
        })
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static DateTimeOffset FromTimestamp(long ticks, int offsetMinutes) =>
        new(new DateTime(ticks, DateTimeKind.Unspecified), TimeSpan.FromMinutes(offsetMinutes));
}
