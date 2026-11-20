namespace TradingBot.Core;

/// <summary>Explicit research settings for the Donchian-flow hypothesis.</summary>
public sealed record DonchianFlowSignalPolicy(
    decimal BodyFloor, int? StopChannelBars = null, int? TargetChannelBars = null)
{
    public const string AlgorithmVersion = "donchian-flow-55-v1";
}

public enum DonchianFlowSignalState
{
    Ready,
    UnknownWarmup,
    UnknownConfiguration,
    UnknownQuality,
    UnknownZeroDenominator,
    UnknownOverflow
}

/// <summary>Passive result for one causal closed-bar cut; it does not represent an order.</summary>
public sealed record DonchianFlowSignalResult(
    DonchianFlowSignalState State,
    Direction? Direction,
    ScreenerBar? SignalBar,
    ScreenerBar? UpperAnchor,
    ScreenerBar? LowerAnchor,
    decimal? Upper55,
    decimal? Lower55,
    decimal? FastWma,
    decimal? SlowWma,
    decimal? VolumeMean,
    decimal? Lower13,
    decimal? Upper13,
    decimal? Lower21,
    decimal? Upper21,
    decimal? Lower89,
    decimal? Upper89,
    decimal? Lower144,
    decimal? Upper144,
    string? UnknownReason);

/// <summary>Closed-bar hypothesis based on the author's public description.</summary>
public static class DonchianFlowSignalEvaluator
{
    private const int ChannelBars = 55;
    private const int NormalizationBars = 10;
    private const int SlowBars = 34;
    private const int FastBars = 9;
    private const int VolumeBars = 20;
    private const int RequiredBars = ChannelBars + 1;

    public static DonchianFlowSignalResult Evaluate(IReadOnlyList<ScreenerBar> bars,
        CausalEventCursor cursor, DonchianFlowSignalPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.BodyFloor <= 0m || policy.StopChannelBars is not (null or 13 or 21)
            || policy.TargetChannelBars is not (null or 89 or 144))
        {
            return Unknown(DonchianFlowSignalState.UnknownConfiguration, "donchian-policy-invalid");
        }

        int end = bars.Count - 1;
        while (end >= 0 && !bars[end].IsEligible(cursor))
        {
            end--;
        }
        TimeSpan duration = end >= 0
            ? bars[end].Bar.CloseTime.Value - bars[end].Bar.OpenTime.Value : TimeSpan.Zero;
        string historyKind = duration == TimeSpan.FromMinutes(30) ? "30m" : "hourly";
        if (end + 1 < RequiredBars)
        {
            return Unknown(DonchianFlowSignalState.UnknownWarmup, $"{historyKind}-history-incomplete");
        }

        if (duration != TimeSpan.FromMinutes(30) && duration != TimeSpan.FromHours(1))
        {
            return Unknown(DonchianFlowSignalState.UnknownQuality, "donchian-timeframe-invalid");
        }

        int first = end - ChannelBars;
        for (int index = first; index <= end; index++)
        {
            ScreenerBar bar = bars[index];
            if (!bar.IsEligible(cursor)
                || bar.Bar.CloseTime.Value - bar.Bar.OpenTime.Value != duration
                || bar.InstrumentId != bars[end].InstrumentId
                || (index > first && bars[index - 1].Bar.CloseTime.Value != bar.Bar.OpenTime.Value))
            {
                return Unknown(DonchianFlowSignalState.UnknownQuality, $"{historyKind}-history-invalid");
            }
        }

