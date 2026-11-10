using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteNativeBarInputLedgerTests
{
    [Fact]
    public void ReopenPreservesSequenceCheckpointDuplicateAndCorrectionReceipts()
    {
        using TemporaryDatabase database = new();
        NativeBarInputLedgerReceipt accepted = Receipt(1, "event-1", 'a',
            NativeBarInputReceiptStatus.Accepted);
        NativeBarInputLedgerReceipt duplicate = Receipt(2, "event-1", 'a',
            NativeBarInputReceiptStatus.Duplicate);
        NativeBarInputLedgerReceipt correction = Receipt(3, "event-1", 'b',
            NativeBarInputReceiptStatus.Correction) with
        {
            DiagnosticCode = "revision-conflict",
            DiagnosticMessage = "Changed content for an accepted identity."
        };

        using (SqliteNativeBarInputLedger ledger = SqliteNativeBarInputLedger.Open(database.Path))
        {
            Assert.True(ledger.Append("BTCUSDT", accepted));
            Assert.False(ledger.Append("BTCUSDT", accepted));
            Assert.Throws<InvalidOperationException>(() => ledger.Append("BTCUSDT",
                correction));
            Assert.Throws<InvalidOperationException>(() => ledger.Append("BTCUSDT",
                accepted with { PayloadJson = "{\"changed\":true}" }));
            Assert.True(ledger.Append("BTCUSDT", duplicate));
            Assert.True(ledger.Append("BTCUSDT", correction));
        }

        using SqliteNativeBarInputLedger reopened = SqliteNativeBarInputLedger.Open(database.Path);
        Assert.False(reopened.Append("BTCUSDT", accepted));
        NativeBarInputLedgerSnapshot snapshot = reopened.Read("BTCUSDT");

        Assert.Equal(3, snapshot.Checkpoint.LastIntakeSequence);
        Assert.Equal(new[] { accepted, duplicate, correction }, snapshot.Receipts);
        Assert.Equal("event-1", snapshot.Checkpoint.LastTriggerEventId);
        Assert.Equal(new UtcTimestamp(new DateTimeOffset(2026, 1, 1, 0, 0, 0,
            TimeSpan.Zero)), snapshot.Checkpoint.LastTriggerEventTime);
    }

    [Fact]
    public void ReceiptAndCheckpointRollBackTogetherWhenCheckpointWriteFails()
    {
        using TemporaryDatabase database = new();
        using SqliteNativeBarInputLedger ledger = SqliteNativeBarInputLedger.Open(database.Path);
        using (SqliteConnection connection = OpenRaw(database.Path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_checkpoint_insert
                BEFORE INSERT ON native_bar_input_checkpoints
                BEGIN
                    SELECT RAISE(ABORT, 'injected checkpoint write failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        NativeBarInputLedgerReceipt receipt = Receipt(1, "event-1", 'a',
            NativeBarInputReceiptStatus.Accepted);
        Assert.Throws<SqliteException>(() => ledger.Append("BTCUSDT", receipt));
        NativeBarInputLedgerSnapshot failed = ledger.Read("BTCUSDT");
        Assert.Equal(0, failed.Checkpoint.LastIntakeSequence);
        Assert.Empty(failed.Receipts);

        using (SqliteConnection connection = OpenRaw(database.Path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER fail_checkpoint_insert;";
            command.ExecuteNonQuery();
        }

        Assert.True(ledger.Append("BTCUSDT", receipt));
        Assert.Equal(receipt, Assert.Single(ledger.Read("BTCUSDT").Receipts));
    }

    [Fact]
    public void ExistingTradingDatabaseIsRejectedWithoutSchemaMutation()
    {
        using TemporaryDatabase database = new();
        using (SqliteConnection connection = OpenRaw(database.Path))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA user_version = 12;
                CREATE TABLE orders (order_id TEXT NOT NULL);
                INSERT INTO orders (order_id) VALUES ('existing-row');
                """;
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() =>
            SqliteNativeBarInputLedger.Open(database.Path));

        using SqliteConnection unchanged = OpenRaw(database.Path);
        using SqliteCommand versionCommand = unchanged.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.Equal(12L, versionCommand.ExecuteScalar());
        using SqliteCommand rowsCommand = unchanged.CreateCommand();
        rowsCommand.CommandText = "SELECT order_id FROM orders;";
        Assert.Equal("existing-row", rowsCommand.ExecuteScalar());
        using SqliteCommand ledgerTablesCommand = unchanged.CreateCommand();
        ledgerTablesCommand.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name LIKE 'native_bar_input_%';
            """;
        Assert.Equal(0L, ledgerTablesCommand.ExecuteScalar());
    }

    private static NativeBarInputLedgerReceipt Receipt(long sequence, string eventId,
        char revision, NativeBarInputReceiptStatus status) => new(sequence, eventId,
        new string(revision, 64), status, "{}", null, null,
        status == NativeBarInputReceiptStatus.Accepted ? eventId : null,
        status == NativeBarInputReceiptStatus.Accepted
            ? new UtcTimestamp(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) : null);

    private static SqliteConnection OpenRaw(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-native-bars-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }
}
