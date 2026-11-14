namespace TradingBot.Core;

public static partial class SignalResearchStrategyEvaluator
{
    private static void EvaluateWavesAndCompression(ScreenerBar[] bars,
        PriceContextFeatureConfiguration configuration, SignalResearchStrategyPolicy policy,
        bool stochasticEnabled, bool rsiEnabled, bool atrEnabled, bool compressionEnabled,
        List<SignalResearchStrategyCandidate> candidates, List<string> unknown)
    {
        int period = policy.FlowLookbackBars;
        int[] highs = new int[bars.Length];
        int[] lows = new int[bars.Length];
        int highHead = 0, highTail = 0, lowHead = 0, lowTail = 0;
        RunningAverage gains = new(period, rma: true);
        RunningAverage losses = new(period, rma: true);
        RunningAverage flowAtr = new(period, rma: true);
        RunningAverage flowBasis = new(period, rma: false);
        WaveState stochastic = new(policy.FlowSmoothingBars);
        WaveState rsi = new(policy.FlowSmoothingBars);
        WaveState atr = new(policy.FlowSmoothingBars);

        bool compressionConfigured = compressionEnabled
            && configuration.CenterKind == PriceContextChannelCenterKind.Ema
            && configuration.WidthKind == PriceContextChannelWidthKind.Atr
            && configuration.AtrMode == PriceContextAtrMode.Rma
            && configuration.CenterPeriod is > 0 && configuration.ChannelWidthPeriod is > 0
            && configuration.ChannelWidthMultiplier is > 0m;
        RunningAverage center = new(configuration.CenterPeriod.GetValueOrDefault(1), rma: false);
        RunningAverage widthAtr = new(configuration.ChannelWidthPeriod.GetValueOrDefault(1), rma: true);
        decimal?[] widths = compressionConfigured ? new decimal?[bars.Length] : [];
        if (compressionEnabled && !compressionConfigured)
        {
            unknown.Add("research-compression-channel-configuration-unsupported");
        }

        for (int index = 0; index < bars.Length; index++)
        {
            ScreenerBar bar = bars[index];
            decimal close = bar.Close.Value;
            decimal previous = index == 0 ? close : bars[index - 1].Close.Value;
            decimal change = checked(close - previous);
            decimal trueRange = Math.Max(checked(bar.Bar.High.Value - bar.Bar.Low.Value),
                Math.Max(Math.Abs(checked(bar.Bar.High.Value - previous)),
                    Math.Abs(checked(bar.Bar.Low.Value - previous))));
            decimal? currentFlowAtr = flowAtr.Add(trueRange);
            decimal? currentFlowBasis = flowBasis.Add(close);
            decimal? gain = index == 0 ? null : gains.Add(Math.Max(change, 0m));
            decimal? loss = index == 0 ? null : losses.Add(Math.Max(-change, 0m));

            while (highHead < highTail && bars[highs[highTail - 1]].Bar.High.Value <= bar.Bar.High.Value)
            {
                highTail--;
            }
            highs[highTail++] = index;
            while (highHead < highTail && highs[highHead] <= index - period)
            {
                highHead++;
            }
            while (lowHead < lowTail && bars[lows[lowTail - 1]].Bar.Low.Value >= bar.Bar.Low.Value)
            {
                lowTail--;
            }
            lows[lowTail++] = index;
            while (lowHead < lowTail && lows[lowHead] <= index - period)
            {
                lowHead++;
            }

            decimal? stochasticRaw = null;
            if (index + 1 >= period)
            {
                decimal high = bars[highs[highHead]].Bar.High.Value;
                decimal low = bars[lows[lowHead]].Bar.Low.Value;
                if (high > low)
                {
                    stochasticRaw = Clamp(checked(100m * (close - low) / (high - low)));
                }
            }
            decimal? rsiRaw = gain is not null && loss is not null && gain + loss > 0m
                ? Clamp(checked(100m * gain.Value / (gain.Value + loss.Value))) : null;
            decimal? atrRaw = currentFlowAtr is > 0m && currentFlowBasis is not null
                ? Clamp(checked(50m + 50m * (close - currentFlowBasis.Value)
                    / (currentFlowAtr.Value * policy.FlowAtrScale))) : null;
            bool current = index == bars.Length - 1;
            if (stochasticEnabled)
            {
                stochastic.Observe(stochasticRaw, bar, current, policy,
                    SetupType.FlowStochasticRecovery, "flow-stochastic-recovery-v1", candidates);
            }
            if (rsiEnabled)
            {
                rsi.Observe(rsiRaw, bar, current, policy,
                    SetupType.FlowRsiRecovery, "flow-rsi-recovery-v1", candidates);
            }
            if (atrEnabled)
            {
                atr.Observe(atrRaw, bar, current, policy,
                    SetupType.FlowAtrRecovery, "flow-atr-recovery-v1", candidates);
            }
            if (compressionConfigured)
            {
                decimal? centerValue = center.Add(close);
                decimal? widthValue = widthAtr.Add(trueRange);
                if (centerValue is > 0m && widthValue is not null
                    && index >= configuration.ChannelWidthPeriod!.Value)
                {
                    widths[index] = checked(2m * widthValue.Value
                        * configuration.ChannelWidthMultiplier!.Value / centerValue.Value);
                }
            }
        }

        if (stochasticEnabled && !stochastic.IsReady)
        {
            unknown.Add("research-flow-stochastic-warmup-or-zero-range");
        }
        if (rsiEnabled && !rsi.IsReady)
        {
            unknown.Add("research-flow-rsi-warmup-or-zero-movement");
        }
        if (atrEnabled && !atr.IsReady)
        {
            unknown.Add("research-flow-atr-warmup-or-zero-atr");
        }
        if (compressionConfigured)
        {
            EvaluateCompression(bars, widths, policy.CompressionLookbackBars, candidates, unknown);
        }
    }

