using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Stores caller-supplied portfolio limits and durable capacity reservations.
/// </summary>
/// <remarks>
/// Each reservation attempt uses one immediate serializable transaction. Decimal
/// totals are reconstructed and checked in application code; SQLite stores only
/// canonical JSON text and BINARY identity columns.
/// </remarks>
public sealed partial class SqlitePortfolioReservationStore : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqlitePortfolioReservationStore(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>Opens a file-backed portfolio reservation store.</summary>
    public static SqlitePortfolioReservationStore Open(string databasePath,
        int commandTimeoutSeconds = 30)
    {
        SqliteDatabase.ValidateTimeout(commandTimeoutSeconds);
        string fullPath = SqliteDatabase.NormalizePath(databasePath);
        SqliteDatabase.Initialize(fullPath, commandTimeoutSeconds);
        return new SqlitePortfolioReservationStore(fullPath, commandTimeoutSeconds);
    }

    /// <summary>
    /// Registers immutable caller-supplied limits. An identical retry returns
    /// <see langword="false"/>; a conflicting identity throws without mutation.
    /// </summary>
    public bool Register(PortfolioLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        string limitsJson = CoreJsonSerializer.Serialize(limits);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);
        PortfolioRow? existing = FindPortfolio(connection, transaction, limits.PortfolioId.Value);
        if (existing is not null)
        {
            PortfolioLimits stored = DeserializePortfolio(existing);
            if (stored == limits)
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The portfolio identifier already contains different limits.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO portfolios (portfolio_id, limits_json)
            VALUES ($portfolio_id, $limits_json);
            """;
        command.Parameters.AddWithValue("$portfolio_id", limits.PortfolioId.Value);
        command.Parameters.AddWithValue("$limits_json", limitsJson);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    /// <summary>Reads immutable limits, or <see langword="null"/> when absent.</summary>
    public PortfolioLimits? FindLimits(PortfolioId portfolioId)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);

        using SqliteConnection connection = OpenConnection();
        PortfolioRow? row = FindPortfolio(connection, transaction: null, portfolioId.Value);
        return row is null ? null : DeserializePortfolio(row);
    }

    /// <summary>Reads one portfolio's immutable reservations in identity order.</summary>
    public IReadOnlyList<PortfolioReservation> ReadReservations(PortfolioId portfolioId)
    {
        ArgumentNullException.ThrowIfNull(portfolioId);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT reservation_id, portfolio_id, intent_id, reservation_json, released
            FROM reservations
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
              AND released = 0
            ORDER BY reservation_id ASC;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId.Value);

        List<PortfolioReservation> reservations = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            reservations.Add(DeserializeReservation(new ReservationRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4) != 0)));
        }

        return new ReadOnlyCollection<PortfolioReservation>(reservations);
    }

    /// <summary>Reads one reservation by its immutable identity.</summary>
    public PortfolioReservation? FindReservation(string reservationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        using SqliteConnection connection = OpenConnection();
        ReservationRow? row = FindReservationById(connection, transaction: null, reservationId);
        return row is null ? null : DeserializeReservation(row);
    }

    /// <summary>
    /// Atomically persists an order intent with its reservation when every
    /// caller-supplied portfolio capacity remains available.
    /// </summary>
    public PortfolioReservationResult TryReserve(OrderIntent intent,
        PortfolioReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(reservation);
        if (!StringComparer.Ordinal.Equals(intent.IntentId, reservation.IntentId))
        {
            throw new ArgumentException(
                "The reservation must refer to the supplied order intent.", nameof(reservation));
        }

        string intentJson = CoreJsonSerializer.Serialize(intent);
        string reservationJson = CoreJsonSerializer.Serialize(reservation);

        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = BeginImmediateTransaction(connection);

        PortfolioRow? portfolioRow = FindPortfolio(connection, transaction,
            reservation.PortfolioId.Value);
        if (portfolioRow is null)
        {
            throw new InvalidOperationException(
                "The reservation portfolio must be registered before reserving capacity.");
        }

        PortfolioLimits limits = DeserializePortfolio(portfolioRow);
        if (!StringComparer.Ordinal.Equals(limits.MaxRisk.Currency, reservation.Risk.Currency))
        {
            throw new ArgumentException(
                "The reservation currency must match the registered portfolio currency.",
                nameof(reservation));
        }

        ReservationRow? existingByReservationId = FindReservationById(connection, transaction,
            reservation.ReservationId);
        ReservationRow? existingByIntentId = FindReservationByIntentId(connection, transaction,
            reservation.IntentId);
        IntentRow? existingIntentById = FindIntentById(connection, transaction, intent.IntentId);
        IntentRow? existingIntentByClientId = FindIntentByClientOrderId(connection, transaction,
            intent.ClientOrderId);

        if (existingByReservationId is not null || existingByIntentId is not null)
        {
            if (existingByReservationId is null || existingByIntentId is null
                || !StringComparer.Ordinal.Equals(existingByReservationId.ReservationId,
                    existingByIntentId.ReservationId))
            {
                throw new InvalidOperationException(
                    "The reservation identity is already associated with a different intent.");
            }

            PortfolioReservation storedReservation = DeserializeReservation(existingByReservationId);
            if (!storedReservation.Equals(reservation))
            {
                throw new InvalidOperationException(
                    "The reservation identifier already contains a different reservation.");
            }

            if (existingIntentById is null)
            {
                throw new InvalidDataException(
                    "The persisted reservation is missing its order intent.");
            }

            OrderIntent storedIntent = DeserializeIntent(existingIntentById);
            if (!storedIntent.Equals(intent))
            {
                throw new InvalidOperationException(
                    "The reservation intent identifier already contains a different order intent.");
            }

            transaction.Commit();
            return PortfolioReservationResult.AlreadyReserved;
        }

        if (existingIntentById is not null)
        {
            OrderIntent storedIntent = DeserializeIntent(existingIntentById);
            if (!storedIntent.Equals(intent))
            {
                throw new InvalidOperationException(
                    "The intent identifier already contains a different order intent.");
            }
        }

        if (existingIntentByClientId is not null
            && !StringComparer.Ordinal.Equals(existingIntentByClientId.IntentId, intent.IntentId))
        {
            _ = DeserializeIntent(existingIntentByClientId);
            throw new InvalidOperationException(
                "The client order identifier already belongs to a different order intent.");
        }

        CapacityTotals totals = ReadTotals(connection, transaction, reservation.PortfolioId.Value);
        decimal totalRisk = checked(totals.Risk + reservation.Risk.Amount);
        decimal totalNotional = checked(totals.NotionalExposure + reservation.NotionalExposure.Amount);
        int totalSlots = checked(totals.PositionSlots + reservation.PositionSlots);
        if (totalRisk > limits.MaxRisk.Amount
            || totalNotional > limits.MaxNotionalExposure.Amount
            || totalSlots > limits.MaxPositionSlots)
        {
            transaction.Commit();
            return PortfolioReservationResult.InsufficientCapacity;
        }

        if (existingIntentById is null)
        {
            InsertIntent(connection, transaction, intent, intentJson);
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reservations
                    (reservation_id, portfolio_id, intent_id, reservation_json)
                VALUES ($reservation_id, $portfolio_id, $intent_id, $reservation_json);
                """;
            command.Parameters.AddWithValue("$reservation_id", reservation.ReservationId);
            command.Parameters.AddWithValue("$portfolio_id", reservation.PortfolioId.Value);
            command.Parameters.AddWithValue("$intent_id", reservation.IntentId);
            command.Parameters.AddWithValue("$reservation_json", reservationJson);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return PortfolioReservationResult.Reserved;
    }

    /// <summary>Releases no shared connection; each operation owns its connection.</summary>
    public void Dispose()
    {
    }

    private SqliteConnection OpenConnection() =>
        SqliteDatabase.OpenConnection(_databasePath, _commandTimeoutSeconds);

    private static SqliteTransaction BeginImmediateTransaction(SqliteConnection connection) =>
        connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

    private static PortfolioRow? FindPortfolio(SqliteConnection connection,
        SqliteTransaction? transaction, string portfolioId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT portfolio_id, limits_json
            FROM portfolios
            WHERE portfolio_id = $portfolio_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new PortfolioRow(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static ReservationRow? FindReservationById(SqliteConnection connection,
        SqliteTransaction? transaction, string reservationId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT reservation_id, portfolio_id, intent_id, reservation_json, released
            FROM reservations
            WHERE reservation_id = $reservation_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$reservation_id", reservationId);
        return ReadReservationRow(command);
    }

    private static ReservationRow? FindReservationByIntentId(SqliteConnection connection,
        SqliteTransaction transaction, string intentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT reservation_id, portfolio_id, intent_id, reservation_json, released
            FROM reservations
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        return ReadReservationRow(command);
    }

    private static ReservationRow? ReadReservationRow(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new ReservationRow(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4) != 0)
            : null;
    }

    private static IntentRow? FindIntentById(SqliteConnection connection,
        SqliteTransaction transaction, string intentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT intent_id, client_order_id, intent_json
            FROM order_intents
            WHERE intent_id = $intent_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$intent_id", intentId);
        return ReadIntentRow(command);
    }

    private static IntentRow? FindIntentByClientOrderId(SqliteConnection connection,
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
        return ReadIntentRow(command);
    }

    private static IntentRow? ReadIntentRow(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new IntentRow(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static void InsertIntent(SqliteConnection connection, SqliteTransaction transaction,
        OrderIntent intent, string intentJson)
    {
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
    }

    private static CapacityTotals ReadTotals(SqliteConnection connection,
        SqliteTransaction transaction, string portfolioId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT reservation_id, portfolio_id, intent_id, reservation_json
            FROM reservations
            WHERE portfolio_id = $portfolio_id COLLATE BINARY
              AND released = 0;
            """;
        command.Parameters.AddWithValue("$portfolio_id", portfolioId);

        decimal risk = 0m;
        decimal notional = 0m;
        int slots = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            PortfolioReservation reservation = DeserializeReservation(new ReservationRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                false));
            risk = checked(risk + reservation.Risk.Amount);
            notional = checked(notional + reservation.NotionalExposure.Amount);
            slots = checked(slots + reservation.PositionSlots);
        }

        return new CapacityTotals(risk, notional, slots);
    }

    private static PortfolioLimits DeserializePortfolio(PortfolioRow row)
    {
        try
        {
            PortfolioLimits limits = CoreJsonSerializer.Deserialize<PortfolioLimits>(row.LimitsJson);
            if (!StringComparer.Ordinal.Equals(limits.PortfolioId.Value, row.PortfolioId))
            {
                throw new InvalidDataException(
                    "The persisted portfolio identity does not match its SQLite row.");
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(limits), row.LimitsJson))
            {
                throw new InvalidDataException("The persisted portfolio limits JSON is not canonical.");
            }

            return limits;
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
            throw new InvalidDataException("The persisted portfolio limits JSON is invalid.", exception);
        }
    }

    private static PortfolioReservation DeserializeReservation(ReservationRow row)
    {
        try
        {
            PortfolioReservation reservation =
                CoreJsonSerializer.Deserialize<PortfolioReservation>(row.ReservationJson);
            if (!StringComparer.Ordinal.Equals(reservation.ReservationId, row.ReservationId)
                || !StringComparer.Ordinal.Equals(reservation.PortfolioId.Value, row.PortfolioId)
                || !StringComparer.Ordinal.Equals(reservation.IntentId, row.IntentId))
            {
                throw new InvalidDataException(
                    "The persisted reservation identity does not match its SQLite row.");
            }

            if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(reservation),
                row.ReservationJson))
            {
                throw new InvalidDataException("The persisted reservation JSON is not canonical.");
            }

            return reservation;
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
            throw new InvalidDataException("The persisted reservation JSON is invalid.", exception);
        }
    }

    private static OrderIntent DeserializeIntent(IntentRow row)
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

    private sealed record PortfolioRow(string PortfolioId, string LimitsJson);

    private sealed record ReservationRow(string ReservationId, string PortfolioId, string IntentId,
        string ReservationJson, bool Released);

    private sealed record IntentRow(string IntentId, string ClientOrderId, string IntentJson);

    private readonly record struct CapacityTotals(decimal Risk, decimal NotionalExposure,
        int PositionSlots);
}
