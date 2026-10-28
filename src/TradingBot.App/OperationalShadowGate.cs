using System.Collections.ObjectModel;

namespace TradingBot.App;

public enum OperationalShadowBlockReason
{
    None,
    ConfigurationUnknown,
    DataStale,
    ReconnectPending,
    RecoveryPending,
    CoverageUnknown,
    CoverageIncomplete,
    PersistenceUnavailable,
    KillSwitchEnabled,
    RequiredFeedCoverageUnknown = CoverageUnknown,
    RequiredFeedCoverageIncomplete = CoverageIncomplete
}

public sealed record OperationalShadowGateInput(bool ConfigurationValid, bool DataFresh,
    bool ReconnectReady, bool RecoveryComplete, bool PersistenceHealthy,
    bool KillSwitchEnabled)
{
    public bool CoverageKnown { get; init; } = true;

    public bool CoverageComplete { get; init; } = true;
}

public sealed record OperationalShadowGateResult
{
    public bool CanCreateHypotheticalEntry { get; }

    public ReadOnlyCollection<OperationalShadowBlockReason> BlockReasons { get; }

    public long RealOrderCount { get; }

    public OperationalShadowGateResult(bool canCreateHypotheticalEntry,
        IEnumerable<OperationalShadowBlockReason> blockReasons)
    {
        ArgumentNullException.ThrowIfNull(blockReasons);
        OperationalShadowBlockReason[] snapshot = blockReasons.Distinct().ToArray();
        if (snapshot.Any(static reason => reason == OperationalShadowBlockReason.None)
            || (canCreateHypotheticalEntry != (snapshot.Length == 0)))
        {
            throw new ArgumentException("The gate result and block reasons must agree.",
                nameof(blockReasons));
        }

        CanCreateHypotheticalEntry = canCreateHypotheticalEntry;
        BlockReasons = Array.AsReadOnly(snapshot);
        RealOrderCount = 0;
    }
}

public static class OperationalShadowGate
{
    public static OperationalShadowGateResult Evaluate(OperationalShadowGateInput input)
    {
        List<OperationalShadowBlockReason> reasons = [];
        if (!input.ConfigurationValid)
        {
            reasons.Add(OperationalShadowBlockReason.ConfigurationUnknown);
        }

        if (!input.DataFresh)
        {
            reasons.Add(OperationalShadowBlockReason.DataStale);
        }

        if (!input.ReconnectReady)
        {
            reasons.Add(OperationalShadowBlockReason.ReconnectPending);
        }

        if (!input.RecoveryComplete)
        {
            reasons.Add(OperationalShadowBlockReason.RecoveryPending);
        }

        if (!input.CoverageKnown)
        {
            reasons.Add(OperationalShadowBlockReason.CoverageUnknown);
        }
        else if (!input.CoverageComplete)
        {
            reasons.Add(OperationalShadowBlockReason.CoverageIncomplete);
        }

        if (!input.PersistenceHealthy)
        {
            reasons.Add(OperationalShadowBlockReason.PersistenceUnavailable);
        }

        if (input.KillSwitchEnabled)
        {
            reasons.Add(OperationalShadowBlockReason.KillSwitchEnabled);
        }

        return new OperationalShadowGateResult(reasons.Count == 0, reasons);
    }
}
