namespace TradingBot.App;

public enum OperationalShadowHealthStatus
{
    Unknown,
    Healthy,
    Degraded
}

public enum OperationalShadowReconciliationStatus
{
    NotApplicableInShadow
}

/// <summary>Health of one required public market-data feed.</summary>
public sealed record OperationalShadowFeedHealth(string Stream,
    bool Connected, bool Fresh, bool GapDetected, bool Reconnecting,
    bool RecoveryComplete, DateTimeOffset? LastReceiveTime,
    DateTimeOffset? LastAvailabilityTime);

/// <summary>Read-only health view supplied by a market-data adapter.</summary>
public interface IOperationalShadowHealthSource
{
    IReadOnlyList<OperationalShadowFeedHealth> GetHealth(DateTimeOffset checkedAt,
        TimeSpan staleAfter);

    event Action? HealthChanged;
}

/// <summary>Immutable operational view. It grants no trading or order authority.</summary>
public sealed record OperationalShadowHealthSnapshot(
    DateTimeOffset CheckedAt,
    OperationalShadowHealthStatus Status,
    IReadOnlyList<OperationalShadowFeedHealth> Feeds,
    bool DataStale,
    bool SequenceGap,
    bool Reconnecting,
    bool RecoveryComplete,
    bool PersistenceHealthy,
    bool KillSwitchKnown,
    bool KillSwitchEnabled,
    bool CanCreateHypotheticalEntry,
    IReadOnlyList<string> BlockReasons,
    OperationalShadowReconciliationStatus ReconciliationStatus,
    long RealOrderCount)
{
    internal static OperationalShadowHealthSnapshot Unknown(DateTimeOffset now) => new(now,
        OperationalShadowHealthStatus.Unknown, Array.Empty<OperationalShadowFeedHealth>(),
        true, false, true, false, false, false, false, false,
        ["health-unknown"], OperationalShadowReconciliationStatus.NotApplicableInShadow, 0);
}
