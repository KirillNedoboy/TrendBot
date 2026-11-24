using System.Data;
using TradingBot.Core;
using Microsoft.Data.Sqlite;

namespace TradingBot.Infrastructure;

public sealed record BybitDemoExitIntent(string SignalId, string ClientOrderId, string Kind,
    decimal Quantity, decimal? TriggerPrice, DateTimeOffset ClaimedAt, bool Market = false);
public sealed record BybitDemoExitOrderState(BybitDemoExitIntent Intent, string? ExchangeOrderId,
    string Status, decimal FilledQuantity);
public sealed record BybitDemoExitFill(string SignalId, string ExecutionId, string ExchangeOrderId,
    string Symbol, Direction Side, decimal Quantity, decimal Price, decimal Fee,
    DateTimeOffset ExchangeAt, DateTimeOffset ReceivedAt);

public sealed partial class SqliteBybitDemoReservationStore
{
    private static void CreateRuntimeSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS demo_runtime_sources (
                signal_id TEXT PRIMARY KEY COLLATE BINARY, source_json TEXT NOT NULL, source_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS demo_exit_orders (
                client_id TEXT PRIMARY KEY COLLATE BINARY, signal_id TEXT NOT NULL COLLATE BINARY,
                intent_json TEXT NOT NULL, intent_hash TEXT NOT NULL, exchange_id TEXT UNIQUE,
                status TEXT NOT NULL, filled TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS demo_exit_fills (
                execution_id TEXT PRIMARY KEY COLLATE BINARY, signal_id TEXT NOT NULL COLLATE BINARY,
                fill_json TEXT NOT NULL, fill_hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS demo_exit_orders_signal ON demo_exit_orders(signal_id);
            CREATE INDEX IF NOT EXISTS demo_exit_fills_signal ON demo_exit_fills(signal_id);
            """);
        command.ExecuteNonQuery();
    }

    public void SaveSource(string signalId, string canonicalJson)
    {
        ValidateHash(signalId, nameof(signalId));
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalJson);
        if (canonicalJson.Length > 16384) throw new ArgumentException("The demo source exceeds its bound.");
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        _ = Required(connection, transaction, signalId);
        using SqliteCommand existing = Command(connection, transaction,
            "SELECT source_json, source_hash FROM demo_runtime_sources WHERE signal_id = $signal;");
        existing.Parameters.AddWithValue("$signal", signalId);
        using (SqliteDataReader reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != canonicalJson || reader.GetString(1) != Hash(canonicalJson))
                    throw new InvalidDataException("The stored demo source conflicts.");
                transaction.Commit();
                return;
            }
        }
        using SqliteCommand insert = Command(connection, transaction,
            "INSERT INTO demo_runtime_sources VALUES($signal, $json, $hash);");
        insert.Parameters.AddWithValue("$signal", signalId);
        insert.Parameters.AddWithValue("$json", canonicalJson);
        insert.Parameters.AddWithValue("$hash", Hash(canonicalJson));
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    public string? ReadSource(string signalId)
    {
        ValidateHash(signalId, nameof(signalId));
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT source_json, source_hash FROM demo_runtime_sources WHERE signal_id=$signal;";
        command.Parameters.AddWithValue("$signal", signalId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string json = reader.GetString(0);
        if (Hash(json) != reader.GetString(1)) throw new InvalidDataException("The demo source hash is invalid.");
        return json;
    }

    public bool ClaimExit(BybitDemoExitIntent intent, string? observedStopOrderId = null)
    {
        ValidateExitIntent(intent);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        BybitDemoReservationSnapshot position = Required(connection, transaction, intent.SignalId);
        BybitDemoExitOrderState? existing = ReadExitOrders(connection, transaction, intent.SignalId)
            .SingleOrDefault(item => item.Intent.ClientOrderId == intent.ClientOrderId);
        if (existing is not null)
        {
            if (existing.Intent with { ClaimedAt = intent.ClaimedAt } != intent
                || observedStopOrderId is not null && existing.ExchangeOrderId != observedStopOrderId)
                throw new InvalidOperationException("The demo exit claim conflicts.");
            transaction.Commit();
            return false;
        }
        decimal entered = ReadFills(connection, transaction, intent.SignalId).Sum(static fill => fill.Quantity);
        decimal exited = ReadExitFills(connection, transaction, intent.SignalId).Sum(static fill => fill.Quantity);
        if (position.ClaimedAt is null || intent.ClaimedAt < position.ClaimedAt || intent.Quantity > entered
            || position.State == BybitDemoReservationState.Closed || entered <= 0m || entered <= exited && observedStopOrderId is null)
            throw new InvalidOperationException("The demo exit has no matching open position.");
        if (intent.Kind == "StopLoss")
        {
            RequireOpaqueId(observedStopOrderId!, nameof(observedStopOrderId));
        }
        else if (observedStopOrderId is not null)
        {
            throw new ArgumentException("Only an observed stop can bind an external identity.");
        }
        else if (intent.Kind is "TP1" or "TP2")
        {
            BybitDemoStoredExitPlan plan = ReadExitPlan(connection, transaction, position.Reservation)
                ?? throw new InvalidOperationException("The demo target has no durable plan.");
            decimal? trigger = intent.Kind == "TP1" ? plan.FirstTarget : plan.SecondTarget;
            decimal quantity = intent.Kind == "TP1" ? plan.FirstQuantity : plan.SecondQuantity;
            if (trigger is null || intent.TriggerPrice != trigger || intent.Quantity > quantity)
                throw new InvalidOperationException("The demo target does not match its plan.");
            decimal claimed = ReadExitOrders(connection, transaction, intent.SignalId)
                .Where(item => item.Intent.Kind == intent.Kind && !Terminal(item.Status))
                .Sum(item => item.Intent.Quantity - item.FilledQuantity);
            decimal filled = ExitQuantity(connection, transaction, intent.SignalId, intent.Kind);
            if (claimed + filled + intent.Quantity > quantity)
                throw new InvalidOperationException("The demo target quantity is already claimed.");
        }
        string json = CoreJsonSerializer.Serialize(intent);
        using SqliteCommand insert = Command(connection, transaction,
            "INSERT INTO demo_exit_orders VALUES($client,$signal,$json,$hash,$exchange,$status,'0');");
        insert.Parameters.AddWithValue("$client", intent.ClientOrderId);
        insert.Parameters.AddWithValue("$signal", intent.SignalId);
        insert.Parameters.AddWithValue("$json", json);
        insert.Parameters.AddWithValue("$hash", Hash(json));
        insert.Parameters.AddWithValue("$exchange", (object?)observedStopOrderId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$status", observedStopOrderId is null ? "Claimed" : "Untriggered");
        insert.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public void ObserveExitOrder(string signalId, string clientId, string exchangeId, string status, decimal filled)
    {
        ValidateHash(signalId, nameof(signalId));
        RequireOpaqueId(exchangeId, nameof(exchangeId));
        if (status is not ("Acknowledged" or "New" or "PartiallyFilled" or "Untriggered" or "Triggered"
            or "Filled" or "Cancelled" or "PartiallyFilledCanceled" or "Rejected" or "Deactivated") || filled < 0m)
            throw new ArgumentException("The demo exit observation is invalid.");
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        BybitDemoExitOrderState current = ReadExitOrders(connection, transaction, signalId)
            .Single(item => item.Intent.ClientOrderId == clientId);
        if (current.ExchangeOrderId is not null && current.ExchangeOrderId != exchangeId
            || filled < current.FilledQuantity || filled > current.Intent.Quantity
            || Terminal(current.Status) && (current.Status != status || current.FilledQuantity != filled))
            throw new InvalidOperationException("The demo exit observation conflicts.");
        using SqliteCommand command = Command(connection, transaction,
            "UPDATE demo_exit_orders SET exchange_id=$id,status=$status,filled=$filled WHERE client_id=$client;");
        command.Parameters.AddWithValue("$id", exchangeId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$filled", Number(filled));
        command.Parameters.AddWithValue("$client", clientId);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public bool RecordExitFill(BybitDemoExitFill fill)
    {
        ValidateHash(fill.SignalId, nameof(fill.SignalId));
        RequireOpaqueId(fill.ExecutionId, nameof(fill.ExecutionId));
        RequireOpaqueId(fill.ExchangeOrderId, nameof(fill.ExchangeOrderId));
        ValidateUtc(fill.ExchangeAt);
        ValidateUtc(fill.ReceivedAt);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        BybitDemoReservationSnapshot position = Required(connection, transaction, fill.SignalId);
        BybitDemoExitOrderState order = ReadExitOrders(connection, transaction, fill.SignalId)
            .Single(item => item.ExchangeOrderId == fill.ExchangeOrderId);
        if (fill.Symbol != position.Reservation.Symbol || fill.Side == position.Reservation.Direction
            || fill.Side is not (Direction.Long or Direction.Short) || fill.Quantity <= 0m || fill.Price <= 0m
            || position.ClaimedAt is null || fill.ExchangeAt < position.ClaimedAt || fill.ExchangeAt > fill.ReceivedAt)
            throw new ArgumentException("The demo exit execution is invalid.");
        string json = CoreJsonSerializer.Serialize(fill);
        using (SqliteCommand find = Command(connection, transaction,
            "SELECT fill_json,fill_hash FROM demo_exit_fills WHERE execution_id=$id;"))
        {
            find.Parameters.AddWithValue("$id", fill.ExecutionId);
            using SqliteDataReader reader = find.ExecuteReader();
            if (reader.Read())
            {
                string previousJson = reader.GetString(0);
                if (Hash(previousJson) != reader.GetString(1)
                    || CoreJsonSerializer.Deserialize<BybitDemoExitFill>(previousJson) with { ReceivedAt = fill.ReceivedAt } != fill)
                    throw new InvalidDataException("The demo exit execution conflicts.");
                transaction.Commit();
                return false;
            }
        }
        List<BybitDemoExitFill> previous = ReadExitFills(connection, transaction, fill.SignalId);
        if (previous.Where(item => item.ExchangeOrderId == fill.ExchangeOrderId).Sum(item => item.Quantity) + fill.Quantity > order.Intent.Quantity
            || previous.Sum(item => item.Quantity) + fill.Quantity > ReadFills(connection, transaction, fill.SignalId).Sum(item => item.Quantity))
            throw new InvalidOperationException("The demo exit overfills its position.");
        using SqliteCommand insert = Command(connection, transaction,
            "INSERT INTO demo_exit_fills VALUES($id,$signal,$json,$hash);");
        insert.Parameters.AddWithValue("$id", fill.ExecutionId);
        insert.Parameters.AddWithValue("$signal", fill.SignalId);
        insert.Parameters.AddWithValue("$json", json);
        insert.Parameters.AddWithValue("$hash", Hash(json));
        insert.ExecuteNonQuery();
        MarkReconciliation(connection, transaction, fill.SignalId);
        transaction.Commit();
        return true;
    }

    public IReadOnlyList<BybitDemoExitOrderState> ReadExitOrders(string signalId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        return ReadExitOrders(connection, transaction, signalId);
    }

    public IReadOnlyList<BybitDemoExitFill> ReadExitFills(string signalId)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        return ReadExitFills(connection, transaction, signalId);
    }

    public decimal FilledExitQuantity(string signalId, string kind)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        return ExitQuantity(connection, transaction, signalId, kind);
    }

    public IReadOnlyList<BybitDemoReservationSnapshot> ReadAll()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT signal_id, symbol, client_order_id, reservation_json, reservation_hash, state, claimed_at, exchange_order_id, position_quantity, average_entry, stop_loss FROM demo_orders ORDER BY signal_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        List<BybitDemoReservationSnapshot> result = [];
        while (reader.Read()) result.Add(ReadSnapshot(reader));
        return result;
    }

    public bool AbandonUnsent(string signalId)
    {
        ValidateHash(signalId, nameof(signalId));
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        ValidateActive(connection, transaction);
        using SqliteCommand command = Command(connection, transaction,
            "UPDATE demo_orders SET state=5 WHERE signal_id=$signal AND state=0 AND claimed_at IS NULL;");
        command.Parameters.AddWithValue("$signal", signalId);
        bool changed = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return changed;
    }

    public void ReconcilePosition(string signalId, decimal quantity, decimal average, decimal stop,
        bool entryTerminal, DateTimeOffset observedAt)
    {
        ValidateUtc(observedAt);
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        BybitDemoReservationSnapshot position = Required(connection, transaction, signalId);
        List<BybitDemoEntryFill> entries = ReadFills(connection, transaction, signalId);
        List<BybitDemoExitFill> exits = ReadExitFills(connection, transaction, signalId);
        if (!entryTerminal || position.ClaimedAt is null || observedAt < position.ClaimedAt
            || quantity < 0m || quantity != entries.Sum(item => item.Quantity) - exits.Sum(item => item.Quantity)
            || entries.Any(item => item.ReceivedAt > observedAt) || exits.Any(item => item.ReceivedAt > observedAt))
            throw new InvalidOperationException("The demo position does not match complete executions.");
        BybitDemoStoredExitPlan? plan = ReadExitPlan(connection, transaction, position.Reservation);
        decimal? expected = plan?.StopLoss;
        if (plan?.StopAfterFirstTarget is { } breakEven && ExitQuantity(connection, transaction, signalId, "TP1") >= plan.FirstQuantity)
            expected = breakEven;
        if (quantity > 0m && (average <= 0m || stop <= 0m || plan is not null
            && (plan.AverageEntry != average || stop != expected && stop != plan.StopLoss)))
            throw new InvalidOperationException("The demo position protection differs from its plan.");
        if (quantity == 0m && ReadExitOrders(connection, transaction, signalId).Any(item => !Terminal(item.Status)))
            throw new InvalidOperationException("The flat demo position still has unresolved exit orders.");
        using (SqliteCommand pending = Command(connection, transaction,
            "SELECT change_json FROM demo_stop_changes WHERE signal_id=$signal AND state<>2;"))
        {
            pending.Parameters.AddWithValue("$signal", signalId);
            using SqliteDataReader reader = pending.ExecuteReader();
            while (reader.Read())
            {
                BybitDemoStopChange change = CoreJsonSerializer.Deserialize<BybitDemoStopChange>(reader.GetString(0));
                if (quantity > 0m && change.NewStop != stop)
                    throw new InvalidOperationException("The pending demo stop has not been observed.");
            }
        }
        using SqliteCommand update = Command(connection, transaction, """
            UPDATE demo_orders SET state=$state,position_quantity=$quantity,average_entry=$average,stop_loss=$stop WHERE signal_id=$signal;
            UPDATE demo_stop_changes SET state=2 WHERE signal_id=$signal AND state<>2;
            """);
        update.Parameters.AddWithValue("$state", quantity == 0m ? 5 : 3);
        update.Parameters.AddWithValue("$quantity", Number(quantity));
        update.Parameters.AddWithValue("$average", Number(average));
        update.Parameters.AddWithValue("$stop", Number(stop));
        update.Parameters.AddWithValue("$signal", signalId);
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    private static bool StopChangeAllowed(SqliteConnection connection, SqliteTransaction transaction,
        BybitDemoReservationSnapshot position, decimal stop, decimal average)
    {
        if (position.Reservation.Direction == Direction.Long ? stop < average : stop > average) return true;
        BybitDemoStoredExitPlan? plan = ReadExitPlan(connection, transaction, position.Reservation);
        return plan?.StopAfterFirstTarget == stop
            && ExitQuantity(connection, transaction, position.Reservation.SignalId, "TP1") >= plan.FirstQuantity;
    }

    public static bool Terminal(string status) => status is "Filled" or "Cancelled" or "PartiallyFilledCanceled" or "Rejected" or "Deactivated";

    private static decimal ExitQuantity(SqliteConnection connection, SqliteTransaction transaction, string signalId, string kind)
    {
        HashSet<string> ids = ReadExitOrders(connection, transaction, signalId).Where(item => item.Intent.Kind == kind)
            .Select(item => item.ExchangeOrderId ?? string.Empty).ToHashSet(StringComparer.Ordinal);
        return ReadExitFills(connection, transaction, signalId).Where(item => ids.Contains(item.ExchangeOrderId)).Sum(item => item.Quantity);
    }

    private static List<BybitDemoExitOrderState> ReadExitOrders(SqliteConnection connection, SqliteTransaction transaction, string signalId)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT client_id,intent_json,intent_hash,exchange_id,status,filled FROM demo_exit_orders WHERE signal_id=$signal ORDER BY client_id;");
        command.Parameters.AddWithValue("$signal", signalId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<BybitDemoExitOrderState> result = [];
        while (reader.Read())
        {
            string json = reader.GetString(1);
            BybitDemoExitIntent intent = CoreJsonSerializer.Deserialize<BybitDemoExitIntent>(json);
            ValidateExitIntent(intent);
            if (Hash(json) != reader.GetString(2) || intent.SignalId != signalId || intent.ClientOrderId != reader.GetString(0)
                || CoreJsonSerializer.Serialize(intent) != json) throw new InvalidDataException("The demo exit intent is corrupt.");
            result.Add(new(intent, reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
                decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static List<BybitDemoExitFill> ReadExitFills(SqliteConnection connection, SqliteTransaction transaction, string signalId)
    {
        using SqliteCommand command = Command(connection, transaction,
            "SELECT execution_id,fill_json,fill_hash FROM demo_exit_fills WHERE signal_id=$signal ORDER BY execution_id;");
        command.Parameters.AddWithValue("$signal", signalId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<BybitDemoExitFill> result = [];
        while (reader.Read())
        {
            string json = reader.GetString(1);
            BybitDemoExitFill fill = CoreJsonSerializer.Deserialize<BybitDemoExitFill>(json);
            if (Hash(json) != reader.GetString(2) || fill.SignalId != signalId || fill.ExecutionId != reader.GetString(0)
                || fill.Quantity <= 0m || CoreJsonSerializer.Serialize(fill) != json)
                throw new InvalidDataException("The demo exit execution is corrupt.");
            result.Add(fill);
        }
        return result;
    }

    private static void ValidateExitIntent(BybitDemoExitIntent intent)
    {
        ValidateHash(intent.SignalId, nameof(intent.SignalId));
        RequireOpaqueId(intent.ClientOrderId, nameof(intent.ClientOrderId));
        ValidateUtc(intent.ClaimedAt);
        if (intent.ClientOrderId.Length > 36 || intent.Quantity <= 0m || intent.TriggerPrice is <= 0m
            || intent.Kind is not ("StopLoss" or "TP1" or "TP2" or "Channel" or "Safety"))
            throw new ArgumentException("The demo exit intent is invalid.");
    }
}
