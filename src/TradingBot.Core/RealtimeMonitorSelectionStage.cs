namespace TradingBot.Core;

/// <summary>
/// Final staged reduction: monitor only the selected CandidatePool membership
/// at the same causal cursor using a caller-supplied six-feature ranking.
/// </summary>
public static class RealtimeMonitorSelectionStage
{
    public static StagedSelectionResult Evaluate(
        StagedSelectionResult candidatePoolResult,
        IEnumerable<ScreenerSnapshot> screenerSnapshots,
        StagedSelectionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(candidatePoolResult);
        ArgumentNullException.ThrowIfNull(screenerSnapshots);
        ArgumentNullException.ThrowIfNull(configuration);
        if (candidatePoolResult.Stage != StagedSelectionStage.CandidatePool)
        {
            throw new ArgumentException(
                "Realtime-monitor selection requires a CandidatePool stage result.",
                nameof(candidatePoolResult));
        }

        if (candidatePoolResult.PreviousStage is null
            || candidatePoolResult.PreviousStage.Stage != StagedSelectionStage.Universe)
        {
            throw new ArgumentException(
                "Realtime-monitor selection requires a CandidatePool result linked to Universe.",
                nameof(candidatePoolResult));
        }

        if (configuration.Stage != StagedSelectionStage.RealtimeMonitor)
        {
            throw new ArgumentException(
                "Realtime-monitor selection requires the RealtimeMonitor configuration.",
                nameof(configuration));
        }

        Dictionary<string, StagedSelectionCandidate> candidatePoolCandidates =
            candidatePoolResult.Selected
                .GroupBy(static candidate => candidate.InstrumentId.Value,
                    StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Single(),
                    StringComparer.Ordinal);
        Dictionary<string, ImmutableValueList<ScreenerSnapshot>> snapshotsByInstrument =
            UniverseSelectionStage.BuildSnapshotIndex(screenerSnapshots);
        List<StagedSelectionExclusion> exclusions = [];

        foreach (KeyValuePair<string, ImmutableValueList<ScreenerSnapshot>> pair in
            snapshotsByInstrument.Where(pair => !candidatePoolCandidates.ContainsKey(pair.Key))
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            exclusions.Add(new StagedSelectionExclusion(null, pair.Value,
                ["not-in-candidate-pool"]));
        }

        List<StagedSelectionCandidate> eligible = [];
        foreach (KeyValuePair<string, StagedSelectionCandidate> pair in candidatePoolCandidates
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            StagedSelectionCandidate previous = pair.Value;
            if (!snapshotsByInstrument.TryGetValue(pair.Key,
                out ImmutableValueList<ScreenerSnapshot>? evidence)
                || evidence.Count == 0)
            {
                exclusions.Add(new StagedSelectionExclusion(previous.Metadata, [],
                    ["screener-snapshot-missing"]));
                continue;
            }

            if (evidence.Count > 1)
            {
                exclusions.Add(new StagedSelectionExclusion(previous.Metadata, evidence,
                    ["screener-snapshot-conflict"]));
                continue;
            }

            ScreenerSnapshot snapshot = evidence[0];
            string[] causalReasons = UniverseSelectionStage.ValidateSnapshot(snapshot,
                candidatePoolResult.Cursor);
            if (causalReasons.Length > 0)
            {
                exclusions.Add(new StagedSelectionExclusion(previous.Metadata, evidence,
                    causalReasons));
                continue;
            }

            ScreenerFeatureResult recalculated = ScreenerFeatureCalculator.Calculate(
                snapshot.Input, candidatePoolResult.Cursor, snapshot.Configuration);
            List<StagedSelectionComparison> comparisons = [];
            List<string> featureReasons = [];
            foreach (StagedSelectionKey key in configuration.RankingKeys)
            {
                ScreenerFeatureValue feature = recalculated.Features.Single(
                    value => value.Kind == key.Feature);
                if (!feature.IsReady)
                {
                    featureReasons.Add($"feature-not-ready:{key.Feature}");
                }
                else
                {
                    comparisons.Add(new StagedSelectionComparison(key.Feature,
                        key.Direction, feature.Value!.Value));
                }
            }

            if (featureReasons.Count > 0)
            {
                exclusions.Add(new StagedSelectionExclusion(previous.Metadata, evidence,
                    featureReasons, comparisons: comparisons));
                continue;
            }

            eligible.Add(new StagedSelectionCandidate(previous.Metadata, snapshot,
                recalculated, comparisons, rank: 1));
        }

        StagedSelectionCandidate[] ranked = eligible
            .OrderBy(static candidate => candidate,
                new StagedSelectionCandidateComparer(configuration))
            .Select((candidate, index) => new StagedSelectionCandidate(candidate.Metadata,
                candidate.ScreenerEvidence, candidate.RecalculatedFeatures,
                candidate.Comparisons, index + 1))
            .ToArray();
        StagedSelectionCandidate[] selected = ranked
            .Take(configuration.MaximumCandidates)
            .ToArray();
        foreach (StagedSelectionCandidate candidate in ranked.Skip(
            configuration.MaximumCandidates))
        {
            exclusions.Add(new StagedSelectionExclusion(candidate.Metadata,
                [candidate.ScreenerEvidence], ["capacity-exceeded"], candidate.Rank,
                candidate.Comparisons));
        }

        StagedSelectionExclusion[] orderedExclusions = exclusions
            .OrderBy(static item => item.InstrumentId?.Value,
                StringComparer.Ordinal)
            .ThenBy(static item => item.InstrumentId is null ? 1 : 0)
            .ThenBy(static item => string.Join("\u001f", item.Reasons),
                StringComparer.Ordinal)
            .ToArray();
        StagedSelectionStageReference previousReference =
            new(StagedSelectionStage.CandidatePool, candidatePoolResult.StageIdentity,
                candidatePoolResult.Cursor,
                candidatePoolResult.Selected.Select(static candidate =>
                    candidate.InstrumentId));
        return new StagedSelectionResult(StagedSelectionStage.RealtimeMonitor,
            configuration, candidatePoolResult.Cursor,
            candidatePoolResult.MetadataFilter, selected, orderedExclusions,
            previousReference);
    }
}
