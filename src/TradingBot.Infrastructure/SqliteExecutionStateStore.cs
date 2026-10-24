using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Durable execution lifecycle operations composed with the existing
/// reservation and operational stores. Every claim and observation uses one
/// immediate SQLite transaction; the adapter is never called by this store.
/// </summary>
public sealed class SqlitePortfolioOperationalStore : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;
    private readonly SqlitePortfolioReservationStore _reservationStore;

    private SqlitePortfolioOperationalStore(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _reservationStore = SqlitePortfolioReservationStore.Open(databasePath,
            commandTimeoutSeconds);
    }

    public static SqlitePortfolioOperationalStore Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        SqliteDatabase.ValidateTimeout(commandTimeoutSeconds);
        string fullPath = SqliteDatabase.NormalizePath(databasePath);
        SqliteDatabase.Initialize(fullPath, commandTimeoutSeconds);
        return new SqlitePortfolioOperationalStore(fullPath, commandTimeoutSeconds);
    }

    public bool Register(PortfolioLimits limits) => _reservationStore.Register(limits);

    public PortfolioLimits? FindLimits(PortfolioId portfolioId) =>
        _reservationStore.FindLimits(portfolioId);

    public IReadOnlyList<PortfolioReservation> ReadReservations(PortfolioId portfolioId) =>
        _reservationStore.ReadReservations(portfolioId);

    public PortfolioReservation? FindReservation(string reservationId) =>
        _reservationStore.FindReservation(reservationId);

    public PortfolioReservationResult TryReserve(OrderIntent intent,
        PortfolioReservation reservation, RiskEvaluationResult riskEvaluation,
        long expectedReconciliationRevision, IRiskMathCalculator riskMathCalculator) =>
        _reservationStore.TryReserve(intent, reservation, riskEvaluation,
            expectedReconciliationRevision, riskMathCalculator);

    public PortfolioReservationResult TryReserve(RiskEvaluationResult riskEvaluation,
        OrderIntent intent, PortfolioReservation reservation, long expectedReconciliationRevision,
        IRiskMathCalculator riskMathCalculator) =>
        _reservationStore.TryReserve(riskEvaluation, intent, reservation,
            expectedReconciliationRevision, riskMathCalculator);

    public RiskEvaluationResult? FindRiskEvaluation(string evaluationId) =>
        _reservationStore.FindRiskEvaluation(evaluationId);

    public OrderIntent? FindIntent(string intentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(_databasePath,
            _commandTimeoutSeconds);
        return journal.Find(intentId);
    }

    public PortfolioObservationResult RecordObservation(Fill? fill,
        IReadOnlyList<PortfolioPositionSnapshot> positionSnapshots,
        ReconciliationCheckpoint checkpoint) =>
        _reservationStore.RecordObservation(fill, positionSnapshots, checkpoint);

    public ReconciliationCheckpoint ReadCheckpoint(PortfolioId portfolioId) =>
        _reservationStore.ReadCheckpoint(portfolioId);

    public Fill? FindFill(string fillId) => _reservationStore.FindFill(fillId);

    public IReadOnlyList<Fill> ReadFills(string intentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT fill_id, intent_id, fill_json
            FROM fills
            WHERE intent_id = $intent_id COLLATE BINARY
            ORDER BY fill_id ASC;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        List<Fill> fills = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string fillId = reader.GetString(0);
            string rowIntentId = reader.GetString(1);
            string json = reader.GetString(2);
            fills.Add(Deserialize<Fill>(json, value =>
                StringComparer.Ordinal.Equals(value.FillId, fillId)
                && StringComparer.Ordinal.Equals(value.IntentId, rowIntentId)
                && StringComparer.Ordinal.Equals(value.IntentId, intentId),
                "The persisted fill identity does not match its SQLite row."));
        }

        return fills.AsReadOnly();
    }

    public IReadOnlyList<PortfolioPositionSnapshot> ReadPositionSnapshots(PortfolioId portfolioId) =>
        _reservationStore.ReadPositionSnapshots(portfolioId);

    public ReconciliationCheckpoint AcknowledgeReconciliation(PortfolioId portfolioId,
        long revision) => _reservationStore.AcknowledgeReconciliation(portfolioId, revision);

    public bool CanRetry(PortfolioId portfolioId) => _reservationStore.CanRetry(portfolioId);

    public bool ReleaseAfterReconciliation(PortfolioId portfolioId, string intentId,
        long revision) => _reservationStore.ReleaseAfterReconciliation(portfolioId, intentId,
            revision);

    /// <summary>
    /// Claims dispatch for an intent using an explicit portfolio checkpoint.
    /// The overload without a portfolio is retained for callers whose database
    /// contains exactly one matching checkpoint.
    /// </summary>
    public ExecutionDispatchClaimResult TryClaimDispatch(PortfolioId portfolioId,
        OrderIntent intent, long expectedReconciliationRevision, UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        return TryClaimDispatchCore(portfolioId, intent, expectedReconciliationRevision,
            claimedAt);
    }

    public ExecutionDispatchClaimResult TryClaimDispatch(OrderIntent intent,
        long expectedReconciliationRevision, UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return TryClaimDispatchCore(null, intent, expectedReconciliationRevision, claimedAt);
    }

    private ExecutionDispatchClaimResult TryClaimDispatchCore(PortfolioId? portfolioId,
        OrderIntent intent, long expectedReconciliationRevision, UtcTimestamp claimedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(claimedAt);
        ValidateRevision(expectedReconciliationRevision);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        OrderIntent storedIntent = ReadIntent(connection, transaction, intent.IntentId);
        EnsureSameIntent(storedIntent, intent);
        ReconciliationCheckpoint checkpoint = ResolveCheckpoint(connection, transaction, intent,
            portfolioId, expectedReconciliationRevision);

        ExecutionStateSnapshot? existing = ReadState(connection, transaction, intent.IntentId);
        ExecutionStateSnapshot state = existing ?? ExecutionStateMachine.Start(intent,
            expectedReconciliationRevision);
        ValidateStateIntent(state, intent);
        ValidateStateRevision(state, expectedReconciliationRevision);

        if (state.State == ExecutionLifecycleState.DispatchClaimed)
        {
            transaction.Commit();
            return new ExecutionDispatchClaimResult(
                ExecutionDispatchClaimStatus.AlreadyClaimed, state, null);
        }

        if (state.RequiresReconciliation || !IsRetryable(checkpoint))
        {
            if (existing is null)
            {
                InsertState(connection, transaction, state);
            }

            transaction.Commit();
            return new ExecutionDispatchClaimResult(ExecutionDispatchClaimStatus.Blocked, state,
                null);
        }

        if (state.State != ExecutionLifecycleState.IntentPersisted)
        {
            transaction.Commit();
            return new ExecutionDispatchClaimResult(ExecutionDispatchClaimStatus.Blocked, state,
                null);
        }

        int attemptNumber = checked(state.DispatchAttempt + 1);
        ExecutionDispatchAttempt attempt = new(
            $"dispatch:{intent.IntentId}:{attemptNumber}", intent.IntentId, attemptNumber,
            expectedReconciliationRevision, claimedAt);
        ExecutionStateSnapshot claimed = ExecutionStateMachine.Claim(state, attemptNumber);
        if (existing is null)
        {
            InsertState(connection, transaction, claimed);
        }
        else
        {
            UpdateState(connection, transaction, claimed);
        }
        InsertAttempt(connection, transaction, attempt);
        transaction.Commit();
        return new ExecutionDispatchClaimResult(ExecutionDispatchClaimStatus.Claimed, claimed,
            attempt);
    }

    public ExecutionStateSnapshot? ReadExecutionState(string intentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        using SqliteConnection connection = OpenConnection();
        return ReadState(connection, transaction: null, intentId);
    }

    public IReadOnlyList<ExecutionDispatchAttempt> ReadExecutionAttempts(string intentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT attempt_id, intent_id, attempt, reconciliation_revision, attempt_json
            FROM execution_attempts
            WHERE intent_id = $intent_id COLLATE BINARY
            ORDER BY attempt ASC;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        List<ExecutionDispatchAttempt> attempts = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            attempts.Add(DeserializeAttempt(new AttemptRow(reader.GetString(0),
                reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
                reader.GetString(4)), intentId));
        }

        return attempts.AsReadOnly();
    }

    public ExecutionTransitionResult ApplyExecutionObservation(
        PortfolioId portfolioId, ExecutionObservation observation,
        long expectedReconciliationRevision)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        return ApplyExecutionObservationCore(portfolioId, observation,
            expectedReconciliationRevision);
    }

    public ExecutionTransitionResult ApplyExecutionObservation(
        ExecutionObservation observation, long expectedReconciliationRevision)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return ApplyExecutionObservationCore(null, observation, expectedReconciliationRevision);
    }

    /// <summary>
    /// Binds reconciliation-required state to the current explicitly
    /// acknowledged revision before applying fresh adapter observations.
    /// </summary>
    public ExecutionStateSnapshot BeginExecutionReconciliation(PortfolioId portfolioId,
        string intentId, long expectedReconciliationRevision)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ValidateRevision(expectedReconciliationRevision);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        OrderIntent intent = ReadIntent(connection, transaction, intentId);
        ReconciliationCheckpoint checkpoint = ResolveCheckpoint(connection, transaction, intent,
            portfolioId, expectedReconciliationRevision);
        if (!IsRetryable(checkpoint))
        {
            throw new InvalidOperationException(
                "Fresh execution reconciliation requires an acknowledged resolved checkpoint.");
        }

        ExecutionStateSnapshot state = ReadState(connection, transaction, intentId)
            ?? throw new InvalidOperationException(
                "Execution reconciliation requires a durable execution state.");
        ValidateStateIntent(state, intent);
        ExecutionStateSnapshot rebound = ExecutionStateMachine.BindReconciliationRevision(state,
            expectedReconciliationRevision);
        if (!ReferenceEquals(rebound, state))
        {
            UpdateState(connection, transaction, rebound);
        }

        transaction.Commit();
        return rebound;
    }

    /// <summary>
    /// Explicitly recovers an interrupted no-order execution into a persisted
    /// intent state. The caller must provide fresh complete evidence; this
    /// method never infers safety from a missing limited-history order.
    /// </summary>
    public ExecutionStateSnapshot RecoverExecutionForRetry(PortfolioId portfolioId,
        ExecutionRecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateRevision(evidence.ReconciliationRevision);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        OrderIntent intent = ReadIntent(connection, transaction, evidence.IntentId);
        ReconciliationCheckpoint checkpoint = ResolveCheckpoint(connection, transaction, intent,
            portfolioId, evidence.ReconciliationRevision);
        if (!IsRetryable(checkpoint))
        {
            throw new InvalidOperationException(
                "Execution retry requires an acknowledged resolved checkpoint.");
        }

        ExecutionStateSnapshot state = ReadState(connection, transaction, evidence.IntentId)
            ?? throw new InvalidOperationException(
                "Execution retry requires a durable execution state.");
        ValidateStateIntent(state, intent);
        ExecutionStateSnapshot recovered = ExecutionStateMachine.RecoverForRetry(state, evidence);
        UpdateState(connection, transaction, recovered);
        transaction.Commit();
        return recovered;
    }

    private ExecutionTransitionResult ApplyExecutionObservationCore(PortfolioId? portfolioId,
        ExecutionObservation observation, long expectedReconciliationRevision)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateRevision(expectedReconciliationRevision);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        OrderIntent intent = ReadIntent(connection, transaction, observation.IntentId);
        _ = ResolveCheckpoint(connection, transaction, intent, portfolioId,
            expectedReconciliationRevision);
        ExecutionStateSnapshot state = ReadState(connection, transaction, observation.IntentId)
            ?? throw new InvalidOperationException(
                "An execution observation requires a durable execution state.");
        ValidateStateIntent(state, intent);
        ValidateStateRevision(state, expectedReconciliationRevision);

        ExecutionTransitionResult transition = ExecutionStateMachine.Apply(state, observation);
        if (transition.Disposition == ExecutionApplyDisposition.AlreadyApplied
            || transition.Disposition == ExecutionApplyDisposition.Blocked)
        {
            transaction.Commit();
            return transition;
        }

        ExecutionObservation? persistedReceipt = ReadReceipt(connection, transaction,
            observation.IntentId, observation.ReceiptId);
        if (persistedReceipt is not null && !persistedReceipt.Equals(observation)
            && transition.Disposition != ExecutionApplyDisposition.Conflict)
        {
            throw new InvalidDataException(
                "The execution receipt identity already contains different evidence.");
        }

        UpdateState(connection, transaction, transition.State);
        if (persistedReceipt is null && transition.State.Receipts.Any(receipt =>
                StringComparer.Ordinal.Equals(receipt.ReceiptId, observation.ReceiptId)))
        {
            InsertReceipt(connection, transaction, observation);
        }

        transaction.Commit();
        return transition;
    }

    public IReadOnlyList<ExecutionObservation> ReadExecutionReceipts(string intentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT intent_id, receipt_id, receipt_json
            FROM execution_receipts
            WHERE intent_id = $intent_id COLLATE BINARY
            ORDER BY receipt_id ASC;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        List<ExecutionObservation> receipts = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            receipts.Add(DeserializeObservation(new ReceiptRow(reader.GetString(0),
                reader.GetString(1), reader.GetString(2)), intentId));
        }

        return receipts.AsReadOnly();
    }

    public long ReadSchemaVersionForTests()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose() => _reservationStore.Dispose();

    private SqliteConnection OpenConnection() =>
        SqliteDatabase.OpenConnection(_databasePath, _commandTimeoutSeconds);

    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

    private static void ValidateRevision(long revision)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision,
                "A reconciliation revision cannot be negative.");
        }
    }

    private static bool IsRetryable(ReconciliationCheckpoint checkpoint) =>
        checkpoint.Status == ReconciliationStatus.Acknowledged
        && checkpoint.UnknownMarkers.Count == 0
        && checkpoint.ManualPositionIds.Count == 0;

    private static ReconciliationCheckpoint ResolveCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, OrderIntent intent, PortfolioId? requestedPortfolio,
        long expectedRevision)
    {
        if (requestedPortfolio is not null)
        {
            ReconciliationCheckpoint? checkpoint = ReadCheckpoint(connection, transaction,
                requestedPortfolio.Value);
            return RequireExpectedCheckpoint(checkpoint, requestedPortfolio.Value, expectedRevision);
        }

        string? reservedPortfolio = FindReservationPortfolio(connection, transaction,
            intent.IntentId);
        if (reservedPortfolio is not null)
        {
            ReconciliationCheckpoint? checkpoint = ReadCheckpoint(connection, transaction,
                reservedPortfolio);
            return RequireExpectedCheckpoint(checkpoint, reservedPortfolio, expectedRevision);
        }

        List<ReconciliationCheckpoint> matches = ReadAcknowledgedCandidates(connection,
            transaction, expectedRevision, intent.IntentId);
        if (matches.Count != 1)
        {
            throw new InvalidOperationException(
                matches.Count == 0
                    ? "No unique acknowledged reconciliation checkpoint matches the intent."
                    : "The intent has multiple possible reconciliation checkpoints.");
        }

        return matches[0];
    }

    private static ReconciliationCheckpoint RequireExpectedCheckpoint(
        ReconciliationCheckpoint? checkpoint, string portfolioId, long expectedRevision)
    {
        if (checkpoint is null || checkpoint.Revision != expectedRevision)
        {
            throw new InvalidOperationException(
                "The expected reconciliation revision is stale or missing.");
        }

        if (!StringComparer.Ordinal.Equals(checkpoint.PortfolioId.Value, portfolioId))
        {
            throw new InvalidDataException(
                "The reconciliation checkpoint identity does not match its row.");
        }

        return checkpoint;
    }

    private static List<ReconciliationCheckpoint> ReadAcknowledgedCandidates(
        SqliteConnection connection, SqliteTransaction transaction, long expectedRevision,
        string intentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT portfolio_id, revision, checkpoint_json
            FROM reconciliation_checkpoints
            WHERE revision = $revision;
            """;
        command.Parameters.AddWithValue("$revision", expectedRevision);
        List<ReconciliationCheckpoint> all = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            ReconciliationCheckpoint checkpoint = DeserializeCheckpoint(reader.GetString(0),
                reader.GetInt64(1), reader.GetString(2));
            if (checkpoint.Status == ReconciliationStatus.Acknowledged)
            {
                all.Add(checkpoint);
            }
        }

        List<ReconciliationCheckpoint> linked = all.Where(checkpoint =>
            checkpoint.OpenIntentIds.Any(id => StringComparer.Ordinal.Equals(id, intentId)))
            .ToList();
        return linked.Count == 0 ? all : linked;
    }

    private static ReconciliationCheckpoint? ReadCheckpoint(SqliteConnection connection,
        SqliteTransaction transaction, string portfolioId)
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
            ? DeserializeCheckpoint(reader.GetString(0), reader.GetInt64(1), reader.GetString(2))
            : null;
    }

    private static string? FindReservationPortfolio(SqliteConnection connection,
        SqliteTransaction transaction, string intentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT portfolio_id
            FROM reservations
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? reader.GetString(0) : null;
    }

    private static OrderIntent ReadIntent(SqliteConnection connection, SqliteTransaction transaction,
        string intentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, client_order_id, intent_json
            FROM order_intents
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("The order intent was not durably persisted.");
        }

        string rowIntentId = reader.GetString(0);
        string clientOrderId = reader.GetString(1);
        string json = reader.GetString(2);
        return Deserialize<OrderIntent>(json,
            value => StringComparer.Ordinal.Equals(value.IntentId, rowIntentId)
                && StringComparer.Ordinal.Equals(value.ClientOrderId, clientOrderId),
            "The persisted order intent identity does not match its SQLite row.");
    }

    private static void EnsureSameIntent(OrderIntent stored, OrderIntent requested)
    {
        if (!stored.Equals(requested))
        {
            throw new InvalidOperationException(
                "The intent identifier already contains different order intent data.");
        }
    }

    private static ExecutionStateSnapshot? ReadState(SqliteConnection connection,
        SqliteTransaction? transaction, string intentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, client_order_id, state_json
            FROM execution_states
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string rowIntentId = reader.GetString(0);
        string clientOrderId = reader.GetString(1);
        return Deserialize<ExecutionStateSnapshot>(reader.GetString(2), value =>
            StringComparer.Ordinal.Equals(value.IntentId, rowIntentId)
            && StringComparer.Ordinal.Equals(value.ClientOrderId, clientOrderId),
            "The persisted execution state identity does not match its SQLite row.");
    }

    private static void ValidateStateIntent(ExecutionStateSnapshot state, OrderIntent intent)
    {
        if (!StringComparer.Ordinal.Equals(state.IntentId, intent.IntentId)
            || !StringComparer.Ordinal.Equals(state.ClientOrderId, intent.ClientOrderId)
            || state.IntentQuantity != intent.Quantity.Value)
        {
            throw new InvalidDataException(
                "The persisted execution state does not match its order intent.");
        }
    }

    private static void ValidateStateRevision(ExecutionStateSnapshot state, long revision)
    {
        if (state.ExpectedReconciliationRevision != revision)
        {
            throw new InvalidOperationException(
                "The execution state is bound to a different reconciliation revision.");
        }
    }

    private static void InsertState(SqliteConnection connection, SqliteTransaction transaction,
        ExecutionStateSnapshot state)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_states (intent_id, client_order_id, state_json)
            VALUES ($intent_id, $client_order_id, $state_json);
            """;
        command.Parameters.AddWithValue("$intent_id", state.IntentId);
        command.Parameters.AddWithValue("$client_order_id", state.ClientOrderId);
        command.Parameters.AddWithValue("$state_json", CoreJsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
    }

    private static void UpdateState(SqliteConnection connection, SqliteTransaction transaction,
        ExecutionStateSnapshot state)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE execution_states
            SET state_json = $state_json
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", state.IntentId);
        command.Parameters.AddWithValue("$state_json", CoreJsonSerializer.Serialize(state));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("The execution state disappeared during an update.");
        }
    }

    private static void InsertAttempt(SqliteConnection connection, SqliteTransaction transaction,
        ExecutionDispatchAttempt attempt)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_attempts
                (attempt_id, intent_id, attempt, reconciliation_revision, attempt_json)
            VALUES ($attempt_id, $intent_id, $attempt, $revision, $attempt_json);
            """;
        command.Parameters.AddWithValue("$attempt_id", attempt.AttemptId);
        command.Parameters.AddWithValue("$intent_id", attempt.IntentId);
        command.Parameters.AddWithValue("$attempt", attempt.Attempt);
        command.Parameters.AddWithValue("$revision", attempt.ReconciliationRevision);
        command.Parameters.AddWithValue("$attempt_json", CoreJsonSerializer.Serialize(attempt));
        command.ExecuteNonQuery();
    }

    private static ExecutionObservation? ReadReceipt(SqliteConnection connection,
        SqliteTransaction transaction, string intentId, string receiptId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, receipt_id, receipt_json
            FROM execution_receipts
            WHERE intent_id = $intent_id COLLATE BINARY
              AND receipt_id = $receipt_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        command.Parameters.AddWithValue("$receipt_id", receiptId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? DeserializeObservation(new ReceiptRow(reader.GetString(0), reader.GetString(1),
                reader.GetString(2)), intentId)
            : null;
    }

    private static void InsertReceipt(SqliteConnection connection, SqliteTransaction transaction,
        ExecutionObservation observation)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_receipts (intent_id, receipt_id, receipt_json)
            VALUES ($intent_id, $receipt_id, $receipt_json);
            """;
        command.Parameters.AddWithValue("$intent_id", observation.IntentId);
        command.Parameters.AddWithValue("$receipt_id", observation.ReceiptId);
        command.Parameters.AddWithValue("$receipt_json",
            CoreJsonSerializer.Serialize(observation));
        command.ExecuteNonQuery();
    }

    private static ExecutionDispatchAttempt DeserializeAttempt(AttemptRow row, string intentId) =>
        Deserialize<ExecutionDispatchAttempt>(row.AttemptJson, value =>
            StringComparer.Ordinal.Equals(value.AttemptId, row.AttemptId)
            && StringComparer.Ordinal.Equals(value.IntentId, row.IntentId)
            && StringComparer.Ordinal.Equals(value.IntentId, intentId)
            && value.Attempt == row.Attempt
            && value.ReconciliationRevision == row.ReconciliationRevision,
            "The persisted execution attempt identity does not match its SQLite row.");

    private static ExecutionObservation DeserializeObservation(ReceiptRow row, string intentId) =>
        Deserialize<ExecutionObservation>(row.ReceiptJson, value =>
            StringComparer.Ordinal.Equals(value.IntentId, row.IntentId)
            && StringComparer.Ordinal.Equals(value.IntentId, intentId)
            && StringComparer.Ordinal.Equals(value.ReceiptId, row.ReceiptId),
            "The persisted execution receipt identity does not match its SQLite row.");

    private static ReconciliationCheckpoint DeserializeCheckpoint(string portfolioId, long revision,
        string json) =>
        Deserialize<ReconciliationCheckpoint>(json, value =>
            StringComparer.Ordinal.Equals(value.PortfolioId.Value, portfolioId)
            && value.Revision == revision,
            "The persisted reconciliation checkpoint identity does not match its SQLite row.");

    private static T Deserialize<T>(string json, Func<T, bool> identity, string identityMessage)
    {
        try
        {
            T value = CoreJsonSerializer.Deserialize<T>(json);
            if (!identity(value))
            {
                throw new InvalidDataException(identityMessage);
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(value), json))
            {
                throw new InvalidDataException("The persisted JSON is not canonical.");
            }

            return value;
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
            throw new InvalidDataException("The persisted execution JSON is invalid.", exception);
        }
    }

    private sealed record AttemptRow(string AttemptId, string IntentId, int Attempt,
        long ReconciliationRevision, string AttemptJson);

    private sealed record ReceiptRow(string IntentId, string ReceiptId, string ReceiptJson);
}
