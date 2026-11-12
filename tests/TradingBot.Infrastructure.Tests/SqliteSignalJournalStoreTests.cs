using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.Infrastructure.Tests;

public sealed class SqliteSignalJournalStoreTests
{
    private const string StreamId = "BINANCE_USDM:BTCUSDT";
    private static readonly string InputHash = new('1', 64);
    private static readonly UtcTimestamp SetupTime = At(1);
    private static readonly UtcTimestamp DecisionTime = At(15);
    private static readonly UtcTimestamp ContextTime = At(-60);

    private static readonly string[] ExpectedSignalJournalTables =
    [
        "signal_journal_checkpoints",
        "signal_journal_diagnostics",
        "signal_journal_inputs",
        "signal_journal_outbox",
        "signal_journal_scenario_events",
        "signal_journal_signals"
    ];

    [Fact]
    public void CommitReopenAndExactRetryPreserveSignalScenarioCursorAndOutbox()
    {
        using TemporaryDatabase database = new();
        ScenarioFixture fixture = new();
        SignalJournalCommit first = fixture.FirstCommit();

        using (SqliteSignalJournalStore store = SqliteSignalJournalStore.Open(database.Path))
        {
            Assert.True(store.Append(first));
        }

        using SqliteSignalJournalStore reopened = SqliteSignalJournalStore.Open(database.Path);
        Assert.False(reopened.Append(first));
        SignalJournalSnapshot snapshot = reopened.Read(StreamId);

        Assert.Equal(1, snapshot.Checkpoint!.InputSequence);
        Assert.Equal(first.InputEventId, snapshot.Checkpoint.InputEventId);
        Assert.Equal(first.ScenarioSnapshot, snapshot.ScenarioSnapshot);
        Assert.Equal(first.SignalRecord, Assert.Single(snapshot.Signals).Signal);
        Assert.Equal(first.EvidenceRecord, snapshot.Signals[0].Evidence);
        Assert.Equal(first.NewScenarioEvents, snapshot.ScenarioEvents);
        Assert.Single(snapshot.Outbox);
        Assert.Equal(snapshot.ScenarioEvents[0].EventId, snapshot.Outbox[0].OutboxId);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(1, CountTable(database.Path, "signal_journal_inputs"));
        Assert.Equal(1, CountTable(database.Path, "signal_journal_signals"));
        Assert.Equal(1, CountTable(database.Path, "signal_journal_scenario_events"));
        Assert.Equal(1, CountTable(database.Path, "signal_journal_outbox"));
    }

    [Fact]
    public void LateInputAddsDiagnosticWithoutChangingCommittedSignalOrScenario()
    {
        using TemporaryDatabase database = new();
        ScenarioFixture fixture = new();
        SignalJournalCommit first = fixture.FirstCommit();
        using SqliteSignalJournalStore store = SqliteSignalJournalStore.Open(database.Path);
        Assert.True(store.Append(first));

        SignalScenarioInput late = fixture.Confirm("late-input", minute: 14,
            stableSequence: 3, availabilityMinute: 16);
        SignalScenarioProjectionTransition transition = fixture.Engine.Apply(
            first.ScenarioSnapshot, late);
        Assert.Equal(SignalScenarioTransitionStatus.Late, transition.Status);
        Assert.Equal(first.ScenarioSnapshot, transition.NextSnapshot);
        Assert.Empty(transition.EmittedEvents);
        SignalJournalDiagnostic diagnostic = SignalJournalDiagnostic.Create(StreamId,
            late.EventId, new string('2', 64), "late-input", ["event-time-before-cursor"]);
        SignalJournalCommit lateCommit = ScenarioFixture.Commit(late, transition,
            expectedPreviousSequence: 1, inputSequence: 2,
            inputHash: new string('2', 64), processedScenarioInputHash: null,
            signalRecord: null, evidenceRecord: null, diagnostic);

        Assert.True(store.Append(lateCommit));
        SignalJournalSnapshot snapshot = store.Read(StreamId);

        Assert.Equal(2, snapshot.Checkpoint!.InputSequence);
        Assert.Equal(first.ScenarioSnapshot, snapshot.ScenarioSnapshot);
        Assert.Equal(first.SignalRecord, Assert.Single(snapshot.Signals).Signal);
        Assert.Equal(first.NewScenarioEvents, snapshot.ScenarioEvents);
        Assert.Single(snapshot.Outbox);
        Assert.Equal(diagnostic, Assert.Single(snapshot.Diagnostics));
    }

