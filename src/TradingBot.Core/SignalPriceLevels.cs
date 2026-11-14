using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum SignalPriceLevelsState
{
    Ready,
    Unknown
}

/// <summary>Research parameters for one signal family, applied on each enabled timeframe.</summary>
public sealed record SignalPriceLevelParameters
{
    public SetupType Family { get; }
    public decimal MinimumStopAtr { get; }
    public decimal WickBufferAtr { get; }
    public decimal TakeProfit1Risk { get; }
    public decimal TakeProfit2Risk { get; }

    [JsonConstructor]
    public SignalPriceLevelParameters(SetupType family, decimal minimumStopAtr,
        decimal wickBufferAtr, decimal takeProfit1Risk, decimal takeProfit2Risk)
    {
        ContractGuard.RequireDefined(family, nameof(family));
        if (minimumStopAtr <= 0m || wickBufferAtr <= 0m || takeProfit1Risk <= 0m
            || takeProfit2Risk <= takeProfit1Risk)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumStopAtr),
                "Price-level multipliers must be positive and targets strictly ordered.");
        }
        Family = family;
        MinimumStopAtr = minimumStopAtr;
        WickBufferAtr = wickBufferAtr;
        TakeProfit1Risk = takeProfit1Risk;
        TakeProfit2Risk = takeProfit2Risk;
    }
}

/// <summary>Explicit, versioned passive price-level settings for all known families.</summary>
public sealed record SignalPriceLevelPolicy
{
    public const int CurrentFormatVersion = 1;
    public const string CurrentFormulaVersion = "signal-atr-wick-levels-v1";

    public int FormatVersion { get; }
    public string PolicyVersion { get; }
    public string FormulaVersion { get; }
    public int AtrPeriodBars { get; }
    public ImmutableValueList<SignalPriceLevelParameters> Families { get; }

    [JsonConstructor]
    public SignalPriceLevelPolicy(int formatVersion, string policyVersion, string formulaVersion,
        int atrPeriodBars, ImmutableValueList<SignalPriceLevelParameters> families)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ContractGuard.RequireText(policyVersion, nameof(policyVersion));
        if (!StringComparer.Ordinal.Equals(formulaVersion, CurrentFormulaVersion))
        {
            throw new ArgumentException("The price-level formula is unsupported.", nameof(formulaVersion));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(atrPeriodBars);
        ArgumentNullException.ThrowIfNull(families);
        SetupType[] known = Enum.GetValues<SetupType>();
        if (families.Count != known.Length || families.Any(static item => item is null)
            || families.Select(static item => item.Family).Distinct().Count() != known.Length)
        {
            throw new ArgumentException("Every signal family requires exactly one price-level policy.", nameof(families));
        }
        FormatVersion = formatVersion;
        PolicyVersion = policyVersion;
        FormulaVersion = formulaVersion;
        AtrPeriodBars = atrPeriodBars;
        Families = new ImmutableValueList<SignalPriceLevelParameters>(families.OrderBy(static item => item.Family));
    }

    public SignalPriceLevelParameters ForFamily(SetupType family)
    {
        for (int index = 0; index < Families.Count; index++)
        {
            if (Families[index].Family == family)
            {
                return Families[index];
            }
        }
        throw new ArgumentOutOfRangeException(nameof(family));
    }
}