        try
        {
            return Calculate(bars, end, policy.BodyFloor, cursor);
        }
        catch (OverflowException)
        {
            (decimal? lower, decimal? upper) = Channel(bars, end, ChannelBars, cursor);
            return new DonchianFlowSignalResult(DonchianFlowSignalState.UnknownOverflow,
                null, bars[end], null, null, upper, lower, null, null, null,
                null, null, null, null, null, null, null, null, "flow-arithmetic-overflow");
        }
    }

    private static DonchianFlowSignalResult Calculate(IReadOnlyList<ScreenerBar> bars,
        int end, decimal bodyFloor, CausalEventCursor cursor)
    {
        ScreenerBar upper = bars[end - ChannelBars];
        ScreenerBar lower = upper;
        for (int index = end - ChannelBars + 1; index < end; index++)
        {
            if (bars[index].Bar.High.Value > upper.Bar.High.Value)
            {
                upper = bars[index];
            }
            if (bars[index].Bar.Low.Value < lower.Bar.Low.Value)
            {
                lower = bars[index];
            }
        }

        Span<decimal> flows = stackalloc decimal[SlowBars + NormalizationBars - 1];
        int start = end - flows.Length + 1;
        decimal rolling = 0m;
        decimal slowNumerator = 0m;
        decimal fastNumerator = 0m;
        for (int index = 0; index < flows.Length; index++)
        {
            ScreenerBar bar = bars[start + index];
            decimal body = Math.Abs(checked(bar.Bar.Close.Value - bar.Bar.Open.Value));
            decimal flow = checked(bar.Bar.Volume.Value / Math.Max(body, bodyFloor));
            flows[index] = flow;
            rolling = checked(rolling + flow);
            if (index >= NormalizationBars)
            {
                rolling = checked(rolling - flows[index - NormalizationBars]);
            }
            if (index < NormalizationBars - 1)
            {
                continue;
            }
            decimal meanFlow = rolling / NormalizationBars;
            if (meanFlow == 0m)
            {
                return new DonchianFlowSignalResult(DonchianFlowSignalState.UnknownZeroDenominator,
                    null, bars[end], upper, lower, upper.Bar.High.Value, lower.Bar.Low.Value,
                    null, null, null, null, null, null, null, null, null, null, null,
                    "flow-average-zero");
            }

            int slowWeight = index - NormalizationBars + 2;
            decimal normalized = checked((flow / meanFlow) * bar.Bar.Close.Value);
            slowNumerator = checked(slowNumerator + checked(normalized * slowWeight));
            if (slowWeight > SlowBars - FastBars)
            {
                fastNumerator = checked(fastNumerator + checked(normalized * (slowWeight - (SlowBars - FastBars))));
            }
        }

        decimal fast = fastNumerator / (FastBars * (FastBars + 1) / 2);
        decimal slow = slowNumerator / (SlowBars * (SlowBars + 1) / 2);
        decimal volumeSum = 0m;
        for (int index = end - VolumeBars + 1; index <= end; index++)
        {
            volumeSum = checked(volumeSum + bars[index].Bar.Volume.Value);
        }
        decimal volumeMean = volumeSum / VolumeBars;
        ScreenerBar signal = bars[end];
        bool confirmedVolume = signal.Bar.Volume.Value > checked(1.5m * volumeMean);
        bool brokeUpper = signal.Bar.High.Value > upper.Bar.High.Value;
        bool brokeLower = signal.Bar.Low.Value < lower.Bar.Low.Value;
        Direction? direction = brokeUpper && brokeLower ? null
            : confirmedVolume && fast > slow && brokeUpper ? Direction.Long
            : confirmedVolume && fast < slow && brokeLower ? Direction.Short : null;
        (decimal? lower13, decimal? upper13) = Channel(bars, end, 13, cursor);
        (decimal? lower21, decimal? upper21) = Channel(bars, end, 21, cursor);
        (decimal? lower89, decimal? upper89) = Channel(bars, end, 89, cursor);
        (decimal? lower144, decimal? upper144) = Channel(bars, end, 144, cursor);
        return new DonchianFlowSignalResult(DonchianFlowSignalState.Ready, direction,
            signal, upper, lower, upper.Bar.High.Value, lower.Bar.Low.Value,
            fast, slow, volumeMean, lower13, upper13, lower21, upper21,
            lower89, upper89, lower144, upper144,
            brokeUpper && brokeLower ? "simultaneous-opposite-breakouts" : null);
    }

    private static (decimal? Lower, decimal? Upper) Channel(IReadOnlyList<ScreenerBar> bars,
        int end, int period, CausalEventCursor cursor)
    {
        if (end < period)
        {
            return (null, null);
        }
        decimal low = decimal.MaxValue;
        decimal high = decimal.MinValue;
        TimeSpan duration = bars[end].Bar.CloseTime.Value - bars[end].Bar.OpenTime.Value;
        for (int index = end - period; index < end; index++)
        {
            ScreenerBar bar = bars[index];
            if (!bar.IsEligible(cursor) || bar.InstrumentId != bars[end].InstrumentId
                || bar.Bar.CloseTime.Value - bar.Bar.OpenTime.Value != duration
                || bar.Bar.CloseTime.Value != bars[index + 1].Bar.OpenTime.Value)
            {
                return (null, null);
            }
            low = Math.Min(low, bar.Bar.Low.Value);
            high = Math.Max(high, bar.Bar.High.Value);
        }
        return (low, high);
    }

    private static DonchianFlowSignalResult Unknown(DonchianFlowSignalState state, string reason) =>
        new(state, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null, reason);
}
