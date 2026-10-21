namespace TradingBot.Core;

/// <summary>
/// Evaluates the boundary-rejection-v1 Lane A setup family from one immutable
/// price-context snapshot and its derived feature-quality evidence. The
/// evaluator is pure, reads no clock or I/O, and cannot produce an order,
/// lifecycle transition, risk decision, or execution state.
/// </summary>
public static class BoundaryRejectionSetupEvaluator
{
    private static readonly FeatureQualityFeatureKind[] RequiredQualityKinds =
    [
        FeatureQualityFeatureKind.PriceContextChannel,
        FeatureQualityFeatureKind.PriceContextLocalTrend,
        FeatureQualityFeatureKind.PriceContextHtfContext
    ];

    /// <summary>Evaluates both long and short Boundary Rejection observations.</summary>
    public static BoundaryRejectionSetupResult Evaluate(
        PriceContextFeatureResult priceContext, FeatureQualityEvaluation quality,
        BoundaryRejectionSetupConfiguration configuration)
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
        BoundaryRejectionSetupState? unknownState = null;
        EvaluateConfiguration(priceContext, configuration, sharedReasons, ref unknownState);
        EvaluateFeatureStates(priceContext, sharedReasons, ref unknownState);
        bool barConfirmed = EvaluateSignalBar(priceContext, sharedReasons, ref unknownState);
        bool qualityPassed = EvaluateQuality(priceContext, quality, sharedReasons,
            ref unknownState);