/// <summary>Immutable indicative levels, not orders, fills, or risk authorization.</summary>
public sealed record SignalPriceLevels
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public string PolicyVersion { get; }
    public string FormulaVersion { get; }
    public int AtrPeriodBars { get; }
    public SignalPriceLevelParameters Parameters { get; }
    public string Timeframe { get; }
    public Direction Direction { get; }
    public string SourceEventId { get; }
    public decimal ReferencePrice { get; }
    public decimal SignalBarExtreme { get; }
    public decimal? Atr { get; }
    public SignalPriceLevelsState State { get; }
    public decimal? StopLoss { get; }
    public decimal? TakeProfit1 { get; }
    public decimal? TakeProfit2 { get; }
    public string? UnknownReason { get; }

    [JsonConstructor]
    public SignalPriceLevels(int formatVersion, string policyVersion, string formulaVersion,
        int atrPeriodBars, SignalPriceLevelParameters parameters, string timeframe,
        Direction direction, string sourceEventId, decimal referencePrice, decimal signalBarExtreme,
        decimal? atr, SignalPriceLevelsState state, decimal? stopLoss, decimal? takeProfit1,
        decimal? takeProfit2, string? unknownReason)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ContractGuard.RequireText(policyVersion, nameof(policyVersion));
        if (!StringComparer.Ordinal.Equals(formulaVersion, SignalPriceLevelPolicy.CurrentFormulaVersion))
        {
            throw new ArgumentException("The price-level formula is unsupported.", nameof(formulaVersion));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(atrPeriodBars);
        ArgumentNullException.ThrowIfNull(parameters);
        ContractGuard.RequireDefined(direction, nameof(direction));
        ContractGuard.RequireDefined(state, nameof(state));
        ContractGuard.RequireText(sourceEventId, nameof(sourceEventId));
        if (PublicSignalTimeframes.ContextForSetup(timeframe) is null || referencePrice <= 0m
            || signalBarExtreme <= 0m || atr < 0m
            || (direction == Direction.Long && signalBarExtreme > referencePrice)
            || (direction == Direction.Short && signalBarExtreme < referencePrice))
        {
            throw new ArgumentException("The price-level input is invalid.");
        }
        if (state == SignalPriceLevelsState.Ready)
        {
            if (atr is not > 0m || stopLoss is null || takeProfit1 is null || takeProfit2 is null
                || unknownReason is not null)
            {
                throw new ArgumentException("Ready price levels require complete inputs and prices.");
            }
            (decimal stop, decimal first, decimal second) = CalculateValues(parameters, direction,
                referencePrice, signalBarExtreme, atr.Value);
            if (!HasOrderedPrices(direction, referencePrice, signalBarExtreme, stop, first, second)
                || stop != stopLoss || first != takeProfit1 || second != takeProfit2)
            {
                throw new ArgumentException("The price levels do not match their declared formula.");
            }
        }
        else if (stopLoss is not null || takeProfit1 is not null || takeProfit2 is not null
            || string.IsNullOrWhiteSpace(unknownReason))
        {
            throw new ArgumentException("Unknown price levels require a reason and no prices.");
        }
        FormatVersion = formatVersion;
        PolicyVersion = policyVersion;
        FormulaVersion = formulaVersion;
        AtrPeriodBars = atrPeriodBars;
        Parameters = parameters;
        Timeframe = timeframe;
        Direction = direction;
        SourceEventId = sourceEventId;
        ReferencePrice = referencePrice;
        SignalBarExtreme = signalBarExtreme;
        Atr = atr;
        State = state;
        StopLoss = stopLoss;
        TakeProfit1 = takeProfit1;
        TakeProfit2 = takeProfit2;
        UnknownReason = unknownReason;
    }

    internal static SignalPriceLevels Unknown(SignalPriceLevelPolicy policy,
        SignalPriceLevelParameters parameters, string timeframe, Direction direction,
        ScreenerBar signalBar, decimal? atr, string reason) => new(CurrentFormatVersion,
            policy.PolicyVersion, policy.FormulaVersion, policy.AtrPeriodBars, parameters,
            timeframe, direction, signalBar.Bar.EventId, signalBar.Close.Value,
            direction == Direction.Long ? signalBar.Bar.Low.Value : signalBar.Bar.High.Value,
            atr, SignalPriceLevelsState.Unknown, null, null, null, reason);

    internal static SignalPriceLevels Calculate(SignalPriceLevelPolicy policy,
        SignalPriceLevelParameters parameters, string timeframe, Direction direction,
        ScreenerBar signalBar, decimal atr)
    {
        decimal reference = signalBar.Close.Value;
        decimal extreme = direction == Direction.Long ? signalBar.Bar.Low.Value : signalBar.Bar.High.Value;
        try
        {
            (decimal stop, decimal first, decimal second) = CalculateValues(parameters, direction,
                reference, extreme, atr);
            if (!HasOrderedPrices(direction, reference, extreme, stop, first, second))
            {
                return Unknown(policy, parameters, timeframe, direction, signalBar, atr,
                    "price-levels-unrepresentable-or-nonpositive-price");
            }
            return new SignalPriceLevels(CurrentFormatVersion, policy.PolicyVersion,
                policy.FormulaVersion, policy.AtrPeriodBars, parameters, timeframe, direction,
                signalBar.Bar.EventId, reference, extreme, atr, SignalPriceLevelsState.Ready,
                stop, first, second, null);
        }
        catch (OverflowException)
        {
            return Unknown(policy, parameters, timeframe, direction, signalBar, atr,
                "price-levels-decimal-overflow");
        }
    }

    private static bool HasOrderedPrices(Direction direction, decimal reference, decimal extreme,
        decimal stop, decimal first, decimal second) => direction == Direction.Long
            ? stop > 0m && stop < extreme && stop < reference
                && first > reference && second > first
            : second > 0m && second < first && first < reference
                && stop > reference && stop > extreme;

    private static (decimal Stop, decimal First, decimal Second) CalculateValues(
        SignalPriceLevelParameters parameters, Direction direction, decimal reference,
        decimal extreme, decimal atr)
    {
        checked
        {
            decimal minimumDistance = parameters.MinimumStopAtr * atr;
            decimal buffer = parameters.WickBufferAtr * atr;
            decimal stop = direction == Direction.Long
                ? Math.Min(reference - minimumDistance, extreme - buffer)
                : Math.Max(reference + minimumDistance, extreme + buffer);
            decimal risk = Math.Abs(reference - stop);
            decimal sign = direction == Direction.Long ? 1m : -1m;
            return (stop, reference + sign * risk * parameters.TakeProfit1Risk,
                reference + sign * risk * parameters.TakeProfit2Risk);
        }
    }
}

