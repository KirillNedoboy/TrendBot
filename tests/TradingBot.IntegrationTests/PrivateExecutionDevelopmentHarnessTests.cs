using System.Reflection;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class PrivateExecutionDevelopmentHarnessTests
{
    private static readonly InstrumentId Instrument = new("BTCUSDT");

    [Fact]
    public void HarnessIsProgrammaticAndCannotReceivePrivateTransportOrCredentials()
    {
        Type harnessType = typeof(Bootstrap).Assembly.GetType(
            "TradingBot.App.PrivateExecutionDevelopmentHarness")
            ?? throw new InvalidOperationException(
                "The development execution harness contract is not implemented.");

        Type[] forbiddenTypes =
        [
            typeof(HttpClient),
            typeof(Uri),
            typeof(TradingBot.Exchange.Binance.IBinancePrivateRestClient)
        ];
        IEnumerable<Type> surfaceTypes = harnessType.GetConstructors()
            .SelectMany(constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType))
            .Concat(harnessType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(field => field.FieldType));

        Assert.NotEmpty(harnessType.GetConstructors());
        Assert.True(harnessType.IsSealed, "The harness must not be replaceable by a live adapter.");
        Assert.DoesNotContain(surfaceTypes, type => forbiddenTypes.Contains(type));
        Assert.DoesNotContain(surfaceTypes, type =>
            type.FullName?.Contains("BinancePrivateWebSocket", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DevelopmentDispatchClaimIsSingleUseAndSurvivesRestart()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("harness-claim", "harness-client");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        PortfolioId portfolioId = new("harness-portfolio");
        ExecutionDispatchAttempt attempt;
        using (SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
                   database.Path))
        {
            Assert.Equal(ReconciliationStatus.Acknowledged,
                store.AcknowledgeReconciliation(portfolioId, 0).Status);
            ExecutionDispatchClaimResult claim = store.TryClaimDispatch(portfolioId, intent, 0,
                Time(1));
            attempt = claim.Attempt ?? throw new InvalidOperationException(
                "The fixture did not acquire a dispatch attempt.");

            bool first = InvokeDevelopmentClaim(store, attempt);
            bool duplicate = InvokeDevelopmentClaim(store, attempt);
            Assert.True(first);
            Assert.False(duplicate);
        }

        using SqlitePortfolioOperationalStore reopened = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(14L, reopened.ReadSchemaVersionForTests());
        Assert.False(InvokeDevelopmentClaim(reopened, attempt));
        Assert.Single(ReadDevelopmentClaims(reopened, intent.IntentId));
        reopened.Dispose();

        using (SqliteConnection downgrade = new($"Data Source={database.Path};Mode=ReadWrite"))
        {
            downgrade.Open();
            using SqliteCommand command = downgrade.CreateCommand();
            command.CommandText =
                "DROP TABLE development_dispatch_claims; DROP TABLE protective_dispatch_claims; "
                + "PRAGMA user_version = 12;";
            command.ExecuteNonQuery();
        }

        using SqlitePortfolioOperationalStore migrated = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(14L, migrated.ReadSchemaVersionForTests());
        Assert.Empty(ReadDevelopmentClaims(migrated, intent.IntentId));
        Assert.True(InvokeDevelopmentClaim(migrated, attempt));
    }

    [Fact]
    public void HarnessDispatchesScriptedObservationOnlyAfterDurableClaim()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("harness-dispatch", "harness-dispatch-client");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        PortfolioId portfolioId = new("harness-dispatch-portfolio");
        ExecutionObservation observation = ExecutionObservation.Accepted(
            "harness-receipt", intent.IntentId, "synthetic-order");
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);

        object harness = CreateHarness(store, portfolioId, [observation]);
        ExecutionTransitionResult first = InvokeHarnessDispatch(harness, intent, 0, Time(1));
        Assert.Equal(ExecutionApplyDisposition.Applied, first.Disposition);
        Assert.Equal(ExecutionLifecycleState.Accepted, first.State.State);
        Assert.Equal(1, ReadHarnessDispatchCount(harness));
        Assert.Single(ReadDevelopmentClaims(store, intent.IntentId));

        object reopenedHarness = CreateHarness(store, portfolioId, [observation]);
        ExecutionTransitionResult duplicate = InvokeHarnessDispatch(reopenedHarness, intent, 0,
            Time(2));
        Assert.Equal(ExecutionApplyDisposition.Blocked, duplicate.Disposition);
        Assert.Equal(0, ReadHarnessDispatchCount(reopenedHarness));
    }

    [Fact]
    public void HarnessReconciliationUsesOnlyScriptedEvidence()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("harness-reconcile", "harness-reconcile-client");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        PortfolioId portfolioId = new("harness-reconcile-portfolio");
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        Assert.Equal(ExecutionDispatchClaimStatus.Claimed,
            store.TryClaimDispatch(portfolioId, intent, 0, Time(1)).Status);

        ExecutionRecoveryEvidence evidence = new(intent.IntentId, 0,
            ExecutionRecoveryEvidenceStatus.Fresh, true, true, true, true, true,
            new ImmutableValueList<ExecutionObservation>(Array.Empty<ExecutionObservation>()),
            new ImmutableValueList<string>(Array.Empty<string>()),
            new ImmutableValueList<string>(Array.Empty<string>()),
            new ImmutableValueList<string>(Array.Empty<string>()));
        object harness = CreateHarness(store, portfolioId,
            Array.Empty<ExecutionObservation>(), [evidence]);
        ExecutionRecoveryResult result = InvokeHarnessReconcile(harness, intent.IntentId, 0);

        Assert.Equal(ExecutionRecoveryDisposition.Blocked, result.Disposition);
        Assert.Contains("no-order", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HarnessRejectsNonDurableIntentBeforeScriptedDispatch()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("harness-no-intent-portfolio");
        OrderIntent intent = CreateIntent("harness-no-intent", "harness-no-intent-client");
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        object harness = CreateHarness(store, portfolioId,
            [ExecutionObservation.Accepted("unused", intent.IntentId, "synthetic")]);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeHarnessDispatch(harness, intent, 0, Time(1)));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(0, ReadHarnessDispatchCount(harness));
    }

    [Fact]
    public void HarnessRejectsNonDurableReservationBeforeScriptedDispatch()
    {
        using TemporaryDatabase database = new();
        PortfolioId portfolioId = new("harness-no-reservation-portfolio");
        OrderIntent intent = CreateIntent("harness-no-reservation", "harness-no-reservation-client");
        using SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path);
        Assert.True(journal.Append(intent));
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        object harness = CreateHarness(store, portfolioId,
            [ExecutionObservation.Accepted("unused-reservation", intent.IntentId, "synthetic")]);
        PortfolioReservation reservation = new("missing-reservation", portfolioId,
            intent.IntentId, new Money(1m, "USDT"), new Money(100m, "USDT"), 1);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeHarnessDispatch(harness, intent, reservation, 0, Time(1)));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(0, ReadHarnessDispatchCount(harness));
    }

    [Fact]
    public void ForgedDevelopmentClaimFailsClosedBeforeAnyScriptedDispatch()
    {
        using TemporaryDatabase database = new();
        OrderIntent intent = CreateIntent("harness-forged", "harness-forged-client");
        using (SqliteOrderIntentJournal journal = SqliteOrderIntentJournal.Open(database.Path))
        {
            Assert.True(journal.Append(intent));
        }

        PortfolioId portfolioId = new("harness-forged-portfolio");
        using SqlitePortfolioOperationalStore store = SqlitePortfolioOperationalStore.Open(
            database.Path);
        Assert.Equal(ReconciliationStatus.Acknowledged,
            store.AcknowledgeReconciliation(portfolioId, 0).Status);
        ExecutionDispatchClaimResult claim = store.TryClaimDispatch(portfolioId, intent, 0,
            Time(1));
        ExecutionDispatchAttempt actual = claim.Attempt
            ?? throw new InvalidOperationException("The fixture did not acquire a claim.");
        ExecutionDispatchAttempt forged = new(actual.AttemptId, actual.IntentId,
            actual.Attempt + 1, actual.ReconciliationRevision, actual.ClaimedAt);

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            InvokeDevelopmentClaim(store, forged));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Empty(ReadDevelopmentClaims(store, intent.IntentId));
    }

    private static bool InvokeDevelopmentClaim(SqlitePortfolioOperationalStore store,
        ExecutionDispatchAttempt attempt)
    {
        MethodInfo method = typeof(SqlitePortfolioOperationalStore).GetMethod(
            "TryClaimDevelopmentDispatch", [typeof(ExecutionDispatchAttempt)])
            ?? throw new InvalidOperationException(
                "The durable development claim API is not implemented.");
        return (bool)(method.Invoke(store, [attempt])
            ?? throw new InvalidOperationException("The development claim returned no result."));
    }

    private static IReadOnlyList<ExecutionDispatchAttempt> ReadDevelopmentClaims(
        SqlitePortfolioOperationalStore store, string intentId)
    {
        MethodInfo method = typeof(SqlitePortfolioOperationalStore).GetMethod(
            "ReadDevelopmentDispatchClaims", [typeof(string)])
            ?? throw new InvalidOperationException(
                "The durable development claim read API is not implemented.");
        return (IReadOnlyList<ExecutionDispatchAttempt>)(method.Invoke(store, [intentId])
            ?? throw new InvalidOperationException("The development claims read returned no result."));
    }

    private static object CreateHarness(SqlitePortfolioOperationalStore store,
        PortfolioId portfolioId, IReadOnlyList<ExecutionObservation> observations,
        IReadOnlyList<ExecutionRecoveryEvidence>? evidence = null)
    {
        Type harnessType = typeof(Bootstrap).Assembly.GetType(
            "TradingBot.App.PrivateExecutionDevelopmentHarness")
            ?? throw new InvalidOperationException(
                "The development execution harness contract is not implemented.");
        ConstructorInfo constructor = harnessType.GetConstructor(
            [typeof(SqlitePortfolioOperationalStore), typeof(PortfolioId),
                typeof(IReadOnlyList<ExecutionObservation>),
                typeof(IReadOnlyList<ExecutionRecoveryEvidence>)])
            ?? throw new InvalidOperationException(
                "The scripted development harness constructor is not implemented.");
        return constructor.Invoke([store, portfolioId, observations, evidence ?? []]);
    }

    private static ExecutionTransitionResult InvokeHarnessDispatch(object harness,
        OrderIntent intent, long revision, UtcTimestamp claimedAt)
    {
        MethodInfo method = harness.GetType().GetMethod("Dispatch", [typeof(OrderIntent),
            typeof(long), typeof(UtcTimestamp)])
            ?? throw new InvalidOperationException("The harness dispatch API is not implemented.");
        return (ExecutionTransitionResult)(method.Invoke(harness, [intent, revision, claimedAt])
            ?? throw new InvalidOperationException("The harness dispatch returned no result."));
    }

    private static ExecutionTransitionResult InvokeHarnessDispatch(object harness,
        OrderIntent intent, PortfolioReservation reservation, long revision,
        UtcTimestamp claimedAt)
    {
        MethodInfo method = harness.GetType().GetMethod("Dispatch", [typeof(OrderIntent),
            typeof(PortfolioReservation), typeof(long), typeof(UtcTimestamp)])
            ?? throw new InvalidOperationException(
                "The reservation-aware harness dispatch API is not implemented.");
        return (ExecutionTransitionResult)(method.Invoke(harness,
            [intent, reservation, revision, claimedAt])
            ?? throw new InvalidOperationException("The harness dispatch returned no result."));
    }

    private static ExecutionRecoveryResult InvokeHarnessReconcile(object harness, string intentId,
        long revision)
    {
        MethodInfo method = harness.GetType().GetMethod("Reconcile", [typeof(string), typeof(long)])
            ?? throw new InvalidOperationException(
                "The harness reconciliation API is not implemented.");
        return (ExecutionRecoveryResult)(method.Invoke(harness, [intentId, revision])
            ?? throw new InvalidOperationException("The harness reconciliation returned no result."));
    }

    private static int ReadHarnessDispatchCount(object harness) =>
        (int)(harness.GetType().GetProperty("DispatchCount")?.GetValue(harness)
            ?? throw new InvalidOperationException("The harness dispatch count is not implemented."));

    private static OrderIntent CreateIntent(string intentId, string clientOrderId) =>
        new(intentId, "run-harness", "setup-harness", clientOrderId, Instrument,
            Direction.Long, OrderRole.Entry, 1, new Quantity(1m), new Price(100m));

    private static UtcTimestamp Time(int minutes) =>
        new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes));

    private sealed class TemporaryDatabase : IDisposable
    {
        public TemporaryDatabase() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"tradingbot-harness-{Guid.NewGuid():N}.sqlite");

        public string Path { get; }

        public void Dispose()
        {
            foreach (string candidate in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                try
                {
                    File.Delete(candidate);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
