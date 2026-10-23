using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Canonical decision projection with the complete decision input and the
/// lifecycle checkpoint used to recover it. The projection has no execution
/// or risk side effects.
/// </summary>
public sealed record DecisionProjectionSnapshot
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The reason-code catalog recorded by P08-M02 projections.</summary>
    public const string CurrentReasonCodesVersion = DecisionReasonCodes.CurrentVersion;

    public int FormatVersion { get; }

    public string ReasonCodesVersion { get; }

    public DecisionEngineResult Result { get; }

    public SetupLifecycleConfiguration LifecycleConfiguration { get; }

    public DecisionEngineConfiguration DecisionConfiguration { get; }

    /// <summary>The complete lifecycle state used as the recovery checkpoint.</summary>
    public SetupLifecycleSnapshot LifecycleSnapshot { get; }

    /// <summary>
    /// Lower-case SHA-256 of the canonical projection JSON. The identity itself
    /// is excluded from that JSON.
    /// </summary>
    [JsonIgnore]
    public string SnapshotId { get; }

    [JsonIgnore]
    public string Identity => SnapshotId;

    [JsonIgnore]
    public string DecisionId => Result.Inputs.DecisionId;

    [JsonIgnore]
    public DecisionEngineResult DecisionResult => Result;

    [JsonIgnore]
    public SetupLifecycleSnapshot Lifecycle => LifecycleSnapshot;

    [JsonIgnore]
    public byte[] IdentityBytes => Convert.FromHexString(SnapshotId);

    [JsonIgnore]
    public SetupLifecycleIdentity LifecycleIdentity => LifecycleSnapshot.Identity;

    [JsonIgnore]
    public CausalEventCursor CausalCursor => LifecycleSnapshot.ProcessedCursor;

    [JsonIgnore]
    public CausalEventCursor Cursor => CausalCursor;

    [JsonIgnore]
    public CausalEventCursor CausalPosition => CausalCursor;

    [JsonIgnore]
    public UtcTimestamp AvailabilityTime => LifecycleSnapshot.ProcessedAvailabilityTime;

    [JsonIgnore]
    public ImmutableValueList<SetupLifecycleEventReceipt> Receipts =>
        LifecycleSnapshot.ProcessedEvents;

    [JsonIgnore]
    public bool RecoveryRequired => LifecycleSnapshot.RecoveryRequired;

    [JsonIgnore]
    public SetupLifecycleSnapshot Checkpoint => LifecycleSnapshot;

    public static DecisionProjectionSnapshot Create(DecisionEngineResult result,
        SetupLifecycleConfiguration lifecycleConfiguration,
        DecisionEngineConfiguration decisionConfiguration,
        string reasonCodesVersion = CurrentReasonCodesVersion) => new(
            CurrentFormatVersion, reasonCodesVersion, result, lifecycleConfiguration,
            decisionConfiguration, result.Inputs.Lifecycle);

    public static DecisionProjectionSnapshot Create(DecisionEngineResult result,
        DecisionEngineConfiguration decisionConfiguration,
        SetupLifecycleConfiguration lifecycleConfiguration,
        string reasonCodesVersion = CurrentReasonCodesVersion) => Create(result,
            lifecycleConfiguration, decisionConfiguration, reasonCodesVersion);

    public DecisionProjectionSnapshot(DecisionEngineResult result,
        SetupLifecycleConfiguration lifecycleConfiguration,
        DecisionEngineConfiguration decisionConfiguration,
        string reasonCodesVersion = CurrentReasonCodesVersion)
        : this(CurrentFormatVersion, reasonCodesVersion, result, lifecycleConfiguration,
            decisionConfiguration, result?.Inputs.Lifecycle
                ?? throw new ArgumentNullException(nameof(result)))
    {
    }

    [JsonConstructor]
    public DecisionProjectionSnapshot(int formatVersion, string reasonCodesVersion,
        DecisionEngineResult result, SetupLifecycleConfiguration lifecycleConfiguration,
        DecisionEngineConfiguration decisionConfiguration,
        SetupLifecycleSnapshot lifecycleSnapshot)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The decision projection format version is unsupported.");
        }

        ContractGuard.RequireText(reasonCodesVersion, nameof(reasonCodesVersion));
        DecisionReasonCodes.RequireVersion(reasonCodesVersion);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(lifecycleConfiguration);
        ArgumentNullException.ThrowIfNull(decisionConfiguration);
        ArgumentNullException.ThrowIfNull(lifecycleSnapshot);
        DecisionReasonCodes.RequireKnown(result.Decision.Reasons);
        DecisionReasonCodes.RequireKnown(result.Reasons);

        if (result.Inputs.Lifecycle != lifecycleSnapshot)
        {
            throw new ArgumentException(
                "The projected lifecycle checkpoint must match the decision input.",
                nameof(lifecycleSnapshot));
        }

        if (!StringComparer.Ordinal.Equals(lifecycleSnapshot.RulesVersion,
                lifecycleConfiguration.RulesVersion)
            || !StringComparer.Ordinal.Equals(lifecycleSnapshot.ConfigurationVersion,
                lifecycleConfiguration.ConfigurationVersion))
        {
            throw new ArgumentException(
                "The lifecycle checkpoint does not match its configuration.",
                nameof(lifecycleConfiguration));
        }

        if (!StringComparer.Ordinal.Equals(result.RulesVersion,
                decisionConfiguration.RulesVersion)
            || !StringComparer.Ordinal.Equals(result.ConfigurationVersion,
                decisionConfiguration.ConfigurationVersion))
        {
            throw new ArgumentException(
                "The decision result does not match its configuration.",
                nameof(decisionConfiguration));
        }

        DecisionEngineResult replayed = DecisionEngine.Evaluate(result.Inputs,
            decisionConfiguration);
        if (!EquivalentDecision(result, replayed))
        {
            throw new ArgumentException(
                "The decision projection does not match a fresh Decision Engine evaluation.",
                nameof(result));
        }

        FormatVersion = formatVersion;
        ReasonCodesVersion = reasonCodesVersion;
        Result = result;
        LifecycleConfiguration = lifecycleConfiguration;
        DecisionConfiguration = decisionConfiguration;
        LifecycleSnapshot = lifecycleSnapshot;
        SnapshotId = ComputeIdentity(CoreJsonSerializer.Serialize(this));
    }

    /// <summary>Re-evaluates the saved input with the saved Decision Engine rules.</summary>
    public DecisionEngineResult Reevaluate()
    {
        DecisionEngineResult replayed = DecisionEngine.Evaluate(Result.Inputs,
            DecisionConfiguration);
        if (!EquivalentDecision(Result, replayed))
        {
            throw new InvalidOperationException(
                "Decision projection replay produced a different result.");
        }

        return replayed;
    }

    private static bool EquivalentDecision(DecisionEngineResult expected,
        DecisionEngineResult actual) => expected.Decision == actual.Decision
        && expected.Inputs == actual.Inputs
        && StringComparer.Ordinal.Equals(expected.RulesVersion, actual.RulesVersion)
        && StringComparer.Ordinal.Equals(expected.ConfigurationVersion,
            actual.ConfigurationVersion)
        && expected.Reasons.Equals(actual.Reasons);

    private static string ComputeIdentity(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();
}
