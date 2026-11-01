using Microsoft.Data.Sqlite;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class LocalPaperStoreTests
{
    [Fact]
    public void OpeningLegacyJournalMigratesItsSchemaVersionWithoutDroppingRows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"local-paper-legacy-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE paper_entries
                    (stream_id TEXT NOT NULL, sequence INTEGER NOT NULL, event_id TEXT NOT NULL,
                     payload TEXT NOT NULL, state TEXT NOT NULL,
                     PRIMARY KEY(stream_id, sequence), UNIQUE(stream_id, event_id));
                    CREATE TABLE paper_checkpoints
                    (stream_id TEXT PRIMARY KEY, sequence INTEGER NOT NULL, state TEXT NOT NULL);
                    INSERT INTO paper_entries VALUES ('legacy', 1, 'legacy-event', 'legacy-payload', 'legacy-state');
                    INSERT INTO paper_checkpoints VALUES ('legacy', 1, 'legacy-state');
                    """;
                command.ExecuteNonQuery();
            }

            using LocalPaperStore store = LocalPaperStore.Open(path);
            using SqliteConnection migrated = new(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            migrated.Open();
            using SqliteCommand version = migrated.CreateCommand();
            version.CommandText = "PRAGMA user_version;";

            Assert.Equal(2L, Convert.ToInt64(version.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("legacy-state", store.ReadCheckpoint("legacy"));
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    [Fact]
    public void StoreReopensWithSameStateAndRejectsConflictingIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), $"local-paper-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (LocalPaperStore store = LocalPaperStore.Open(path))
            {
                Assert.True(store.Append("stream", 1, "event", "payload", "state"));
                Assert.False(store.Append("stream", 1, "event", "payload", "state"));
            }

            using LocalPaperStore reopened = LocalPaperStore.Open(path);
            Assert.Equal("state", reopened.ReadCheckpoint("stream"));
            Action appendConflict = () => reopened.Append(
                "stream", 1, "event", "different", "state");
            Assert.Throws<InvalidOperationException>(appendConflict);
        }
        finally
        {
            DeleteDatabase(path);
        }
    }

    private static void DeleteDatabase(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
        if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
    }
}