    [Fact]
    public void SameInputIdentityWithDifferentPayloadHashFailsWithoutAdvancingCursor()
    {
        using TemporaryDatabase database = new();
        ScenarioFixture fixture = new();
        SignalJournalCommit first = fixture.FirstCommit();
        using SqliteSignalJournalStore store = SqliteSignalJournalStore.Open(database.Path);
        Assert.True(store.Append(first));

        SignalJournalCommit conflicting = ScenarioFixture.Commit(fixture.FirstInput,
            fixture.FirstTransition, expectedPreviousSequence: 0, inputSequence: 1,
            inputHash: new string('2', 64),
            processedScenarioInputHash: fixture.FirstInput.InputHash,
            signalRecord: fixture.Signal, evidenceRecord: fixture.Evidence);

        Assert.Throws<InvalidOperationException>(() => store.Append(conflicting));
        SignalJournalSnapshot snapshot = store.Read(StreamId);
        Assert.Equal(1, snapshot.Checkpoint!.InputSequence);
        Assert.Equal(first.SignalRecord, Assert.Single(snapshot.Signals).Signal);
        Assert.Single(snapshot.ScenarioEvents);
        Assert.Single(snapshot.Outbox);
    }

    [Fact]
    public void StableSignalIdentityRejectsChangedRecordHashAtomically()
    {
        using TemporaryDatabase database = new();
        ScenarioFixture fixture = new();
        SignalJournalCommit first = fixture.FirstCommit();
        using SqliteSignalJournalStore store = SqliteSignalJournalStore.Open(database.Path);
        Assert.True(store.Append(first));

        SignalScenarioInput continued = fixture.Confirm("continued-input", minute: 16,
            stableSequence: 3);
        SignalScenarioProjectionTransition transition = fixture.Engine.Apply(
            first.ScenarioSnapshot, continued);
        Assert.Equal(SignalScenarioTransitionStatus.Continued, transition.Status);
        SignalEvidenceRecord changedEvidence = fixture.CreateEvidence("changed-evidence");
        SignalRecord changedSignal = fixture.CreateSignal(changedEvidence);
        Assert.Equal(first.SignalRecord!.Identity.SignalId, changedSignal.Identity.SignalId);
        Assert.NotEqual(first.SignalRecord.RecordHash, changedSignal.RecordHash);
        SignalJournalCommit conflicting = ScenarioFixture.Commit(continued, transition,
            expectedPreviousSequence: 1, inputSequence: 2,
            inputHash: new string('3', 64), processedScenarioInputHash: continued.InputHash,
            signalRecord: changedSignal, evidenceRecord: changedEvidence);

        Assert.Throws<InvalidOperationException>(() => store.Append(conflicting));
        SignalJournalSnapshot snapshot = store.Read(StreamId);
        Assert.Equal(1, snapshot.Checkpoint!.InputSequence);
        Assert.Equal(first.ScenarioSnapshot, snapshot.ScenarioSnapshot);
        Assert.Single(snapshot.Signals);
        Assert.Single(snapshot.ScenarioEvents);
        Assert.Single(snapshot.Outbox);
    }

    [Fact]
    public void CheckpointFailureRollsBackEveryProjectionRowAndAllowsRetry()
    {
        using TemporaryDatabase database = new();
        ScenarioFixture fixture = new();
        SignalJournalCommit first = fixture.FirstCommit(diagnosticCode: "closed-confirmation");
        using SqliteSignalJournalStore store = SqliteSignalJournalStore.Open(database.Path);
        ExecuteNonQuery(database.Path, """
            CREATE TRIGGER fail_signal_checkpoint
            BEFORE INSERT ON signal_journal_checkpoints
            BEGIN
                SELECT RAISE(ABORT, 'injected signal checkpoint failure');
            END;
            """);

        Assert.Throws<SqliteException>(() => store.Append(first));
        SignalJournalSnapshot failed = store.Read(StreamId);
        Assert.Null(failed.Checkpoint);
        Assert.Null(failed.ScenarioSnapshot);
        Assert.Empty(failed.Signals);
        Assert.Empty(failed.ScenarioEvents);
        Assert.Empty(failed.Diagnostics);
        Assert.Empty(failed.Outbox);

        ExecuteNonQuery(database.Path, "DROP TRIGGER fail_signal_checkpoint;");
        Assert.True(store.Append(first));
        SignalJournalSnapshot recovered = store.Read(StreamId);
        Assert.Equal(1, recovered.Checkpoint!.InputSequence);
        Assert.Single(recovered.Signals);
        Assert.Single(recovered.ScenarioEvents);
        Assert.Single(recovered.Diagnostics);
        Assert.Single(recovered.Outbox);
    }

