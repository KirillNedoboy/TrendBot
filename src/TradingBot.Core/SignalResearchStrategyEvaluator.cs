using System.Globalization;

namespace TradingBot.Core;

/// <summary>A causally anchored research-only candidate at one closed bar.</summary>
public sealed record SignalResearchStrategyCandidate(SetupType Family, Direction Direction,
    string AlgorithmVersion, ScreenerBar AnchorBar, ScreenerBar SignalBar,
    ImmutableValueList<string> Reasons);

/// <summary>Additional candidates and unavailable-input diagnostics for one cut.</summary>
public sealed record SignalResearchStrategyEvaluation(
    ImmutableValueList<SignalResearchStrategyCandidate> Candidates,
    ImmutableValueList<string> UnknownReasons);

/// <summary>Independent closed-bar research strategies without execution side effects.</summary>
public static partial class SignalResearchStrategyEvaluator
{
    public static SignalResearchStrategyEvaluation Evaluate(PriceContextFeatureResult context,
        SignalResearchStrategyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        string timeframe = context.Input.SetupTimeframe;
        bool reclaim = policy.FailedReclaimTimeframes.Contains(timeframe);
        bool retest = policy.BreakoutRetestTimeframes.Contains(timeframe);
        bool stochastic = policy.FlowStochasticTimeframes.Contains(timeframe);
        bool rsi = policy.FlowRsiTimeframes.Contains(timeframe);
        bool atr = policy.FlowAtrTimeframes.Contains(timeframe);
        bool compression = policy.CompressionTimeframes.Contains(timeframe);
        List<SignalResearchStrategyCandidate> candidates = [];
        List<string> unknown = [];
        if (!(reclaim || retest || stochastic || rsi || atr || compression))
        {
            return Result(candidates, unknown);
        }

        ScreenerBar[] bars = context.Input.SetupBars
            .Where(bar => bar.IsEligible(context.Cursor)).ToArray();
        if (bars.Length == 0)
        {
            unknown.Add("research-current-bar-unavailable");
            return Result(candidates, unknown);
        }
        for (int index = 1; index < bars.Length; index++)
        {
            if (bars[index - 1].CloseTime.Value != bars[index].Bar.OpenTime.Value)
            {
                unknown.Add("research-history-gap-or-duplicate");
                return Result(candidates, unknown);
            }
        }

        if (reclaim || retest)
        {
            if (!context.ConfirmedPivots.IsReady)
            {
                unknown.Add("research-structural-pivots-unavailable");
            }
            else
            {
                EvaluateStructural(bars, context.ConfirmedPivots.Pivots, policy,
                    reclaim, retest, candidates, unknown);
            }
        }
        if (stochastic || rsi || atr || compression)
        {
            try
            {
                EvaluateWavesAndCompression(bars, context.Configuration, policy,
                    stochastic, rsi, atr, compression, candidates, unknown);
            }
            catch (OverflowException)
            {
                unknown.Add("research-wave-or-compression-overflow");
            }
        }
        return Result(candidates, unknown);
    }

