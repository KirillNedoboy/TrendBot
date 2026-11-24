using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

public sealed record BybitDemoReservation(string SignalId, string SignalRecordHash,
    string Symbol, string ClientOrderId, SetupType Family, string Timeframe,
    Direction Direction, decimal Quantity, decimal ReferenceQuote, decimal InitialStop);

public sealed record BybitDemoEntryFill(string SignalId, string ExecutionId, string ClientOrderId,
    string ExchangeOrderId, string Symbol, Direction Direction, decimal Quantity, decimal Price,
    decimal Fee, long Sequence, DateTimeOffset ExchangeAt, DateTimeOffset ReceivedAt);

public sealed record BybitDemoStopChange(string CommandId, string SignalId, decimal PositionQuantity,
    decimal ExpectedStop, decimal NewStop, DateTimeOffset ClaimedAt);

public sealed record BybitDemoStoredExitPlan(string SignalId, decimal PositionQuantity, decimal AverageEntry,
    decimal StopLoss, decimal FirstTarget, decimal FirstQuantity, decimal? SecondTarget,
    decimal SecondQuantity, decimal? StopAfterFirstTarget, decimal TickSize, decimal QuantityStep,
    decimal MinimumQuantity);

public sealed record BybitDemoStopProof(string Symbol, Direction PositionDirection, decimal PositionQuantity,
    decimal AverageEntry, decimal PositionStop, string StopOrderId, string ParentClientOrderId,
    Direction StopSide, decimal StopQuantity, decimal StopTrigger, int PositionIndex, string StopStatus,
    string TriggerBy, bool ReduceOnly, bool CloseOnTrigger, DateTimeOffset ReconciledAt, DateTimeOffset ObservedAt);

public enum BybitDemoStopClaimResult
{
    Claimed,
    Duplicate,
    Conflict,
    ReconciliationRequired
}

public enum BybitDemoFillResult
{
    Recorded,
    Duplicate,
    Conflict,
    ReconciliationRequired
}

public enum BybitDemoReservationResult
{
    Reserved,
    AlreadyReserved,
    Conflict,
    SymbolBusy,
    CapacityReached,
    ReconciliationRequired
}

public enum BybitDemoReservationState
{
    Reserved,
    DispatchClaimed,
    Acknowledged,
    Protected,
    ReconciliationRequired,
    Closed
}

public sealed record BybitDemoReservationSnapshot(BybitDemoReservation Reservation,
    BybitDemoReservationState State, DateTimeOffset? ClaimedAt, string? ExchangeOrderId,
    decimal? PositionQuantity, decimal? AverageEntry, decimal? StopLoss);

/// <summary>Standalone demo-only durable reservations and entry dispatch claims.</summary>
public sealed partial class SqliteBybitDemoReservationStore
{
    private const int ApplicationId = 1111770445;
    private const int SchemaVersion = 7;
    private readonly string _connectionString;
    private readonly int _capacity;

    private SqliteBybitDemoReservationStore(string path, int capacity)
    {
        _capacity = capacity;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
    }

    public static SqliteBybitDemoReservationStore Open(string path, int capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (StringComparer.Ordinal.Equals(path, ":memory:"))
        {
            throw new ArgumentException("The demo reservation store must be file-backed.", nameof(path));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 20);
        SqliteBybitDemoReservationStore store = new(Path.GetFullPath(path), capacity);
        store.Initialize();
        return store;
    }

    public int Capacity => _capacity;

    public BybitDemoReservationResult TryReserve(BybitDemoReservation reservation)
    {
        ValidateReservation(reservation);
        string json = CoreJsonSerializer.Serialize(reservation);
        string hash = Hash(json);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        BybitDemoReservationSnapshot? existing = ReadOne(connection, transaction, reservation.SignalId);
        if (existing is not null)
        {
            transaction.Commit();
            return existing.Reservation == reservation
                ? BybitDemoReservationResult.AlreadyReserved : BybitDemoReservationResult.Conflict;
        }
        if (HasUnresolvedDispatch(connection, transaction))
        {
            transaction.Commit();
            return BybitDemoReservationResult.ReconciliationRequired;
        }
        if (Exists(connection, transaction,
                "SELECT 1 FROM demo_orders WHERE symbol = $value AND state <> 5 LIMIT 1;", reservation.Symbol))
        {
            transaction.Commit();
            return BybitDemoReservationResult.SymbolBusy;
        }
        if (Exists(connection, transaction,
                "SELECT 1 FROM demo_orders WHERE client_order_id = $value LIMIT 1;", reservation.ClientOrderId))
        {
            transaction.Commit();
            return BybitDemoReservationResult.Conflict;
        }
        using (SqliteCommand count = Command(connection, transaction,
                   "SELECT COUNT(*) FROM demo_orders WHERE state <> 5;"))
        {
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= _capacity)
            {
                transaction.Commit();
                return BybitDemoReservationResult.CapacityReached;
            }
        }
        using (SqliteCommand insert = Command(connection, transaction, """
                   INSERT INTO demo_orders (signal_id, symbol, client_order_id, reservation_json, reservation_hash, state)
                   VALUES ($signal, $symbol, $client, $json, $hash, 0);
                   """))
        {
            insert.Parameters.AddWithValue("$signal", reservation.SignalId);
            insert.Parameters.AddWithValue("$symbol", reservation.Symbol);
            insert.Parameters.AddWithValue("$client", reservation.ClientOrderId);
            insert.Parameters.AddWithValue("$json", json);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return BybitDemoReservationResult.Reserved;
    }