    [Fact]
    public async Task ConcurrentExactCommitsCreateOneDurableSignalAndOutbox()
    {
        using TemporaryDatabase database = new();
        ScenarioFixture fixture = new();
        SignalJournalCommit commit = fixture.FirstCommit();
        using SqliteSignalJournalStore first = SqliteSignalJournalStore.Open(database.Path);
        using SqliteSignalJournalStore second = SqliteSignalJournalStore.Open(database.Path);
        using Barrier barrier = new(2);

        Task<bool> firstWrite = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return first.Append(commit);
        });
        Task<bool> secondWrite = Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return second.Append(commit);
        });
        bool[] outcomes = await Task.WhenAll(firstWrite, secondWrite);

        Assert.Equal(1, outcomes.Count(static written => written));
        Assert.Equal(1, outcomes.Count(static written => !written));
        SignalJournalSnapshot snapshot = first.Read(StreamId);
        Assert.Single(snapshot.Signals);
        Assert.Single(snapshot.ScenarioEvents);
        Assert.Single(snapshot.Outbox);
    }

    [Fact]
    public void ContinuousAndRestartedProjectionProduceIdenticalJournalState()
    {
        using TemporaryDatabase uninterruptedDatabase = new();
        using TemporaryDatabase restartedDatabase = new();
        ScenarioFixture fixture = new();
        SignalJournalCommit first = fixture.FirstCommit();
        SignalScenarioInput suffixInput = fixture.Confirm("continued-input", minute: 16,
            stableSequence: 3);
        SignalScenarioProjectionTransition continuousSuffix = fixture.Engine.Apply(
            first.ScenarioSnapshot, suffixInput);
        SignalJournalCommit continuousCommit = ScenarioFixture.Commit(suffixInput,
            continuousSuffix, expectedPreviousSequence: 1, inputSequence: 2,
            inputHash: new string('2', 64),
            processedScenarioInputHash: suffixInput.InputHash,
            signalRecord: fixture.Signal, evidenceRecord: fixture.Evidence);

        SignalJournalSnapshot continuous;
        using (SqliteSignalJournalStore store =
            SqliteSignalJournalStore.Open(uninterruptedDatabase.Path))
        {
            Assert.True(store.Append(first));
            Assert.True(store.Append(continuousCommit));
            continuous = store.Read(StreamId);
        }

        using (SqliteSignalJournalStore store =
            SqliteSignalJournalStore.Open(restartedDatabase.Path))
        {
            Assert.True(store.Append(first));
        }

        using SqliteSignalJournalStore reopened =
            SqliteSignalJournalStore.Open(restartedDatabase.Path);
        SignalJournalCurrentState current = reopened.ReadCurrentState(StreamId);
        Assert.Equal(1, current.Checkpoint!.InputSequence);
        SignalScenarioProjectionTransition restartedSuffix = fixture.Engine.Apply(
            current.ScenarioSnapshot!, suffixInput);
        SignalJournalCommit restartedCommit = ScenarioFixture.Commit(suffixInput,
            restartedSuffix, expectedPreviousSequence: current.Checkpoint.InputSequence,
            inputSequence: 2, inputHash: new string('2', 64),
            processedScenarioInputHash: suffixInput.InputHash,
            signalRecord: fixture.Signal, evidenceRecord: fixture.Evidence);
        Assert.True(reopened.Append(restartedCommit));

        SignalJournalSnapshot restarted = reopened.Read(StreamId);
        Assert.Equal(CoreJsonSerializer.Serialize(continuous),
            CoreJsonSerializer.Serialize(restarted));
        Assert.Equal(CoreJsonSerializer.Serialize(continuousSuffix.NextSnapshot),
            CoreJsonSerializer.Serialize(restarted.ScenarioSnapshot));
        Assert.Equal(continuousSuffix.NextSnapshot.Events, restarted.ScenarioEvents);
        Assert.Single(restarted.Signals);
        Assert.Equal(2, restarted.Outbox.Count);
    }

    [Fact]
    public void StandaloneDatabaseContainsOnlySignalJournalTablesAndRejectsSharedSchema()
    {
        using TemporaryDatabase database = new();
        using (SqliteSignalJournalStore.Open(database.Path))
        {
        }

        List<string> tables = ReadTableNames(database.Path);
        Assert.Equal(ExpectedSignalJournalTables, tables);

        using TemporaryDatabase contaminatedDatabase = new();
        using (SqliteSignalJournalStore.Open(contaminatedDatabase.Path))
        {
        }
        ExecuteNonQuery(contaminatedDatabase.Path,
            "CREATE TABLE order_intents (intent_id TEXT NOT NULL);");
        Assert.Throws<InvalidDataException>(() =>
            SqliteSignalJournalStore.Open(contaminatedDatabase.Path));
        Assert.Equal(1L, ReadScalar(contaminatedDatabase.Path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'order_intents';"));

        using TemporaryDatabase sharedDatabase = new();
        ExecuteNonQuery(sharedDatabase.Path, """
            PRAGMA user_version = 12;
            CREATE TABLE order_intents (intent_id TEXT NOT NULL);
            CREATE TABLE portfolios (portfolio_id TEXT NOT NULL);
            INSERT INTO order_intents VALUES ('preserved');
            INSERT INTO portfolios VALUES ('preserved');
            """);
        Assert.Throws<InvalidDataException>(() =>
            SqliteSignalJournalStore.Open(sharedDatabase.Path));
        Assert.Equal(12L, ReadScalar(sharedDatabase.Path, "PRAGMA user_version;"));
        Assert.Equal(1L, ReadScalar(sharedDatabase.Path,
            "SELECT COUNT(*) FROM order_intents WHERE intent_id = 'preserved';"));
        Assert.Equal(1L, ReadScalar(sharedDatabase.Path,
            "SELECT COUNT(*) FROM portfolios WHERE portfolio_id = 'preserved';"));
        Assert.Equal(0L, ReadScalar(sharedDatabase.Path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'signal_journal_%';"));
    }

    [Fact]
    public void ReadOnlyJournalReadsCommittedRowsAndRejectsWritesWithoutCreatingFiles()
    {
        using TemporaryDatabase database = new();
        ScenarioFixture fixture = new();
        using (SqliteSignalJournalStore writable =
            SqliteSignalJournalStore.Open(database.Path))
        {
            Assert.True(writable.Append(fixture.FirstCommit()));
        }

        SignalJournalSnapshot expected;
        using (SqliteSignalJournalStore writable =
            SqliteSignalJournalStore.Open(database.Path))
        {
            expected = writable.Read(StreamId);
        }

        using SqliteSignalJournalStore readOnly =
            SqliteSignalJournalStore.OpenReadOnly(database.Path);
        SignalJournalSnapshot actual = readOnly.Read(StreamId);
        Assert.Equal(expected.Signals, actual.Signals);
        Assert.Equal(expected.ScenarioEvents, actual.ScenarioEvents);
        Assert.Throws<InvalidOperationException>(() =>
            readOnly.Append(fixture.FirstCommit()));

        string missingPath = System.IO.Path.Combine(Path.GetTempPath(),
            $"tradingbot-missing-signals-{Guid.NewGuid():N}.sqlite");
        Assert.Throws<FileNotFoundException>(() =>
            SqliteSignalJournalStore.OpenReadOnly(missingPath));
        Assert.False(File.Exists(missingPath));
    }



    private static long CountTable(string path, string table) =>
        ReadScalar(path, $"SELECT COUNT(*) FROM {table};");

    private static long ReadScalar(string path, string sql)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> ReadTableNames(string path)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> names = [];
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void ExecuteNonQuery(string path, string sql)
    {
        using SqliteConnection connection = OpenRaw(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenRaw(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static UtcTimestamp At(int minute) =>
        new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute));

    private sealed class ScenarioFixture
    {
        private readonly SignalScenarioAnchor _anchor;

        public ScenarioFixture()
        {
            SignalScenarioPolicySet policies = new(
                SignalScenarioPolicySet.CurrentFormatVersion,
                new SignalScenarioExpiryPolicy(
                    SignalScenarioExpiryPolicy.CurrentFormatVersion, "expiry-v1",
                    TimeSpan.FromHours(1)),
                new SignalScenarioRearmPolicy(
                    SignalScenarioRearmPolicy.CurrentFormatVersion, "rearm-v1",
                    TimeSpan.FromMinutes(2), true),
                new SignalScenarioInvalidationPolicy(
                    SignalScenarioInvalidationPolicy.CurrentFormatVersion,
                    "invalidation-v1", true, true));
            Engine = new SignalScenarioProjectionEngine(policies);
            _anchor = SignalScenarioAnchor.Create(new InstrumentId("BTCUSDT"),
                "BINANCE_USDM", "15m", Direction.Long,
                SetupType.BoundaryRejection, "profile-v1",
                "boundary-rejection-v1", "anchor-event", SetupTime, 1);
            FirstInput = Confirm("primary-input", minute: 15, stableSequence: 2);
            FirstTransition = Engine.Apply(Engine.CreateInitialSnapshot(), FirstInput);
            Evidence = CreateEvidence("complete-evidence");
            Signal = CreateSignal(Evidence);
        }

        public SignalScenarioProjectionEngine Engine { get; }

        public SignalScenarioInput FirstInput { get; }

        public SignalScenarioProjectionTransition FirstTransition { get; }

        public SignalEvidenceRecord Evidence { get; }

        public SignalRecord Signal { get; }

        public SignalScenarioInput Confirm(string eventId, int minute,
            long stableSequence, int? availabilityMinute = null) =>
            SignalScenarioInput.Confirm(StreamId, eventId, _anchor, At(minute),
                At(availabilityMinute ?? minute), stableSequence);

        public SignalJournalCommit FirstCommit(string? diagnosticCode = null)
        {
            SignalJournalDiagnostic? diagnostic = diagnosticCode is null ? null
                : SignalJournalDiagnostic.Create(StreamId, FirstInput.EventId, InputHash,
                    diagnosticCode, ["closed-primary-input"]);
            return Commit(FirstInput, FirstTransition, expectedPreviousSequence: 0,
                inputSequence: 1, inputHash: InputHash,
                processedScenarioInputHash: FirstInput.InputHash,
                signalRecord: Signal, evidenceRecord: Evidence, diagnostic);
        }

        public static SignalJournalCommit Commit(SignalScenarioInput input,
            SignalScenarioProjectionTransition transition, long expectedPreviousSequence,
            long inputSequence, string inputHash, string? processedScenarioInputHash,
            SignalRecord? signalRecord, SignalEvidenceRecord? evidenceRecord,
            SignalJournalDiagnostic? diagnostic = null) => new(StreamId,
            expectedPreviousSequence, inputSequence, input.EventId, inputHash,
            input.EventTime, input.AvailabilityTime, input.StableSequence,
            processedScenarioInputHash, transition.NextSnapshot,
            transition.EmittedEvents, signalRecord, evidenceRecord, diagnostic);

        public SignalEvidenceRecord CreateEvidence(string reason) =>
            SignalEvidenceRecord.Create(_anchor.Identity, Provenance(),
                SignalEvidenceState.Complete, [reason]);

        public SignalRecord CreateSignal(SignalEvidenceRecord evidence) =>
            SignalRecord.Create(_anchor.Identity, evidence,
                SignalAssessmentState.ConfirmedOnClosedBar,
                new SignalBarInterval(At(0), DecisionTime), DecisionTime,
                new SignalReferencePrice("binance-public", "native-kline-v1",
                    _anchor.AnchorEventId, SetupTime, new Price(100m)));

        private static SignalProvenance Provenance() => SignalProvenance.Create(
            "profile-v1", new string('a', 64),
            new CausalEventCursor(DecisionTime, 2),
            new CausalEventCursor(ContextTime, 0),
            [new SignalInputReference("binance-public", "native-kline-v1",
                    "anchor-event", new string('c', 64), SignalInputRole.Setup,
                    "15m", SetupTime, SetupTime, 1),
                new SignalInputReference("binance-public", "native-kline-v1",
                    "context-event", new string('d', 64),
                    SignalInputRole.HigherTimeframe, "1h", ContextTime,
                    ContextTime, 0)]);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tradingbot-signals-{Guid.NewGuid():N}.sqlite");

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
