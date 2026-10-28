using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Core;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class OperationalShadowSoakRecoveryTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BoundedSoakAcrossFourProcessLifetimesPreservesCausalReplayAndJsonParity()
    {
        string directory = CreateTempDirectory("tradingbot-shadow-soak-");
        string continuousPath = Path.Combine(directory, "continuous.sqlite");
        string restartedPath = Path.Combine(directory, "restarted.sqlite");
        try
        {
            IReadOnlyList<OperationalShadowJournalEntry> continuous =
                await RunContinuousAsync(continuousPath);
            IReadOnlyList<OperationalShadowJournalEntry> restarted =
                await RunRestartedAsync(restartedPath);

            Assert.Equal(256, continuous.Count);
            Assert.Equal(256, restarted.Count);
            Assert.Equal(continuous, restarted);

            for (int index = 0; index < 256; index++)
            {
                OperationalShadowJournalEntry entry = continuous[index];
                Assert.Equal("shadow", entry.StreamId);
                Assert.Equal(index, entry.StableSequence);
                Assert.Equal($"soak-{index:D3}", entry.EventId);
                Assert.Equal(BaseTime.AddMilliseconds(index * 4), entry.EventTime.Value);
                Assert.Equal(BaseTime.AddMilliseconds(index * 4 + 2), entry.ReceiveTime.Value);
                Assert.Equal(BaseTime.AddMilliseconds(index * 4 + 3),
                    entry.AvailabilityTime.Value);
                Assert.Equal(BaseTime.AddMilliseconds(index * 4 + 1),
                    entry.ExchangeTime?.Value);

                using JsonDocument causal = JsonDocument.Parse(entry.CausalEventJson);
                JsonElement causalRoot = causal.RootElement;
                Assert.Equal("shadow", causalRoot.GetProperty("streamId").GetString());
                Assert.Equal(entry.EventId, causalRoot.GetProperty("eventId").GetString());
                Assert.Equal("BTCUSDT", causalRoot.GetProperty("instrumentId").GetString());
                Assert.Equal(entry.StableSequence,
                    causalRoot.GetProperty("stableSequence").GetInt64());
                Assert.Equal(entry.EventTime.Value,
                    ReadTimestamp(causalRoot, "eventTime"));
                Assert.Equal(entry.ExchangeTime?.Value,
                    ReadNullableTimestamp(causalRoot, "exchangeTime"));
                Assert.Equal(entry.ReceiveTime.Value,
                    ReadTimestamp(causalRoot, "receiveTime"));
                Assert.Equal(entry.AvailabilityTime.Value,
                    ReadTimestamp(causalRoot, "availabilityTime"));

                using JsonDocument state = JsonDocument.Parse(entry.DerivedStateJson);
                JsonElement stateRoot = state.RootElement;
                Assert.True(stateRoot.GetProperty("dataFresh").GetBoolean());
                Assert.True(stateRoot.GetProperty("reconnectReady").GetBoolean());
                Assert.True(stateRoot.GetProperty("recoveryComplete").GetBoolean());
                Assert.True(stateRoot.GetProperty("persistenceHealthy").GetBoolean());
                Assert.True(stateRoot.GetProperty("killSwitchKnown").GetBoolean());
                Assert.False(stateRoot.GetProperty("killSwitchEnabled").GetBoolean());
                Assert.True(stateRoot.GetProperty("canCreateHypotheticalEntry").GetBoolean());

                using JsonDocument outcome = JsonDocument.Parse(entry.OutcomeJson);
                JsonElement outcomeRoot = outcome.RootElement;
                Assert.False(outcomeRoot.GetProperty("hypotheticalEntry").GetBoolean());
                Assert.Equal(0, outcomeRoot.GetProperty("realOrderCount").GetInt64());

                using JsonDocument checkpoint = JsonDocument.Parse(entry.CheckpointJson);
                JsonElement checkpointRoot = checkpoint.RootElement;
                Assert.Equal("shadow", checkpointRoot.GetProperty("streamId").GetString());
                Assert.Equal(entry.StableSequence,
                    checkpointRoot.GetProperty("stableSequence").GetInt64());
                Assert.Equal(entry.EventId, checkpointRoot.GetProperty("eventId").GetString());
                Assert.Equal("blocked-or-observed",
                    checkpointRoot.GetProperty("outcome").GetString());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GapAndReconnectRemainFailClosedUntilFreshRecovery()
    {
        string directory = CreateTempDirectory("tradingbot-shadow-recovery-");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            StaticHealthSource source = new();
            FixedTimeProvider time = new(BaseTime.AddMinutes(10));
            await using OperationalShadowRuntime runtime = new(source, store,
                timeProvider: time);

            source.SetFeeds(HealthyFeeds());
            OperationalShadowCycleResult healthy = runtime.Process(CreateObservation(0));
            Assert.True(healthy.Gate.CanCreateHypotheticalEntry);
            Assert.True(runtime.HealthSnapshot.CanCreateHypotheticalEntry);

            source.SetFeeds(Feeds(gap: true));
            OperationalShadowCycleResult gapped = runtime.Process(CreateObservation(1));
            Assert.False(gapped.Gate.CanCreateHypotheticalEntry);
            Assert.Contains(OperationalShadowBlockReason.DataStale,
                gapped.Gate.BlockReasons);
            Assert.True(runtime.HealthSnapshot.SequenceGap);
            Assert.False(runtime.HealthSnapshot.CanCreateHypotheticalEntry);

            source.SetFeeds(Feeds(reconnecting: true, recoveryComplete: false,
                connected: false, fresh: false, gap: true));
            OperationalShadowCycleResult reconnecting = runtime.Process(CreateObservation(2));
            Assert.False(reconnecting.Gate.CanCreateHypotheticalEntry);
            Assert.Contains(OperationalShadowBlockReason.ReconnectPending,
                reconnecting.Gate.BlockReasons);
            Assert.Contains(OperationalShadowBlockReason.RecoveryPending,
                reconnecting.Gate.BlockReasons);
            Assert.True(runtime.HealthSnapshot.Reconnecting);
            Assert.False(runtime.HealthSnapshot.RecoveryComplete);

            source.SetFeeds(HealthyFeeds());
            OperationalShadowCycleResult recovered = runtime.Process(CreateObservation(3));
            Assert.True(recovered.Gate.CanCreateHypotheticalEntry);
            Assert.False(runtime.HealthSnapshot.SequenceGap);
            Assert.False(runtime.HealthSnapshot.Reconnecting);
            Assert.True(runtime.HealthSnapshot.RecoveryComplete);
            Assert.True(runtime.HealthSnapshot.CanCreateHypotheticalEntry);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HeldSqliteWriterFailsClosedThenRetryPersistsOnceAfterRelease()
    {
        string directory = CreateTempDirectory("tradingbot-shadow-lock-");
        string databasePath = Path.Combine(directory, "shadow.sqlite");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(databasePath,
                commandTimeoutSeconds: 1);
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store);
            OperationalShadowMarketObservation observation = CreateObservation(0);

            using SqliteConnection writer = new(
                $"Data Source={databasePath};Pooling=False;Default Timeout=1");
            writer.Open();
            using (SqliteTransaction transaction = writer.BeginTransaction(
                IsolationLevel.Serializable, deferred: false))
            {
                OperationalShadowCycleResult blocked = runtime.Process(observation);
                Assert.False(blocked.Persisted);
                Assert.False(blocked.Gate.CanCreateHypotheticalEntry);
                Assert.Contains(OperationalShadowBlockReason.PersistenceUnavailable,
                    blocked.Gate.BlockReasons);
                Assert.Equal(0, blocked.RealOrderCount);
            }

            OperationalShadowCycleResult recovered = runtime.Process(observation);
            Assert.True(recovered.Persisted);
            Assert.False(recovered.Duplicate);
            Assert.Equal(0, recovered.RealOrderCount);
            Assert.Single(store.ReadAll("shadow"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShadowCommandEmitsStructuredHealthAndZeroOrderEvidenceForBoundedFixture()
    {
        string directory = CreateTempDirectory("tradingbot-shadow-json-");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(
                    token => FiniteObservations(3, token)), store);
            using StringWriter output = new();

            int exitCode = await OperationalShadowCommand.RunAsync(runtime, output,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            string[] lines = output.ToString().Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(9, lines.Length);
            foreach (string line in lines)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                Assert.Equal("shadow", root.GetProperty("mode").GetString());
                Assert.Equal(0, root.GetProperty("realOrderCount").GetInt64());
            }

            Assert.Equal("started", JsonDocument.Parse(lines[0]).RootElement
                .GetProperty("status").GetString());
            Assert.Equal("unknown", JsonDocument.Parse(lines[1]).RootElement
                .GetProperty("status").GetString());
            Assert.Equal("stopped", JsonDocument.Parse(lines[^1]).RootElement
                .GetProperty("status").GetString());
            Assert.Equal(3, lines.Count(line => line.Contains("\"hypotheticalEntry\"",
                StringComparison.Ordinal)));
            Assert.Equal(3, lines.Count(line => line.Contains("\"feeds\"",
                StringComparison.Ordinal)) - 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<OperationalShadowJournalEntry>>
        RunContinuousAsync(string databasePath)
    {
        using OperationalShadowStore store = OperationalShadowStore.Open(databasePath);
        await using OperationalShadowRuntime runtime = new(
            new DelegateOperationalShadowMarketDataSource(_ => Empty()), store);
        for (long sequence = 0; sequence < 256; sequence++)
        {
            OperationalShadowCycleResult result = runtime.Process(CreateObservation(sequence));
            Assert.True(result.Persisted);
            Assert.False(result.Duplicate);
            Assert.Equal(0, result.RealOrderCount);
        }

        return store.ReadAll("shadow");
    }

    private static async Task<IReadOnlyList<OperationalShadowJournalEntry>>
        RunRestartedAsync(string databasePath)
    {
        for (int lifetime = 0; lifetime < 4; lifetime++)
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(databasePath);
            await using OperationalShadowRuntime runtime = new(
                new DelegateOperationalShadowMarketDataSource(_ => Empty()), store);
            long start = lifetime * 64L;
            if (start > 0)
            {
                OperationalShadowCycleResult retry = runtime.Process(CreateObservation(start - 1));
                Assert.True(retry.Persisted);
                Assert.True(retry.Duplicate);
                Assert.Equal(0, retry.RealOrderCount);
            }

            for (long sequence = start; sequence < start + 64; sequence++)
            {
                OperationalShadowCycleResult result = runtime.Process(CreateObservation(sequence));
                Assert.True(result.Persisted);
                Assert.False(result.Duplicate);
                Assert.Equal(0, result.RealOrderCount);
            }
        }

        using OperationalShadowStore reopened = OperationalShadowStore.Open(databasePath);
        Assert.Equal(255, reopened.ReadCheckpoint("shadow")?.StableSequence);
        return reopened.ReadAll("shadow");
    }

    private static OperationalShadowMarketObservation CreateObservation(long sequence)
    {
        DateTimeOffset eventAt = BaseTime.AddMilliseconds(sequence * 4);
        UtcTimestamp eventTime = new(eventAt);
        Trade trade = new($"soak-{sequence:D3}", new InstrumentId("BTCUSDT"), eventTime,
            new Price(100m + sequence), new Quantity(1m), TradeSide.Buy);
        CausalMarketEvent causal = new(trade,
            new UtcTimestamp(eventAt.AddMilliseconds(2)),
            new UtcTimestamp(eventAt.AddMilliseconds(3)), sequence,
            new UtcTimestamp(eventAt.AddMilliseconds(1)));
        return new OperationalShadowMarketObservation("shadow", causal, dataFresh: true,
            reconnectReady: true, recoveryComplete: true, killSwitchEnabled: false,
            killSwitchKnown: true, OperationalShadowRuntimeTests.CreateShadowEvaluation());
    }

    private static IReadOnlyList<OperationalShadowFeedHealth> HealthyFeeds() =>
        Feeds(gap: false, reconnecting: false, recoveryComplete: true,
            connected: true, fresh: true);

    private static IReadOnlyList<OperationalShadowFeedHealth> Feeds(bool gap,
        bool reconnecting = false, bool recoveryComplete = true, bool connected = true,
        bool fresh = true)
    {
        DateTimeOffset receive = BaseTime.AddMinutes(10);
        DateTimeOffset availability = BaseTime.AddMinutes(10).AddSeconds(1);
        return
        [
            new("trade", connected, fresh, gap, reconnecting, recoveryComplete,
                receive, availability),
            new("depth", connected, fresh, gap, reconnecting, recoveryComplete,
                receive, availability)
        ];
    }

    private static async IAsyncEnumerable<OperationalShadowMarketObservation> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<OperationalShadowMarketObservation> FiniteObservations(
        int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (long sequence = 0; sequence < count; sequence++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateObservation(sequence);
            await Task.Yield();
        }
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).GetDateTimeOffset();

    private static DateTimeOffset? ReadNullableTimestamp(JsonElement root, string propertyName)
    {
        JsonElement value = root.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetDateTimeOffset();
    }

    private static string CreateTempDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StaticHealthSource : IOperationalShadowMarketDataSource,
        IOperationalShadowHealthSource
    {
        private IReadOnlyList<OperationalShadowFeedHealth> _feeds = [];

        public IReadOnlyCollection<string> RequiredFeedStreams => ["trade", "depth"];

        public event Action? HealthChanged;

        public IReadOnlyList<OperationalShadowFeedHealth> GetHealth(DateTimeOffset checkedAt,
            TimeSpan staleAfter) => _feeds;

        public void SetFeeds(IReadOnlyList<OperationalShadowFeedHealth> feeds)
        {
            _feeds = feeds;
            HealthChanged?.Invoke();
        }

        public async IAsyncEnumerable<OperationalShadowMarketObservation> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
