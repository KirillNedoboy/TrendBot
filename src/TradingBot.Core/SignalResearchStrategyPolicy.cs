using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Explicit, independent public-research strategy settings.</summary>
public sealed record SignalResearchStrategyPolicy
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public string PolicyVersion { get; }
    public ImmutableValueList<string> BoundaryTimeframes { get; }
    public ImmutableValueList<string> CenterRetestTimeframes { get; }
    public ImmutableValueList<string> FailedReclaimTimeframes { get; }
    public ImmutableValueList<string> BreakoutRetestTimeframes { get; }
    public ImmutableValueList<string> FlowStochasticTimeframes { get; }
    public ImmutableValueList<string> FlowRsiTimeframes { get; }
    public ImmutableValueList<string> FlowAtrTimeframes { get; }
    public ImmutableValueList<string> CompressionTimeframes { get; }
    public int StructuralMaximumAgeBars { get; }
    public int FlowLookbackBars { get; }
    public int FlowSmoothingBars { get; }
    public decimal FlowLowerExtreme { get; }
    public decimal FlowUpperExtreme { get; }
    public decimal FlowAtrScale { get; }
    public int CompressionLookbackBars { get; }

    [JsonConstructor]
    public SignalResearchStrategyPolicy(int formatVersion, string policyVersion,
        ImmutableValueList<string> boundaryTimeframes,
        ImmutableValueList<string> centerRetestTimeframes,
        ImmutableValueList<string> failedReclaimTimeframes,
        ImmutableValueList<string> breakoutRetestTimeframes,
        ImmutableValueList<string> flowStochasticTimeframes,
        ImmutableValueList<string> flowRsiTimeframes,
        ImmutableValueList<string> flowAtrTimeframes,
        ImmutableValueList<string> compressionTimeframes, int structuralMaximumAgeBars,
        int flowLookbackBars, int flowSmoothingBars, decimal flowLowerExtreme,
        decimal flowUpperExtreme, decimal flowAtrScale, int compressionLookbackBars)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ContractGuard.RequireText(policyVersion, nameof(policyVersion));
        if (structuralMaximumAgeBars <= 0 || flowLookbackBars < 2
            || flowSmoothingBars <= 0 || flowLowerExtreme <= 0m
            || flowUpperExtreme >= 100m || flowLowerExtreme >= flowUpperExtreme
            || flowAtrScale <= 0m || compressionLookbackBars < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(structuralMaximumAgeBars),
                "Research strategy periods and thresholds must be explicit and valid.");
        }
        FormatVersion = formatVersion;
        PolicyVersion = policyVersion;
        BoundaryTimeframes = ValidateTimeframes(boundaryTimeframes);
        CenterRetestTimeframes = ValidateTimeframes(centerRetestTimeframes);
        FailedReclaimTimeframes = ValidateTimeframes(failedReclaimTimeframes);
        BreakoutRetestTimeframes = ValidateTimeframes(breakoutRetestTimeframes);
        FlowStochasticTimeframes = ValidateTimeframes(flowStochasticTimeframes);
        FlowRsiTimeframes = ValidateTimeframes(flowRsiTimeframes);
        FlowAtrTimeframes = ValidateTimeframes(flowAtrTimeframes);
        CompressionTimeframes = ValidateTimeframes(compressionTimeframes);
        StructuralMaximumAgeBars = structuralMaximumAgeBars;
        FlowLookbackBars = flowLookbackBars;
        FlowSmoothingBars = flowSmoothingBars;
        FlowLowerExtreme = flowLowerExtreme;
        FlowUpperExtreme = flowUpperExtreme;
        FlowAtrScale = flowAtrScale;
        CompressionLookbackBars = compressionLookbackBars;
    }

    private static ImmutableValueList<string> ValidateTimeframes(
        ImmutableValueList<string> timeframes)
    {
        ArgumentNullException.ThrowIfNull(timeframes);
        if (timeframes.Any(static timeframe => PublicSignalTimeframes.ContextForSetup(timeframe) is null)
            || timeframes.Distinct(StringComparer.Ordinal).Count() != timeframes.Count)
        {
            throw new ArgumentException("Research strategy timeframes must be unique and supported.",
                nameof(timeframes));
        }
        return new ImmutableValueList<string>(timeframes.Order(StringComparer.Ordinal));
    }
}
