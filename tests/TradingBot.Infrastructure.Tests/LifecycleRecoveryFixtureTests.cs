using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class LifecycleRecoveryFixtureTests
{
    [Fact]
    public void InterruptedCloseReopenRecoveryMatchesContinuousReplay()
    {
        LifecycleRecoveryFixture fixture = LifecycleRecoveryFixtures.Create();
        SetupLifecycleEngine engine = new(fixture.LifecycleConfiguration);
        ImmutableValueList<SetupLifecycleInput> delivery = new(
            fixture.Prefix.Concat(fixture.Tail));
        ImmutableValueList<SetupLifecycleTransition> continuous = engine.Replay(
            fixture.Initial, delivery);

        SetupLifecycleSnapshot checkpoint = fixture.Initial;
        foreach (SetupLifecycleInput input in fixture.Prefix)
        {
            SetupLifecycleTransition transition = engine.Apply(checkpoint, input);
            Assert.Equal(SetupLifecycleTransitionStatus.Applied, transition.Status);
            checkpoint = transition.NextSnapshot;
        }

        Assert.Equal(fixture.Projection.LifecycleSnapshot, checkpoint);
        using TemporaryDatabase database = new();
        using (SqliteDecisionProjectionStore store = SqliteDecisionProjectionStore.Open(
            database.Path))
        {
            Assert.True(store.Append(fixture.Projection));
        }

        DecisionProjectionSnapshot recoveredProjection;
        using (SqliteDecisionProjectionStore reopened = SqliteDecisionProjectionStore.Open(
            database.Path))
        {
            recoveredProjection = reopened.Find(fixture.Projection.SnapshotId)!;
        }

        SetupLifecycleSnapshot recovered = recoveredProjection.LifecycleSnapshot;
        for (int index = fixture.Prefix.Count; index < delivery.Count; index++)
        {
            SetupLifecycleTransition actual = engine.Apply(recovered, delivery[index]);
            SetupLifecycleTransition expected = continuous[index];
            Assert.Equal(expected.Status, actual.Status);
            Assert.Equal(CoreJsonSerializer.Serialize(expected),
                CoreJsonSerializer.Serialize(actual));
            DecisionEngineResult expectedDecision = EvaluateDecision(fixture,
                expected.NextSnapshot);
            DecisionEngineResult actualDecision = EvaluateDecision(fixture,
                actual.NextSnapshot);
            Assert.Equal(CoreJsonSerializer.Serialize(expectedDecision),
                CoreJsonSerializer.Serialize(actualDecision));
            Assert.Equal(expectedDecision.Decision, actualDecision.Decision);
            Assert.Equal(expectedDecision.Reasons, actualDecision.Reasons);
            recovered = actual.NextSnapshot;
        }

        SetupLifecycleSnapshot expectedFinal = continuous[^1].NextSnapshot;
        Assert.Equal(CoreJsonSerializer.Serialize(expectedFinal),
            CoreJsonSerializer.Serialize(recovered));
        Assert.Equal(expectedFinal.Identity, recovered.Identity);
        Assert.Equal(expectedFinal.State, recovered.State);
        Assert.Equal(expectedFinal.ProcessedCursor, recovered.ProcessedCursor);
        Assert.Equal(expectedFinal.ProcessedAvailabilityTime,
            recovered.ProcessedAvailabilityTime);
        Assert.Equal(expectedFinal.ProcessedEvents, recovered.ProcessedEvents);
        Assert.Equal(expectedFinal.RecoveryRequired, recovered.RecoveryRequired);
        DecisionEngineResult finalDecision = EvaluateDecision(fixture, expectedFinal);
        Assert.Equal(fixture.Projection.Result.Decision, finalDecision.Decision);
        Assert.Equal(fixture.Projection.Result.Reasons, finalDecision.Reasons);
        Assert.Equal(SetupLifecycleTransitionStatus.Duplicate, continuous[fixture.Prefix.Count].Status);
        Assert.Equal(SetupLifecycleTransitionStatus.Late, continuous[fixture.Prefix.Count + 1].Status);
        Assert.Equal(SetupLifecycleTransitionStatus.IdentityConflict,
            continuous[fixture.Prefix.Count + 2].Status);
        Assert.Equal(SetupLifecycleTransitionStatus.RecoveryApplied,
            continuous[fixture.Prefix.Count + 3].Status);
        Assert.Equal(fixture.Projection.LifecycleSnapshot,
            continuous[^1].NextSnapshot);
    }

    private static DecisionEngineResult EvaluateDecision(LifecycleRecoveryFixture fixture,
        SetupLifecycleSnapshot lifecycle) => DecisionEngine.Evaluate(new DecisionEngineInput(
            fixture.Projection.DecisionId, lifecycle, fixture.Evidence,
            fixture.Projection.Result.Inputs.LaneEvidence,
            fixture.Projection.Result.Inputs.Provenance), fixture.DecisionConfiguration);

    [Fact]
    public void EqualTimestampDeliveryAndWriteFailureKeepCheckpointDeterministic()
    {
        LifecycleRecoveryFixture fixture = LifecycleRecoveryFixtures.Create();
        Assert.All(fixture.Prefix, input => Assert.Equal(
            fixture.Evidence.Cursor.AsOf, input.EventTime));
        Assert.Equal(fixture.Prefix[2].StableSequence, fixture.Prefix[3].StableSequence);
        Assert.Equal(fixture.Prefix[3].StableSequence, fixture.Prefix[4].StableSequence);

        using TemporaryDatabase database = new();
        using SqliteDecisionProjectionStore store = SqliteDecisionProjectionStore.Open(
            database.Path, commandTimeoutSeconds: 1);
        using SqliteConnection blocker = OpenRaw(database.Path);
        ExecuteNonQuery(blocker, "BEGIN IMMEDIATE;");

        Assert.Throws<SqliteException>(() => store.Append(fixture.Projection));
        ExecuteNonQuery(blocker, "ROLLBACK;");
        Assert.Null(store.Find(fixture.Projection.SnapshotId));
        Assert.True(store.Append(fixture.Projection));
        Assert.Equal(fixture.Projection,
            store.Find(fixture.Projection.IdentityBytes));
    }

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

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-lifecycle-recovery-{Guid.NewGuid():N}.sqlite");

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
