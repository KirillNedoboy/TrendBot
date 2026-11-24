using Microsoft.Data.Sqlite;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class BybitDemoReservationStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentSignalsCannotReserveMoreThanTheApprovedCapacity()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        using ManualResetEventSlim start = new(false);
        Task<BybitDemoReservationResult>[] tasks = Enumerable.Range(0, 30).Select(index => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return store.TryReserve(Reservation(index));
        }, TestContext.Current.CancellationToken)).ToArray();
        start.Set();
        BybitDemoReservationResult[] results = await Task.WhenAll(tasks);

        Assert.Equal(20, results.Count(static result => result == BybitDemoReservationResult.Reserved));
        Assert.Equal(10, results.Count(static result => result == BybitDemoReservationResult.CapacityReached));
        Assert.Equal(20, store.ReadActive().Count);
    }

    [Fact]
    public void RestartCannotRepeatAClaimOrOpenAnotherEntryWhileItsOutcomeIsUnknown()
    {
        using TemporaryDatabase database = new();
        BybitDemoReservation first = Reservation(1);
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.False(reopened.TryClaimDispatch(first.SignalId, Now.AddSeconds(1)));
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, reopened.TryReserve(Reservation(2)));
        Assert.Equal(BybitDemoReservationState.DispatchClaimed, Assert.Single(reopened.ReadActive()).State);
    }

    [Fact]
    public void SameSignalIsIdempotentButDifferentContentsAndSameSymbolAreRejected()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.Equal(BybitDemoReservationResult.AlreadyReserved, store.TryReserve(first));
        Assert.Equal(BybitDemoReservationResult.Conflict, store.TryReserve(first with { Quantity = 2m }));
        Assert.Equal(BybitDemoReservationResult.SymbolBusy, store.TryReserve(Reservation(2) with { Symbol = first.Symbol }));
    }

    [Fact]
    public void UncertainAcknowledgementBlocksEntriesUntilAConfirmedProtectiveStop()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, store.TryReserve(Reservation(2)));
        Assert.Throws<ArgumentException>(() => store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 101m, Now, true));
        Assert.Throws<ArgumentException>(() => store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, false));
        Assert.Throws<InvalidOperationException>(() => store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true));
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Throws<InvalidOperationException>(() => store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "different-order"));
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(Reservation(2)));
    }

    [Fact]
    public void LateAcknowledgementDoesNotClearAnUnknownOutcome()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RequireReconciliation(first.SignalId);
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");

        BybitDemoReservationSnapshot snapshot = Assert.Single(store.ReadActive());
        Assert.Equal(BybitDemoReservationState.ReconciliationRequired, snapshot.State);
        Assert.Equal("exchange-1", snapshot.ExchangeOrderId);
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, store.TryReserve(Reservation(2)));
        Assert.Throws<InvalidOperationException>(() => store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "different-order"));
    }

    [Fact]
    public void CorruptedProtectedPositionCannotBeReadAsSafe()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);

        using SqliteConnection connection = new($"Data Source={database.Path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE demo_orders SET stop_loss = '101' WHERE signal_id = $signal;";
        command.Parameters.AddWithValue("$signal", first.SignalId);
        Assert.Equal(1, command.ExecuteNonQuery());

        Assert.Throws<InvalidDataException>(() => store.ReadActive());
        Assert.Throws<InvalidDataException>(() => store.TryReserve(Reservation(2)));
        Assert.Throws<InvalidDataException>(() => SqliteBybitDemoReservationStore.Open(database.Path, 20));
    }

    [Fact]
    public void EntryFillsDeduplicateAcrossRestartAndConflictsBlockFurtherEntries()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        BybitDemoEntryFill partial = Fill(first, "fill-1", 0.4m);
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(partial));
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(BybitDemoFillResult.Duplicate, reopened.RecordEntryFill(partial with { ReceivedAt = Now.AddSeconds(1) }));
        Assert.Single(reopened.ReadEntryFills(first.SignalId));
        Assert.Throws<InvalidOperationException>(() => reopened.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true));
        Assert.Equal(BybitDemoFillResult.Conflict, reopened.RecordEntryFill(partial with { Quantity = 0.5m }));
        Assert.Equal(BybitDemoReservationState.ReconciliationRequired, Assert.Single(reopened.ReadActive()).State);
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, reopened.TryReserve(Reservation(2)));
    }

    [Fact]
    public void ExecutionBeforeAcknowledgementIsPersistedButBlocksNewEntries()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        Assert.Equal(BybitDemoFillResult.ReconciliationRequired, store.RecordEntryFill(Fill(first, "fill-1", 0.4m)));
        Assert.Single(store.ReadEntryFills(first.SignalId));
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, store.TryReserve(Reservation(2)));
        Assert.Throws<InvalidOperationException>(() => store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "wrong-order"));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Throws<InvalidOperationException>(() => store.ConfirmProtected(first.SignalId, "exchange-1", 0.4m, 100m, 95m, Now, true));
        Assert.Throws<InvalidOperationException>(() => store.ConfirmProtected(first.SignalId, "exchange-1", 0.4m, 100m, 95m, Now, true, Now.AddTicks(-1)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 0.4m, 100m, 95m, Now, true, Now);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(Reservation(2)));
    }

    [Fact]
    public void UnmatchedActivityCannotBeBypassedByAReconciliationTimestamp()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        Assert.Equal(BybitDemoFillResult.ReconciliationRequired, store.RecordEntryFill(Fill(first, "fill-1", 0.4m)));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.True(store.RecordUnmatchedActivity("manual-order", "ETHUSDT", "order", Now));
        Assert.Throws<InvalidOperationException>(() => store.ConfirmProtected(
            first.SignalId, "exchange-1", 0.4m, 100m, 95m, Now, true, Now));
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, store.TryReserve(Reservation(2)));
    }

    [Fact]
    public void CorruptedFillBlocksRestartAndNewReservations()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE demo_entry_fills SET fill_hash = 'corrupt';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Throws<InvalidDataException>(() => store.ReadActive());
        Assert.Throws<InvalidDataException>(() => store.TryReserve(Reservation(2)));
        Assert.Throws<InvalidDataException>(() => SqliteBybitDemoReservationStore.Open(database.Path, 20));
    }

    [Fact]
    public void VersionOneStoreMigratesWithoutTrustingPreviouslyUnverifiedProtection()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE demo_exit_plans; DROP TABLE demo_entry_fills; DROP TABLE demo_unmatched_activity; DROP TABLE demo_stream_faults; DROP TABLE demo_stop_changes; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(BybitDemoReservationState.ReconciliationRequired, Assert.Single(reopened.ReadActive()).State);
        Assert.Empty(reopened.ReadEntryFills(first.SignalId));
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, reopened.TryReserve(Reservation(2)));
    }

    [Fact]
    public void UnknownExchangeActivityBlocksClaimsAndPersistsAcrossRestart()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation pending = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(pending));
        Assert.True(store.RecordUnmatchedActivity("external-execution", "BTCUSDT", "execution", Now));
        Assert.False(store.RecordUnmatchedActivity("external-execution", "BTCUSDT", "execution", Now.AddSeconds(1)));
        Assert.Throws<InvalidDataException>(() => store.RecordUnmatchedActivity("external-execution", "ETHUSDT", "execution", Now));
        Assert.False(store.TryClaimDispatch(pending.SignalId, Now));
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.False(reopened.TryClaimDispatch(pending.SignalId, Now));
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, reopened.TryReserve(Reservation(2)));
    }

    [Fact]
    public void VersionTwoMigrationPreservesExistingProtectedFills()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE demo_exit_plans; DROP TABLE demo_unmatched_activity; DROP TABLE demo_stream_faults; DROP TABLE demo_stop_changes; PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(BybitDemoReservationState.Protected, Assert.Single(reopened.ReadActive()).State);
        Assert.Single(reopened.ReadEntryFills(first.SignalId));
        Assert.True(reopened.RecordUnmatchedActivity("manual-position", "ETHUSDT", "position", Now));
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, reopened.TryReserve(Reservation(2)));
    }

    [Fact]
    public void VersionThreeMigrationPreservesUnmatchedActivityAndAddsFaultBarrier()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.True(store.RecordUnmatchedActivity("external-order", "BTCUSDT", "order", Now));
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE demo_exit_plans; DROP TABLE demo_stream_faults; DROP TABLE demo_stop_changes; PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.True(reopened.RecordStreamFault(new string('a', 64), Now));
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, reopened.TryReserve(Reservation(1)));
    }

    [Fact]
    public void StopClaimSurvivesRestartAndRequiresMatchingExchangeProof()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);
        BybitDemoStopChange change = new("stop-1", first.SignalId, 1m, 95m, 96m, Now.AddSeconds(1));
        Assert.Equal(BybitDemoStopClaimResult.Claimed, store.TryClaimStopChange(change));
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(change, Assert.Single(reopened.ReadPendingStopChanges()));
        Assert.Equal(BybitDemoStopClaimResult.Duplicate, reopened.TryClaimStopChange(change));
        Assert.Equal(BybitDemoStopClaimResult.Conflict, reopened.TryClaimStopChange(change with { NewStop = 97m }));
        reopened.RecordStopAcknowledgement(change.CommandId);
        Assert.Equal(BybitDemoReservationResult.ReconciliationRequired, reopened.TryReserve(Reservation(2)));
        BybitDemoStopProof proof = new(first.Symbol, Direction.Long, 1m, 100m, 96m, "stop-order",
            first.ClientOrderId, Direction.Short, 1m, 96m, 0, "Untriggered", "LastPrice", true, true,
            Now.AddSeconds(2), Now.AddSeconds(2));
        Assert.Throws<ArgumentException>(() => reopened.ConfirmStopChange(change.CommandId, proof with { StopTrigger = 97m }));
        Assert.Throws<ArgumentException>(() => reopened.ConfirmStopChange(change.CommandId, proof with { ReconciledAt = Now }));
        reopened.ConfirmStopChange(change.CommandId, proof);
        reopened.ConfirmStopChange(change.CommandId, proof);
        Assert.Empty(reopened.ReadPendingStopChanges());
        Assert.Equal(96m, Assert.Single(reopened.ReadActive()).StopLoss);
        Assert.Equal(BybitDemoReservationResult.Reserved, reopened.TryReserve(Reservation(2)));
    }

    [Fact]
    public void VersionFourMigrationKeepsProtectedPositions()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE demo_exit_plans; DROP TABLE demo_stop_changes; PRAGMA user_version = 4;";
            command.ExecuteNonQuery();
        }
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(BybitDemoReservationState.Protected, Assert.Single(reopened.ReadActive()).State);
        Assert.Equal(BybitDemoStopClaimResult.Claimed,
            reopened.TryClaimStopChange(new BybitDemoStopChange("stop-1", first.SignalId, 1m, 95m, 96m, Now)));
    }

    [Fact]
    public void ExitPlanRequiresProtectedPositionAndSurvivesRestartWithoutConflictingRewrite()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        BybitDemoStoredExitPlan plan = new(first.SignalId, 1m, 100m, 95m, 105m, 0.5m,
            110m, 0.5m, 100m, 0.1m, 0.1m, 0.1m);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        Assert.Throws<InvalidOperationException>(() => store.RecordExitPlan(plan));
        Assert.True(store.TryClaimDispatch(first.SignalId, Now));
        store.RecordAcknowledgement(first.SignalId, first.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(first, "fill-1", 1m)));
        store.ConfirmProtected(first.SignalId, "exchange-1", 1m, 100m, 95m, Now, true);
        Assert.Throws<ArgumentException>(() => store.RecordExitPlan(plan with { FirstQuantity = 0.4m, SecondQuantity = 0.6m }));
        Assert.True(store.RecordExitPlan(plan));
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(plan, reopened.ReadExitPlan(first.SignalId));
        Assert.False(reopened.RecordExitPlan(plan));
        Assert.Throws<InvalidOperationException>(() => reopened.RecordExitPlan(plan with { FirstTarget = 106m }));
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE demo_exit_plans SET plan_hash = 'corrupt';";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Assert.Throws<InvalidDataException>(() => reopened.ReadActive());
        Assert.Throws<InvalidDataException>(() => SqliteBybitDemoReservationStore.Open(database.Path, 20));
    }

    [Fact]
    public void DonchianShortPersistsOneTargetAndRejectsFlowStyleSplits()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation signal = Reservation(1) with
        {
            Family = SetupType.DonchianFlowBreakout,
            Timeframe = "1h",
            Direction = Direction.Short,
            InitialStop = 105m
        };
        BybitDemoStoredExitPlan plan = new(signal.SignalId, 1m, 100m, 105m, 90m, 1m,
            null, 0m, null, 0.1m, 0.1m, 0.1m);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(signal));
        Assert.True(store.TryClaimDispatch(signal.SignalId, Now));
        store.RecordAcknowledgement(signal.SignalId, signal.ClientOrderId, "exchange-1");
        Assert.Equal(BybitDemoFillResult.Recorded, store.RecordEntryFill(Fill(signal, "fill-1", 1m)));
        store.ConfirmProtected(signal.SignalId, "exchange-1", 1m, 100m, 105m, Now, true);
        Assert.Throws<ArgumentException>(() => store.RecordExitPlan(plan with
        {
            FirstQuantity = 0.5m,
            SecondQuantity = 0.5m,
            SecondTarget = 85m,
            StopAfterFirstTarget = 100m
        }));
        Assert.True(store.RecordExitPlan(plan));
        Assert.Equal(plan, SqliteBybitDemoReservationStore.Open(database.Path, 20).ReadExitPlan(signal.SignalId));
    }

    [Fact]
    public void VersionFiveStoreMigratesWithoutLosingReservations()
    {
        using TemporaryDatabase database = new();
        SqliteBybitDemoReservationStore store = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        BybitDemoReservation first = Reservation(1);
        Assert.Equal(BybitDemoReservationResult.Reserved, store.TryReserve(first));
        using (SqliteConnection connection = new($"Data Source={database.Path};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE demo_exit_plans; PRAGMA user_version = 5;";
            command.ExecuteNonQuery();
        }
        SqliteBybitDemoReservationStore reopened = SqliteBybitDemoReservationStore.Open(database.Path, 20);
        Assert.Equal(first, Assert.Single(reopened.ReadActive()).Reservation);
        Assert.Null(reopened.ReadExitPlan(first.SignalId));
    }

    [Fact]
    public void NonDemoOrExistingUnrelatedDatabaseCannotBeOverwritten()
    {
        using TemporaryDatabase database = new();
        System.IO.File.WriteAllText(database.Path, "unrelated data");
        Assert.ThrowsAny<Exception>(() => SqliteBybitDemoReservationStore.Open(database.Path, 20));
        Assert.Equal("unrelated data", System.IO.File.ReadAllText(database.Path));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqliteBybitDemoReservationStore.Open(
            System.IO.Path.Combine(database.DirectoryPath, "other.sqlite"), 21));
    }

    private static BybitDemoReservation Reservation(int index) => new(
        index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
        new string('a', 64), $"COIN{index}USDT", $"demo-{index}",
        SetupType.FlowAtrRecovery, "15m", Direction.Long, 1m, 100m, 95m);

    private static BybitDemoEntryFill Fill(BybitDemoReservation reservation, string id, decimal quantity) =>
        new(reservation.SignalId, id, reservation.ClientOrderId, "exchange-1", reservation.Symbol,
            reservation.Direction, quantity, 100m, 0.01m, 1, Now, Now);

    private sealed class TemporaryDatabase : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bybit-demo-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(DirectoryPath, "state.sqlite");

        public TemporaryDatabase() => System.IO.Directory.CreateDirectory(DirectoryPath);
        public void Dispose() => System.IO.Directory.Delete(DirectoryPath, recursive: true);
    }
}
