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
}

public sealed record OperationalShadowMetricsSnapshot(
    long CycleCount,
    long BlockedCycleCount,
    long HypotheticalEntryCount,
    long RealOrderCount);

