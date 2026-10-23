using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>One auditable, serialized Decision Engine reason definition.</summary>
public sealed record DecisionReasonCodeDefinition
{
    public string Code { get; }

    public string Meaning { get; }

    [JsonIgnore]
    public string Value => Code;

    [JsonIgnore]
    public string Description => Meaning;

    [JsonConstructor]
    public DecisionReasonCodeDefinition(string code, string meaning)
    {
        ContractGuard.RequireText(code, nameof(code));
        ContractGuard.RequireText(meaning, nameof(meaning));
        Code = code;
        Meaning = meaning;
    }
}

/// <summary>
/// Versioned Decision Engine reason-code catalog. Codes are wire values: their
/// spelling, ordering, and duplicate behavior are part of the contract.
/// </summary>
public static class DecisionReasonCodes
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentVersion = "decision-reasons-v1";

    public static string Version => CurrentVersion;

    public const string LaneANotReady = "lane-a-not-ready";
    public const string LifecycleNotEntryArmed = "lifecycle-not-entry-armed";
    public const string LifecycleRecoveryRequired = "lifecycle-recovery-required";
    public const string LaneAFutureInput = "lane-a-future-input";
    public const string LaneAStaleInput = "lane-a-stale-input";
    public const string LaneBFutureInput = "lane-b-future-input";
    public const string LaneBStaleInput = "lane-b-stale-input";
    public const string LaneBVeto = "lane-b-veto";
    public const string LaneBUnknown = "lane-b-unknown";
    public const string LaneBMissing = "lane-b-missing";
    public const string LaneBNeutralRequiredConfirm = "lane-b-neutral-required-confirm";
    public const string LaneCFutureInput = "lane-c-future-input";
    public const string LaneCStaleInput = "lane-c-stale-input";
    public const string LaneCVeto = "lane-c-veto";
    public const string LaneCUnknown = "lane-c-unknown";
    public const string LaneCMissing = "lane-c-missing";
    public const string LaneCNeutralRequiredConfirm = "lane-c-neutral-required-confirm";
    public const string ShadowProfile = "shadow-profile";
    public const string DecisionAllow = "decision-allow";

    public static ImmutableValueList<string> Codes { get; } = new(
    [
        LaneANotReady,
        LifecycleNotEntryArmed,
        LifecycleRecoveryRequired,
        LaneAFutureInput,
        LaneAStaleInput,
        LaneBFutureInput,
        LaneBStaleInput,
        LaneBVeto,
        LaneBUnknown,
        LaneBMissing,
        LaneBNeutralRequiredConfirm,
        LaneCFutureInput,
        LaneCStaleInput,
        LaneCVeto,
        LaneCUnknown,
        LaneCMissing,
        LaneCNeutralRequiredConfirm,
        ShadowProfile,
        DecisionAllow
    ]);

    public static ImmutableValueList<string> All => Codes;

    public static ImmutableValueList<DecisionReasonCodeDefinition> Catalog => Definitions;

    public static ImmutableValueList<DecisionReasonCodeDefinition> Definitions { get; } = new(
    [
        Definition(LaneANotReady, "Lane A evidence is not ready."),
        Definition(LifecycleNotEntryArmed, "The lifecycle is not entry armed."),
        Definition(LifecycleRecoveryRequired, "The lifecycle requires recovery."),
        Definition(LaneAFutureInput, "Lane A evidence is newer than the lifecycle cursor."),
        Definition(LaneAStaleInput, "Lane A evidence is older than the lifecycle cursor."),
        Definition(LaneBFutureInput, "Lane B evidence is newer than the lifecycle cursor."),
        Definition(LaneBStaleInput, "Lane B evidence is older than the lifecycle cursor."),
        Definition(LaneBVeto, "Lane B vetoed the decision."),
        Definition(LaneBUnknown, "Lane B evidence is unknown."),
        Definition(LaneBMissing, "Required Lane B evidence is missing."),
        Definition(LaneBNeutralRequiredConfirm,
            "Lane B is neutral while confirmation is required."),
        Definition(LaneCFutureInput, "Lane C evidence is newer than the lifecycle cursor."),
        Definition(LaneCStaleInput, "Lane C evidence is older than the lifecycle cursor."),
        Definition(LaneCVeto, "Lane C vetoed the decision."),
        Definition(LaneCUnknown, "Lane C evidence is unknown."),
        Definition(LaneCMissing, "Required Lane C evidence is missing."),
        Definition(LaneCNeutralRequiredConfirm,
            "Lane C is neutral while confirmation is required."),
        Definition(ShadowProfile, "The shadow decision profile produced the outcome."),
        Definition(DecisionAllow, "All configured decision gates passed.")
    ]);

    public static bool IsKnown(string code) => !string.IsNullOrWhiteSpace(code)
        && Codes.Any(candidate => StringComparer.Ordinal.Equals(candidate, code));

    public static void RequireVersion(string version)
    {
        ContractGuard.RequireText(version, nameof(version));
        if (!StringComparer.Ordinal.Equals(version, CurrentVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version,
                $"Decision reason-code version {version} is unsupported.");
        }
    }

    public static void RequireKnown(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        foreach (string reason in reasons)
        {
            if (!IsKnown(reason))
            {
                throw new ArgumentException(
                    $"Decision reason code '{reason}' is not in {CurrentVersion}.",
                    nameof(reasons));
            }
        }
    }

    public static string FutureInput(DecisionLane lane) => lane switch
    {
        DecisionLane.LaneA => LaneAFutureInput,
        DecisionLane.LaneB => LaneBFutureInput,
        DecisionLane.LaneC => LaneCFutureInput,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane,
            "The decision lane is unsupported.")
    };

    public static string StaleInput(DecisionLane lane) => lane switch
    {
        DecisionLane.LaneA => LaneAStaleInput,
        DecisionLane.LaneB => LaneBStaleInput,
        DecisionLane.LaneC => LaneCStaleInput,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane,
            "The decision lane is unsupported.")
    };

    public static string Veto(DecisionLane lane) => lane switch
    {
        DecisionLane.LaneB => LaneBVeto,
        DecisionLane.LaneC => LaneCVeto,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane,
            "The decision lane is unsupported.")
    };

    public static string Unknown(DecisionLane lane) => lane switch
    {
        DecisionLane.LaneB => LaneBUnknown,
        DecisionLane.LaneC => LaneCUnknown,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane,
            "The decision lane is unsupported.")
    };

    public static string Missing(DecisionLane lane) => lane switch
    {
        DecisionLane.LaneB => LaneBMissing,
        DecisionLane.LaneC => LaneCMissing,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane,
            "The decision lane is unsupported.")
    };

    public static string NeutralRequiredConfirm(DecisionLane lane) => lane switch
    {
        DecisionLane.LaneB => LaneBNeutralRequiredConfirm,
        DecisionLane.LaneC => LaneCNeutralRequiredConfirm,
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane,
            "The decision lane is unsupported.")
    };

    private static DecisionReasonCodeDefinition Definition(string code, string meaning) =>
        new(code, meaning);
}