    /// <summary>Durably claims one submission; an existing claim is never repeated.</summary>
    public bool TryClaimDispatch(string signalId, DateTimeOffset claimedAt)
    {
        ValidateHash(signalId, nameof(signalId));
        ValidateUtc(claimedAt);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        BybitDemoReservationSnapshot? current = ReadOne(connection, transaction, signalId);
        if (current is null || current.State != BybitDemoReservationState.Reserved
            || HasUnresolvedDispatch(connection, transaction))
        {
            transaction.Commit();
            return false;
        }
        using SqliteCommand command = Command(connection, transaction,
            "UPDATE demo_orders SET state = 1, claimed_at = $time WHERE signal_id = $signal AND state = 0;");
        command.Parameters.AddWithValue("$time", claimedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$signal", signalId);
        bool claimed = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return claimed;
    }

    public void RecordAcknowledgement(string signalId, string clientOrderId, string exchangeOrderId)
    {
        ValidateHash(signalId, nameof(signalId));
        RequireOpaqueId(clientOrderId, nameof(clientOrderId));
        RequireOpaqueId(exchangeOrderId, nameof(exchangeOrderId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        BybitDemoReservationSnapshot current = Required(connection, transaction, signalId);
        if (!StringComparer.Ordinal.Equals(current.Reservation.ClientOrderId, clientOrderId))
        {
            throw new InvalidOperationException("The acknowledgement does not match its durable claim.");
        }
        if (current.State is BybitDemoReservationState.Acknowledged or BybitDemoReservationState.Protected
                or BybitDemoReservationState.ReconciliationRequired
            && StringComparer.Ordinal.Equals(current.ExchangeOrderId, exchangeOrderId))
        {
            transaction.Commit();
            return;
        }
        if (current.State != BybitDemoReservationState.DispatchClaimed
            && !(current.State == BybitDemoReservationState.ReconciliationRequired && current.ExchangeOrderId is null))
        {
            throw new InvalidOperationException("The demo order cannot accept this acknowledgement.");
        }
        if (ReadFills(connection, transaction, signalId).Any(fill => fill.ExchangeOrderId != exchangeOrderId))
        {
            throw new InvalidOperationException("The acknowledgement conflicts with a recorded execution.");
        }
        using SqliteCommand command = Command(connection, transaction,
            "UPDATE demo_orders SET state = CASE WHEN state = 1 THEN 2 ELSE state END, exchange_order_id = $order WHERE signal_id = $signal;");
        command.Parameters.AddWithValue("$order", exchangeOrderId);
        command.Parameters.AddWithValue("$signal", signalId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Persists entry executions by exchange execution ID before protection is confirmed.</summary>
    public BybitDemoFillResult RecordEntryFill(BybitDemoEntryFill fill)
    {
        ValidateFill(fill);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        BybitDemoReservationSnapshot current = Required(connection, transaction, fill.SignalId);
        if (current.State is BybitDemoReservationState.Reserved or BybitDemoReservationState.Closed
            || current.ClaimedAt is null || fill.ExchangeAt < current.ClaimedAt
            || current.Reservation.Symbol != fill.Symbol || current.Reservation.Direction != fill.Direction
            || current.Reservation.ClientOrderId != fill.ClientOrderId
            || (current.ExchangeOrderId is not null && current.ExchangeOrderId != fill.ExchangeOrderId))
        {
            throw new InvalidOperationException("The entry execution does not match its durable claim.");
        }
        using (SqliteCommand find = Command(connection, transaction,
                   "SELECT signal_id, fill_json, fill_hash FROM demo_entry_fills WHERE execution_id = $execution;"))
        {
            find.Parameters.AddWithValue("$execution", fill.ExecutionId);
            using SqliteDataReader reader = find.ExecuteReader();
            if (reader.Read())
            {
                string otherSignal = reader.GetString(0);
                BybitDemoEntryFill previous = ReadFill(reader.GetString(1), reader.GetString(2));
                if (previous.SignalId != otherSignal || previous.ExecutionId != fill.ExecutionId)
                {
                    throw new InvalidDataException("The demo execution row is inconsistent.");
                }
                bool duplicate = previous with { ReceivedAt = fill.ReceivedAt } == fill;
                reader.Close();
                if (!duplicate)
                {
                    MarkReconciliation(connection, transaction, fill.SignalId);
                    MarkReconciliation(connection, transaction, otherSignal);
                }
                transaction.Commit();
                return duplicate ? BybitDemoFillResult.Duplicate : BybitDemoFillResult.Conflict;
            }
        }
        string json = CoreJsonSerializer.Serialize(fill);
        using (SqliteCommand insert = Command(connection, transaction, """
                   INSERT INTO demo_entry_fills (execution_id, signal_id, fill_json, fill_hash)
                   VALUES ($execution, $signal, $json, $hash);
                   """))
        {
            insert.Parameters.AddWithValue("$execution", fill.ExecutionId);
            insert.Parameters.AddWithValue("$signal", fill.SignalId);
            insert.Parameters.AddWithValue("$json", json);
            insert.Parameters.AddWithValue("$hash", Hash(json));
            insert.ExecuteNonQuery();
        }
        decimal total = ReadFills(connection, transaction, fill.SignalId).Sum(static item => item.Quantity);
        bool uncertain = current.State != BybitDemoReservationState.Acknowledged
            || total > current.Reservation.Quantity;
        if (uncertain)
        {
            MarkReconciliation(connection, transaction, fill.SignalId);
        }
        transaction.Commit();
        return uncertain ? BybitDemoFillResult.ReconciliationRequired : BybitDemoFillResult.Recorded;
    }

    public IReadOnlyList<BybitDemoEntryFill> ReadEntryFills(string signalId)
    {
        ValidateHash(signalId, nameof(signalId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        _ = Required(connection, transaction, signalId);
        List<BybitDemoEntryFill> fills = ReadFills(connection, transaction, signalId);
        transaction.Commit();
        return fills.AsReadOnly();
    }

    /// <summary>Records externally verified position and protective-stop observations.</summary>
    public void ConfirmProtected(string signalId, string exchangeOrderId, decimal positionQuantity,
        decimal averageEntry, decimal stopLoss, DateTimeOffset observedAt, bool entryOrderTerminal,
        DateTimeOffset? accountReconciledAt = null)
    {
        ValidateHash(signalId, nameof(signalId));
        RequireOpaqueId(exchangeOrderId, nameof(exchangeOrderId));
        ValidateUtc(observedAt);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        if (Exists(connection, transaction,
                "SELECT 1 FROM demo_stop_changes WHERE signal_id = $value AND state <> 2 LIMIT 1;", signalId))
        {
            throw new InvalidOperationException("The pending stop change must be reconciled first.");
        }
        BybitDemoReservationSnapshot current = Required(connection, transaction, signalId);
        if (!entryOrderTerminal || positionQuantity <= 0m
            || positionQuantity > current.Reservation.Quantity || averageEntry <= 0m
            || stopLoss <= 0m || (current.Reservation.Direction == Direction.Long ? stopLoss >= averageEntry : stopLoss <= averageEntry)
            || current.ClaimedAt is null || observedAt < current.ClaimedAt)
        {
            throw new ArgumentException("The protective-stop observation is invalid.");
        }
        if (current.State != BybitDemoReservationState.Acknowledged
                && current.State != BybitDemoReservationState.ReconciliationRequired
            || !StringComparer.Ordinal.Equals(current.ExchangeOrderId, exchangeOrderId))
        {
            throw new InvalidOperationException("The position cannot be confirmed against this order.");
        }
        List<BybitDemoEntryFill> fills = ReadFills(connection, transaction, signalId);
        if (fills.Sum(static item => item.Quantity) != positionQuantity
            || fills.Any(item => item.ReceivedAt > observedAt))
        {
            throw new InvalidOperationException("The protected position does not match durable entry executions.");
        }
        if (current.State == BybitDemoReservationState.ReconciliationRequired
            && (accountReconciledAt is null || accountReconciledAt.Value.Offset != TimeSpan.Zero
                || accountReconciledAt < current.ClaimedAt || accountReconciledAt > observedAt
                || fills.Any(item => item.ReceivedAt > accountReconciledAt)
                || Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_unmatched_activity;") != 0
                || Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_stream_faults;") != 0))
        {
            throw new InvalidOperationException("Time-ordered account reconciliation is required before confirming protection.");
        }
        using SqliteCommand command = Command(connection, transaction, """
            UPDATE demo_orders SET state = 3, position_quantity = $quantity,
                average_entry = $entry, stop_loss = $stop WHERE signal_id = $signal;
            """);
        command.Parameters.AddWithValue("$quantity", Number(positionQuantity));
        command.Parameters.AddWithValue("$entry", Number(averageEntry));
        command.Parameters.AddWithValue("$stop", Number(stopLoss));
        command.Parameters.AddWithValue("$signal", signalId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Persists an immutable exit plan only for a confirmed protected position.</summary>
    public bool RecordExitPlan(BybitDemoStoredExitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateHash(plan.SignalId, nameof(plan.SignalId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        BybitDemoReservationSnapshot current = Required(connection, transaction, plan.SignalId);
        ValidateExitPlan(plan, current.Reservation);
        BybitDemoStoredExitPlan? previous = ReadExitPlan(connection, transaction, current.Reservation);
        if (previous is not null)
        {
            if (previous != plan) throw new InvalidOperationException("The demo exit plan conflicts with the stored plan.");
            transaction.Commit();
            return false;
        }
        if (current.State != BybitDemoReservationState.Protected || current.PositionQuantity != plan.PositionQuantity
            || current.AverageEntry != plan.AverageEntry || current.StopLoss != plan.StopLoss)
        {
            throw new InvalidOperationException("The demo exit plan does not match the protected position.");
        }
        string json = CoreJsonSerializer.Serialize(plan);
        using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO demo_exit_plans(signal_id, plan_json, plan_hash) VALUES ($signal, $json, $hash);
            """);
        insert.Parameters.AddWithValue("$signal", plan.SignalId);
        insert.Parameters.AddWithValue("$json", json);
        insert.Parameters.AddWithValue("$hash", Hash(json));
        insert.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public BybitDemoStoredExitPlan? ReadExitPlan(string signalId)
    {
        ValidateHash(signalId, nameof(signalId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        ValidateActive(connection, transaction);
        BybitDemoReservationSnapshot current = Required(connection, transaction, signalId);
        BybitDemoStoredExitPlan? plan = ReadExitPlan(connection, transaction, current.Reservation);
        transaction.Commit();
        return plan;
    }

    /// <summary>Claims one stop change before dispatch; unresolved claims block new entries.</summary>
    public BybitDemoStopClaimResult TryClaimStopChange(BybitDemoStopChange change)
    {
        ValidateStopChange(change);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        (BybitDemoStopChange Change, int State)? existing = ReadStopChange(connection, transaction, change.CommandId);
        if (existing is { } prior)
        {
            transaction.Commit();
            return prior.Change with { ClaimedAt = change.ClaimedAt } == change
                ? BybitDemoStopClaimResult.Duplicate : BybitDemoStopClaimResult.Conflict;
        }
        BybitDemoReservationSnapshot current = Required(connection, transaction, change.SignalId);
        if (current.State != BybitDemoReservationState.Protected || HasUnresolvedDispatch(connection, transaction)
            || current.PositionQuantity != change.PositionQuantity || current.StopLoss != change.ExpectedStop
            || current.AverageEntry is not { } entry || current.ClaimedAt is null || change.ClaimedAt < current.ClaimedAt
            || !StopChangeAllowed(connection, transaction, current, change.NewStop, entry))
        {
            transaction.Commit();
            return BybitDemoStopClaimResult.ReconciliationRequired;
        }
        string json = CoreJsonSerializer.Serialize(change);
        using (SqliteCommand insert = Command(connection, transaction, """
                   INSERT INTO demo_stop_changes(command_id, signal_id, change_json, change_hash, state)
                   VALUES ($command, $signal, $json, $hash, 0);
                   """))
        {
            insert.Parameters.AddWithValue("$command", change.CommandId);
            insert.Parameters.AddWithValue("$signal", change.SignalId);
            insert.Parameters.AddWithValue("$json", json);
            insert.Parameters.AddWithValue("$hash", Hash(json));
            insert.ExecuteNonQuery();
        }
        MarkReconciliation(connection, transaction, change.SignalId);
        transaction.Commit();
        return BybitDemoStopClaimResult.Claimed;
    }

    /// <summary>Records only API acceptance; protection is not yet confirmed.</summary>
    public void RecordStopAcknowledgement(string commandId)
    {
        RequireOpaqueId(commandId, nameof(commandId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        (BybitDemoStopChange Change, int State) current = ReadStopChange(connection, transaction, commandId)
            ?? throw new InvalidOperationException("The demo stop claim does not exist.");
        if (current.State == 0)
        {
            using SqliteCommand update = Command(connection, transaction,
                "UPDATE demo_stop_changes SET state = 1 WHERE command_id = $command AND state = 0;");
            update.Parameters.AddWithValue("$command", commandId);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Confirms a claimed stop only from matching time-ordered account and order observations.</summary>
    public void ConfirmStopChange(string commandId, BybitDemoStopProof proof)
    {
        RequireOpaqueId(commandId, nameof(commandId));
        ArgumentNullException.ThrowIfNull(proof);
        RequireOpaqueId(proof.StopOrderId, nameof(proof.StopOrderId));
        RequireOpaqueId(proof.ParentClientOrderId, nameof(proof.ParentClientOrderId));
        ValidateUtc(proof.ReconciledAt);
        ValidateUtc(proof.ObservedAt);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        (BybitDemoStopChange Change, int State) saved = ReadStopChange(connection, transaction, commandId)
            ?? throw new InvalidOperationException("The demo stop claim does not exist.");
        BybitDemoStopChange change = saved.Change;
        BybitDemoReservationSnapshot current = Required(connection, transaction, change.SignalId);
        Direction expectedSide = current.Reservation.Direction == Direction.Long ? Direction.Short : Direction.Long;
        if (proof.Symbol != current.Reservation.Symbol || proof.PositionDirection != current.Reservation.Direction
            || proof.PositionQuantity != change.PositionQuantity || proof.AverageEntry != current.AverageEntry
            || proof.PositionStop != change.NewStop || proof.StopTrigger != change.NewStop
            || proof.StopQuantity != change.PositionQuantity || proof.StopSide != expectedSide || proof.PositionIndex != 0
            || proof.ParentClientOrderId != current.Reservation.ClientOrderId || proof.StopOrderId == current.ExchangeOrderId
            || proof.StopStatus != "Untriggered" || proof.TriggerBy != "LastPrice" || !proof.ReduceOnly || !proof.CloseOnTrigger
            || proof.ReconciledAt < change.ClaimedAt || proof.ObservedAt < proof.ReconciledAt)
        {
            throw new ArgumentException("The demo stop observation does not match its claim.", nameof(proof));
        }
        if (saved.State == 2)
        {
            transaction.Commit();
            return;
        }
        if (current.State != BybitDemoReservationState.ReconciliationRequired
            || current.PositionQuantity != change.PositionQuantity || current.StopLoss != change.ExpectedStop
            || Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_unmatched_activity;") != 0
            || Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_stream_faults;") != 0)
        {
            throw new InvalidOperationException("The demo stop change cannot be confirmed.");
        }
        using (SqliteCommand update = Command(connection, transaction, """
                   UPDATE demo_orders SET state = 3, stop_loss = $stop WHERE signal_id = $signal;
                   UPDATE demo_stop_changes SET state = 2 WHERE command_id = $command;
                   """))
        {
            update.Parameters.AddWithValue("$stop", Number(change.NewStop));
            update.Parameters.AddWithValue("$signal", change.SignalId);
            update.Parameters.AddWithValue("$command", change.CommandId);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<BybitDemoStopChange> ReadPendingStopChanges()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        ValidateActive(connection, transaction);
        using SqliteCommand command = Command(connection, transaction,
            "SELECT command_id FROM demo_stop_changes WHERE state <> 2 ORDER BY command_id;");
        List<string> identities = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read()) identities.Add(reader.GetString(0));
        }
        List<BybitDemoStopChange> changes = [];
        foreach (string id in identities)
        {
            changes.Add((ReadStopChange(connection, transaction, id)
                ?? throw new InvalidDataException("The pending stop claim is missing.")).Change);
        }
        transaction.Commit();
        return changes.AsReadOnly();
    }

    public void RequireReconciliation(string signalId)
    {
        ValidateHash(signalId, nameof(signalId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        BybitDemoReservationSnapshot current = Required(connection, transaction, signalId);
        if (current.State is BybitDemoReservationState.Reserved or BybitDemoReservationState.Closed)
        {
            throw new InvalidOperationException("This demo reservation has no dispatched order to reconcile.");
        }
        using SqliteCommand command = Command(connection, transaction,
            "UPDATE demo_orders SET state = 4 WHERE signal_id = $signal;");
        command.Parameters.AddWithValue("$signal", signalId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>Durably blocks new dispatches when external account activity is unrecognized.</summary>
    public bool RecordUnmatchedActivity(string eventId, string symbol, string kind, DateTimeOffset observedAt)
    {
        RequireOpaqueId(eventId, nameof(eventId));
        ValidateUtc(observedAt);
        if (string.IsNullOrEmpty(symbol) || symbol.Length > 20
            || symbol.Any(static c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            || kind is not ("order" or "execution" or "position"))
        {
            throw new ArgumentException("The unmatched demo activity is invalid.");
        }
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        using SqliteCommand command = Command(connection, transaction, """
            INSERT OR IGNORE INTO demo_unmatched_activity(event_id, symbol, kind, observed_at)
            VALUES ($event, $symbol, $kind, $time);
            """);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$symbol", symbol);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$time", observedAt.ToString("O", CultureInfo.InvariantCulture));
        bool inserted = command.ExecuteNonQuery() == 1;
        if (!inserted)
        {
            using SqliteCommand existing = Command(connection, transaction,
                "SELECT symbol FROM demo_unmatched_activity WHERE event_id = $event AND kind = $kind;");
            existing.Parameters.AddWithValue("$event", eventId);
            existing.Parameters.AddWithValue("$kind", kind);
            if (!StringComparer.Ordinal.Equals(existing.ExecuteScalar() as string, symbol))
            {
                throw new InvalidDataException("The unmatched demo activity identity conflicts.");
            }
        }
        transaction.Commit();
        return inserted;
    }

    /// <summary>Durably blocks dispatch after a private-stream protocol fault.</summary>
    public bool RecordStreamFault(string payloadHash, DateTimeOffset observedAt)
    {
        ValidateHash(payloadHash, nameof(payloadHash));
        ValidateUtc(observedAt);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        using SqliteCommand command = Command(connection, transaction, """
            INSERT OR IGNORE INTO demo_stream_faults(payload_hash, observed_at) VALUES ($hash, $time);
            """);
        command.Parameters.AddWithValue("$hash", payloadHash);
        command.Parameters.AddWithValue("$time", observedAt.ToString("O", CultureInfo.InvariantCulture));
        bool inserted = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return inserted;
    }

    public IReadOnlyList<BybitDemoReservationSnapshot> ReadActive()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        ValidateActive(connection, transaction);
        using SqliteCommand command = Command(connection, transaction,
            "SELECT signal_id, symbol, client_order_id, reservation_json, reservation_hash, state, claimed_at, exchange_order_id, position_quantity, average_entry, stop_loss FROM demo_orders WHERE state <> 5 ORDER BY signal_id;");
        using SqliteDataReader reader = command.ExecuteReader();
        List<BybitDemoReservationSnapshot> entries = [];
        while (reader.Read())
        {
            entries.Add(ReadSnapshot(reader));
        }
        return entries.AsReadOnly();
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        long applicationId = Scalar(connection, transaction, "PRAGMA application_id;");
        long version = Scalar(connection, transaction, "PRAGMA user_version;");
        if (applicationId == 0 && version == 0)
        {
            if (Scalar(connection, transaction,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view','trigger') AND name NOT LIKE 'sqlite_%';") != 0)
            {
                throw new InvalidDataException("The database is not an empty Bybit demo store.");
            }
            using SqliteCommand create = Command(connection, transaction, """
                CREATE TABLE demo_meta (id INTEGER PRIMARY KEY CHECK (id = 1), capacity INTEGER NOT NULL CHECK (capacity BETWEEN 1 AND 20));
                CREATE TABLE demo_orders (
                    signal_id TEXT PRIMARY KEY COLLATE BINARY,
                    symbol TEXT NOT NULL COLLATE BINARY,
                    client_order_id TEXT NOT NULL UNIQUE COLLATE BINARY,
                    reservation_json TEXT NOT NULL,
                    reservation_hash TEXT NOT NULL,
                    state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 5),
                    claimed_at TEXT,
                    exchange_order_id TEXT,
                    position_quantity TEXT,
                    average_entry TEXT,
                    stop_loss TEXT
                );
                CREATE UNIQUE INDEX demo_one_open_symbol ON demo_orders(symbol) WHERE state <> 5;
                PRAGMA application_id = 1111770445;
                PRAGMA user_version = 6;
                """);
            create.ExecuteNonQuery();
            CreateFillSchema(connection, transaction);
            CreateUnmatchedActivitySchema(connection, transaction);
            CreateStreamFaultSchema(connection, transaction);
            CreateStopChangeSchema(connection, transaction);
            CreateExitPlanSchema(connection, transaction);
            using SqliteCommand insert = Command(connection, transaction,
                "INSERT INTO demo_meta(id, capacity) VALUES (1, $capacity);");
            insert.Parameters.AddWithValue("$capacity", _capacity);
            insert.ExecuteNonQuery();
        }
        else if (applicationId == ApplicationId && version == 1)
        {
            CreateFillSchema(connection, transaction);
            CreateUnmatchedActivitySchema(connection, transaction);
            CreateStreamFaultSchema(connection, transaction);
            CreateStopChangeSchema(connection, transaction);
            CreateExitPlanSchema(connection, transaction);
            using SqliteCommand migrate = Command(connection, transaction, """
                UPDATE demo_orders SET state = 4 WHERE state = 3;
                PRAGMA user_version = 6;
                """);
            migrate.ExecuteNonQuery();
        }
        else if (applicationId == ApplicationId && version == 2)
        {
            CreateUnmatchedActivitySchema(connection, transaction);
            CreateStreamFaultSchema(connection, transaction);
            CreateStopChangeSchema(connection, transaction);
            CreateExitPlanSchema(connection, transaction);
            using SqliteCommand migrate = Command(connection, transaction, "PRAGMA user_version = 6;");
            migrate.ExecuteNonQuery();
        }
        else if (applicationId == ApplicationId && version == 3)
        {
            CreateStreamFaultSchema(connection, transaction);
            CreateStopChangeSchema(connection, transaction);
            CreateExitPlanSchema(connection, transaction);
            using SqliteCommand migrate = Command(connection, transaction, "PRAGMA user_version = 6;");
            migrate.ExecuteNonQuery();
        }
        else if (applicationId == ApplicationId && version == 4)
        {
            CreateStopChangeSchema(connection, transaction);
            CreateExitPlanSchema(connection, transaction);
            using SqliteCommand migrate = Command(connection, transaction, "PRAGMA user_version = 6;");
            migrate.ExecuteNonQuery();
        }
        else if (applicationId == ApplicationId && version == 5)
        {
            CreateExitPlanSchema(connection, transaction);
            using SqliteCommand migrate = Command(connection, transaction, "PRAGMA user_version = 6;");
            migrate.ExecuteNonQuery();
        }
        else if (applicationId == ApplicationId && version == 6)
        {
            // Runtime tables are added transactionally below.
        }
        else if (applicationId != ApplicationId || version != SchemaVersion)
        {
            throw new InvalidDataException("The database is not a supported Bybit demo store.");
        }
        using (SqliteCommand capacity = Command(connection, transaction,
                   "SELECT capacity FROM demo_meta WHERE id = 1;"))
        {
            if (Convert.ToInt32(capacity.ExecuteScalar(), CultureInfo.InvariantCulture) != _capacity)
            {
                throw new InvalidDataException("The configured demo capacity does not match its durable store.");
            }
        }
        if (version < 7) CreateRuntimeSchema(connection, transaction);
        else if (Scalar(connection, transaction,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('demo_runtime_sources','demo_exit_orders','demo_exit_fills');") != 3)
            throw new InvalidDataException("The demo runtime schema is incomplete; it cannot be recreated.");
        using (SqliteCommand versionUpdate = Command(connection, transaction, "PRAGMA user_version = 7;"))
        {
            versionUpdate.ExecuteNonQuery();
        }
        ValidateActive(connection, transaction);
        transaction.Commit();
    }

    private static void CreateExitPlanSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE demo_exit_plans (
                signal_id TEXT PRIMARY KEY COLLATE BINARY,
                plan_json TEXT NOT NULL,
                plan_hash TEXT NOT NULL,
                FOREIGN KEY(signal_id) REFERENCES demo_orders(signal_id)
            );
            """);
        command.ExecuteNonQuery();
    }

    private static void CreateStopChangeSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE demo_stop_changes (
                command_id TEXT PRIMARY KEY COLLATE BINARY,
                signal_id TEXT NOT NULL COLLATE BINARY,
                change_json TEXT NOT NULL,
                change_hash TEXT NOT NULL,
                state INTEGER NOT NULL CHECK (state BETWEEN 0 AND 2),
                FOREIGN KEY(signal_id) REFERENCES demo_orders(signal_id)
            );
            CREATE UNIQUE INDEX demo_one_pending_stop ON demo_stop_changes(signal_id) WHERE state <> 2;
            """);
        command.ExecuteNonQuery();
    }

    private static void CreateStreamFaultSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE demo_stream_faults (
                payload_hash TEXT PRIMARY KEY COLLATE BINARY,
                observed_at TEXT NOT NULL
            );
            """);
        command.ExecuteNonQuery();
    }

    private static void CreateUnmatchedActivitySchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE demo_unmatched_activity (
                event_id TEXT NOT NULL COLLATE BINARY,
                symbol TEXT NOT NULL COLLATE BINARY,
                kind TEXT NOT NULL CHECK (kind IN ('order', 'execution', 'position')),
                observed_at TEXT NOT NULL,
                PRIMARY KEY(kind, event_id)
            );
            """);
        command.ExecuteNonQuery();
    }

    private static void CreateFillSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE demo_entry_fills (
                execution_id TEXT PRIMARY KEY COLLATE BINARY,
                signal_id TEXT NOT NULL COLLATE BINARY,
                fill_json TEXT NOT NULL,
                fill_hash TEXT NOT NULL,
                FOREIGN KEY(signal_id) REFERENCES demo_orders(signal_id)
            );
            CREATE INDEX demo_entry_fills_signal ON demo_entry_fills(signal_id);
            """);
        command.ExecuteNonQuery();
    }

    private static BybitDemoStoredExitPlan? ReadExitPlan(SqliteConnection connection, SqliteTransaction transaction,
        BybitDemoReservation reservation)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT plan_json, plan_hash FROM demo_exit_plans WHERE signal_id = $signal;");
        command.Parameters.AddWithValue("$signal", reservation.SignalId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string json = reader.GetString(0);
        if (Hash(json) != reader.GetString(1)) throw new InvalidDataException("The demo exit plan hash is invalid.");
        BybitDemoStoredExitPlan plan = CoreJsonSerializer.Deserialize<BybitDemoStoredExitPlan>(json);
        ValidateExitPlan(plan, reservation);
        if (CoreJsonSerializer.Serialize(plan) != json) throw new InvalidDataException("The demo exit plan is not canonical.");
        return plan;
    }

    private static void ValidateExitPlan(BybitDemoStoredExitPlan plan, BybitDemoReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        bool flow = reservation.Family is SetupType.FlowAtrRecovery or SetupType.FlowStochasticRecovery;
        if (plan.SignalId != reservation.SignalId || plan.PositionQuantity <= 0m
            || plan.PositionQuantity > reservation.Quantity || plan.AverageEntry <= 0m
            || plan.TickSize <= 0m || plan.QuantityStep <= 0m || plan.MinimumQuantity <= 0m
            || plan.StopLoss <= 0m || plan.FirstTarget <= 0m || plan.FirstQuantity < plan.MinimumQuantity
            || plan.SecondQuantity < 0m || plan.FirstQuantity + plan.SecondQuantity != plan.PositionQuantity
            || plan.PositionQuantity % plan.QuantityStep != 0m || plan.FirstQuantity % plan.QuantityStep != 0m
            || plan.SecondQuantity % plan.QuantityStep != 0m || plan.StopLoss % plan.TickSize != 0m
            || plan.FirstTarget % plan.TickSize != 0m
            || (reservation.Direction == Direction.Long
                ? plan.StopLoss >= plan.AverageEntry || plan.FirstTarget <= plan.AverageEntry
                : plan.StopLoss <= plan.AverageEntry || plan.FirstTarget >= plan.AverageEntry))
        {
            throw new ArgumentException("The demo exit plan is invalid.", nameof(plan));
        }
        if (flow)
        {
            decimal half = decimal.Floor(plan.PositionQuantity / plan.QuantityStep / 2m) * plan.QuantityStep;
            decimal breakEven = (reservation.Direction == Direction.Long
                ? decimal.Ceiling(plan.AverageEntry / plan.TickSize)
                : decimal.Floor(plan.AverageEntry / plan.TickSize)) * plan.TickSize;
            if (plan.FirstQuantity != half || plan.SecondQuantity < plan.MinimumQuantity
                || plan.SecondTarget is not { } second || second <= 0m || second % plan.TickSize != 0m
                || (reservation.Direction == Direction.Long ? second <= plan.FirstTarget : second >= plan.FirstTarget)
                || plan.StopAfterFirstTarget != breakEven || breakEven <= 0m)
            {
                throw new ArgumentException("The demo Flow exit plan is invalid.", nameof(plan));
            }
        }
        else if (plan.SecondTarget is not null || plan.SecondQuantity != 0m || plan.StopAfterFirstTarget is not null)
        {
            throw new ArgumentException("The demo Donchian exit plan is invalid.", nameof(plan));
        }
    }

    private static (BybitDemoStopChange Change, int State)? ReadStopChange(SqliteConnection connection,
        SqliteTransaction transaction, string commandId)
    {
        using SqliteCommand command = Command(connection, transaction, """
            SELECT signal_id, change_json, change_hash, state FROM demo_stop_changes WHERE command_id = $command;
            """);
        command.Parameters.AddWithValue("$command", commandId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string signalId = reader.GetString(0);
        string json = reader.GetString(1);
        string hash = reader.GetString(2);
        int state = reader.GetInt32(3);
        if (Hash(json) != hash || state is < 0 or > 2)
        {
            throw new InvalidDataException("The demo stop claim is invalid.");
        }
        BybitDemoStopChange change = CoreJsonSerializer.Deserialize<BybitDemoStopChange>(json);
        ValidateStopChange(change);
        if (change.CommandId != commandId || change.SignalId != signalId || CoreJsonSerializer.Serialize(change) != json)
        {
            throw new InvalidDataException("The demo stop claim is inconsistent.");
        }
        return (change, state);
    }

    private static void ValidateStopChange(BybitDemoStopChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        RequireOpaqueId(change.CommandId, nameof(change.CommandId));
        ValidateHash(change.SignalId, nameof(change.SignalId));
        ValidateUtc(change.ClaimedAt);
        if (change.PositionQuantity <= 0m || change.ExpectedStop <= 0m || change.NewStop <= 0m
            || change.NewStop == change.ExpectedStop)
        {
            throw new ArgumentException("The demo stop change is invalid.", nameof(change));
        }
    }

    private static void MarkReconciliation(SqliteConnection connection, SqliteTransaction transaction, string signalId)
    {
        using SqliteCommand command = Command(connection, transaction,
            "UPDATE demo_orders SET state = 4 WHERE signal_id = $signal AND state IN (1, 2, 3, 4);");
        command.Parameters.AddWithValue("$signal", signalId);
        command.ExecuteNonQuery();
    }

    private static List<BybitDemoEntryFill> ReadFills(SqliteConnection connection, SqliteTransaction transaction, string signalId)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT execution_id, fill_json, fill_hash FROM demo_entry_fills WHERE signal_id = $signal ORDER BY execution_id;");
        command.Parameters.AddWithValue("$signal", signalId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<BybitDemoEntryFill> fills = [];
        while (reader.Read())
        {
            BybitDemoEntryFill fill = ReadFill(reader.GetString(1), reader.GetString(2));
            if (fill.SignalId != signalId || fill.ExecutionId != reader.GetString(0))
            {
                throw new InvalidDataException("The demo execution row is inconsistent.");
            }
            fills.Add(fill);
        }
        return fills;
    }

    private static BybitDemoEntryFill ReadFill(string json, string hash)
    {
        if (!StringComparer.Ordinal.Equals(Hash(json), hash))
        {
            throw new InvalidDataException("The demo execution hash is invalid.");
        }
        BybitDemoEntryFill fill = CoreJsonSerializer.Deserialize<BybitDemoEntryFill>(json);
        ValidateFill(fill);
        if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(fill), json))
        {
            throw new InvalidDataException("The demo execution is not canonical.");
        }
        return fill;
    }

    private static void ValidateFill(BybitDemoEntryFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ValidateHash(fill.SignalId, nameof(fill.SignalId));
        RequireOpaqueId(fill.ExecutionId, nameof(fill.ExecutionId));
        RequireOpaqueId(fill.ClientOrderId, nameof(fill.ClientOrderId));
        RequireOpaqueId(fill.ExchangeOrderId, nameof(fill.ExchangeOrderId));
        ValidateUtc(fill.ExchangeAt);
        ValidateUtc(fill.ReceivedAt);
        if (string.IsNullOrEmpty(fill.Symbol) || fill.Symbol.Length > 20
            || fill.Symbol.Any(static c => !char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            || fill.Direction is not (Direction.Long or Direction.Short) || fill.Quantity <= 0m
            || fill.Price <= 0m || fill.Sequence < 0 || fill.ExchangeAt > fill.ReceivedAt)
        {
            throw new ArgumentException("The demo entry execution is invalid.", nameof(fill));
        }
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool Exists(SqliteConnection connection, SqliteTransaction transaction, string sql, string value)
    {
        using SqliteCommand command = Command(connection, transaction, sql);
        command.Parameters.AddWithValue("$value", value);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasUnresolvedDispatch(SqliteConnection connection, SqliteTransaction transaction) =>
        Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_orders WHERE state IN (1, 2, 4);") != 0
        || Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_unmatched_activity;") != 0
        || Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_stream_faults;") != 0
        || Scalar(connection, transaction, "SELECT COUNT(*) FROM demo_stop_changes WHERE state <> 2;") != 0;

    private static void ValidateActive(SqliteConnection connection, SqliteTransaction transaction)
    {
        List<BybitDemoReservationSnapshot> active = [];
        using (SqliteCommand command = Command(connection, transaction,
                   "SELECT signal_id, symbol, client_order_id, reservation_json, reservation_hash, state, claimed_at, exchange_order_id, position_quantity, average_entry, stop_loss FROM demo_orders WHERE state <> 5;"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                active.Add(ReadSnapshot(reader));
            }
        }
        foreach (BybitDemoReservationSnapshot snapshot in active)
        {
            List<BybitDemoEntryFill> fills = ReadFills(connection, transaction, snapshot.Reservation.SignalId);
            decimal total = 0m;
            foreach (BybitDemoEntryFill fill in fills)
            {
                if (fill.Symbol != snapshot.Reservation.Symbol || fill.Direction != snapshot.Reservation.Direction
                    || fill.ClientOrderId != snapshot.Reservation.ClientOrderId || snapshot.ClaimedAt is null
                    || fill.ExchangeAt < snapshot.ClaimedAt
                    || (snapshot.ExchangeOrderId is not null && fill.ExchangeOrderId != snapshot.ExchangeOrderId))
                {
                    throw new InvalidDataException("The demo execution does not match its reservation.");
                }
                total = checked(total + fill.Quantity);
            }
            decimal exited = ReadExitFills(connection, transaction, snapshot.Reservation.SignalId).Sum(static fill => fill.Quantity);
            if (exited > total || (snapshot.State != BybitDemoReservationState.ReconciliationRequired && total > snapshot.Reservation.Quantity)
                || (snapshot.State == BybitDemoReservationState.Protected && total - exited != snapshot.PositionQuantity))
            {
                throw new InvalidDataException("The demo execution quantities are inconsistent.");
            }
            BybitDemoStoredExitPlan? plan = ReadExitPlan(connection, transaction, snapshot.Reservation);
            if (plan is not null && (snapshot.AverageEntry != plan.AverageEntry
                || snapshot.State == BybitDemoReservationState.Protected && snapshot.PositionQuantity != plan.PositionQuantity - exited))
            {
                throw new InvalidDataException("The demo exit plan does not match its position.");
            }
            if (snapshot.State == BybitDemoReservationState.Protected
                && (snapshot.Reservation.Direction == Direction.Long ? snapshot.StopLoss >= snapshot.AverageEntry : snapshot.StopLoss <= snapshot.AverageEntry)
                && (plan?.StopAfterFirstTarget != snapshot.StopLoss
                    || ExitQuantity(connection, transaction, snapshot.Reservation.SignalId, "TP1") < plan.FirstQuantity))
            {
                throw new InvalidDataException("The demo entry-price stop has no completed first-target proof.");
            }
        }
        List<string> pending = [];
        using (SqliteCommand command = Command(connection, transaction,
                   "SELECT command_id FROM demo_stop_changes WHERE state <> 2;"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read()) pending.Add(reader.GetString(0));
        }
        foreach (string commandId in pending)
        {
            (BybitDemoStopChange Change, int State) saved = ReadStopChange(connection, transaction, commandId)
                ?? throw new InvalidDataException("The pending stop claim is missing.");
            BybitDemoReservationSnapshot? owner = active.Find(item => item.Reservation.SignalId == saved.Change.SignalId);
            if (owner is null || owner.State != BybitDemoReservationState.ReconciliationRequired
                || owner.PositionQuantity != saved.Change.PositionQuantity || owner.StopLoss != saved.Change.ExpectedStop)
            {
                throw new InvalidDataException("The pending stop claim does not match its position.");
            }
        }
    }

    private static BybitDemoReservationSnapshot Required(SqliteConnection connection,
        SqliteTransaction transaction, string signalId) => ReadOne(connection, transaction, signalId)
        ?? throw new InvalidOperationException("The demo reservation does not exist.");

    private static BybitDemoReservationSnapshot? ReadOne(SqliteConnection connection,
        SqliteTransaction transaction, string signalId)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT signal_id, symbol, client_order_id, reservation_json, reservation_hash, state, claimed_at, exchange_order_id, position_quantity, average_entry, stop_loss FROM demo_orders WHERE signal_id = $signal;");
        command.Parameters.AddWithValue("$signal", signalId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    private static BybitDemoReservationSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        string json = reader.GetString(3);
        if (!StringComparer.Ordinal.Equals(Hash(json), reader.GetString(4)))
        {
            throw new InvalidDataException("The demo reservation hash is invalid.");
        }
        BybitDemoReservation reservation = CoreJsonSerializer.Deserialize<BybitDemoReservation>(json);
        ValidateReservation(reservation);
        if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(reservation), json)
            || !StringComparer.Ordinal.Equals(reservation.SignalId, reader.GetString(0))
            || !StringComparer.Ordinal.Equals(reservation.Symbol, reader.GetString(1))
            || !StringComparer.Ordinal.Equals(reservation.ClientOrderId, reader.GetString(2)))
        {
            throw new InvalidDataException("The demo reservation row is inconsistent.");
        }
        BybitDemoReservationState state = (BybitDemoReservationState)reader.GetInt32(5);
        if (!Enum.IsDefined(state))
        {
            throw new InvalidDataException("The demo reservation state is invalid.");
        }
        DateTimeOffset? claimedAt = reader.IsDBNull(6) ? null : DateTimeOffset.ParseExact(reader.GetString(6), "O", CultureInfo.InvariantCulture);
        string? exchangeOrderId = reader.IsDBNull(7) ? null : reader.GetString(7);
        decimal? quantity = ReadDecimal(reader, 8);
        decimal? entry = ReadDecimal(reader, 9);
        decimal? stop = ReadDecimal(reader, 10);
        if ((claimedAt is { Offset: var offset } && offset != TimeSpan.Zero)
            || (state == BybitDemoReservationState.Reserved
                && (claimedAt is not null || exchangeOrderId is not null))
            || (state is BybitDemoReservationState.DispatchClaimed or BybitDemoReservationState.Acknowledged
                or BybitDemoReservationState.Protected or BybitDemoReservationState.ReconciliationRequired
                && claimedAt is null)
            || (state == BybitDemoReservationState.DispatchClaimed && exchangeOrderId is not null)
            || (state is BybitDemoReservationState.Acknowledged or BybitDemoReservationState.Protected
                && string.IsNullOrWhiteSpace(exchangeOrderId))
            || (state is BybitDemoReservationState.Reserved or BybitDemoReservationState.DispatchClaimed
                or BybitDemoReservationState.Acknowledged
                && (quantity is not null || entry is not null || stop is not null))
            || (state == BybitDemoReservationState.Protected
                && (quantity is not > 0m || quantity > reservation.Quantity || entry is not > 0m
                    || stop is not > 0m)))
        {
            throw new InvalidDataException("The demo reservation lifecycle is inconsistent.");
        }
        return new BybitDemoReservationSnapshot(reservation, state, claimedAt, exchangeOrderId, quantity, entry, stop);
    }

    private static decimal? ReadDecimal(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : decimal.Parse(reader.GetString(ordinal), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static void ValidateReservation(BybitDemoReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateHash(reservation.SignalId, nameof(reservation.SignalId));
        ValidateHash(reservation.SignalRecordHash, nameof(reservation.SignalRecordHash));
        if (string.IsNullOrEmpty(reservation.Symbol) || reservation.Symbol.Length > 20
            || reservation.Symbol.Any(static character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character))
            || string.IsNullOrEmpty(reservation.ClientOrderId) || reservation.ClientOrderId.Length > 36
            || reservation.ClientOrderId.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            || reservation.Direction is not (Direction.Long or Direction.Short)
            || !Selected(reservation.Family, reservation.Timeframe)
            || reservation.Quantity <= 0m || reservation.ReferenceQuote <= 0m || reservation.InitialStop <= 0m
            || reservation.Quantity * reservation.ReferenceQuote > 250m
            || (reservation.Direction == Direction.Long
                ? reservation.InitialStop >= reservation.ReferenceQuote : reservation.InitialStop <= reservation.ReferenceQuote))
        {
            throw new ArgumentException("The demo reservation is invalid.", nameof(reservation));
        }
    }

    private static bool Selected(SetupType family, string timeframe) => family switch
    {
        SetupType.FlowAtrRecovery => timeframe is "15m" or "30m",
        SetupType.FlowStochasticRecovery => timeframe == "15m",
        SetupType.DonchianFlowBreakout => timeframe is "30m" or "1h",
        _ => false
    };

    private static void ValidateHash(string value, string parameterName)
    {
        if (value is null || value.Length != 64
            || value.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lower-case SHA-256 identity is required.", parameterName);
        }
    }

    private static void RequireOpaqueId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("The order identity is invalid.", parameterName);
        }
    }

    private static void ValidateUtc(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC timestamp is required.", nameof(value));
        }
    }

    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
