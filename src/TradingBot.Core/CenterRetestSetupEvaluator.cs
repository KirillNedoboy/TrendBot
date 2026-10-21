namespace TradingBot.Core;

/// <summary>
/// Evaluates the center-retest-v1 Lane A setup family from one immutable
/// price-context snapshot and its derived feature-quality evidence. The
/// evaluator is pure, reads no clock or I/O, and cannot produce an order,
/// lifecycle transition, risk decision, or execution state.
/// </summary>
public static class CenterRetestSetupEvaluator
{
    private static readonly FeatureQualityFeatureKind[] RequiredQualityKinds =
    [
        FeatureQualityFeatureKind.PriceContextChannel,
        FeatureQualityFeatureKind.PriceContextLocalTrend,
        FeatureQualityFeatureKind.PriceContextHtfContext
    ];

    /// <summary>Evaluates both long and short Center Retest observations.</summary>
    public static CenterRetestSetupResult Evaluate(
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        CenterRetestSetupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(priceContext);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(configuration);
        if (quality.Cursor != priceContext.Cursor)
        {
            throw new ArgumentException(
                "Price-context and quality evidence must use the same causal cursor.",
                nameof(quality));
        }

        List<string> sharedReasons = [];
        CenterRetestSetupState? unknownState = null;
        EvaluateConfiguration(priceContext, configuration, sharedReasons, ref unknownState);
        EvaluateFeatureStates(priceContext, sharedReasons, ref unknownState);
        bool barConfirmed = EvaluateSignalBar(priceContext, sharedReasons, ref unknownState);
        bool qualityPassed = EvaluateQuality(priceContext, quality, sharedReasons,
            ref unknownState);

        CenterRetestSetupObservation longObservation = EvaluateDirection(priceContext,
            Direction.Long, barConfirmed, qualityPassed, sharedReasons, unknownState);
        CenterRetestSetupObservation shortObservation = EvaluateDirection(priceContext,
            Direction.Short, barConfirmed, qualityPassed, sharedReasons, unknownState);
        return new CenterRetestSetupResult(
            CenterRetestSetupResult.CurrentAlgorithmVersion, configuration,
            priceContext, quality, longObservation, shortObservation);
    }

    private static void EvaluateConfiguration(PriceContextFeatureResult priceContext,
        CenterRetestSetupConfiguration configuration, List<string> reasons,
        ref CenterRetestSetupState? unknownState)
    {
        bool algorithmVersionValid = configuration.AlgorithmVersion is { } version
            && StringComparer.Ordinal.Equals(version.Value,
                CenterRetestSetupResult.CurrentAlgorithmVersion);
        if (configuration.AlgorithmVersion is null)
        {
            reasons.Add("configuration-algorithm-version-missing");
        }
        else if (!algorithmVersionValid)
        {
            reasons.Add("configuration-algorithm-version-unsupported");
        }

        bool priceContextVersionValid = priceContext.Configuration.AlgorithmVersion is
        { } contextVersion
            && StringComparer.Ordinal.Equals(contextVersion.Value,
                PriceContextFeatureCalculator.CurrentAlgorithmVersion);
        if (!priceContextVersionValid)
        {
            reasons.Add("price-context-algorithm-version-unsupported");
        }

        if (!algorithmVersionValid || !priceContextVersionValid)
        {
            ConsiderUnknown(ref unknownState,
                CenterRetestSetupState.UnknownConfiguration);
        }
    }

    private static void EvaluateFeatureStates(PriceContextFeatureResult priceContext,
        List<string> reasons, ref CenterRetestSetupState? unknownState)
    {
        EvaluateFeatureState("channel", priceContext.Channel.State, reasons,
            ref unknownState);
        EvaluateFeatureState("local-trend", priceContext.LocalTrend.State, reasons,
            ref unknownState);
        EvaluateFeatureState("htf-context", priceContext.HtfContext.State, reasons,
            ref unknownState);

        if (priceContext.Channel.State == PriceContextFeatureState.Ready
            && (priceContext.Channel.Center is null
                || priceContext.Channel.Provenance.Count == 0))
        {
            reasons.Add("channel-evidence-incomplete");
            ConsiderUnknown(ref unknownState, CenterRetestSetupState.UnknownQuality);
        }

        if (priceContext.LocalTrend.State == PriceContextFeatureState.Ready
            && (priceContext.LocalTrend.Direction is null
                || priceContext.LocalTrend.CurrentBar is null
                || priceContext.LocalTrend.CurrentCenter is null
                || priceContext.LocalTrend.PriorCenter is null
                || priceContext.LocalTrend.CurrentClose is null))
        {
            reasons.Add("local-trend-evidence-incomplete");
            ConsiderUnknown(ref unknownState, CenterRetestSetupState.UnknownQuality);
        }
    }

