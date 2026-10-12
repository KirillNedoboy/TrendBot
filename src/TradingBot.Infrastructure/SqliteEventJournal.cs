using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>
/// Stores causal market events in a versioned SQLite file.
/// </summary>
/// <remarks>
/// The journal is deliberately a small recording boundary. It assigns no
/// sequence values, applies no replay filtering, and has no order or execution
/// authorization state.
/// </remarks>
public sealed class SqliteEventJournal : IDisposable
{
    private readonly string _databasePath;
    private readonly int _commandTimeoutSeconds;

    private SqliteEventJournal(string databasePath, int commandTimeoutSeconds)
    {
        _databasePath = databasePath;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>
    /// Opens a file-backed journal and applies supported transactional schema
    /// migrations when the file is empty or on a prior supported version.
    /// </summary>
    public static SqliteEventJournal Open(string databasePath, int commandTimeoutSeconds = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteDatabase.ValidateTimeout(commandTimeoutSeconds);
        string fullPath = SqliteDatabase.NormalizePath(databasePath);
        SqliteEventJournal journal = new(fullPath, commandTimeoutSeconds);
        SqliteDatabase.Initialize(fullPath, commandTimeoutSeconds);
        return journal;
    }

    /// <summary>
    /// Appends one event to a stream. The caller supplies its stable sequence.
    /// </summary>
    /// <returns><see langword="false"/> when the same row is already present.</returns>
    public bool Append(string streamId, CausalMarketEvent causalEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(causalEvent);

        return Append(new ReplayEnvelope(streamId, causalEvent));
    }

    /// <summary>Appends a complete replay envelope to its declared stream.</summary>
    /// <returns><see langword="false"/> when the same row is already present.</returns>
    public bool Append(ReplayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteTransaction transaction = connection.BeginTransaction(
            IsolationLevel.Serializable, deferred: false);

        JournalRow? existing = FindRow(connection, transaction, envelope.StreamId,
            envelope.CausalEvent.StableSequence);
        if (existing is not null)
        {
            ReplayEnvelope stored = DeserializeRow(envelope.StreamId, existing);
            if (stored == envelope)
            {
                transaction.Commit();
                return false;
            }

            throw new InvalidOperationException(
                "The stream sequence already contains a different replay envelope.");
        }

        long? maximumSequence = FindMaximumSequence(connection, transaction, envelope.StreamId);
        if (maximumSequence is not null
            && envelope.CausalEvent.StableSequence <= maximumSequence.Value)
        {
            throw new InvalidOperationException(
                "A new stream event must have a stable sequence greater than the current maximum.");
        }

        InsertRow(connection, transaction, envelope);
        transaction.Commit();
        return true;
    }

    /// <summary>Reads one stream as an immutable snapshot ordered by stable sequence.</summary>
    public IReadOnlyList<CausalMarketEvent> ReadStream(string streamId)
    {
        return new ReadOnlyCollection<CausalMarketEvent>(
            ReadEnvelopes(streamId).Select(static envelope => envelope.CausalEvent).ToList());
    }

    /// <summary>Reads one stream as immutable replay envelopes ordered by stable sequence.</summary>
    public IReadOnlyList<ReplayEnvelope> ReadEnvelopes(string streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);

        using SqliteConnection connection = SqliteDatabase.OpenConnection(_databasePath,
            _commandTimeoutSeconds);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT stable_sequence, envelope_version, payload_json, event_time_ticks,
                   receive_time_ticks, availability_time_ticks, exchange_time_ticks
            FROM event_journal
            WHERE stream_id = $stream_id COLLATE BINARY
            ORDER BY stable_sequence ASC;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);

