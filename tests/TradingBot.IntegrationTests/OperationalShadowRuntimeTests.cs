using System.Reflection;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Core.Tests;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class OperationalShadowRuntimeTests
{
    [Fact]
    public void CompleteFreshHealthyRuntimeAllowsOnlyHypotheticalEntries()
    {
        OperationalShadowGateResult result = OperationalShadowGate.Evaluate(
            new OperationalShadowGateInput(ConfigurationValid: true, DataFresh: true,
                ReconnectReady: true, RecoveryComplete: true, PersistenceHealthy: true,
                KillSwitchEnabled: false));

        Assert.True(result.CanCreateHypotheticalEntry);
        Assert.Empty(result.BlockReasons);
        Assert.Equal(0, result.RealOrderCount);
    }

    [Theory]
    [InlineData(false, true, true, true, true, false,
        OperationalShadowBlockReason.ConfigurationUnknown)]
    [InlineData(true, false, true, true, true, false,
        OperationalShadowBlockReason.DataStale)]
    [InlineData(true, true, false, true, true, false,
        OperationalShadowBlockReason.ReconnectPending)]
    [InlineData(true, true, true, false, true, false,
        OperationalShadowBlockReason.RecoveryPending)]
    [InlineData(true, true, true, true, false, false,
        OperationalShadowBlockReason.PersistenceUnavailable)]
    [InlineData(true, true, true, true, true, true,
        OperationalShadowBlockReason.KillSwitchEnabled)]
    public void UnsafeRuntimeStateBlocksHypotheticalEntries(bool configurationValid,
        bool dataFresh, bool reconnectReady, bool recoveryComplete, bool persistenceHealthy,
        bool killSwitchEnabled, OperationalShadowBlockReason expectedReason)
    {
        OperationalShadowGateResult result = OperationalShadowGate.Evaluate(
            new OperationalShadowGateInput(configurationValid, dataFresh, reconnectReady,
                recoveryComplete, persistenceHealthy, killSwitchEnabled));

        Assert.False(result.CanCreateHypotheticalEntry);
        Assert.Contains(expectedReason, result.BlockReasons);
        Assert.Equal(0, result.RealOrderCount);
    }

    [Fact]
    public void CombinedUnsafeStateUsesStableFailClosedReasonOrder()
    {
        OperationalShadowGateResult result = OperationalShadowGate.Evaluate(
            new OperationalShadowGateInput(ConfigurationValid: false, DataFresh: false,
                ReconnectReady: false, RecoveryComplete: false, PersistenceHealthy: false,
                KillSwitchEnabled: true));

        Assert.False(result.CanCreateHypotheticalEntry);
        Assert.Equal(
            [
                OperationalShadowBlockReason.ConfigurationUnknown,
                OperationalShadowBlockReason.DataStale,
                OperationalShadowBlockReason.ReconnectPending,
                OperationalShadowBlockReason.RecoveryPending,
                OperationalShadowBlockReason.PersistenceUnavailable,
                OperationalShadowBlockReason.KillSwitchEnabled
            ], result.BlockReasons);
        Assert.Equal(0, result.RealOrderCount);
    }

    [Fact]
    public void ShadowStoreAppendAndRestartAreAtomicAndIdempotent()
    {
        Type storeType = RequireType("TradingBot.Infrastructure.OperationalShadowStore");
        Type entryType = RequireType("TradingBot.Infrastructure.OperationalShadowJournalEntry");
        MethodInfo open = storeType.GetMethod("Open", [typeof(string), typeof(int)])
            ?? throw new MissingMethodException(storeType.FullName, "Open");
        ConstructorInfo entryConstructor = entryType.GetConstructor([
                typeof(string), typeof(string), typeof(long), typeof(UtcTimestamp),
                typeof(UtcTimestamp), typeof(UtcTimestamp), typeof(UtcTimestamp),
                typeof(string), typeof(string), typeof(string), typeof(string)])
            ?? throw new MissingMethodException(entryType.FullName, ".ctor");

        string directory = Path.Combine(Path.GetTempPath(), "tradingbot-shadow-red-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "shadow.sqlite");
        UtcTimestamp eventTime = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));
        object entry = entryConstructor.Invoke([
            "shadow", "event-1", 1L, eventTime, eventTime, eventTime, null!,
            "{\"event\":1}", "{\"state\":1}", "{\"outcome\":\"blocked\"}",
            "{\"checkpoint\":1}"]);

        try
        {
            using (IDisposable store = (IDisposable)(open.Invoke(null, [databasePath, 30])
                ?? throw new InvalidOperationException("Store open returned null.")))
            {
                MethodInfo append = storeType.GetMethod("Append", [entryType])
                    ?? throw new MissingMethodException(storeType.FullName, "Append");
                object first = append.Invoke(store, [entry])
                    ?? throw new InvalidOperationException("Append returned null.");
                Assert.True(ReadBoolean(first, "Appended"));
                Assert.False(ReadBoolean(first, "Duplicate"));

                object second = append.Invoke(store, [entry])
                    ?? throw new InvalidOperationException("Duplicate append returned null.");
                Assert.False(ReadBoolean(second, "Appended"));
                Assert.True(ReadBoolean(second, "Duplicate"));
            }

            using (IDisposable reopened = (IDisposable)(open.Invoke(null, [databasePath, 30])
                ?? throw new InvalidOperationException("Reopen returned null.")))
            {
                MethodInfo readAll = storeType.GetMethod("ReadAll", [typeof(string)])
                    ?? throw new MissingMethodException(storeType.FullName, "ReadAll");
                object rows = readAll.Invoke(reopened, ["shadow"])
                    ?? throw new InvalidOperationException("ReadAll returned null.");
                Assert.Single((System.Collections.IEnumerable)rows);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OperationalShadowRuntimeExposesAProcessAndZeroOrderMetricsSurface()
    {
        Type runtimeType = RequireType("TradingBot.App.OperationalShadowRuntime");
        Assert.NotNull(runtimeType.GetMethod("Process", BindingFlags.Public
            | BindingFlags.Instance));

        Type metricsType = RequireType("TradingBot.App.OperationalShadowMetrics");
        PropertyInfo realOrderCount = metricsType.GetProperty("RealOrderCount",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(metricsType.FullName, "RealOrderCount");
        Assert.Equal(typeof(long), realOrderCount.PropertyType);
    }

    [Fact]
    public async Task ReadyShadowRuntimeKeepsDecisionShadowAndNeverCreatesARealOrder()
    {
        string directory = CreateTempDirectory();
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            using OperationalShadowMetrics metrics = new();
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store,
                metrics: metrics);

            OperationalShadowCycleResult result = runtime.Process(
                CreateObservation(ready: true, evaluation: CreateShadowEvaluation()));

            Assert.True(result.Gate.CanCreateHypotheticalEntry);
            Assert.Equal(DecisionOutcome.Shadow, result.Decision?.Decision.Outcome);
            Assert.NotNull(result.Risk);
            Assert.False(result.Risk!.IsApproved);
            Assert.False(result.HypotheticalEntryCreated);
            Assert.Equal(0, result.RealOrderCount);
            Assert.Equal(0, metrics.RealOrderCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BlockedRiskNeverCreatesHypotheticalEntry()
    {
        string directory = CreateTempDirectory();
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store);

            OperationalShadowCycleResult result = runtime.Process(
                CreateObservation(ready: true, evaluation: CreateShadowEvaluation()));

            Assert.Equal(DecisionOutcome.Shadow, result.Decision?.Decision.Outcome);
            Assert.False(result.Risk!.IsApproved);
            Assert.False(result.HypotheticalEntryCreated);
            Assert.Equal(0, result.RealOrderCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EvaluationFailureClosesTheGateBeforePersistingTheOutcome()
    {
        string directory = CreateTempDirectory();
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store);
            OperationalShadowEvaluation valid = CreateShadowEvaluation();
            OperationalShadowEvaluation failing = new(valid.DecisionInput,
                valid.DecisionConfiguration, valid.RiskInput, valid.RiskConfiguration,
                new ThrowingRiskMathCalculator());

            OperationalShadowCycleResult result = runtime.Process(
                CreateObservation(ready: true, evaluation: failing));

            Assert.False(result.Gate.CanCreateHypotheticalEntry);
            Assert.Contains(OperationalShadowBlockReason.ConfigurationUnknown,
                result.Gate.BlockReasons);
            Assert.Contains("shadow-evaluation-unavailable", result.Reasons);
            Assert.False(result.HypotheticalEntryCreated);
            Assert.True(result.Persisted);
            Assert.Equal(0, result.RealOrderCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedKillSwitchRemainsAClosedGateAfterRestart()
    {
        string directory = CreateTempDirectory();
        try
        {
            string databasePath = Path.Combine(directory, "shadow.sqlite");
            using OperationalShadowStore store = OperationalShadowStore.Open(databasePath);
            UtcTimestamp time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture));
            store.Append(new OperationalShadowJournalEntry("shadow", "kill-switch-event", 1,
                time, time, time, null, "{\"event\":1}",
                "{\"killSwitchKnown\":true,\"killSwitchEnabled\":true}",
                "{\"outcome\":\"blocked\"}", "{\"checkpoint\":1}"));
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store);

            OperationalShadowCycleResult result = runtime.Process(CreateObservation(ready: true,
                evaluation: CreateShadowEvaluation(), stableSequence: 2));

            Assert.False(result.Gate.CanCreateHypotheticalEntry);
            Assert.Contains(OperationalShadowBlockReason.KillSwitchEnabled,
                result.Gate.BlockReasons);
            Assert.True(runtime.HealthSnapshot.KillSwitchKnown);
            Assert.True(runtime.HealthSnapshot.KillSwitchEnabled);
            Assert.False(runtime.HealthSnapshot.CanCreateHypotheticalEntry);
            Assert.False(result.HypotheticalEntryCreated);
            Assert.Equal(0, result.RealOrderCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StaleShadowRuntimePersistsAClosedGateAndRecoversIdempotently()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "shadow.sqlite");
        OperationalShadowMarketObservation observation = CreateObservation(ready: false,
            evaluation: null);
        try
        {
            using (OperationalShadowStore store = OperationalShadowStore.Open(databasePath))
            await using (OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store))
            {
                OperationalShadowCycleResult first = runtime.Process(observation);
                Assert.False(first.Gate.CanCreateHypotheticalEntry);
                Assert.Contains(OperationalShadowBlockReason.DataStale, first.Gate.BlockReasons);
                Assert.True(first.Persisted);
                Assert.False(first.Duplicate);
            }

            using OperationalShadowStore reopened = OperationalShadowStore.Open(databasePath);
            Assert.Single(reopened.ReadAll("shadow"));
            Assert.Equal(1, reopened.ReadCheckpoint("shadow")?.StableSequence);
            await using OperationalShadowRuntime retry = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), reopened);
            OperationalShadowCycleResult duplicate = retry.Process(observation);
            Assert.True(duplicate.Persisted);
            Assert.True(duplicate.Duplicate);
            Assert.Equal(0, duplicate.RealOrderCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShadowStoreUsesItsOwnVersionOneSchemaAndFailsClosedAfterDispose()
    {
        string directory = CreateTempDirectory();
        string databasePath = Path.Combine(directory, "shadow.sqlite");
        try
        {
            OperationalShadowStore store = OperationalShadowStore.Open(databasePath);
            using (SqliteConnection connection = new($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using SqliteCommand version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Assert.Equal(2L, (long)(version.ExecuteScalar() ?? -1L));
                using SqliteCommand tables = connection.CreateCommand();
                tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
                    + "AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using SqliteDataReader reader = tables.ExecuteReader();
                List<string> names = [];
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }

                Assert.Equal(["shadow_checkpoints", "shadow_entries",
                    "shadow_health_evidence"], names);
            }

            store.Dispose();
            Assert.False(store.IsHealthy);
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store);
            OperationalShadowCycleResult result = runtime.Process(CreateObservation(ready: false,
                evaluation: null));
            Assert.Contains(OperationalShadowBlockReason.PersistenceUnavailable,
                result.Gate.BlockReasons);
            Assert.False(result.HypotheticalEntryCreated);
            Assert.Equal(0, result.RealOrderCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Type RequireType(string name)
    {
        string assembly = name.StartsWith("TradingBot.App.", StringComparison.Ordinal)
            ? "TradingBot.App"
            : "TradingBot.Infrastructure";
        return Type.GetType($"{name}, {assembly}")
        ?? throw new MissingMemberException(name);
    }

    private static bool ReadBoolean(object value, string propertyName) =>
        (bool)(value.GetType().GetProperty(propertyName)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, propertyName));

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tradingbot-shadow-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async IAsyncEnumerable<OperationalShadowMarketObservation> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    internal static OperationalShadowMarketObservation CreateObservation(bool ready,
        OperationalShadowEvaluation? evaluation, long stableSequence = 1)
    {
        UtcTimestamp time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture));
        Trade trade = new("trade-1", new InstrumentId("BTCUSDT"), time,
            new Price(100m), new Quantity(1m), TradeSide.Buy);
        CausalMarketEvent causal = new(trade, time, time, stableSequence);
        return new OperationalShadowMarketObservation("shadow", causal, ready, ready, ready,
            killSwitchEnabled: false, killSwitchKnown: true, evaluation: evaluation);
    }

    internal static OperationalShadowEvaluation CreateShadowEvaluation()
    {
        SetupEvidenceSnapshot evidence = LifecycleInputFixtures.All[0].Evidence;
        SetupLifecycleIdentity identity = SetupLifecycleIdentity.Create(
            new InstrumentId("BTCUSDT"), SetupType.BoundaryRejection, Direction.Long,
            "shadow-anchor", evidence.Cursor.AsOf, "context-v1");
        SetupLifecycleSnapshot lifecycle = SetupLifecycleSnapshot.CreateInitial(identity,
            new SetupLifecycleConfiguration("lifecycle-v1", "config-v1",
                requireExternalPlan: true, requireConfirmationWindow: true)) with
        {
            State = SetupState.EntryArmed,
            ProcessedCursor = evidence.Cursor,
            ProcessedAvailabilityTime = evidence.Cursor.AsOf,
            ProcessedEvents = new ImmutableValueList<SetupLifecycleEventReceipt>([])
        };
        DecisionEngineInput decisionInput = new("shadow-decision", lifecycle, evidence,
            Array.Empty<DecisionEvidence>(), "shadow-fixture");
        DecisionEngineConfiguration decisionConfiguration = new("decision-v1", "shadow-v1",
            DecisionEngineProfile.Shadow, requireLaneB: false, requireLaneC: false,
            allowNeutralLaneB: true, allowNeutralLaneC: true);
        PortfolioId portfolio = new("shadow-portfolio");
        RiskMathInput math = new(new InstrumentId("BTCUSDT"), Direction.Long,
            new Money(1_000m, "USDT"), 0.01m, new Price(100m), new Price(90m),
            new MoneyPerQuantityUnit(0m, "USDT"),
            new MoneyPerQuantityUnit(0m, "USDT"));
        DecisionProjectionSnapshot projection = DecisionProjectionFixtures.CreateProjection(
            DecisionEngineProfile.Shadow, blocked: false);
        RiskEvaluationInput riskInput = new("shadow-risk", projection, math,
            portfolio, evidence.Cursor,
            new RiskPortfolioUsage(portfolio, new Money(0m, "USDT"),
                new Money(0m, "USDT"), new Money(0m, "USDT"), 0, "usage-v1", "v1",
                evidence.Cursor, "shadow-fixture"), new Money(0m, "USDT"), "period-1",
            new Money(10m, "USDT"), 1,
            RiskHealthEvidence.Healthy("account", "v1", evidence.Cursor, "shadow-fixture"),
            RiskHealthEvidence.Healthy("portfolio", "v1", evidence.Cursor, "shadow-fixture"),
            RiskHealthEvidence.Healthy("market", "v1", evidence.Cursor, "shadow-fixture"),
            RiskKillSwitchEvidence.Clear("kill", "v1", evidence.Cursor, "shadow-fixture"),
            "shadow-fixture");
        RiskEvaluationConfiguration riskConfiguration = new(RiskEvaluationVersion.Current,
            new RiskMathVersion("linear-perpetual-risk-v1"),
            new RiskLimitConfiguration(new Money(10m, "USDT"), new Money(100m, "USDT"),
                new Money(100m, "USDT"), "period-1"),
            new PortfolioLimits(portfolio, new Money(100m, "USDT"),
                new Money(10_000m, "USDT"), 2), "shadow-fixture");
        return new OperationalShadowEvaluation(decisionInput, decisionConfiguration, riskInput,
            riskConfiguration, new LinearPerpetualRiskMathCalculator());
    }

    private sealed class ThrowingRiskMathCalculator : IRiskMathCalculator
    {
        public RiskMathVersion Version => LinearPerpetualRiskMathCalculator.CurrentVersion;

        public RiskMathResult Calculate(RiskMathInput input) =>
            throw new InvalidOperationException("Injected shadow evaluation failure.");
    }
}
