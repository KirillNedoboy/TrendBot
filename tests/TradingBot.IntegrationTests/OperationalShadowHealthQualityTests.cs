using System.Data;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradingBot.App;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class OperationalShadowHealthQualityTests
{
    [Fact]
    public async Task KnownPartialCoverageIsVisibleAndClosesTheHypotheticalGate()
    {
        string directory = CreateTempDirectory("shadow-quality-partial-");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            QualitySource source = new(["trade", "depth"]);
            source.SetFeeds([
                Feed("trade", covered: true),
                Feed("trade", covered: false),
                Feed("unrequired", covered: true)
            ]);
            await using OperationalShadowRuntime runtime = new(source, store);

            OperationalShadowCycleResult result = runtime.Process(
                OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));

            Assert.Equal(2, ReadInt(runtime.HealthSnapshot, "ExpectedFeedCount"));
            Assert.Equal(1, ReadInt(runtime.HealthSnapshot, "CoveredFeedCount"));
            Assert.Equal(0.5d, ReadNullableDouble(runtime.HealthSnapshot, "CoverageRatio"));
            Assert.False(result.Gate.CanCreateHypotheticalEntry);
            Assert.Contains(result.Gate.BlockReasons,
                reason => reason.ToString().Contains("Coverage", StringComparison.Ordinal));
            Assert.Contains(ReadMissing(runtime.HealthSnapshot), missing =>
                ReadString(missing, "stream") == "depth");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownRequiredCoverageIsNotReportedAsFullCoverage()
    {
        string directory = CreateTempDirectory("shadow-quality-unknown-");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            QualitySource source = new(null);
            source.SetFeeds([Feed("trade", covered: true)]);
            await using OperationalShadowRuntime runtime = new(source, store);

            OperationalShadowCycleResult result = runtime.Process(
                OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));

            Assert.Null(ReadNullableDouble(runtime.HealthSnapshot, "CoverageRatio"));
            Assert.Equal(0, ReadInt(runtime.HealthSnapshot, "ExpectedFeedCount"));
            Assert.False(result.Gate.CanCreateHypotheticalEntry);
            Assert.Contains(result.Gate.BlockReasons,
                reason => reason.ToString().Contains("Coverage", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateRequiredAndExtraFeedsDoNotInflateCoverage()
    {
        string directory = CreateTempDirectory("shadow-quality-duplicates-");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            QualitySource source = new(["trade", "depth", "trade"]);
            source.SetFeeds([
                Feed("trade", covered: true),
                Feed("trade", covered: true),
                Feed("depth", covered: true),
                Feed("extra", covered: true)
            ]);
            await using OperationalShadowRuntime runtime = new(source, store);

            OperationalShadowCycleResult result = runtime.Process(
                OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));

            Assert.Equal(2, ReadInt(runtime.HealthSnapshot, "ExpectedFeedCount"));
            Assert.Equal(2, ReadInt(runtime.HealthSnapshot, "CoveredFeedCount"));
            Assert.Equal(1d, ReadNullableDouble(runtime.HealthSnapshot, "CoverageRatio"));
            Assert.True(result.Gate.CanCreateHypotheticalEntry);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthJsonAndMetricsPublishCoverageAndZeroOrderSignals()
    {
        string directory = CreateTempDirectory("shadow-quality-json-");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            QualitySource source = new(["trade", "depth"], finite: true);
            source.SetFeeds([Feed("trade", covered: true), Feed("depth", covered: true)]);
            await using OperationalShadowRuntime runtime = new(source, store,
                new OperationalShadowRuntimeOptions { HealthCheckInterval = TimeSpan.FromHours(1) });
            using StringWriter output = new();
            using MeterListener listener = new();
            List<string> measurements = [];
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == OperationalShadowMetrics.MeterName)
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
                measurements.Add(instrument.Name));
            listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
                measurements.Add(instrument.Name));
            listener.Start();

            runtime.Process(OperationalShadowRuntimeTests.CreateObservation(ready: true,
                evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));
            listener.RecordObservableInstruments();
            Assert.Contains("shadow.required_feed_count", measurements);
            Assert.Contains("shadow.covered_feed_count", measurements);
            Assert.Contains("shadow.real_order_count", measurements);

            int exitCode = await OperationalShadowCommand.RunAsync(runtime, output,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            JsonElement health = output.ToString().Split(Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line))
                .Select(document => document.RootElement.Clone())
                .First(root => root.TryGetProperty("expectedFeedCount", out _));
            Assert.Equal(2, health.GetProperty("expectedFeedCount").GetInt32());
            Assert.Equal(2, health.GetProperty("coveredFeedCount").GetInt32());
            Assert.Equal(0, health.GetProperty("realOrderCount").GetInt64());
            Assert.Contains("shadow.coverage_ratio", measurements);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SilentHealthTimerPersistsVersionedSnapshotsWithoutCausalLink()
    {
        string directory = CreateTempDirectory("shadow-quality-timer-evidence-");
        string databasePath = Path.Combine(directory, "shadow.sqlite");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(databasePath);
            QualitySource source = new(["trade", "depth"]);
            source.SetFeeds([Feed("trade", covered: true), Feed("depth", covered: true)]);
            await using OperationalShadowRuntime runtime = new(source, store,
                new OperationalShadowRuntimeOptions
                {
                    HealthCheckInterval = TimeSpan.FromMilliseconds(5)
                });
            using CancellationTokenSource cancellation = new();
            Task run = runtime.RunAsync(cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(35),
                TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);

            IReadOnlyList<OperationalShadowHealthEvidence> evidence =
                store.ReadHealthEvidence(runtime.RunId);
            Assert.True(evidence.Count >= 2);
            Assert.All(evidence, item =>
            {
                Assert.Equal(runtime.RunId, item.RunId);
                Assert.Contains("formatVersion", item.HealthJson);
                Assert.Null(item.CausalCheckpointLink);
            });
            Assert.Equal(evidence.Select(item => item.HealthSequence).Order(),
                evidence.Select(item => item.HealthSequence));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthEvidenceUsesDistinctRunIdentityAfterRestart()
    {
        string directory = CreateTempDirectory("shadow-quality-runs-");
        string databasePath = Path.Combine(directory, "shadow.sqlite");
        string firstRunId;
        try
        {
            using (OperationalShadowStore store = OperationalShadowStore.Open(databasePath))
            {
                QualitySource source = new(["trade", "depth"]);
                source.SetFeeds([Feed("trade", covered: true), Feed("depth", covered: true)]);
                await using OperationalShadowRuntime runtime = new(source, store);
                firstRunId = runtime.RunId;
                runtime.Process(OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));
            }

            using (OperationalShadowStore reopened = OperationalShadowStore.Open(databasePath))
            {
                QualitySource source = new(["trade", "depth"]);
                source.SetFeeds([Feed("trade", covered: true), Feed("depth", covered: true)]);
                await using OperationalShadowRuntime runtime = new(source, reopened);
                runtime.Process(OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));
                Assert.NotEqual(firstRunId, runtime.RunId);
                Assert.NotEmpty(reopened.ReadHealthEvidence(firstRunId));
                Assert.NotEmpty(reopened.ReadHealthEvidence(runtime.RunId));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthEvidenceWriteFailureIsVisibleAndClosesTheGate()
    {
        string directory = CreateTempDirectory("shadow-quality-lock-");
        string databasePath = Path.Combine(directory, "shadow.sqlite");
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(databasePath,
                commandTimeoutSeconds: 1);
            QualitySource source = new(["trade", "depth"]);
            source.SetFeeds([Feed("trade", covered: true), Feed("depth", covered: true)]);
            using SqliteConnection writer = new(
                $"Data Source={databasePath};Pooling=False;Default Timeout=1");
            writer.Open();
            using SqliteTransaction transaction = writer.BeginTransaction(
                IsolationLevel.Serializable, deferred: false);
            await using OperationalShadowRuntime runtime = new(source, store,
                new OperationalShadowRuntimeOptions { HealthCheckInterval = TimeSpan.FromHours(1) });

            OperationalShadowCycleResult result = runtime.Process(
                OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));

            Assert.False(runtime.HealthSnapshot.PersistenceHealthy);
            Assert.Contains(OperationalShadowBlockReason.PersistenceUnavailable.ToString(),
                runtime.HealthSnapshot.BlockReasons);
            Assert.False(result.Gate.CanCreateHypotheticalEntry);
            Assert.Equal(0, result.RealOrderCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int ReadInt(OperationalShadowHealthSnapshot snapshot, string propertyName) =>
        (int)(snapshot.GetType().GetProperty(propertyName)?.GetValue(snapshot)
            ?? throw new MissingMemberException(snapshot.GetType().FullName, propertyName));

    private static double? ReadNullableDouble(OperationalShadowHealthSnapshot snapshot,
        string propertyName)
    {
        PropertyInfo property = snapshot.GetType().GetProperty(propertyName)
            ?? throw new MissingMemberException(snapshot.GetType().FullName, propertyName);
        return (double?)property.GetValue(snapshot);
    }

    private static JsonElement[] ReadMissing(OperationalShadowHealthSnapshot snapshot)
    {
        object value = snapshot.GetType().GetProperty("MissingStreamReasons")?.GetValue(snapshot)
            ?? throw new MissingMemberException(snapshot.GetType().FullName,
                "MissingStreamReasons");
        string json = JsonSerializer.Serialize(value);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement lower)
            ? lower.GetString()
            : element.GetProperty("Stream").GetString();

    private static OperationalShadowFeedHealth Feed(string stream, bool covered) =>
        new(stream, Connected: true, Fresh: true, GapDetected: false, Reconnecting: false,
            RecoveryComplete: true,
            LastReceiveTime: covered ? DateTimeOffset.UtcNow : null,
            LastAvailabilityTime: covered ? DateTimeOffset.UtcNow : null);

    private static string CreateTempDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class QualitySource(IReadOnlyCollection<string>? requiredFeedStreams,
        bool finite = false) :
        IOperationalShadowMarketDataSource, IOperationalShadowHealthSource
    {
        private IReadOnlyList<OperationalShadowFeedHealth> _feeds = [];

        public IReadOnlyCollection<string>? RequiredFeedStreams { get; } = requiredFeedStreams;

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
            if (finite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation());
                yield break;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
