using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteDecisionProjectionStoreTests
{
    [Theory]
    [InlineData(DecisionEngineProfile.Allow, false, DecisionOutcome.Allow)]
    [InlineData(DecisionEngineProfile.Allow, true, DecisionOutcome.Block)]
    [InlineData(DecisionEngineProfile.Shadow, false, DecisionOutcome.Shadow)]
    public void AppendFindReopenPreservesOutcomeAndLifecycleCheckpoint(
        DecisionEngineProfile profile, bool blocked, DecisionOutcome expectedOutcome)
    {
        using TemporaryDatabase database = new();
        DecisionProjectionSnapshot snapshot = DecisionProjectionFixtures.CreateProjection(
            profile, blocked);

        using (SqliteDecisionProjectionStore store = SqliteDecisionProjectionStore.Open(
            database.Path))
        {
            Assert.True(store.Append(snapshot));
            Assert.False(store.Append(snapshot));
            DecisionProjectionSnapshot found = store.Find(snapshot.SnapshotId)!;
            Assert.Equal(expectedOutcome, found.Result.Decision.Outcome);
            Assert.Equal(snapshot.LifecycleSnapshot, found.LifecycleSnapshot);
            Assert.Equal(snapshot.SnapshotId, found.SnapshotId);
        }

        using (SqliteDecisionProjectionStore reopened = SqliteDecisionProjectionStore.Open(
            database.Path))
        {
            DecisionProjectionSnapshot found = reopened.Find(snapshot.IdentityBytes)!;
            Assert.Equal(snapshot, found);
            Assert.Equal(snapshot.Result.Decision, found.Reevaluate().Decision);
        }
    }

    [Fact]
    public void MigrationFromSchemaNinePreservesPriorRowsAndCreatesProjectionTable()
    {
        using TemporaryDatabase database = new();
        DecisionProjectionSnapshot snapshot = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow);
        using (SqliteEventJournal journal = SqliteEventJournal.Open(database.Path))
        {
            Assert.True(journal.Append("stream-before-projection", new CausalMarketEvent(
                new Trade("trade-before-projection", new InstrumentId("BTCUSDT"),
                    Time(0), new Price(100m), new Quantity(1m), TradeSide.Buy),
                Time(0), Time(0), 0)));
        }

        using (SqliteConnection downgrade = OpenRaw(database.Path))
        {
            ExecuteNonQuery(downgrade, "PRAGMA user_version = 9;");
        }

        using (SqliteDecisionProjectionStore store = SqliteDecisionProjectionStore.Open(
            database.Path))
        {
            Assert.True(store.Append(snapshot));
        }

        using SqliteConnection verify = OpenRaw(database.Path);
        Assert.Equal(14L, ExecuteScalar<long>(verify, "PRAGMA user_version;"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM event_journal WHERE stream_id = 'stream-before-projection';"));
        Assert.Equal(1L, ExecuteScalar<long>(verify,
            "SELECT COUNT(*) FROM decision_projections;"));
    }

    [Fact]
    public void ConflictingIdentityIsRejected()
    {
        using TemporaryDatabase database = new();
        DecisionProjectionSnapshot snapshot = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow);
        using SqliteDecisionProjectionStore store = SqliteDecisionProjectionStore.Open(
            database.Path);
        Assert.True(store.Append(snapshot));

        using SqliteConnection connection = OpenRaw(database.Path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE decision_projections SET snapshot_json = $json "
            + "WHERE projection_id = $id;";
        command.Parameters.AddWithValue("$json", CoreJsonSerializer.Serialize(
            DecisionProjectionFixtures.CreateProjection(DecisionEngineProfile.Shadow)));
        command.Parameters.AddWithValue("$id", snapshot.IdentityBytes);
        command.ExecuteNonQuery();

        Assert.Throws<InvalidDataException>(() => { _ = store.Find(snapshot.SnapshotId); });
    }

    [Fact]
    public void ConflictingDecisionIdIsRejected()
    {
        using TemporaryDatabase database = new();
        DecisionProjectionSnapshot first = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Allow);
        DecisionEngineConfiguration shadowConfiguration =
            DecisionProjectionFixtures.CreateDecisionConfiguration(DecisionEngineProfile.Shadow);
        DecisionEngineResult shadowResult = DecisionEngine.Evaluate(new DecisionEngineInput(
            first.DecisionId, first.Result.Inputs.Lifecycle, first.Result.Inputs.LaneAEvidence,
            first.Result.Inputs.LaneEvidence, first.Result.Inputs.Provenance),
            shadowConfiguration);
        DecisionProjectionSnapshot conflicting = DecisionProjectionSnapshot.Create(
            shadowResult, first.LifecycleConfiguration, shadowConfiguration);

        using SqliteDecisionProjectionStore store = SqliteDecisionProjectionStore.Open(
            database.Path);
        Assert.True(store.Append(first));
        Assert.Throws<InvalidOperationException>(() => store.Append(conflicting));
    }

    private static UtcTimestamp Time(int offset) => new(new DateTimeOffset(2026, 1, 1,
        0, 0, 0, TimeSpan.Zero).AddMinutes(offset));

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

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-decision-projection-{Guid.NewGuid():N}.sqlite");

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
