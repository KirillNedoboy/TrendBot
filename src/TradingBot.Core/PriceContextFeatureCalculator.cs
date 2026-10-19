using System.Globalization;
using System.Numerics;

namespace TradingBot.Core;

/// <summary>
/// Calculates the price-context v2 features from one immutable causal snapshot.
/// The calculator has no clock, I/O, persistence, ranking, or execution side
/// effects. Setup and HTF histories are evaluated independently.
/// </summary>
public static class PriceContextFeatureCalculator
{
    public const string CurrentAlgorithmVersion = "price-context-v2";

    public static PriceContextFeatureResult Calculate(PriceContextFeatureInput input,
        CausalEventCursor cursor, PriceContextFeatureConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(configuration);

        List<string> reasons = [];
        PreparedHistory setup = PrepareHistory(input.SetupBars, input.SetupHistoryStart,
            input.SetupTimeframe, cursor, reasons);
        PreparedHistory htf = PrepareHistory(input.HtfBars, input.HtfHistoryStart,
            input.HtfTimeframe, cursor, reasons);
        PreparedTrades trades = PrepareTrades(input.PriceVolumeObservations,
            input.SetupHistoryStart, cursor, reasons);

        List<string> configurationReasons = ValidateConfiguration(input, configuration);
        reasons.AddRange(configurationReasons);
        PriceContextObservationQuality quality = new(setup.Bars, htf.Bars, trades.Observations,
            reasons);

        bool commonConfigurationValid = !configurationReasons.Any(static reason =>
            reason.StartsWith("configuration-timeframe", StringComparison.Ordinal)
            || reason.StartsWith("configuration-algorithm-version", StringComparison.Ordinal));
        bool inputHistoryInvalid = setup.HistoryStartAfterCursor || htf.HistoryStartAfterCursor;
        bool blockedByQuality = HasBlockingQuality(reasons);

        PriceContextConfirmedPivots pivots;
        PriceContextStructuralLevels levels;
        PriceContextChannel channel;
        PriceContextLocalTrend localTrend;
        PriceContextHtfContext htfContext;

        if (!commonConfigurationValid)
        {
            pivots = UnknownPivots(PriceContextFeatureState.UnknownConfiguration,
                configuration, setup.Bars.Length, configurationReasons);
            levels = UnknownLevels(PriceContextFeatureState.UnknownConfiguration,
                configuration, setup.Bars.Length, configurationReasons);
            channel = UnknownChannel(PriceContextFeatureState.UnknownConfiguration,
                configuration, setup.Bars.Length, configurationReasons);
            localTrend = UnknownTrend(PriceContextFeatureState.UnknownConfiguration,
                configurationReasons);
            htfContext = UnknownHtf(PriceContextFeatureState.UnknownConfiguration,
                configurationReasons);
        }
        else if (inputHistoryInvalid)
        {
            pivots = UnknownPivots(PriceContextFeatureState.UnknownInput,
                configuration, setup.Bars.Length, ["history-start-after-cursor"]);
            levels = UnknownLevels(PriceContextFeatureState.UnknownInput,
                configuration, setup.Bars.Length, ["history-start-after-cursor"]);
            channel = UnknownChannel(PriceContextFeatureState.UnknownInput,
                configuration, setup.Bars.Length, ["history-start-after-cursor"]);
            localTrend = UnknownTrend(PriceContextFeatureState.UnknownInput,
                ["history-start-after-cursor"]);
            htfContext = UnknownHtf(PriceContextFeatureState.UnknownInput,
                ["history-start-after-cursor"]);
        }
        else
        {
            pivots = CalculatePivots(setup.Bars, cursor, configuration, blockedByQuality,
                reasons);
            StructuralComputation structural = CalculateStructuralLevels(setup.Bars, cursor,
                configuration, blockedByQuality, reasons);
            levels = structural.Output;
            channel = CalculateChannel(setup.Bars, configuration, blockedByQuality, reasons);
            localTrend = CalculateLocalTrend(setup.Bars, channel, reasons);
            htfContext = CalculateHtfContext(htf.Bars, cursor, configuration,
                blockedByQuality, reasons);
        }

        PriceContextFeatureValue vwap = CalculateVwap(trades.Observations, configuration,
            blockedByQuality, reasons);
        PriceContextFeatureValue poc = CalculatePoc(trades.Observations, configuration,
            blockedByQuality, reasons);
        return new PriceContextFeatureResult(configuration, input, cursor, quality, pivots,
            levels, channel, localTrend, htfContext, vwap, poc);
    }