    private static void EvaluateFeatureState(string name,
        PriceContextFeatureState state, List<string> reasons,
        ref CenterRetestSetupState? unknownState)
    {
        if (state == PriceContextFeatureState.Ready)
        {
            return;
        }

        reasons.Add(name + "-not-ready");
        CenterRetestSetupState? mapped = MapNonReadyState(state);
        if (mapped is { } value)
        {
            ConsiderUnknown(ref unknownState, value);
        }
    }

    private static CenterRetestSetupState? MapNonReadyState(
        PriceContextFeatureState state) => state switch
        {
            PriceContextFeatureState.Ready => null,
            PriceContextFeatureState.UnknownConfiguration =>
                CenterRetestSetupState.UnknownConfiguration,
            PriceContextFeatureState.UnknownInput =>
                CenterRetestSetupState.UnknownInput,
            PriceContextFeatureState.UnknownWarmup =>
                CenterRetestSetupState.UnknownWarmup,
            PriceContextFeatureState.UnknownQuality =>
                CenterRetestSetupState.UnknownQuality,
            PriceContextFeatureState.UnknownOverflow =>
                CenterRetestSetupState.UnknownOverflow,
            PriceContextFeatureState.UnknownZeroDenominator =>
                CenterRetestSetupState.UnknownZeroDenominator,
            _ => CenterRetestSetupState.UnknownQuality
        };

    private static bool EvaluateSignalBar(PriceContextFeatureResult priceContext,
        List<string> reasons, ref CenterRetestSetupState? unknownState)
    {
        if (priceContext.Channel.State != PriceContextFeatureState.Ready
            || priceContext.LocalTrend.State != PriceContextFeatureState.Ready)
        {
            reasons.Add("bar-not-confirmed");
            return false;
        }

        ScreenerBar? signalBar = priceContext.LocalTrend.CurrentBar;
        if (signalBar is null)
        {
            reasons.Add("signal-bar-missing");
            reasons.Add("bar-not-confirmed");
            ConsiderUnknown(ref unknownState, CenterRetestSetupState.UnknownQuality);
            return false;
        }

        if (!signalBar.IsEligible(priceContext.Cursor))
        {
            reasons.Add("signal-bar-not-eligible");
            reasons.Add("bar-not-confirmed");
            ConsiderUnknown(ref unknownState, CenterRetestSetupState.UnknownInput);
            return false;
        }

        if (priceContext.Channel.Provenance.Count == 0
            || priceContext.Channel.Provenance[^1] != signalBar)
        {
            reasons.Add("signal-bar-not-latest-channel-bar");
            reasons.Add("bar-not-confirmed");
            ConsiderUnknown(ref unknownState, CenterRetestSetupState.UnknownQuality);
            return false;
        }

        if (priceContext.LocalTrend.CurrentCenter != priceContext.Channel.Center
            || priceContext.LocalTrend.PriorCenter != priceContext.Channel.PriorCenter
            || priceContext.LocalTrend.CurrentClose != signalBar.Bar.Close.Value)
        {
            reasons.Add("local-trend-evidence-incomplete");
            reasons.Add("bar-not-confirmed");
            ConsiderUnknown(ref unknownState, CenterRetestSetupState.UnknownQuality);
            return false;
        }

        return true;
    }