        List<ReplayEnvelope> envelopes = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            envelopes.Add(DeserializeRow(streamId, new JournalRow(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6))));
        }

        return new ReadOnlyCollection<ReplayEnvelope>(envelopes);
    }

    /// <summary>Alias for reading the immutable replay-envelope stream snapshot.</summary>
    public IReadOnlyList<ReplayEnvelope> ReadEnvelopeStream(string streamId)
    {
        return ReadEnvelopes(streamId);
    }

    /// <summary>Releases no shared connection; each operation owns its connection.</summary>
    public void Dispose()
    {
    }

    private static JournalRow? FindRow(SqliteConnection connection, SqliteTransaction transaction,
        string streamId, long stableSequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT stable_sequence, envelope_version, payload_json, event_time_ticks,
                   receive_time_ticks, availability_time_ticks, exchange_time_ticks
            FROM event_journal
            WHERE stream_id = $stream_id COLLATE BINARY
              AND stable_sequence = $stable_sequence;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        command.Parameters.AddWithValue("$stable_sequence", stableSequence);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new JournalRow(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6))
            : null;
    }

    private static long? FindMaximumSequence(SqliteConnection connection,
        SqliteTransaction transaction, string streamId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MAX(stable_sequence)
            FROM event_journal
            WHERE stream_id = $stream_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$stream_id", streamId);
        object? value = command.ExecuteScalar();
        return value is DBNull or null ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void InsertRow(SqliteConnection connection, SqliteTransaction transaction,
        ReplayEnvelope envelope)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO event_journal
                (stream_id, stable_sequence, envelope_version, payload_json, event_time_ticks,
                 receive_time_ticks, availability_time_ticks, exchange_time_ticks)
            VALUES
                ($stream_id, $stable_sequence, $envelope_version, $payload_json, $event_time_ticks,
                 $receive_time_ticks, $availability_time_ticks, $exchange_time_ticks);
            """;
        command.Parameters.AddWithValue("$stream_id", envelope.StreamId);
        command.Parameters.AddWithValue("$stable_sequence", envelope.CausalEvent.StableSequence);
        command.Parameters.AddWithValue("$envelope_version", envelope.FormatVersion);
        command.Parameters.AddWithValue("$payload_json",
            CoreJsonSerializer.Serialize(envelope.CausalEvent.Payload));
        command.Parameters.AddWithValue("$event_time_ticks",
            ToUtcTicks(envelope.CausalEvent.Payload.EventTime));
        command.Parameters.AddWithValue("$receive_time_ticks",
            ToUtcTicks(envelope.CausalEvent.ReceiveTime));
        command.Parameters.AddWithValue("$availability_time_ticks",
            ToUtcTicks(envelope.CausalEvent.AvailabilityTime));
        command.Parameters.AddWithValue("$exchange_time_ticks",
            envelope.CausalEvent.ExchangeTime is null
                ? DBNull.Value
                : ToUtcTicks(envelope.CausalEvent.ExchangeTime));
        command.ExecuteNonQuery();
    }

    private static ReplayEnvelope DeserializeRow(string streamId, JournalRow row)
    {
        try
        {
            if (row.EnvelopeVersion != ReplayEnvelope.CurrentFormatVersion)
            {
                throw new InvalidDataException(
                    $"Replay envelope format version {row.EnvelopeVersion} is not supported.");
            }

            MarketEvent payload = CoreJsonSerializer.Deserialize<MarketEvent>(row.PayloadJson);
            UtcTimestamp eventTime = FromUtcTicks(row.EventTimeTicks);
            UtcTimestamp receiveTime = FromUtcTicks(row.ReceiveTimeTicks);
            UtcTimestamp availabilityTime = FromUtcTicks(row.AvailabilityTimeTicks);
            UtcTimestamp? exchangeTime = row.ExchangeTimeTicks is null
                ? null
                : FromUtcTicks(row.ExchangeTimeTicks.Value);

            if (ToUtcTicks(payload.EventTime) != row.EventTimeTicks)
            {
                throw new InvalidDataException("The persisted event time does not match its payload.");
            }

            CausalMarketEvent causalEvent = new(payload, receiveTime, availabilityTime,
                row.StableSequence, exchangeTime);
            if (ToUtcTicks(causalEvent.ReceiveTime) != row.ReceiveTimeTicks
                || ToUtcTicks(causalEvent.AvailabilityTime) != row.AvailabilityTimeTicks
                || (causalEvent.ExchangeTime is null
                    ? row.ExchangeTimeTicks is not null
                    : ToUtcTicks(causalEvent.ExchangeTime) != row.ExchangeTimeTicks))
            {
                throw new InvalidDataException("The persisted causal metadata does not match the row.");
            }

            _ = eventTime;
            return new ReplayEnvelope(row.EnvelopeVersion, streamId, causalEvent);
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
            throw new InvalidDataException("The persisted event payload or causal metadata is invalid.",
                exception);
        }
    }

    private static long ToUtcTicks(UtcTimestamp timestamp) => timestamp.Value.UtcDateTime.Ticks;

    private static UtcTimestamp FromUtcTicks(long ticks)
    {
        DateTime utcDateTime = new(ticks, DateTimeKind.Utc);
        return new UtcTimestamp(new DateTimeOffset(utcDateTime));
    }

    private sealed record JournalRow(long StableSequence, int EnvelopeVersion, string PayloadJson,
        long EventTimeTicks, long ReceiveTimeTicks, long AvailabilityTimeTicks,
        long? ExchangeTimeTicks);
}