/// <summary>Calculates levels solely from the signal bar and its causal channel ATR.</summary>
public static class SignalPriceLevelEvaluator
{
    public static SignalPriceLevels Evaluate(PriceContextFeatureResult context,
        ScreenerBar signalBar, SetupType family, Direction direction, SignalPriceLevelPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(signalBar);
        ArgumentNullException.ThrowIfNull(policy);
        ContractGuard.RequireDefined(family, nameof(family));
        ContractGuard.RequireDefined(direction, nameof(direction));
        SignalPriceLevelParameters parameters = policy.ForFamily(family);
        string timeframe = context.Input.SetupTimeframe;
        ImmutableValueList<ScreenerBar> provenance = context.Channel.Provenance;
        ScreenerBar? last = provenance.Count == 0 ? null : provenance[provenance.Count - 1];
        if (signalBar.CloseTime.Value > context.Cursor.AsOf.Value
            || signalBar.EventTime.Value > context.Cursor.AsOf.Value
            || signalBar.AvailabilityTime.Value > context.Cursor.AsOf.Value
            || signalBar.StableSequence > context.Cursor.MaxStableSequence
            || last is null || !last.Equals(signalBar))
        {
            return SignalPriceLevels.Unknown(policy, parameters, timeframe, direction, signalBar,
                null, "price-levels-signal-bar-unavailable");
        }
        if (context.Configuration.WidthKind != PriceContextChannelWidthKind.Atr
            || context.Configuration.AtrMode != PriceContextAtrMode.Rma
            || context.Configuration.ChannelWidthPeriod != policy.AtrPeriodBars)
        {
            return SignalPriceLevels.Unknown(policy, parameters, timeframe, direction, signalBar,
                null, "price-levels-atr-configuration-mismatch");
        }
        if (context.Channel.State != PriceContextFeatureState.Ready || context.Channel.Atr is not > 0m)
        {
            return SignalPriceLevels.Unknown(policy, parameters, timeframe, direction, signalBar,
                context.Channel.Atr is < 0m ? null : context.Channel.Atr, "price-levels-atr-unavailable");
        }
        return SignalPriceLevels.Calculate(policy, parameters, timeframe, direction,
            signalBar, context.Channel.Atr.Value);
    }
}