    private static bool EvaluateQuality(PriceContextFeatureResult priceContext,
        FeatureQualityEvaluation quality, List<string> reasons,
        ref CenterRetestSetupState? unknownState)
    {
        bool passed = true;
        string expectedFormula = priceContext.Configuration.AlgorithmVersion?.Value
            ?? "unknown";
        if (!StringComparer.Ordinal.Equals(quality.PriceContextFormulaVersion,
                expectedFormula))
        {
            reasons.Add("quality-price-context-version-mismatch");
            passed = false;
        }

        foreach (FeatureQualityFeatureKind kind in RequiredQualityKinds)
        {
            string name = QualityName(kind);
            FeatureQualityEvidence[] matches = quality.Features
                .Where(feature => feature.Kind == kind)
                .ToArray();
            if (matches.Length == 0)
            {
                reasons.Add("quality-evidence-missing:" + name);
                passed = false;
                continue;
            }

            if (matches.Length > 1)
            {
                reasons.Add("quality-evidence-duplicate:" + name);
                passed = false;
            }

            FeatureQualityEvidence evidence = matches[0];
            if (evidence.InputSeries != ExpectedSeries(kind))
            {
                reasons.Add("quality-evidence-series-mismatch:" + name);
                passed = false;
            }

            if (!StringComparer.Ordinal.Equals(evidence.FormulaVersion, expectedFormula))
            {
                reasons.Add("quality-evidence-formula-mismatch:" + name);
                passed = false;
            }

            if (!StringComparer.Ordinal.Equals(evidence.OriginalState,
                    ExpectedOriginalState(priceContext, kind)))
            {
                reasons.Add("quality-evidence-state-mismatch:" + name);
                passed = false;
            }

            if (!evidence.IsReady)
            {
                reasons.Add("quality-evidence-not-ready:" + name);
                passed = false;
            }
        }

        if (!passed)
        {
            reasons.Add("data-quality-not-ready");
            ConsiderUnknown(ref unknownState, CenterRetestSetupState.UnknownQuality);
        }

        return passed;
    }

    private static string QualityName(FeatureQualityFeatureKind kind) => kind switch
    {
        FeatureQualityFeatureKind.PriceContextChannel => "channel",
        FeatureQualityFeatureKind.PriceContextLocalTrend => "local-trend",
        FeatureQualityFeatureKind.PriceContextHtfContext => "htf",
        _ => kind.ToString()
    };

    private static FeatureQualityInputSeries ExpectedSeries(
        FeatureQualityFeatureKind kind) => kind switch
        {
            FeatureQualityFeatureKind.PriceContextChannel =>
                FeatureQualityInputSeries.Setup,
            FeatureQualityFeatureKind.PriceContextLocalTrend =>
                FeatureQualityInputSeries.Setup,
            FeatureQualityFeatureKind.PriceContextHtfContext =>
                FeatureQualityInputSeries.Htf,
            _ => FeatureQualityInputSeries.Setup
        };

    private static string ExpectedOriginalState(PriceContextFeatureResult priceContext,
        FeatureQualityFeatureKind kind) => kind switch
        {
            FeatureQualityFeatureKind.PriceContextChannel =>
                priceContext.Channel.State.ToString(),
            FeatureQualityFeatureKind.PriceContextLocalTrend =>
                priceContext.LocalTrend.State.ToString(),
            FeatureQualityFeatureKind.PriceContextHtfContext =>
                priceContext.HtfContext.State.ToString(),
            _ => string.Empty
        };

