using System.Diagnostics.Metrics;

namespace TradingBot.App;

/// <summary>BCL Meter surface for shadow health and the invariant zero-order signal.</summary>
public sealed class OperationalShadowMetrics : IDisposable
{
    public const string MeterName = "TradingBot.App.OperationalShadow";

    private readonly Meter _meter;
    private readonly Counter<long> _cycles;
    private readonly Counter<long> _blockedCycles;
    private readonly Counter<long> _hypotheticalEntries;
    private readonly ObservableGauge<long> _realOrderGauge;
    private readonly ObservableGauge<long> _healthStatusGauge;
    private readonly ObservableGauge<long> _dataStaleGauge;
    private readonly ObservableGauge<long> _sequenceGapGauge;
    private readonly ObservableGauge<long> _reconnectingGauge;
    private readonly ObservableGauge<long> _recoveryCompleteGauge;
    private readonly ObservableGauge<long> _persistenceHealthyGauge;
    private readonly ObservableGauge<long> _gateOpenGauge;
    private readonly ObservableGauge<long> _killSwitchKnownGauge;
    private readonly ObservableGauge<long> _killSwitchEnabledGauge;
    private readonly ObservableGauge<long> _reconciliationApplicableGauge;
    private OperationalShadowHealthSnapshot? _healthSnapshot;
    private long _blockedCycleCount;
    private long _hypotheticalEntryCount;
    private long _realOrderCount;
    private bool _disposed;

    public OperationalShadowMetrics(string meterName = MeterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        _meter = new Meter(meterName);
        _cycles = _meter.CreateCounter<long>("shadow.cycles", unit: "{cycle}");
        _blockedCycles = _meter.CreateCounter<long>("shadow.blocked_cycles", unit: "{cycle}");
        _hypotheticalEntries = _meter.CreateCounter<long>("shadow.hypothetical_entries",
            unit: "{entry}");
        _realOrderGauge = _meter.CreateObservableGauge("shadow.real_order_count",
            static () => 0L, unit: "{order}");
        _healthStatusGauge = _meter.CreateObservableGauge("shadow.health_status",
            () => (long)(Volatile.Read(ref _healthSnapshot)?.Status
                ?? OperationalShadowHealthStatus.Unknown));
        _dataStaleGauge = _meter.CreateObservableGauge("shadow.data_stale",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.DataStale));
        _sequenceGapGauge = _meter.CreateObservableGauge("shadow.sequence_gap",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.SequenceGap));
        _reconnectingGauge = _meter.CreateObservableGauge("shadow.reconnecting",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.Reconnecting));
        _recoveryCompleteGauge = _meter.CreateObservableGauge("shadow.recovery_complete",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.RecoveryComplete));
        _persistenceHealthyGauge = _meter.CreateObservableGauge("shadow.persistence_healthy",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.PersistenceHealthy));
        _gateOpenGauge = _meter.CreateObservableGauge("shadow.gate_open",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.CanCreateHypotheticalEntry));
        _killSwitchKnownGauge = _meter.CreateObservableGauge("shadow.kill_switch_known",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.KillSwitchKnown));
        _killSwitchEnabledGauge = _meter.CreateObservableGauge("shadow.kill_switch_enabled",
            () => BoolValue(Volatile.Read(ref _healthSnapshot)?.KillSwitchEnabled));
        _reconciliationApplicableGauge = _meter.CreateObservableGauge(
            "shadow.private_reconciliation_applicable", static () => 0L);
        _realOrderCount = 0;
    }

    public long RealOrderCount => Interlocked.Read(ref _realOrderCount);

    public long CycleCount => _cycles is null ? 0 : Interlocked.Read(ref _cycleCount);

    public long BlockedCycleCount => Interlocked.Read(ref _blockedCycleCount);

    public long HypotheticalEntryCount => Interlocked.Read(ref _hypotheticalEntryCount);

    private long _cycleCount;

    internal void Record(OperationalShadowCycleResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_disposed)
        {
            return;
        }

        _cycles.Add(1);
        Interlocked.Increment(ref _cycleCount);
        if (!result.Gate.CanCreateHypotheticalEntry)
        {
            _blockedCycles.Add(1);
            Interlocked.Increment(ref _blockedCycleCount);
        }

        if (result.HypotheticalEntryCreated)
        {
            _hypotheticalEntries.Add(1);
            Interlocked.Increment(ref _hypotheticalEntryCount);
        }
    }

    internal void RecordHealth(OperationalShadowHealthSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_disposed)
        {
            Volatile.Write(ref _healthSnapshot, snapshot);
        }
    }

    public OperationalShadowMetricsSnapshot Snapshot() => new(CycleCount,
        BlockedCycleCount, HypotheticalEntryCount, RealOrderCount);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    private static long BoolValue(bool? value) => value == true ? 1 : 0;
}

public sealed record OperationalShadowMetricsSnapshot(
    long CycleCount,
    long BlockedCycleCount,
    long HypotheticalEntryCount,
    long RealOrderCount);