    private static void EvaluateCompression(ScreenerBar[] bars, decimal?[] widths,
        int lookback, List<SignalResearchStrategyCandidate> candidates, List<string> unknown)
    {
        int current = bars.Length - 1;
        int compressed = current - 1;
        if (compressed < lookback || widths[current] is not { } currentWidth
            || widths[compressed] is not { } compressedWidth)
        {
            unknown.Add("research-compression-warmup");
            return;
        }
        decimal minimum = decimal.MaxValue;
        for (int index = compressed - lookback; index < compressed; index++)
        {
            if (widths[index] is not { } value)
            {
                unknown.Add("research-compression-warmup");
                return;
            }
            minimum = Math.Min(minimum, value);
        }
        if (compressedWidth >= minimum || currentWidth <= compressedWidth)
        {
            return;
        }
        ScreenerBar anchor = bars[compressed];
        ScreenerBar signal = bars[current];
        Direction? direction = signal.Close.Value > anchor.Bar.High.Value ? Direction.Long
            : signal.Close.Value < anchor.Bar.Low.Value ? Direction.Short : null;
        if (direction is not null)
        {
            candidates.Add(Candidate(SetupType.VolatilityCompressionExpansion, direction.Value,
                "volatility-compression-expansion-v1", anchor, signal,
                compressedWidth, anchor.Bar.EventId, "compressed-width", currentWidth));
        }
    }

    private static decimal Clamp(decimal value) => Math.Clamp(value, 0m, 100m);

    private struct RunningAverage(int period, bool rma)
    {
        private int _count;
        private decimal _sum;
        private decimal _value;

        public decimal? Add(decimal value)
        {
            if (_count < period)
            {
                _sum = checked(_sum + value);
                _count++;
                if (_count < period)
                {
                    return null;
                }
                _value = checked(_sum / period);
                return _value;
            }
            decimal adjustment = rma ? checked((value - _value) / period)
                : checked(2m / (period + 1m) * (value - _value));
            _value = checked(_value + adjustment);
            return _value;
        }
    }

    private struct WaveState(int smoothingBars)
    {
        private RunningAverage _smooth = new(smoothingBars, rma: false);
        private ScreenerBar? _longAnchor;
        private ScreenerBar? _shortAnchor;
        public bool IsReady { get; private set; }

        public void Observe(decimal? raw, ScreenerBar bar, bool current,
            SignalResearchStrategyPolicy policy, SetupType family, string version,
            List<SignalResearchStrategyCandidate> candidates)
        {
            if (raw is null)
            {
                _smooth = new RunningAverage(smoothingBars, rma: false);
                _longAnchor = null;
                _shortAnchor = null;
                IsReady = false;
                return;
            }
            decimal? wave = _smooth.Add(raw.Value);
            IsReady = wave is not null;
            if (wave is null)
            {
                return;
            }
            if (wave < policy.FlowLowerExtreme)
            {
                _longAnchor ??= bar;
            }
            else if (_longAnchor is not null && wave > policy.FlowLowerExtreme)
            {
                if (current && wave < policy.FlowUpperExtreme)
                {
                    candidates.Add(Candidate(family, Direction.Long, version,
                        _longAnchor, bar, policy.FlowLowerExtreme, _longAnchor.Bar.EventId,
                        "extreme", wave.Value));
                }
                _longAnchor = null;
            }
            if (wave > policy.FlowUpperExtreme)
            {
                _shortAnchor ??= bar;
            }
            else if (_shortAnchor is not null && wave < policy.FlowUpperExtreme)
            {
                if (current && wave > policy.FlowLowerExtreme)
                {
                    candidates.Add(Candidate(family, Direction.Short, version,
                        _shortAnchor, bar, policy.FlowUpperExtreme, _shortAnchor.Bar.EventId,
                        "extreme", wave.Value));
                }
                _shortAnchor = null;
            }
        }
    }
}
