using System.Diagnostics.Metrics;
using System.Reflection;
using TradingBot.App;
using TradingBot.Infrastructure;
using Xunit;

namespace TradingBot.IntegrationTests;

public sealed class OperationalShadowObservabilityTests
{
    [Fact]
    public void RuntimePublishesAnInitialHealthSnapshotAndHealthChanges()
    {
        PropertyInfo healthSnapshot = typeof(OperationalShadowRuntime).GetProperty(
            "HealthSnapshot", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(typeof(OperationalShadowRuntime).FullName,
                "HealthSnapshot");
        EventInfo healthChanged = typeof(OperationalShadowRuntime).GetEvent(
            "HealthChanged", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(typeof(OperationalShadowRuntime).FullName,
                "HealthChanged");

        Assert.NotNull(healthSnapshot.PropertyType);
        Assert.NotNull(healthChanged.EventHandlerType);
    }

    [Fact]
    public async Task SnapshotReportsFeedGateAndShadowReconciliationSignals()
    {
        string directory = Path.Combine(Path.GetTempPath(), "shadow-health-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite"));
            HealthSource source = new();
            await using OperationalShadowRuntime runtime = new(source, store);
            Assert.Equal(OperationalShadowHealthStatus.Unknown, runtime.HealthSnapshot.Status);

            List<string> measurements = [];
            using MeterListener listener = new();
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == OperationalShadowMetrics.MeterName)
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
                measurements.Add(instrument.Name));
            listener.Start();

            source.SetFeeds([Feed("trade", fresh: true), Feed("depth", fresh: true)]);
            runtime.Process(OperationalShadowRuntimeTests.CreateObservation(ready: true,
                evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation()));
            Assert.Equal(OperationalShadowHealthStatus.Healthy, runtime.HealthSnapshot.Status);
            Assert.Equal(OperationalShadowReconciliationStatus.NotApplicableInShadow,
                runtime.HealthSnapshot.ReconciliationStatus);
            Assert.True(runtime.HealthSnapshot.CanCreateHypotheticalEntry);
            Assert.Equal(0, runtime.HealthSnapshot.RealOrderCount);

            source.SetFeeds([
                Feed("trade", fresh: false),
                Feed("depth", fresh: true)
            ]);
            Assert.Equal(OperationalShadowHealthStatus.Degraded, runtime.HealthSnapshot.Status);
            Assert.True(runtime.HealthSnapshot.DataStale);
            Assert.False(runtime.HealthSnapshot.CanCreateHypotheticalEntry);
            OperationalShadowCycleResult staleCycle = runtime.Process(
                OperationalShadowRuntimeTests.CreateObservation(ready: true,
                    evaluation: OperationalShadowRuntimeTests.CreateShadowEvaluation(),
                    stableSequence: 2));
            Assert.False(staleCycle.Gate.CanCreateHypotheticalEntry);
            Assert.Contains(OperationalShadowBlockReason.DataStale,
                staleCycle.Gate.BlockReasons);

            source.SetFeeds([
                Feed("trade", fresh: true, gap: true),
                Feed("depth", fresh: true)
            ]);
            Assert.True(runtime.HealthSnapshot.SequenceGap);
            Assert.Contains("SequenceGap", runtime.HealthSnapshot.BlockReasons);
            Assert.False(runtime.HealthSnapshot.CanCreateHypotheticalEntry);

            listener.RecordObservableInstruments();
            Assert.Contains("shadow.health_status", measurements);
            Assert.Contains("shadow.data_stale", measurements);
            Assert.Contains("shadow.sequence_gap", measurements);
            Assert.Contains("shadow.private_reconciliation_applicable", measurements);
            Assert.Contains("shadow.real_order_count", measurements);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HealthTimerPublishesStalenessWhileSourceIsSilent()
    {
        string directory = Path.Combine(Path.GetTempPath(), "shadow-health-timer-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (OperationalShadowStore store = OperationalShadowStore.Open(
                Path.Combine(directory, "shadow.sqlite")))
            {
                HealthSource source = new();
                source.SetFeeds([Feed("trade", fresh: true), Feed("depth", fresh: true)]);
                await using (OperationalShadowRuntime runtime = new(source, store,
                    new OperationalShadowRuntimeOptions
                    {
                        HealthCheckInterval = TimeSpan.FromMilliseconds(5),
                        StaleAfter = TimeSpan.FromMilliseconds(1)
                    }))
                {
                    using CancellationTokenSource cancellation = new();
                    Task run = runtime.RunAsync(cancellation.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(30),
                        TestContext.Current.CancellationToken);

                    Assert.True(runtime.HealthSnapshot.DataStale);
                    Assert.Equal(OperationalShadowHealthStatus.Degraded,
                        runtime.HealthSnapshot.Status);
                    Assert.Equal(0, runtime.HealthSnapshot.RealOrderCount);
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OperationalShadowFeedHealth Feed(string stream, bool fresh,
        bool gap = false) => new(stream, Connected: true, fresh, gap,
            Reconnecting: false, RecoveryComplete: true,
            LastReceiveTime: fresh ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddHours(-1),
            LastAvailabilityTime: fresh ? DateTimeOffset.UtcNow
                : DateTimeOffset.UtcNow.AddHours(-1));

    private sealed class HealthSource : IOperationalShadowMarketDataSource,
        IOperationalShadowHealthSource
    {
        private IReadOnlyList<OperationalShadowFeedHealth> _feeds = [];

        public IReadOnlyCollection<string> RequiredFeedStreams => ["trade", "depth"];

        public event Action? HealthChanged;

        public IReadOnlyList<OperationalShadowFeedHealth> GetHealth(DateTimeOffset checkedAt,
            TimeSpan staleAfter) => _feeds.Select(feed => feed with
            {
                Fresh = feed.LastAvailabilityTime is { } availability
                    && checkedAt - availability <= staleAfter
            }).ToArray();

        public void SetFeeds(IReadOnlyList<OperationalShadowFeedHealth> feeds)
        {
            _feeds = feeds;
            HealthChanged?.Invoke();
        }

        public async IAsyncEnumerable<OperationalShadowMarketObservation> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan,
                cancellationToken.CanBeCanceled ? cancellationToken
                    : TestContext.Current.CancellationToken);
            yield break;
        }
    }
}
