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

/// <summary>Why a required public stream is not covered by causal evidence.</summary>
public sealed record OperationalShadowMissingStreamReason(string Stream, string Reason);

/// <summary>Read-only health view supplied by a market-data adapter.</summary>
public interface IOperationalShadowHealthSource
{
    /// <summary>
    /// Unique identities of the required public streams. Null means that the
    /// source cannot establish a coverage denominator and coverage is unknown.
    /// </summary>
    IReadOnlyCollection<string>? RequiredFeedStreams => null;

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
    /// <summary>Number of unique required streams in the source contract.</summary>
    public int ExpectedFeedCount { get; init; }

    /// <summary>Number of unique required streams with receive and availability evidence.</summary>
    public int CoveredFeedCount { get; init; }

    /// <summary>CoveredFeedCount / ExpectedFeedCount, or null when unknown/empty.</summary>
    public double? CoverageRatio { get; init; }

    /// <summary>Whether the source supplied a known required-stream set.</summary>
    public bool CoverageKnown { get; init; }

    /// <summary>Whether this source participates in required-stream coverage.</summary>
    public bool CoverageApplicable { get; init; }

    /// <summary>Reasons for each required stream missing coverage evidence.</summary>
    public IReadOnlyList<OperationalShadowMissingStreamReason> MissingStreamReasons { get; init; } =
        Array.Empty<OperationalShadowMissingStreamReason>();

    /// <summary>Run identity and monotonic durable evidence sequence.</summary>
    public string RunId { get; init; } = string.Empty;

    /// <summary>Health evidence sequence within RunId.</summary>
    public long HealthSequence { get; init; } = -1;

    /// <summary>Nullable link to the causal shadow checkpoint represented by this snapshot.</summary>
    public string? CausalCheckpointLink { get; init; }

    public int ExpectedRequiredFeedCount => ExpectedFeedCount;

    public int CoveredRequiredFeedCount => CoveredFeedCount;

    public double? RequiredFeedCoverageRatio => CoverageRatio;

    public bool CoverageComplete => CoverageKnown
        && (!CoverageApplicable || (ExpectedFeedCount > 0
            && CoveredFeedCount == ExpectedFeedCount));

    internal static OperationalShadowHealthSnapshot Unknown(DateTimeOffset now) => new(now,
        OperationalShadowHealthStatus.Unknown, Array.Empty<OperationalShadowFeedHealth>(),
        true, false, true, false, false, false, false, false,
        ["health-unknown"], OperationalShadowReconciliationStatus.NotApplicableInShadow, 0)
    {
        CoverageKnown = false,
        CoverageRatio = null,
        MissingStreamReasons = Array.Empty<OperationalShadowMissingStreamReason>()
    };
}