    private static List<string> ValidateConfiguration(PriceContextFeatureInput input,
        PriceContextFeatureConfiguration configuration)
    {
        List<string> reasons = [];
        if (string.IsNullOrWhiteSpace(configuration.SetupTimeframe))
        {
            reasons.Add("configuration-setup-timeframe-missing");
        }
        else if (!StringComparer.Ordinal.Equals(configuration.SetupTimeframe,
                     input.SetupTimeframe))
        {
            reasons.Add("configuration-setup-timeframe-mismatch");
        }
        else if (!TryParseTimeframe(configuration.SetupTimeframe, out _))
        {
            reasons.Add("configuration-setup-timeframe-invalid");
        }

        if (string.IsNullOrWhiteSpace(configuration.HtfTimeframe))
        {
            reasons.Add("configuration-htf-timeframe-missing");
        }
        else if (!StringComparer.Ordinal.Equals(configuration.HtfTimeframe,
                     input.HtfTimeframe))
        {
            reasons.Add("configuration-htf-timeframe-mismatch");
        }
        else if (!TryParseTimeframe(configuration.HtfTimeframe, out _))
        {
            reasons.Add("configuration-htf-timeframe-invalid");
        }

        if (configuration.AlgorithmVersion is null)
        {
            reasons.Add("configuration-algorithm-version-missing");
        }
        else if (!StringComparer.Ordinal.Equals(configuration.AlgorithmVersion.Value,
                     CurrentAlgorithmVersion))
        {
            reasons.Add("configuration-algorithm-version-unsupported"
                + $":{configuration.AlgorithmVersion.Value}");
        }

        ValidatePeriod(configuration.PivotLeft, "pivot-left", reasons, requiresNext: false);
        ValidatePeriod(configuration.PivotRight, "pivot-right", reasons, requiresNext: false);
        if (configuration.PivotLeft is > 0 && configuration.PivotRight is > 0)
        {
            try
            {
                _ = checked(configuration.PivotLeft.Value + configuration.PivotRight.Value + 1);
            }
            catch (OverflowException)
            {
                reasons.Add("configuration-pivot-window-too-large");
            }
        }

        ValidatePeriod(configuration.AtrPeriod, "atr-period", reasons, requiresNext: true);
        ValidatePeriod(configuration.CenterPeriod, "center-period", reasons, requiresNext: true);
        ValidatePeriod(configuration.ChannelWidthPeriod, "channel-width-period", reasons,
            requiresNext: configuration.WidthKind == PriceContextChannelWidthKind.Atr);
        ValidatePeriod(configuration.HtfFastPeriod, "htf-fast-period", reasons,
            requiresNext: false);
        ValidatePeriod(configuration.HtfSlowPeriod, "htf-slow-period", reasons,
            requiresNext: false);
        ValidateConfirmation(configuration.HtfConfirmationCount, reasons);

        if (configuration.AtrMode is null || !Enum.IsDefined(configuration.AtrMode.Value))
        {
            AddEnumReason(reasons, "atr-mode", configuration.AtrMode);
        }

        if (configuration.CenterKind is null || !Enum.IsDefined(configuration.CenterKind.Value))
        {
            AddEnumReason(reasons, "center-kind", configuration.CenterKind);
        }
        else if (configuration.CenterKind == PriceContextChannelCenterKind.Hlc3Smoothed
            && (configuration.Hlc3SmoothingKind is null
                || !Enum.IsDefined(configuration.Hlc3SmoothingKind.Value)
                || configuration.Hlc3SmoothingKind is PriceContextMovingAverageKind.Sma
                    or PriceContextMovingAverageKind.Wma))
        {
            AddEnumReason(reasons, "hlc3-smoothing-kind", configuration.Hlc3SmoothingKind);
        }

        if (configuration.WidthKind is null || !Enum.IsDefined(configuration.WidthKind.Value))
        {
            AddEnumReason(reasons, "width-kind", configuration.WidthKind);
        }

        if (configuration.HtfMovingAverageKind is null
            || !Enum.IsDefined(configuration.HtfMovingAverageKind.Value))
        {
            AddEnumReason(reasons, "htf-moving-average-kind", configuration.HtfMovingAverageKind);
        }

        ValidatePositiveMultiplier(configuration.ChannelWidthMultiplier,
            "channel-width-multiplier", reasons);
        ValidatePositiveMultiplier(configuration.LevelBufferMultiplier,
            "level-buffer-multiplier", reasons);

        ValidateOptionalPeriod(configuration.VwapWindow, "vwap-window", reasons);
        ValidateOptionalPeriod(configuration.PocWindow, "poc-window", reasons);
        if (configuration.PocBinSize is <= 0m)
        {
            reasons.Add("configuration-poc-bin-size-invalid"
                + $":{configuration.PocBinSize.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return reasons;
    }

    private static void ValidatePeriod(int? period, string name, List<string> reasons,
        bool requiresNext)
    {
        if (period is null)
        {
            reasons.Add($"configuration-{name}-missing");
        }
        else if (period <= 0)
        {
            reasons.Add($"configuration-{name}-invalid:{period.Value.ToString(CultureInfo.InvariantCulture)}");
            reasons.Add(period.Value.ToString(CultureInfo.InvariantCulture));
        }
        else if (period == int.MaxValue || (requiresNext && period == int.MaxValue - 1))
        {
            reasons.Add($"configuration-{name}-too-large:{period.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void ValidateOptionalPeriod(int? period, string name, List<string> reasons)
    {
        if (period is <= 0)
        {
            reasons.Add($"configuration-{name}-invalid:{period.Value.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (period == int.MaxValue)
        {
            reasons.Add($"configuration-{name}-too-large:{period.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void ValidateConfirmation(int? count, List<string> reasons)
    {
        if (count is null)
        {
            reasons.Add("configuration-htf-confirmation-count-missing");
        }
        else if (count is < 1 or > 3)
        {
            reasons.Add("configuration-htf-confirmation-count-invalid"
                + $":{count.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void ValidatePositiveMultiplier(decimal? value, string name,
        List<string> reasons)
    {
        if (value is null)
        {
            reasons.Add($"configuration-{name}-missing");
        }
        else if (value <= 0m)
        {
            reasons.Add($"configuration-{name}-invalid:{value.Value.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void AddEnumReason<TEnum>(List<string> reasons, string name, TEnum? value)
        where TEnum : struct, Enum
    {
        reasons.Add(value is null
            ? $"configuration-{name}-missing"
            : $"configuration-{name}-invalid:{Convert.ToInt32(value.Value, CultureInfo.InvariantCulture)}");
        if (value is { } defined)
        {
            reasons.Add(Convert.ToInt32(defined, CultureInfo.InvariantCulture)
                .ToString(CultureInfo.InvariantCulture));
        }
    }

    private static PreparedHistory PrepareHistory(ImmutableValueList<ScreenerBar> source,
        UtcTimestamp historyStart, string timeframe, CausalEventCursor cursor,
        List<string> reasons)
    {
        if (historyStart.Value > cursor.AsOf.Value)
        {
            reasons.Add("history-start-after-cursor");
            return new PreparedHistory([], true);
        }

        List<ScreenerBar> eligible = [];
        foreach (ScreenerBar bar in source)
        {
            bool future = bar.EventTime.Value > cursor.AsOf.Value
                || bar.AvailabilityTime.Value > cursor.AsOf.Value
                || bar.StableSequence > cursor.MaxStableSequence
                || bar.CloseTime.Value > cursor.AsOf.Value;
            if (future)
            {
                reasons.Add("future");
                if (bar.AvailabilityTime.Value > cursor.AsOf.Value)
                {
                    reasons.Add("late-availability");
                }

                if (bar.CloseTime.Value > cursor.AsOf.Value)
                {
                    reasons.Add("bar-close-after-cursor");
                }

                if (bar.StableSequence > cursor.MaxStableSequence)
                {
                    reasons.Add("future-sequence");
                }

                continue;
            }

            if (bar.Bar.OpenTime.Value < historyStart.Value)
            {
                reasons.Add("history-start");
                continue;
            }

            eligible.Add(bar);
        }

        ScreenerBar[] canonical = DeduplicateBars(eligible, reasons);
        if (TryParseTimeframe(timeframe, out TimeSpan interval))
        {
            AddGaps(canonical, interval, reasons);
        }

        return new PreparedHistory(canonical, false);
    }

    private static PreparedTrades PrepareTrades(
        ImmutableValueList<PriceVolumeObservation> source, UtcTimestamp historyStart,
        CausalEventCursor cursor, List<string> reasons)
    {
        List<PriceVolumeObservation> eligible = [];
        foreach (PriceVolumeObservation observation in source)
        {
            if (observation.EventTime.Value > cursor.AsOf.Value
                || observation.AvailabilityTime.Value > cursor.AsOf.Value
                || observation.StableSequence > cursor.MaxStableSequence)
            {
                reasons.Add("future");
                if (observation.AvailabilityTime.Value > cursor.AsOf.Value)
                {
                    reasons.Add("late-availability");
                }

                if (observation.StableSequence > cursor.MaxStableSequence)
                {
                    reasons.Add("future-sequence");
                }

                continue;
            }

            if (observation.EventTime.Value < historyStart.Value)
            {
                reasons.Add("history-start");
                continue;
            }

            eligible.Add(observation);
        }

        return new PreparedTrades(DeduplicateTrades(eligible, reasons));
    }

    private static ScreenerBar[] DeduplicateBars(IEnumerable<ScreenerBar> bars,
        List<string> reasons)
    {
        List<ScreenerBar> canonical = [];
        foreach (IGrouping<DateTimeOffset, ScreenerBar> group in bars
            .GroupBy(static bar => bar.Bar.OpenTime.Value)
            .OrderBy(static group => group.Key))
        {
            ScreenerBar[] items = group.ToArray();
            ScreenerBar first = items[0];
            if (items.Any(item => !SameBarPayload(first, item)))
            {
                reasons.Add("conflict");
                reasons.Add("conflict:" + FormatTime(new UtcTimestamp(group.Key)));
                continue;
            }

            if (items.Length > 1)
            {
                reasons.Add("duplicate");
            }

            canonical.Add(items
                .OrderBy(static item => item.AvailabilityTime.Value)
                .ThenBy(static item => item.ReceiveTime.Value)
                .ThenBy(static item => item.StableSequence)
                .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
                .First());
        }

        return canonical
            .OrderBy(static item => item.Bar.OpenTime.Value)
            .ThenBy(static item => item.Bar.CloseTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static PriceVolumeObservation[] DeduplicateTrades(
        IEnumerable<PriceVolumeObservation> observations, List<string> reasons)
    {
        List<PriceVolumeObservation> canonical = [];
        foreach (IGrouping<(DateTimeOffset EventTime, long Sequence),
                     PriceVolumeObservation> group in observations
            .GroupBy(static item => (EventTime: item.EventTime.Value,
                Sequence: item.StableSequence))
            .OrderBy(static group => group.Key.EventTime)
            .ThenBy(static group => group.Key.Sequence))
        {
            PriceVolumeObservation[] items = group.ToArray();
            PriceVolumeObservation first = items[0];
            if (items.Any(item => item.Price != first.Price || item.Volume != first.Volume))
            {
                reasons.Add("conflict");
                reasons.Add("conflict-trade:" + FormatTime(first.EventTime));
                continue;
            }

            if (items.Length > 1)
            {
                reasons.Add("duplicate");
            }

            canonical.Add(items
                .OrderBy(static item => item.AvailabilityTime.Value)
                .ThenBy(static item => item.ReceiveTime.Value)
                .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
                .First());
        }

        return canonical
            .OrderBy(static item => item.EventTime.Value)
            .ThenBy(static item => item.AvailabilityTime.Value)
            .ThenBy(static item => item.StableSequence)
            .ThenBy(static item => item.SourceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool SameBarPayload(ScreenerBar left, ScreenerBar right) =>
        left.Bar == right.Bar && left.QuoteVolume == right.QuoteVolume
        && left.IsClosed == right.IsClosed;

    private static void AddGaps(ScreenerBar[] bars, TimeSpan interval, List<string> reasons)
    {
        for (int index = 1; index < bars.Length; index++)
        {
            TimeSpan distance = bars[index].Bar.OpenTime.Value
                - bars[index - 1].Bar.OpenTime.Value;
            if (distance > interval)
            {
                reasons.Add("gap");
                reasons.Add("gap:" + FormatTime(bars[index - 1].EventTime)
                    + ":" + FormatTime(bars[index].EventTime));
            }
        }
    }

    private static PriceContextConfirmedPivots CalculatePivots(ScreenerBar[] bars,
        CausalEventCursor cursor, PriceContextFeatureConfiguration configuration,
        bool blockedByQuality, IEnumerable<string> inputReasons)
    {
        int left = ValidPositive(configuration.PivotLeft);
        int right = ValidPositive(configuration.PivotRight);
        int required = SafeAdd(left, right, 1);
        List<string> reasons = [.. inputReasons];
        if (!HasPivotIdentificationConfiguration(configuration))
        {
            return UnknownPivots(PriceContextFeatureState.UnknownConfiguration,
                configuration, bars.Length, reasons);
        }

        if (blockedByQuality)
        {
            return UnknownPivots(PriceContextFeatureState.UnknownQuality,
                configuration, bars.Length, reasons);
        }

        if (bars.Length < required)
        {
            reasons.Add("warmup");
            return UnknownPivots(PriceContextFeatureState.UnknownWarmup,
                configuration, bars.Length, reasons);
        }

        PivotCandidate[] candidates = FindPivotCandidates(bars, left, right);
        List<PriceContextPivotEvidence> evidence = [];
        bool atrConfigurationValid = HasAtrConfiguration(configuration);
        bool bufferConfigurationValid = configuration.LevelBufferMultiplier is > 0m;
        foreach (PivotCandidate candidate in candidates)
        {
            decimal? atr = null;
            if (atrConfigurationValid)
            {
                atr = TryAtrAt(bars, candidate.VisibleAt, candidate.ConfirmationSequence,
                    configuration.AtrPeriod!.Value, configuration.AtrMode!.Value,
                    out decimal value) ? value : null;
            }

            decimal? buffer = atr.HasValue && bufferConfigurationValid
                && TryMultiply(atr.Value, configuration.LevelBufferMultiplier!.Value,
                    out decimal computedBuffer) ? computedBuffer : null;
            evidence.Add(CreateEvidence(candidate, atr, buffer));
        }

        if (evidence.Count == 0)
        {
            reasons.Add("no-strict-pivot");
        }
        else if (atrConfigurationValid
            && evidence.Any(static pivot => !pivot.AtrAtConfirmation.HasValue))
        {
            reasons.Add("atr-warmup-at-pivot-confirmation");
        }
        else if (atrConfigurationValid && bufferConfigurationValid
            && evidence.Any(static pivot => !pivot.FrozenBuffer.HasValue))
        {
            reasons.Add("decimal-overflow:level-buffer");
        }

        return new PriceContextConfirmedPivots(PriceContextFeatureState.Ready,
            new ImmutableValueList<PriceContextPivotEvidence>(evidence), left, right,
            required, bars.Length,
            reasons.Count == 0 ? PriceContextFeatureCompleteness.Complete
                : PriceContextFeatureCompleteness.Incomplete,
            new ImmutableValueList<string>(reasons));
    }

    private static StructuralComputation CalculateStructuralLevels(ScreenerBar[] bars,
        CausalEventCursor cursor, PriceContextFeatureConfiguration configuration,
        bool blockedByQuality, IEnumerable<string> inputReasons)
    {
        int left = ValidPositive(configuration.PivotLeft);
        int right = ValidPositive(configuration.PivotRight);
        int required = SafeAdd(left, right, 1);
        List<string> reasons = [.. inputReasons];
        if (!HasStructureConfiguration(configuration))
        {
            return new StructuralComputation(UnknownLevels(
                PriceContextFeatureState.UnknownConfiguration, configuration, bars.Length,
                reasons), []);
        }

        if (blockedByQuality)
        {
            return new StructuralComputation(UnknownLevels(
                PriceContextFeatureState.UnknownQuality, configuration, bars.Length, reasons), []);
        }

        if (bars.Length < required)
        {
            reasons.Add("warmup");
            return new StructuralComputation(UnknownLevels(
                PriceContextFeatureState.UnknownWarmup, configuration, bars.Length, reasons), []);
        }

        PivotCandidate[] candidates = FindPivotCandidates(bars, left, right).ToArray();
        List<StructuralStep> steps = [];
        ActiveLevels active = new();
        List<PriceContextPivotEvidence> pivots = [];
        bool atrWarmup = false;
        bool atrOverflow = false;
        ScreenerBar[] orderedBars = OrderByCausalClose(bars);
        int nextCandidate = 0;

        void RegisterCandidates(CausalKey threshold, bool includeEqual)
        {
            while (nextCandidate < candidates.Length)
            {
                PivotCandidate candidate = candidates[nextCandidate];
                int comparison = candidate.Key.CompareTo(threshold);
                if (comparison > 0 || (!includeEqual && comparison == 0))
                {
                    break;
                }

                decimal? atr = TryAtrAt(bars, candidate.VisibleAt, candidate.ConfirmationSequence,
                    configuration.AtrPeriod!.Value, configuration.AtrMode!.Value,
                    out decimal value) ? value : null;
                decimal? buffer = atr.HasValue
                    && TryMultiply(atr.Value, configuration.LevelBufferMultiplier!.Value,
                        out decimal computedBuffer) ? computedBuffer : null;
                PriceContextPivotEvidence evidence = CreateEvidence(candidate, atr, buffer);
                pivots.Add(evidence);
                if (!buffer.HasValue)
                {
                    atrWarmup |= !atr.HasValue;
                    atrOverflow |= atr.HasValue;
                }
                else if (candidate.Kind == PriceContextPivotKind.Low)
                {
                    active.Support = new PriceContextStructuralLevel(
                        PriceContextLevelRole.Support, evidence.Anchor,
                        evidence.FrozenBuffer.GetValueOrDefault(),
                        evidence);
                }
                else
                {
                    active.Resistance = new PriceContextStructuralLevel(
                        PriceContextLevelRole.Resistance, evidence.Anchor,
                        evidence.FrozenBuffer.GetValueOrDefault(),
                        evidence);
                }

                nextCandidate++;
            }
        }

        for (int barIndex = 0; barIndex < orderedBars.Length; barIndex++)
        {
            ScreenerBar bar = orderedBars[barIndex];
            RegisterCandidates(CausalKey.ForBar(bar), includeEqual: false);
            ActiveLevels before = active.Clone();
            ApplyBreak(bar, active);
            steps.Add(new StructuralStep(bar, before, active.Clone()));

            if (barIndex + 1 < orderedBars.Length)
            {
                RegisterCandidates(CausalKey.ForBar(orderedBars[barIndex + 1]),
                    includeEqual: false);
            }
        }

        if (orderedBars.Length > 0)
        {
            RegisterCandidates(CausalKey.ForBar(orderedBars[^1]), includeEqual: true);
        }

        if (pivots.Count == 0)
        {
            reasons.Add("no-strict-pivot");
        }

        PriceContextFeatureState state = active.IsInverted
            ? PriceContextFeatureState.UnknownQuality
            : atrOverflow ? PriceContextFeatureState.UnknownOverflow
            : atrWarmup ? PriceContextFeatureState.UnknownWarmup
            : PriceContextFeatureState.Ready;
        if (active.IsInverted)
        {
            reasons.Add("inverted-active-levels");
        }
        if (atrWarmup)
        {
            reasons.Add("atr-warmup-at-pivot-confirmation");
        }
        if (atrOverflow)
        {
            reasons.Add("decimal-overflow:level-buffer");
        }

        PriceContextStructuralLevels output = new(state, active.Support, active.Resistance,
            active.Support?.Anchor, active.Resistance?.Anchor, required, bars.Length,
            reasons.Count == 0 ? PriceContextFeatureCompleteness.Complete
                : PriceContextFeatureCompleteness.Incomplete,
            new ImmutableValueList<string>(reasons));
        return new StructuralComputation(output, steps);
    }

    private static PriceContextChannel CalculateChannel(ScreenerBar[] bars,
        PriceContextFeatureConfiguration configuration, bool blockedByQuality,
        IEnumerable<string> inputReasons)
    {
        int centerPeriod = ValidPositive(configuration.CenterPeriod);
        int widthPeriod = ValidPositive(configuration.ChannelWidthPeriod);
        int centerRequired = SafeAdd(centerPeriod, 1);
        int widthRequired = configuration.WidthKind == PriceContextChannelWidthKind.Atr
            ? SafeAdd(widthPeriod, 1) : widthPeriod;
        int required = Math.Max(centerRequired, widthRequired);
        List<string> reasons = [.. inputReasons];
        if (!HasChannelConfiguration(configuration))
        {
            return UnknownChannel(PriceContextFeatureState.UnknownConfiguration,
                configuration, bars.Length, reasons);
        }

        if (blockedByQuality)
        {
            return UnknownChannel(PriceContextFeatureState.UnknownQuality,
                configuration, bars.Length, reasons);
        }

        if (bars.Length < required)
        {
            reasons.Add("warmup");
            return UnknownChannel(PriceContextFeatureState.UnknownWarmup,
                configuration, bars.Length, reasons);
        }

        try
        {
            ScreenerBar[] causalBars = OrderByCausalClose(bars);
            decimal[] centerValues = configuration.CenterKind ==
                PriceContextChannelCenterKind.Hlc3Smoothed
                ? causalBars.Select(static bar => checked((bar.Bar.High.Value + bar.Bar.Low.Value
                    + bar.Bar.Close.Value) / 3m)).ToArray()
                : causalBars.Select(static bar => bar.Bar.Close.Value).ToArray();
            PriceContextMovingAverageKind centerKind = configuration.CenterKind switch
            {
                PriceContextChannelCenterKind.Ema => PriceContextMovingAverageKind.Ema,
                PriceContextChannelCenterKind.Rma => PriceContextMovingAverageKind.Rma,
                PriceContextChannelCenterKind.Hlc3Smoothed => configuration.Hlc3SmoothingKind!.Value,
                _ => throw new InvalidOperationException("Unsupported center kind.")
            };
            decimal[] centers = MovingAverageSeries(centerValues, centerPeriod, centerKind);
            decimal center = centers[^1];
            decimal priorCenter = centers[^2];
            decimal halfWidth;
            decimal? atr = null;
            decimal? standardDeviation = null;
            decimal[] widthValues = causalBars[^widthPeriod..]
                .Select(static bar => bar.Bar.Close.Value)
                .ToArray();
            if (configuration.WidthKind == PriceContextChannelWidthKind.Atr)
            {
                atr = AtrValue(causalBars, widthPeriod, configuration.AtrMode!.Value);
                halfWidth = checked(atr.Value * configuration.ChannelWidthMultiplier!.Value);
            }
            else
            {
                standardDeviation = PopulationStandardDeviation(widthValues);
                halfWidth = checked(standardDeviation.Value
                    * configuration.ChannelWidthMultiplier!.Value);
            }

            decimal upper = checked(center + halfWidth);
            decimal lower = checked(center - halfWidth);
            decimal donchianWidth = checked(causalBars.Max(static bar => bar.Bar.High.Value)
                - causalBars.Min(static bar => bar.Bar.Low.Value));
            ScreenerBar[] provenance = causalBars[^required..];
            return new PriceContextChannel(PriceContextFeatureState.Ready, center, priorCenter,
                upper, lower, halfWidth, checked(halfWidth * 2m), donchianWidth, atr,
                standardDeviation, centerPeriod, widthPeriod, configuration.CenterKind,
                configuration.WidthKind, configuration.Hlc3SmoothingKind, required, bars.Length,
                reasons.Count == 0 ? PriceContextFeatureCompleteness.Complete
                    : PriceContextFeatureCompleteness.Incomplete,
                new ImmutableValueList<ScreenerBar>(provenance),
                new ImmutableValueList<string>(reasons));
        }
        catch (OverflowException)
        {
            reasons.Add("decimal-overflow");
            return UnknownChannel(PriceContextFeatureState.UnknownOverflow,
                configuration, bars.Length, reasons);
        }
        catch (DivideByZeroException)
        {
            reasons.Add("zero-denominator");
            return UnknownChannel(PriceContextFeatureState.UnknownZeroDenominator,
                configuration, bars.Length, reasons);
        }
    }

    private static PriceContextLocalTrend CalculateLocalTrend(ScreenerBar[] bars,
        PriceContextChannel channel, IEnumerable<string> inputReasons)
    {
        List<string> reasons = [.. inputReasons, .. channel.Reasons];
        if (channel.State != PriceContextFeatureState.Ready
            || !channel.Center.HasValue || !channel.PriorCenter.HasValue)
        {
            return UnknownTrend(channel.State, reasons);
        }

        ScreenerBar current = OrderByCausalClose(bars).Last();
        decimal close = current.Close.Value;
        PriceContextTrendDirection direction = channel.Center.Value > channel.PriorCenter.Value
            && close >= channel.Center.Value
            ? PriceContextTrendDirection.Bull
            : channel.Center.Value < channel.PriorCenter.Value
                && close <= channel.Center.Value
                ? PriceContextTrendDirection.Bear
                : PriceContextTrendDirection.Neutral;
        return new PriceContextLocalTrend(PriceContextFeatureState.Ready, direction,
            channel.Center, channel.PriorCenter, close, current,
            new ImmutableValueList<string>(reasons));
    }

    private static PriceContextHtfContext CalculateHtfContext(ScreenerBar[] bars,
        CausalEventCursor cursor, PriceContextFeatureConfiguration configuration,
        bool blockedByQuality, IEnumerable<string> inputReasons)
    {
        List<string> reasons = [.. inputReasons];
        if (!HasHtfConfiguration(configuration))
        {
            return UnknownHtf(PriceContextFeatureState.UnknownConfiguration, reasons);
        }

        if (blockedByQuality)
        {
            return UnknownHtf(PriceContextFeatureState.UnknownQuality, reasons);
        }

        int required = Math.Max(configuration.HtfFastPeriod!.Value,
            configuration.HtfSlowPeriod!.Value);
        if (bars.Length < required)
        {
            reasons.Add("warmup");
            return UnknownHtf(PriceContextFeatureState.UnknownWarmup, reasons);
        }

        StructuralComputation structural = CalculateStructuralLevels(bars, cursor,
            configuration, blockedByQuality: false, reasons);
        if (structural.Output.State == PriceContextFeatureState.UnknownConfiguration)
        {
            return UnknownHtf(PriceContextFeatureState.UnknownConfiguration,
                [.. reasons, .. structural.Output.Reasons]);
        }

        if (structural.Output.State == PriceContextFeatureState.UnknownQuality)
        {
            return UnknownHtf(PriceContextFeatureState.UnknownQuality,
                [.. reasons, .. structural.Output.Reasons]);
        }
        if (structural.Output.State is PriceContextFeatureState.UnknownWarmup
            or PriceContextFeatureState.UnknownOverflow)
        {
            return UnknownHtf(structural.Output.State,
                [.. reasons, .. structural.Output.Reasons]);
        }

        int fastPeriod = configuration.HtfFastPeriod!.Value;
        int slowPeriod = configuration.HtfSlowPeriod!.Value;
        PriceContextMovingAverageKind htfMovingAverageKind =
            configuration.HtfMovingAverageKind!.Value;
        int confirmationCount = configuration.HtfConfirmationCount!.Value;

        PriceContextHtfCandidate? pendingCandidate = null;
        int pendingCount = 0;
        PriceContextHtfRegime? confirmed = null;
        decimal? fast = null;
        decimal? slow = null;
        decimal? close = null;
        ScreenerBar? currentBar = null;
        decimal? rawSupport = null;
        decimal? rawResistance = null;
        decimal[] seenCloses = [];
        bool hadUnknown = false;
        foreach (StructuralStep step in structural.Steps)
        {
            currentBar = step.Bar;
            decimal closeValue = step.Bar.Close.Value;
            close = closeValue;
            rawSupport = step.Before.Support?.Anchor;
            rawResistance = step.Before.Resistance?.Anchor;
            seenCloses = seenCloses.Append(closeValue).ToArray();
            if (!TryMovingAverage(seenCloses, fastPeriod, htfMovingAverageKind,
                    out decimal fastValue)
                || !TryMovingAverage(seenCloses, slowPeriod, htfMovingAverageKind,
                    out decimal slowValue))
            {
                hadUnknown = true;
                pendingCandidate = null;
                pendingCount = 0;
                continue;
            }

            fast = fastValue;
            slow = slowValue;
            PriceContextHtfCandidate? candidate = Candidate(closeValue, rawSupport, rawResistance,
                fastValue, slowValue, reasons);
            if (candidate is null)
            {
                hadUnknown = true;
                pendingCandidate = null;
                pendingCount = 0;
                continue;
            }

            hadUnknown = false;
            if (confirmed is not null && MapRegime(candidate.Value) == confirmed.Value)
            {
                pendingCandidate = null;
                pendingCount = 0;
                continue;
            }

            if (pendingCandidate != candidate)
            {
                pendingCandidate = candidate;
                pendingCount = 1;
            }
            else
            {
                pendingCount = Math.Max(0, pendingCount + 1);
            }

            if (pendingCount >= confirmationCount)
            {
                confirmed = MapRegime(candidate.Value);
                pendingCandidate = null;
                pendingCount = 0;
            }
        }

        if (currentBar is null)
        {
            return UnknownHtf(PriceContextFeatureState.UnknownWarmup, [.. reasons, "warmup"]);
        }

        if (structural.Output.State == PriceContextFeatureState.UnknownQuality)
        {
            reasons.AddRange(structural.Output.Reasons);
        }

        PriceContextFeatureState state = hadUnknown
            ? PriceContextFeatureState.UnknownWarmup
            : PriceContextFeatureState.Ready;
        if (hadUnknown)
        {
            reasons.Add("missing-direction-level");
        }

        return new PriceContextHtfContext(state, pendingCandidate, Math.Max(0, pendingCount),
            confirmed, fast, slow, close, rawSupport, rawResistance, currentBar,
            new ImmutableValueList<ScreenerBar>(structural.Steps.Select(static item => item.Bar)),
            new ImmutableValueList<string>(reasons));
    }

    private static PriceContextHtfCandidate? Candidate(decimal close, decimal? support,
        decimal? resistance, decimal fast, decimal slow, List<string> reasons)
    {
        if (fast > slow)
        {
            if (!support.HasValue)
            {
                reasons.Add("missing-support");
                return null;
            }

            return close > support.Value ? PriceContextHtfCandidate.Bull
                : PriceContextHtfCandidate.Neutral;
        }

        if (fast < slow)
        {
            if (!resistance.HasValue)
            {
                reasons.Add("missing-resistance");
                return null;
            }

            return close < resistance.Value ? PriceContextHtfCandidate.Bear
                : PriceContextHtfCandidate.Neutral;
        }

        return PriceContextHtfCandidate.Neutral;
    }

    private static PriceContextHtfRegime MapRegime(PriceContextHtfCandidate candidate) =>
        candidate switch
        {
            PriceContextHtfCandidate.Bull => PriceContextHtfRegime.Bull,
            PriceContextHtfCandidate.Bear => PriceContextHtfRegime.Bear,
            PriceContextHtfCandidate.Neutral => PriceContextHtfRegime.Neutral,
            _ => throw new InvalidOperationException("Unsupported HTF candidate.")
        };

    private static PriceContextFeatureValue CalculateVwap(
        PriceVolumeObservation[] observations, PriceContextFeatureConfiguration configuration,
        bool blockedByQuality, IEnumerable<string> inputReasons)
    {
        List<string> reasons = [.. inputReasons];
        int? period = configuration.VwapWindow;
        if (period is null || period <= 0 || period == int.MaxValue)
        {
            reasons.Add(period is null ? "configuration-vwap-window-missing"
                : "configuration-vwap-window-invalid");
            return UnknownValue(PriceContextFeatureKind.Vwap, PriceContextFeatureState.UnknownConfiguration,
                period is > 0 and < int.MaxValue ? period : null, observations.Length, reasons,
                observations);
        }

        PriceVolumeObservation[] provenance = TakeLatest(observations, period.Value);
        if (blockedByQuality)
        {
            return UnknownValue(PriceContextFeatureKind.Vwap, PriceContextFeatureState.UnknownQuality,
                period, observations.Length, reasons, provenance);
        }

        if (observations.Length < period)
        {
            reasons.Add("warmup");
            return UnknownValue(PriceContextFeatureKind.Vwap, PriceContextFeatureState.UnknownWarmup,
                period, observations.Length, reasons, provenance);
        }

        try
        {
            decimal numerator = 0m;
            decimal denominator = 0m;
            foreach (PriceVolumeObservation observation in provenance)
            {
                numerator = checked(numerator
                    + checked(observation.Price.Value * observation.Volume.Value));
                denominator = checked(denominator + observation.Volume.Value);
            }

            if (denominator == 0m)
            {
                reasons.Add("zero-denominator");
                return UnknownValue(PriceContextFeatureKind.Vwap,
                    PriceContextFeatureState.UnknownZeroDenominator, period, observations.Length,
                    reasons, provenance);
            }

            decimal value = checked(numerator / denominator);
            return ReadyValue(PriceContextFeatureKind.Vwap, value, period.Value, observations.Length,
                provenance, reasons);
        }
        catch (OverflowException)
        {
            reasons.Add("decimal-overflow");
            return UnknownValue(PriceContextFeatureKind.Vwap,
                PriceContextFeatureState.UnknownOverflow, period, observations.Length, reasons,
                provenance);
        }
    }

    private static PriceContextFeatureValue CalculatePoc(
        PriceVolumeObservation[] observations, PriceContextFeatureConfiguration configuration,
        bool blockedByQuality, IEnumerable<string> inputReasons)
    {
        List<string> reasons = [.. inputReasons];
        int? period = configuration.PocWindow;
        if (period is null || period <= 0 || period == int.MaxValue
            || configuration.PocBinSize is null || configuration.PocBinSize <= 0m)
        {
            reasons.Add(period is null ? "configuration-poc-window-missing"
                : period <= 0 || period == int.MaxValue
                    ? "configuration-poc-window-invalid" : "configuration-poc-bin-size-invalid");
            return UnknownValue(PriceContextFeatureKind.Poc, PriceContextFeatureState.UnknownConfiguration,
                period is > 0 and < int.MaxValue ? period : null, observations.Length, reasons,
                TakeLatest(observations, period is > 0 and < int.MaxValue ? period.Value : 0));
        }

        PriceVolumeObservation[] provenance = TakeLatest(observations, period.Value);
        if (blockedByQuality)
        {
            return UnknownValue(PriceContextFeatureKind.Poc, PriceContextFeatureState.UnknownQuality,
                period, observations.Length, reasons, provenance);
        }

        if (observations.Length < period)
        {
            reasons.Add("warmup");
            return UnknownValue(PriceContextFeatureKind.Poc, PriceContextFeatureState.UnknownWarmup,
                period, observations.Length, reasons, provenance);
        }

        try
        {
            decimal minimum = provenance.Min(static observation => observation.Price.Value);
            Dictionary<decimal, decimal> volumes = [];
            foreach (PriceVolumeObservation observation in provenance)
            {
                decimal offset = checked((observation.Price.Value - minimum)
                    / configuration.PocBinSize.Value);
                decimal binIndex = decimal.Floor(offset);
                decimal binStart = checked(minimum
                    + checked(binIndex * configuration.PocBinSize.Value));
                volumes[binStart] = volumes.TryGetValue(binStart, out decimal existing)
                    ? checked(existing + observation.Volume.Value)
                    : observation.Volume.Value;
            }

            if (volumes.Count == 0 || volumes.Values.All(static value => value == 0m))
            {
                reasons.Add("zero-denominator");
                return UnknownValue(PriceContextFeatureKind.Poc,
                    PriceContextFeatureState.UnknownZeroDenominator, period, observations.Length,
                    reasons, provenance);
            }

            decimal poc = volumes.OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key)
                .First().Key;
            return ReadyValue(PriceContextFeatureKind.Poc, poc, period.Value, observations.Length,
                provenance, reasons);
        }
        catch (OverflowException)
        {
            reasons.Add("decimal-overflow");
            return UnknownValue(PriceContextFeatureKind.Poc,
                PriceContextFeatureState.UnknownOverflow, period, observations.Length, reasons,
                provenance);
        }
        catch (DivideByZeroException)
        {
            reasons.Add("zero-denominator");
            return UnknownValue(PriceContextFeatureKind.Poc,
                PriceContextFeatureState.UnknownZeroDenominator, period, observations.Length,
                reasons, provenance);
        }
    }

    private static bool HasPivotIdentificationConfiguration(
        PriceContextFeatureConfiguration configuration) =>
        configuration.PivotLeft is > 0 && configuration.PivotRight is > 0
        && configuration.PivotLeft < int.MaxValue
        && configuration.PivotRight < int.MaxValue;

    private static bool HasAtrConfiguration(PriceContextFeatureConfiguration configuration) =>
        configuration.AtrPeriod is > 0 && configuration.AtrPeriod < int.MaxValue
        && configuration.AtrMode is not null && Enum.IsDefined(configuration.AtrMode.Value);

    private static bool HasStructureConfiguration(PriceContextFeatureConfiguration configuration) =>
        HasPivotIdentificationConfiguration(configuration)
        && HasAtrConfiguration(configuration)
        && configuration.LevelBufferMultiplier is > 0m;

    private static bool HasChannelConfiguration(PriceContextFeatureConfiguration configuration) =>
        configuration.CenterPeriod is > 0 && configuration.CenterPeriod < int.MaxValue
        && configuration.ChannelWidthPeriod is > 0
        && configuration.ChannelWidthPeriod < int.MaxValue
        && configuration.CenterKind is not null && Enum.IsDefined(configuration.CenterKind.Value)
        && configuration.WidthKind is not null && Enum.IsDefined(configuration.WidthKind.Value)
        && configuration.ChannelWidthMultiplier is > 0m
        && (configuration.CenterKind != PriceContextChannelCenterKind.Hlc3Smoothed
            || configuration.Hlc3SmoothingKind is PriceContextMovingAverageKind.Ema
                or PriceContextMovingAverageKind.Rma)
        && (configuration.WidthKind != PriceContextChannelWidthKind.Atr
            || HasAtrConfiguration(configuration))
        && (configuration.WidthKind != PriceContextChannelWidthKind.Atr
            || configuration.ChannelWidthPeriod < int.MaxValue);

    private static bool HasHtfConfiguration(PriceContextFeatureConfiguration configuration) =>
        HasStructureConfiguration(configuration)
        && configuration.HtfFastPeriod is > 0 && configuration.HtfFastPeriod < int.MaxValue
        && configuration.HtfSlowPeriod is > 0 && configuration.HtfSlowPeriod < int.MaxValue
        && configuration.HtfMovingAverageKind is not null
        && Enum.IsDefined(configuration.HtfMovingAverageKind.Value)
        && configuration.HtfConfirmationCount is >= 1 and <= 3;

    private static PriceContextConfirmedPivots UnknownPivots(
        PriceContextFeatureState state, PriceContextFeatureConfiguration configuration,
        int available, IEnumerable<string> reasons) => new(state,
            new ImmutableValueList<PriceContextPivotEvidence>([]),
            ValidNullable(configuration.PivotLeft), ValidNullable(configuration.PivotRight),
            SafeAdd(ValidPositive(configuration.PivotLeft), ValidPositive(configuration.PivotRight), 1),
            Math.Max(0, available), Completeness(state), new ImmutableValueList<string>(reasons));

    private static PriceContextStructuralLevels UnknownLevels(
        PriceContextFeatureState state, PriceContextFeatureConfiguration configuration,
        int available, IEnumerable<string> reasons) => new(state, null, null, null, null,
            SafeAdd(ValidPositive(configuration.PivotLeft), ValidPositive(configuration.PivotRight), 1),
            Math.Max(0, available), Completeness(state), new ImmutableValueList<string>(reasons));

    private static PriceContextChannel UnknownChannel(
        PriceContextFeatureState state, PriceContextFeatureConfiguration configuration,
        int available, IEnumerable<string> reasons) => new(state, null, null, null, null, null,
            null, null, null, null, ValidNullable(configuration.CenterPeriod),
            ValidNullable(configuration.ChannelWidthPeriod), ValidEnum(configuration.CenterKind),
            ValidEnum(configuration.WidthKind), ValidEnum(configuration.Hlc3SmoothingKind),
            Math.Max(0, SafeAdd(ValidPositive(configuration.CenterPeriod), 1)),
            Math.Max(0, available), Completeness(state),
            new ImmutableValueList<ScreenerBar>([]),
            new ImmutableValueList<string>(reasons));

    private static PriceContextLocalTrend UnknownTrend(PriceContextFeatureState state,
        IEnumerable<string> reasons) => new(state, null, null, null, null, null,
        new ImmutableValueList<string>(reasons));

    private static PriceContextHtfContext UnknownHtf(PriceContextFeatureState state,
        IEnumerable<string> reasons) => new(state, null, 0, null, null, null, null, null, null,
        null, new ImmutableValueList<ScreenerBar>([]), new ImmutableValueList<string>(reasons));

    private static PriceContextFeatureValue UnknownValue(PriceContextFeatureKind kind,
        PriceContextFeatureState state, int? period, int available, IEnumerable<string> reasons,
        IEnumerable<PriceVolumeObservation> provenance) => new(kind, state, null, period,
        SafeNonNegative(period), Math.Max(0, available), Completeness(state),
        new ImmutableValueList<PriceVolumeObservation>(provenance),
        new ImmutableValueList<string>(reasons));

    private static PriceContextFeatureValue ReadyValue(PriceContextFeatureKind kind,
        decimal value, int period, int available, IEnumerable<PriceVolumeObservation> provenance,
        IEnumerable<string> reasons) => new(kind, PriceContextFeatureState.Ready, value, period,
        period, Math.Max(0, available), reasons.Any()
            ? PriceContextFeatureCompleteness.Incomplete
            : PriceContextFeatureCompleteness.Complete,
        new ImmutableValueList<PriceVolumeObservation>(provenance),
        new ImmutableValueList<string>(reasons));

    private static PriceContextFeatureCompleteness Completeness(PriceContextFeatureState state) =>
        state switch
        {
            PriceContextFeatureState.UnknownWarmup => PriceContextFeatureCompleteness.Warmup,
            PriceContextFeatureState.UnknownConfiguration => PriceContextFeatureCompleteness.Unknown,
            _ => PriceContextFeatureCompleteness.Incomplete
        };

    private static int? ValidNullable(int? value) => value is > 0 and < int.MaxValue ? value : null;

    private static TEnum? ValidEnum<TEnum>(TEnum? value) where TEnum : struct, Enum =>
        value is { } defined && Enum.IsDefined(defined) ? defined : null;

    private static int ValidPositive(int? value) => value is > 0 and < int.MaxValue ? value.Value : 0;

    private static int SafeAdd(params int[] values)
    {
        try
        {
            int result = 0;
            foreach (int value in values)
            {
                result = checked(result + value);
            }

            return Math.Max(0, result);
        }
        catch (OverflowException)
        {
            return int.MaxValue;
        }
    }

    private static int SafeNonNegative(int? value) => value is > 0 and < int.MaxValue ? value.Value : 0;

    private static PivotCandidate[] FindPivotCandidates(ScreenerBar[] bars, int left, int right)
    {
        List<PivotCandidate> candidates = [];
        for (int index = left; index + right < bars.Length; index++)
        {
            int start = index - left;
            int end = index + right;
            ScreenerBar pivot = bars[index];
            IEnumerable<ScreenerBar> neighbors = bars[start..(end + 1)]
                .Where((_, neighborIndex) => start + neighborIndex != index);
            ScreenerBar[] neighborArray = neighbors.ToArray();
            if (neighborArray.All(item => pivot.Bar.Low.Value < item.Bar.Low.Value))
            {
                candidates.Add(CreateCandidate(PriceContextPivotKind.Low, bars, index, start,
                    end, left, right));
            }

            if (neighborArray.All(item => pivot.Bar.High.Value > item.Bar.High.Value))
            {
                candidates.Add(CreateCandidate(PriceContextPivotKind.High, bars, index, start,
                    end, left, right));
            }
        }

        return candidates.OrderBy(static candidate => candidate.Key)
            .ThenBy(static candidate => candidate.PivotBar.Bar.OpenTime.Value)
            .ThenBy(static candidate => candidate.Kind)
            .ToArray();
    }

    private static PivotCandidate CreateCandidate(PriceContextPivotKind kind,
        ScreenerBar[] bars, int index, int start, int end, int left, int right)
    {
        ScreenerBar confirmation = bars[end];
        DateTimeOffset visible = confirmation.CloseTime.Value;
        foreach (ScreenerBar bar in bars[start..(end + 1)])
        {
            if (bar.AvailabilityTime.Value > visible)
            {
                visible = bar.AvailabilityTime.Value;
            }
        }

        long sequence = bars[start..(end + 1)].Max(static bar => bar.StableSequence);
        return new PivotCandidate(kind, bars[index], bars[start..(end + 1)],
            new UtcTimestamp(visible), sequence, left, right);
    }

    private static PriceContextPivotEvidence CreateEvidence(PivotCandidate candidate,
        decimal? atr, decimal? buffer) => new(candidate.Kind, candidate.PivotBar,
        candidate.Kind == PriceContextPivotKind.Low
            ? candidate.PivotBar.Bar.Low.Value : candidate.PivotBar.Bar.High.Value,
        atr, buffer, candidate.PivotLeft, candidate.PivotRight,
        new ImmutableValueList<ScreenerBar>(candidate.Window), candidate.PivotBar.EventTime,
        candidate.PivotBar.ReceiveTime, candidate.PivotBar.AvailabilityTime,
        candidate.PivotBar.StableSequence, candidate.ConfirmationBar.CloseTime,
        candidate.VisibleAt, candidate.ConfirmationSequence);

    private static void ApplyBreak(ScreenerBar bar, ActiveLevels active)
    {
        if (active.Support is not null
            && bar.Close.Value < active.Support.Boundary)
        {
            PriceContextStructuralLevel broken = active.Support;
            active.Support = null;
            active.Resistance = new PriceContextStructuralLevel(
                PriceContextLevelRole.Resistance, broken.Anchor, broken.FrozenBuffer, broken.Pivot);
        }
        else if (active.Resistance is not null
            && bar.Close.Value > active.Resistance.Boundary)
        {
            PriceContextStructuralLevel broken = active.Resistance;
            active.Resistance = null;
            active.Support = new PriceContextStructuralLevel(
                PriceContextLevelRole.Support, broken.Anchor, broken.FrozenBuffer, broken.Pivot);
        }
    }

    private static bool TryAtrAt(ScreenerBar[] bars, UtcTimestamp visibleAt,
        long maxSequence, int period, PriceContextAtrMode mode, out decimal value)
    {
        ScreenerBar[] available = bars.Where(bar => bar.CloseTime.Value <= visibleAt.Value
                && bar.AvailabilityTime.Value <= visibleAt.Value
                && bar.StableSequence <= maxSequence)
            .ToArray();
        available = OrderByCausalClose(available);
        if (available.Length < period + 1)
        {
            value = 0m;
            return false;
        }

        try
        {
            value = AtrValue(available, period, mode);
            return true;
        }
        catch (OverflowException)
        {
            value = 0m;
            return false;
        }
    }

    private static decimal AtrValue(ScreenerBar[] bars, int period, PriceContextAtrMode mode)
    {
        decimal[] trueRanges = TrueRanges(bars);
        if (mode == PriceContextAtrMode.Sma)
        {
            return Mean(trueRanges[^period..]);
        }

        return RollingIndicatorMath.RecursiveMean(trueRanges, period, isRma: true);
    }

    private static decimal[] TrueRanges(ScreenerBar[] bars)
        => RollingIndicatorMath.TrueRanges(bars);

    private static decimal[] MovingAverageSeries(decimal[] values, int period,
        PriceContextMovingAverageKind kind)
    {
        if (values.Length < period)
        {
            throw new InvalidOperationException("Moving-average warmup was not met.");
        }

        decimal[] result = new decimal[values.Length];
        switch (kind)
        {
            case PriceContextMovingAverageKind.Ema:
            case PriceContextMovingAverageKind.Rma:
                {
                    for (int index = 0; index < values.Length; index++)
                    {
                        result[index] = index < period - 1
                            ? RollingIndicatorMath.Mean(values[..(index + 1)])
                            : RollingIndicatorMath.RecursiveMean(values[..(index + 1)], period,
                                isRma: kind == PriceContextMovingAverageKind.Rma);
                    }

                    break;
                }
            case PriceContextMovingAverageKind.Sma:
                for (int index = period - 1; index < values.Length; index++)
                {
                    result[index] = RollingIndicatorMath.Mean(
                        values[(index - period + 1)..(index + 1)]);
                }

                break;
            case PriceContextMovingAverageKind.Wma:
                for (int index = period - 1; index < values.Length; index++)
                {
                    result[index] = RollingIndicatorMath.WeightedMean(
                        values[(index - period + 1)..(index + 1)]);
                }

                break;
            default:
                throw new InvalidOperationException("Unsupported moving-average kind.");
        }

        return result;
    }

    private static bool TryMovingAverage(decimal[] values, int period,
        PriceContextMovingAverageKind kind, out decimal value)
    {
        if (values.Length < period)
        {
            value = 0m;
            return false;
        }

        try
        {
            value = MovingAverageSeries(values, period, kind)[^1];
            return true;
        }
        catch (OverflowException)
        {
            value = 0m;
            return false;
        }
    }

    private static decimal PopulationStandardDeviation(decimal[] values)
    {
        decimal mean = Mean(values);
        decimal sum = 0m;
        foreach (decimal value in values)
        {
            decimal difference = checked(value - mean);
            sum = checked(sum + checked(difference * difference));
        }

        return DecimalSqrt(checked(sum / values.Length));
    }

    private static decimal DecimalSqrt(decimal value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0m);

        if (value == 0m)
        {
            return 0m;
        }

        int[] bits = decimal.GetBits(value);
        BigInteger numerator = (new BigInteger((uint)bits[2]) << 64)
            | (new BigInteger((uint)bits[1]) << 32) | (uint)bits[0];
        int sourceScale = (bits[3] >> 16) & 0x7F;
        BigInteger denominator = BigInteger.Pow(10, sourceScale);
        int[] maxBits = decimal.GetBits(decimal.MaxValue);
        BigInteger maxCoefficient = (new BigInteger((uint)maxBits[2]) << 64)
            | (new BigInteger((uint)maxBits[1]) << 32) | (uint)maxBits[0];

        for (int scale = 28; scale >= 0; scale--)
        {
            BigInteger scaledNumerator = numerator * BigInteger.Pow(10, 2 * scale);
            BigInteger floor = FloorSqrtRatio(scaledNumerator, denominator);
            BigInteger rounded = RoundSqrtRatio(floor, scaledNumerator, denominator);
            if (rounded <= maxCoefficient)
            {
                int low = unchecked((int)(uint)(rounded & uint.MaxValue));
                int middle = unchecked((int)(uint)((rounded >> 32) & uint.MaxValue));
                int high = unchecked((int)(uint)((rounded >> 64) & uint.MaxValue));
                return new decimal(low, middle, high, false,
                    (byte)scale);
            }
        }

        throw new OverflowException("The decimal square root is outside decimal range.");
    }

    private static BigInteger FloorSqrtRatio(BigInteger numerator, BigInteger denominator)
    {
        BigInteger high = IntegerSqrt(numerator) + 1;
        BigInteger low = 0;
        while (low + 1 < high)
        {
            BigInteger middle = (low + high) / 2;
            if (middle * middle * denominator <= numerator)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static BigInteger RoundSqrtRatio(BigInteger floor, BigInteger numerator,
        BigInteger denominator)
    {
        BigInteger left = 4 * numerator;
        BigInteger right = (4 * floor * (floor + 1) + 1) * denominator;
        if (left > right || (left == right && !floor.IsEven))
        {
            return floor + 1;
        }

        return floor;
    }

    private static BigInteger IntegerSqrt(BigInteger value)
    {
        if (value < 2)
        {
            return value;
        }

        BigInteger current = BigInteger.One << ((int)value.GetBitLength() / 2 + 1);
        BigInteger next = (current + value / current) / 2;
        while (next < current)
        {
            current = next;
            next = (current + value / current) / 2;
        }

        return current;
    }

    private static decimal Mean(IEnumerable<decimal> values)
        => RollingIndicatorMath.Mean(values);

    private static decimal WeightedMean(IEnumerable<decimal> values)
        => RollingIndicatorMath.WeightedMean(values);

    private static decimal Abs(decimal value) => value < 0m ? checked(-value) : value;

    private static PriceVolumeObservation[] TakeLatest(PriceVolumeObservation[] values, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        int start = Math.Max(0, values.Length - count);
        return values[start..];
    }

    private static ScreenerBar[] OrderByCausalClose(ScreenerBar[] bars) => bars
        .OrderBy(static bar => CausalKey.ForBar(bar))
        .ThenBy(static bar => bar.Bar.OpenTime.Value)
        .ThenBy(static bar => bar.StableSequence)
        .ToArray();

    private static bool HasBlockingQuality(IEnumerable<string> reasons) => reasons.Any(reason =>
        StringComparer.Ordinal.Equals(reason, "conflict")
        || StringComparer.Ordinal.Equals(reason, "gap")
        || StringComparer.Ordinal.Equals(reason, "gap-timeframe-unknown"));

    private static bool TryMultiply(decimal left, decimal right, out decimal result)
    {
        try
        {
            result = checked(left * right);
            return true;
        }
        catch (OverflowException)
        {
            result = 0m;
            return false;
        }
    }

    private static bool TryParseTimeframe(string timeframe, out TimeSpan interval)
    {
        interval = default;
        string value = timeframe.Trim();
        string suffix;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            suffix = "ms";
        }
        else if (value.Length > 0)
        {
            suffix = value[^1].ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            return false;
        }

        string number = value[..^suffix.Length];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture,
                out long count) || count <= 0)
        {
            return false;
        }

        try
        {
            interval = suffix.ToLowerInvariant() switch
            {
                "ms" => TimeSpan.FromMilliseconds(count),
                "s" => TimeSpan.FromSeconds(count),
                "m" => TimeSpan.FromMinutes(count),
                "h" => TimeSpan.FromHours(count),
                "d" => TimeSpan.FromDays(count),
                "w" => TimeSpan.FromDays(checked(count * 7L)),
                _ => default
            };
            return interval > TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string FormatTime(UtcTimestamp timestamp) => timestamp.Value
        .ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record PreparedHistory(ScreenerBar[] Bars, bool HistoryStartAfterCursor);

    private sealed record PreparedTrades(PriceVolumeObservation[] Observations);

    private sealed record StructuralComputation(PriceContextStructuralLevels Output,
        IReadOnlyList<StructuralStep> Steps);

    private sealed record StructuralStep(ScreenerBar Bar, ActiveLevels Before,
        ActiveLevels After);

    private sealed record PivotCandidate(PriceContextPivotKind Kind, ScreenerBar PivotBar,
        ScreenerBar[] Window, UtcTimestamp VisibleAt, long ConfirmationSequence,
        int PivotLeft, int PivotRight)
    {
        public ScreenerBar ConfirmationBar => Window[^1];

        public CausalKey Key => new(VisibleAt.Value, ConfirmationSequence);
    }

    private sealed class ActiveLevels
    {
        public PriceContextStructuralLevel? Support { get; set; }

        public PriceContextStructuralLevel? Resistance { get; set; }

        public bool IsInverted => Support is not null && Resistance is not null
            && Support.Anchor >= Resistance.Anchor;

        public ActiveLevels Clone() => new() { Support = Support, Resistance = Resistance };
    }

    private readonly record struct CausalKey(DateTimeOffset Time, long Sequence)
        : IComparable<CausalKey>
    {
        public static CausalKey ForBar(ScreenerBar bar) => new(
            bar.CloseTime.Value > bar.AvailabilityTime.Value
                ? bar.CloseTime.Value : bar.AvailabilityTime.Value, bar.StableSequence);

        public int CompareTo(CausalKey other)
        {
            int time = Time.CompareTo(other.Time);
            return time != 0 ? time : Sequence.CompareTo(other.Sequence);
        }
    }
}