    private static SignalResearchStrategyEvaluation Result(
        IEnumerable<SignalResearchStrategyCandidate> candidates, IEnumerable<string> unknown) =>
        new(new ImmutableValueList<SignalResearchStrategyCandidate>(candidates
                .OrderBy(static candidate => candidate.Family)
                .ThenBy(static candidate => candidate.Direction)),
            new ImmutableValueList<string>(unknown.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)));

    private static void EvaluateStructural(ScreenerBar[] bars,
        IReadOnlyList<PriceContextPivotEvidence> pivots, SignalResearchStrategyPolicy policy,
        bool reclaim, bool retest, List<SignalResearchStrategyCandidate> candidates,
        List<string> unknown)
    {
        PriceContextPivotEvidence[] ordered = pivots
            .OrderBy(static pivot => pivot.ConfirmationTime.Value)
            .ThenBy(static pivot => pivot.ConfirmationSequence)
            .ThenBy(static pivot => pivot.Kind).ToArray();
        PriceContextPivotEvidence? support = null;
        PriceContextPivotEvidence? resistance = null;
        StructuralEpisode longReclaim = default;
        StructuralEpisode shortReclaim = default;
        StructuralEpisode longRetest = default;
        StructuralEpisode shortRetest = default;
        int pivotIndex = 0;
        int first = Math.Max(1, bars.Length - policy.StructuralMaximumAgeBars - 1);
        for (int index = first; index < bars.Length; index++)
        {
            ScreenerBar bar = bars[index];
            while (pivotIndex < ordered.Length
                && ordered[pivotIndex].ConfirmationTime.Value <= bar.Bar.OpenTime.Value)
            {
                PriceContextPivotEvidence pivot = ordered[pivotIndex++];
                if (pivot.ConfirmationSequence >= bar.StableSequence)
                {
                    unknown.Add("research-pivot-sequence-after-bar");
                    return;
                }
                if (pivot.Kind == PriceContextPivotKind.Low)
                {
                    support = pivot;
                }
                else
                {
                    resistance = pivot;
                }
            }
            bool current = index == bars.Length - 1;
            if (reclaim)
            {
                ApplyReclaim(ref longReclaim, support, Direction.Long, bar, index,
                    policy.StructuralMaximumAgeBars, current, candidates);
                ApplyReclaim(ref shortReclaim, resistance, Direction.Short, bar, index,
                    policy.StructuralMaximumAgeBars, current, candidates);
            }
            if (retest)
            {
                ApplyRetest(ref longRetest, resistance, Direction.Long, bars[index - 1],
                    bar, index, policy.StructuralMaximumAgeBars, current, candidates);
                ApplyRetest(ref shortRetest, support, Direction.Short, bars[index - 1],
                    bar, index, policy.StructuralMaximumAgeBars, current, candidates);
            }
        }
        if (support is null || resistance is null)
        {
            unknown.Add("research-confirmed-structural-level-unavailable");
        }
    }

    private static void ApplyReclaim(ref StructuralEpisode episode,
        PriceContextPivotEvidence? available, Direction direction, ScreenerBar bar,
        int index, int maximumAge, bool current,
        List<SignalResearchStrategyCandidate> candidates)
    {
        if (episode.Anchor is { } anchor)
        {
            if (index - episode.StartIndex > maximumAge)
            {
                episode = default;
            }
            else
            {
                bool reclaimed = direction == Direction.Long
                    ? bar.Close.Value > anchor.Anchor : bar.Close.Value < anchor.Anchor;
                if (reclaimed)
                {
                    if (current)
                    {
                        candidates.Add(Candidate(SetupType.FailedBreakoutReclaim, direction,
                            "failed-breakout-reclaim-v1", anchor.PivotBar, bar,
                            anchor.Anchor, episode.StartEventId!));
                    }
                    episode = default;
                }
                return;
            }
        }
        if (available is null)
        {
            return;
        }
        bool breached = direction == Direction.Long
            ? bar.Bar.Open.Value >= available.Anchor && bar.Close.Value <= available.Anchor
                && bar.Bar.Low.Value < available.Anchor
            : bar.Bar.Open.Value <= available.Anchor && bar.Close.Value >= available.Anchor
                && bar.Bar.High.Value > available.Anchor;
        if (breached)
        {
            episode = new StructuralEpisode(available, index, bar.Bar.EventId, 0, 0m);
        }
    }

    private static void ApplyRetest(ref StructuralEpisode episode,
        PriceContextPivotEvidence? available, Direction direction, ScreenerBar previous,
        ScreenerBar bar, int index, int maximumAge, bool current,
        List<SignalResearchStrategyCandidate> candidates)
    {
        if (episode.Anchor is { } anchor)
        {
            bool invalidated = direction == Direction.Long
                ? bar.Close.Value < anchor.Anchor : bar.Close.Value > anchor.Anchor;
            if (index - episode.StartIndex > maximumAge || invalidated)
            {
                episode = default;
                return;
            }
            if (episode.Phase == 0)
            {
                bool touched = direction == Direction.Long
                    ? bar.Bar.Low.Value <= anchor.Anchor : bar.Bar.High.Value >= anchor.Anchor;
                if (touched)
                {
                    episode.Phase = 1;
                    episode.RetestExtreme = direction == Direction.Long
                        ? bar.Bar.High.Value : bar.Bar.Low.Value;
                }
            }
            else
            {
                bool continued = direction == Direction.Long
                    ? bar.Close.Value > episode.RetestExtreme
                    : bar.Close.Value < episode.RetestExtreme;
                if (continued)
                {
                    if (current)
                    {
                        candidates.Add(Candidate(SetupType.BreakoutRetestContinuation, direction,
                            "breakout-retest-continuation-v1", anchor.PivotBar, bar,
                            anchor.Anchor, episode.StartEventId!));
                    }
                    episode = default;
                }
            }
            return;
        }
        if (available is null)
        {
            return;
        }
        bool broke = direction == Direction.Long
            ? previous.Close.Value <= available.Anchor && bar.Close.Value > available.Anchor
            : previous.Close.Value >= available.Anchor && bar.Close.Value < available.Anchor;
        if (broke)
        {
            episode = new StructuralEpisode(available, index, bar.Bar.EventId, 0, 0m);
        }
    }

    private static SignalResearchStrategyCandidate Candidate(SetupType family,
        Direction direction, string version, ScreenerBar anchor, ScreenerBar signal,
        decimal reference, string triggeringEventId, string referenceKind = "level",
        decimal? observedValue = null)
    {
        List<string> reasons =
        [
            $"strategy:{version}",
            $"{referenceKind}:{reference.ToString(CultureInfo.InvariantCulture)}",
            $"trigger:{triggeringEventId}"
        ];
        if (observedValue is { } value)
        {
            reasons.Add($"observed-value:{value.ToString(CultureInfo.InvariantCulture)}");
        }
        return new SignalResearchStrategyCandidate(family, direction, version,
            anchor, signal, new ImmutableValueList<string>(reasons));
    }

    private struct StructuralEpisode(PriceContextPivotEvidence anchor, int startIndex,
        string startEventId, int phase, decimal retestExtreme)
    {
        public PriceContextPivotEvidence? Anchor = anchor;
        public int StartIndex = startIndex;
        public string? StartEventId = startEventId;
        public int Phase = phase;
        public decimal RetestExtreme = retestExtreme;
    }
}
