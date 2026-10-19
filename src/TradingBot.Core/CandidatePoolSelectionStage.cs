namespace TradingBot.Core;

/// <summary>
/// Second staged selection: reduce only the selected Universe membership using
/// a fresh, same-cursor screener snapshot and an explicit six-feature ranking.
/// </summary>
public static class CandidatePoolSelectionStage
{
    public static StagedSelectionResult Evaluate(
        StagedSelectionResult universeResult,
        IEnumerable<ScreenerSnapshot> screenerSnapshots,
        StagedSelectionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(universeResult);
        ArgumentNullException.ThrowIfNull(screenerSnapshots);
        ArgumentNullException.ThrowIfNull(configuration);
        if (universeResult.Stage != StagedSelectionStage.Universe)
        {
            throw new ArgumentException(
                "Candidate-pool selection requires a Universe stage result.",
                nameof(universeResult));
        }

        if (configuration.Stage != StagedSelectionStage.CandidatePool)
        {
            throw new ArgumentException(
                "Candidate-pool selection requires the CandidatePool configuration.",
                nameof(configuration));
        }

        Dictionary<string, StagedSelectionCandidate> universeCandidates =
            universeResult.Selected
                .GroupBy(static candidate => candidate.InstrumentId.Value,
                    StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Single(),
                    StringComparer.Ordinal);
        Dictionary<string, ImmutableValueList<ScreenerSnapshot>> snapshotsByInstrument =
            UniverseSelectionStage.BuildSnapshotIndex(screenerSnapshots);
        List<StagedSelectionExclusion> exclusions = [];

        foreach (KeyValuePair<string, ImmutableValueList<ScreenerSnapshot>> pair in
            snapshotsByInstrument.Where(pair => !universeCandidates.ContainsKey(pair.Key))
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            exclusions.Add(new StagedSelectionExclusion(null, pair.Value,
                ["not-in-universe"]));
        }

        List<StagedSelectionCandidate> eligible = [];
        foreach (KeyValuePair<string, StagedSelectionCandidate> pair in universeCandidates
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
                universeResult.Cursor);
            if (causalReasons.Length > 0)
            {
                exclusions.Add(new StagedSelectionExclusion(previous.Metadata, evidence,
                    causalReasons));
                continue;
            }

            ScreenerFeatureResult recalculated = ScreenerFeatureCalculator.Calculate(
                snapshot.Input, universeResult.Cursor, snapshot.Configuration);
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
            new(StagedSelectionStage.Universe, universeResult.StageIdentity,
                universeResult.Cursor,
                universeResult.Selected.Select(static candidate => candidate.InstrumentId));
        return new StagedSelectionResult(StagedSelectionStage.CandidatePool,
            configuration, universeResult.Cursor, universeResult.MetadataFilter, selected,
            orderedExclusions, previousReference);
    }
}