    private static CenterRetestSetupObservation EvaluateDirection(
        PriceContextFeatureResult priceContext, Direction direction,
        bool barConfirmed, bool qualityPassed, IEnumerable<string> sharedReasons,
        CenterRetestSetupState? unknownState)
    {
        List<string> reasons = [.. sharedReasons];
        PriceContextChannel channel = priceContext.Channel;
        PriceContextLocalTrend localTrend = priceContext.LocalTrend;
        PriceContextHtfContext htfContext = priceContext.HtfContext;
        ScreenerBar? signalBar = localTrend.CurrentBar;

        bool htfMatches = direction == Direction.Long
            ? htfContext.ConfirmedRegime == PriceContextHtfRegime.Bull
            : htfContext.ConfirmedRegime == PriceContextHtfRegime.Bear;
        bool localTrendMatches = direction == Direction.Long
            ? localTrend.Direction == PriceContextTrendDirection.Bull
            : localTrend.Direction == PriceContextTrendDirection.Bear;

        bool centerTouched = false;
        bool openHoldsCenter = false;
        bool closeHoldsCenter = false;
        bool candleDirectionMatches = false;
        bool closedThroughCenter = false;
        if (signalBar is not null && channel.Center is { } center)
        {
            if (direction == Direction.Long)
            {
                centerTouched = signalBar.Bar.Low.Value <= center;
                openHoldsCenter = signalBar.Bar.Open.Value >= center;
                closeHoldsCenter = signalBar.Bar.Close.Value >= center;
                candleDirectionMatches = signalBar.Bar.Close.Value
                    > signalBar.Bar.Open.Value;
                closedThroughCenter = signalBar.Bar.Close.Value < center;
            }
            else
            {
                centerTouched = signalBar.Bar.High.Value >= center;
                openHoldsCenter = signalBar.Bar.Open.Value <= center;
                closeHoldsCenter = signalBar.Bar.Close.Value <= center;
                candleDirectionMatches = signalBar.Bar.Close.Value
                    < signalBar.Bar.Open.Value;
                closedThroughCenter = signalBar.Bar.Close.Value > center;
            }
        }

        CenterRetestConditionEvidence conditions = new(htfMatches,
            localTrendMatches, centerTouched, openHoldsCenter, closeHoldsCenter,
            candleDirectionMatches, closedThroughCenter, barConfirmed, qualityPassed);

        CenterRetestSetupState state;
        if (unknownState is { } unknown)
        {
            state = unknown;
        }
        else
        {
            AddRuleReasons(direction, conditions, reasons);
            if (conditions.AllConditionsPassed)
            {
                state = CenterRetestSetupState.Ready;
            }
            else if (htfMatches && localTrendMatches && centerTouched
                && closedThroughCenter && barConfirmed && qualityPassed)
            {
                state = CenterRetestSetupState.Invalidated;
            }
            else
            {
                state = CenterRetestSetupState.NoSetup;
            }
        }

        return new CenterRetestSetupObservation(direction, state, signalBar,
            channel.Center, localTrend.Direction, htfContext.ConfirmedRegime,
            htfContext.PendingCandidate, htfContext.PendingCount, conditions,
            new ImmutableValueList<string>(reasons));
    }

    private static void AddRuleReasons(Direction direction,
        CenterRetestConditionEvidence conditions, List<string> reasons)
    {
        if (!conditions.HtfContextMatches)
        {
            reasons.Add(direction == Direction.Long
                ? "htf-context-not-bull"
                : "htf-context-not-bear");
        }

        if (!conditions.LocalTrendMatches)
        {
            reasons.Add(direction == Direction.Long
                ? "local-trend-not-bull"
                : "local-trend-not-bear");
        }

        if (!conditions.CenterTouched)
        {
            reasons.Add("center-not-touched");
        }

        if (!conditions.OpenHoldsCenter)
        {
            reasons.Add(direction == Direction.Long
                ? "open-not-at-or-above-center"
                : "open-not-at-or-below-center");
        }

        if (!conditions.CloseHoldsCenter)
        {
            bool invalidated = conditions.HtfContextMatches
                && conditions.LocalTrendMatches && conditions.CenterTouched
                && conditions.ClosedThroughCenter && conditions.BarConfirmed
                && conditions.DataQualityPassed;
            reasons.Add(direction == Direction.Long
                ? invalidated ? "closed-through-center"
                    : "close-not-at-or-above-center"
                : invalidated ? "closed-through-center"
                    : "close-not-at-or-below-center");
        }

        if (!conditions.CandleDirectionMatches)
        {
            reasons.Add(direction == Direction.Long
                ? "candle-not-bullish"
                : "candle-not-bearish");
        }

        if (!conditions.BarConfirmed)
        {
            reasons.Add("bar-not-confirmed");
        }

        if (!conditions.DataQualityPassed)
        {
            reasons.Add("data-quality-not-ready");
        }
    }

    private static void ConsiderUnknown(ref CenterRetestSetupState? current,
        CenterRetestSetupState candidate)
    {
        if (current is null || UnknownSeverity(candidate) < UnknownSeverity(current.Value))
        {
            current = candidate;
        }
    }

    private static int UnknownSeverity(CenterRetestSetupState state) => state switch
    {
        CenterRetestSetupState.UnknownConfiguration => 0,
        CenterRetestSetupState.UnknownInput => 1,
        CenterRetestSetupState.UnknownOverflow => 2,
        CenterRetestSetupState.UnknownZeroDenominator => 3,
        CenterRetestSetupState.UnknownWarmup => 4,
        CenterRetestSetupState.UnknownQuality => 5,
        _ => 6
    };
}
