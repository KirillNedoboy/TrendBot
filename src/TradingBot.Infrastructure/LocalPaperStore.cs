using System.Data;
using Microsoft.Data.Sqlite;

namespace TradingBot.Infrastructure;

/// <summary>Durable journal for deterministic local paper observations.</summary>
public sealed class LocalPaperStore : IDisposable
{
    private readonly string _path;
    private bool _disposed;

    private LocalPaperStore(string path) => _path = path;

    public static LocalPaperStore Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:")
            throw new ArgumentException("Paper state requires a file-backed database.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using SqliteConnection connection = Connect(fullPath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS paper_entries
            (stream_id TEXT NOT NULL, sequence INTEGER NOT NULL, event_id TEXT NOT NULL,
             payload TEXT NOT NULL, state TEXT NOT NULL,
             PRIMARY KEY(stream_id, sequence), UNIQUE(stream_id, event_id));
            CREATE TABLE IF NOT EXISTS paper_checkpoints
            (stream_id TEXT PRIMARY KEY, sequence INTEGER NOT NULL, state TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        return new LocalPaperStore(fullPath);
    }

    public bool Append(string streamId, long sequence, string eventId, string payload,
        string state)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        using SqliteConnection connection = Connect(_path);
        using SqliteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        using SqliteCommand existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT event_id,payload,state FROM paper_entries WHERE stream_id=$s AND sequence=$q;";
        existing.Parameters.AddWithValue("$s", streamId);
        existing.Parameters.AddWithValue("$q", sequence);
        using SqliteDataReader reader = existing.ExecuteReader();
        if (reader.Read())
        {
            bool same = StringComparer.Ordinal.Equals(reader.GetString(0), eventId)
                && StringComparer.Ordinal.Equals(reader.GetString(1), payload)
                && StringComparer.Ordinal.Equals(reader.GetString(2), state);
            reader.Close();
            if (!same) throw new InvalidOperationException("Paper sequence conflicts with durable evidence.");
            transaction.Commit();
            return false;
        }
        reader.Close();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO paper_entries VALUES($s,$q,$e,$p,$v); INSERT INTO paper_checkpoints VALUES($s,$q,$v) ON CONFLICT(stream_id) DO UPDATE SET sequence=excluded.sequence,state=excluded.state WHERE excluded.sequence>paper_checkpoints.sequence;";
        insert.Parameters.AddWithValue("$s", streamId);
        insert.Parameters.AddWithValue("$q", sequence);
        insert.Parameters.AddWithValue("$e", eventId);
        insert.Parameters.AddWithValue("$p", payload);
        insert.Parameters.AddWithValue("$v", state);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public string? ReadCheckpoint(string streamId)
    {
        ThrowIfDisposed();
        using SqliteConnection connection = Connect(_path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM paper_checkpoints WHERE stream_id=$s;";
        command.Parameters.AddWithValue("$s", streamId);
        return command.ExecuteScalar() as string;
    }

    public void Dispose() { _disposed = true; GC.SuppressFinalize(this); }

    private static SqliteConnection Connect(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