        BoundaryRejectionSetupObservation longObservation = EvaluateDirection(priceContext,
            configuration, Direction.Long, barConfirmed, qualityPassed, sharedReasons,
            unknownState);
        BoundaryRejectionSetupObservation shortObservation = EvaluateDirection(priceContext,
            configuration, Direction.Short, barConfirmed, qualityPassed, sharedReasons,
            unknownState);
        return new BoundaryRejectionSetupResult(
            BoundaryRejectionSetupResult.CurrentAlgorithmVersion, configuration,
            priceContext, quality, longObservation, shortObservation);
    }

    private static void EvaluateConfiguration(PriceContextFeatureResult priceContext,
        BoundaryRejectionSetupConfiguration configuration, List<string> reasons,
        ref BoundaryRejectionSetupState? unknownState)
    {
        bool algorithmVersionValid = configuration.AlgorithmVersion is { } version
            && StringComparer.Ordinal.Equals(version.Value,
                BoundaryRejectionSetupResult.CurrentAlgorithmVersion);
        if (configuration.AlgorithmVersion is null)
        {
            reasons.Add("configuration-algorithm-version-missing");
        }
        else if (!algorithmVersionValid)
        {
            reasons.Add("configuration-algorithm-version-unsupported");
        }

        bool minimumWidthValid = configuration.MinimumChannelWidth is > 0m;
        if (configuration.MinimumChannelWidth is null)
        {
            reasons.Add("configuration-minimum-channel-width-missing");
        }
        else if (!minimumWidthValid)
        {
            reasons.Add("configuration-minimum-channel-width-non-positive");
        }

        bool priceContextVersionValid = priceContext.Configuration.AlgorithmVersion is
        { } contextVersion
            && StringComparer.Ordinal.Equals(contextVersion.Value,
                PriceContextFeatureCalculator.CurrentAlgorithmVersion);
        if (!priceContextVersionValid)
        {
            reasons.Add("price-context-algorithm-version-unsupported");
        }

        if (!algorithmVersionValid || !minimumWidthValid || !priceContextVersionValid)
        {
            ConsiderUnknown(ref unknownState,
                BoundaryRejectionSetupState.UnknownConfiguration);
        }
    }

    private static void EvaluateFeatureStates(PriceContextFeatureResult priceContext,
        List<string> reasons, ref BoundaryRejectionSetupState? unknownState)
    {
        EvaluateFeatureState("channel", priceContext.Channel.State, reasons,
            ref unknownState);
        EvaluateFeatureState("local-trend", priceContext.LocalTrend.State, reasons,
            ref unknownState);
        EvaluateFeatureState("htf-context", priceContext.HtfContext.State, reasons,
            ref unknownState);

        if (priceContext.Channel.State == PriceContextFeatureState.Ready
            && (priceContext.Channel.Center is null
                || priceContext.Channel.PriorCenter is null
                || priceContext.Channel.HalfWidth is null
                || priceContext.Channel.Width is null
                || priceContext.Channel.Lower is null
                || priceContext.Channel.Upper is null
                || priceContext.Channel.Provenance.Count == 0))
        {
            reasons.Add("channel-evidence-incomplete");
            ConsiderUnknown(ref unknownState, BoundaryRejectionSetupState.UnknownQuality);
        }

        if (priceContext.LocalTrend.State == PriceContextFeatureState.Ready
            && (priceContext.LocalTrend.Direction is null
                || priceContext.LocalTrend.CurrentBar is null
                || priceContext.LocalTrend.CurrentCenter is null
                || priceContext.LocalTrend.PriorCenter is null
                || priceContext.LocalTrend.CurrentClose is null))
        {
            reasons.Add("local-trend-evidence-incomplete");
            ConsiderUnknown(ref unknownState, BoundaryRejectionSetupState.UnknownQuality);
        }
    }

    private static void EvaluateFeatureState(string name,
        PriceContextFeatureState state, List<string> reasons,
        ref BoundaryRejectionSetupState? unknownState)
    {
        if (state == PriceContextFeatureState.Ready)
        {
            return;
        }

        reasons.Add(name + "-not-ready");
        BoundaryRejectionSetupState? mapped = MapNonReadyState(state);
        if (mapped is { } value)
        {
            ConsiderUnknown(ref unknownState, value);
        }
    }

    private static BoundaryRejectionSetupState? MapNonReadyState(
        PriceContextFeatureState state) => state switch
        {
            PriceContextFeatureState.Ready => null,
            PriceContextFeatureState.UnknownConfiguration =>
                BoundaryRejectionSetupState.UnknownConfiguration,
            PriceContextFeatureState.UnknownInput =>
                BoundaryRejectionSetupState.UnknownInput,
            PriceContextFeatureState.UnknownWarmup =>
                BoundaryRejectionSetupState.UnknownWarmup,
            PriceContextFeatureState.UnknownQuality =>
                BoundaryRejectionSetupState.UnknownQuality,
            PriceContextFeatureState.UnknownOverflow =>
                BoundaryRejectionSetupState.UnknownOverflow,
            PriceContextFeatureState.UnknownZeroDenominator =>
                BoundaryRejectionSetupState.UnknownZeroDenominator,
            _ => BoundaryRejectionSetupState.UnknownQuality
        };

    private static bool EvaluateSignalBar(PriceContextFeatureResult priceContext,
        List<string> reasons, ref BoundaryRejectionSetupState? unknownState)
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
            ConsiderUnknown(ref unknownState, BoundaryRejectionSetupState.UnknownQuality);
            return false;
        }

        if (!signalBar.IsEligible(priceContext.Cursor))
        {
            reasons.Add("signal-bar-not-eligible");
            reasons.Add("bar-not-confirmed");
            ConsiderUnknown(ref unknownState, BoundaryRejectionSetupState.UnknownInput);
            return false;
        }

        if (priceContext.Channel.Provenance.Count == 0
            || priceContext.Channel.Provenance[^1] != signalBar)
        {
            reasons.Add("signal-bar-not-latest-channel-bar");
            reasons.Add("bar-not-confirmed");
            ConsiderUnknown(ref unknownState, BoundaryRejectionSetupState.UnknownQuality);
            return false;
        }

        if (priceContext.LocalTrend.CurrentCenter != priceContext.Channel.Center
            || priceContext.LocalTrend.PriorCenter != priceContext.Channel.PriorCenter
            || priceContext.LocalTrend.CurrentClose != signalBar.Bar.Close.Value)
        {
            reasons.Add("local-trend-evidence-incomplete");
            reasons.Add("bar-not-confirmed");
            ConsiderUnknown(ref unknownState, BoundaryRejectionSetupState.UnknownQuality);
            return false;
        }

        return true;
    }

    private static bool EvaluateQuality(PriceContextFeatureResult priceContext,
        FeatureQualityEvaluation quality, List<string> reasons,
        ref BoundaryRejectionSetupState? unknownState)
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
            ConsiderUnknown(ref unknownState, BoundaryRejectionSetupState.UnknownQuality);
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

    private static BoundaryRejectionSetupObservation EvaluateDirection(
        PriceContextFeatureResult priceContext,
        BoundaryRejectionSetupConfiguration configuration, Direction direction,
        bool barConfirmed, bool qualityPassed, IEnumerable<string> sharedReasons,
        BoundaryRejectionSetupState? unknownState)
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

        bool boundaryTouched = false;
        bool closeReclaimed = false;
        bool closedThroughBoundary = false;
        decimal? band = direction == Direction.Long ? channel.Lower : channel.Upper;
        if (signalBar is not null && band is { } bandValue)
        {
            if (direction == Direction.Long)
            {
                boundaryTouched = signalBar.Bar.Low.Value <= bandValue;
                closeReclaimed = signalBar.Bar.Close.Value > bandValue;
                closedThroughBoundary = signalBar.Bar.Close.Value < bandValue;
            }
            else
            {
                boundaryTouched = signalBar.Bar.High.Value >= bandValue;
                closeReclaimed = signalBar.Bar.Close.Value < bandValue;
                closedThroughBoundary = signalBar.Bar.Close.Value > bandValue;
            }
        }

        bool minimumChannelWidthPassed = false;
        if (configuration.MinimumChannelWidth is { } minimumWidth && minimumWidth > 0m
            && channel.Width is { } channelWidth)
        {
            minimumChannelWidthPassed = channelWidth >= minimumWidth;
        }

        BoundaryRejectionConditionEvidence conditions = new(htfMatches,
            localTrendMatches, boundaryTouched, closeReclaimed, closedThroughBoundary,
            barConfirmed, minimumChannelWidthPassed, qualityPassed);

        BoundaryRejectionSetupState state;
        if (unknownState is { } unknown)
        {
            state = unknown;
        }
        else
        {
            AddRuleReasons(direction, conditions, reasons);
            if (conditions.AllConditionsPassed)
            {
                state = BoundaryRejectionSetupState.Ready;
            }
            else if (htfMatches && localTrendMatches && boundaryTouched
                && closedThroughBoundary && barConfirmed && minimumChannelWidthPassed
                && qualityPassed)
            {
                state = BoundaryRejectionSetupState.Invalidated;
            }
            else
            {
                state = BoundaryRejectionSetupState.NoSetup;
            }
        }

        return new BoundaryRejectionSetupObservation(direction, state,
            configuration.MinimumChannelWidth, signalBar, channel.Center,
            channel.PriorCenter, channel.HalfWidth, channel.Width, channel.Lower,
            channel.Upper, localTrend.Direction, htfContext.ConfirmedRegime,
            htfContext.PendingCandidate, htfContext.PendingCount, conditions,
            new ImmutableValueList<string>(reasons));
    }

    private static void AddRuleReasons(Direction direction,
        BoundaryRejectionConditionEvidence conditions, List<string> reasons)
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

        if (!conditions.BoundaryTouched)
        {
            reasons.Add(direction == Direction.Long
                ? "lower-band-not-touched"
                : "upper-band-not-touched");
        }

        if (!conditions.CloseReclaimed)
        {
            bool invalidated = conditions.HtfContextMatches
                && conditions.LocalTrendMatches && conditions.BoundaryTouched
                && conditions.ClosedThroughBoundary && conditions.BarConfirmed
                && conditions.MinimumChannelWidthPassed && conditions.DataQualityPassed;
            reasons.Add(direction == Direction.Long
                ? invalidated ? "closed-through-lower-band" : "close-not-above-lower-band"
                : invalidated ? "closed-through-upper-band" : "close-not-below-upper-band");
        }

        if (!conditions.MinimumChannelWidthPassed)
        {
            reasons.Add("channel-width-below-minimum");
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

    private static void ConsiderUnknown(
        ref BoundaryRejectionSetupState? current,
        BoundaryRejectionSetupState candidate)
    {
        if (current is null || UnknownSeverity(candidate) < UnknownSeverity(current.Value))
        {
            current = candidate;
        }
    }

    private static int UnknownSeverity(BoundaryRejectionSetupState state) => state switch
    {
        BoundaryRejectionSetupState.UnknownConfiguration => 0,
        BoundaryRejectionSetupState.UnknownInput => 1,
        BoundaryRejectionSetupState.UnknownOverflow => 2,
        BoundaryRejectionSetupState.UnknownZeroDenominator => 3,
        BoundaryRejectionSetupState.UnknownWarmup => 4,
        BoundaryRejectionSetupState.UnknownQuality => 5,
        _ => 6
    };
}
