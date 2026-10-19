using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies one bounded stage in the candidate reduction chain.</summary>
public enum StagedSelectionStage
{
    Universe = 1,
    CandidatePool = 2,
    RealtimeMonitor = 3
}

/// <summary>Controls the lexical direction for one configured comparison.</summary>
public enum StagedSelectionDirection
{
    Ascending = 1,
    Descending = 2
}

/// <summary>One explicit feature key in a staged selection configuration.</summary>
public sealed record StagedSelectionKey
{
    public ScreenerFeatureKind Feature { get; }

    public StagedSelectionDirection Direction { get; }

    [JsonIgnore]
    public ScreenerFeatureKind FeatureKind => Feature;

    [JsonConstructor]
    public StagedSelectionKey(ScreenerFeatureKind feature,
        StagedSelectionDirection direction)
    {
        ContractGuard.RequireDefined(feature, nameof(feature));
        ContractGuard.RequireDefined(direction, nameof(direction));
        Feature = feature;
        Direction = direction;
    }
}

/// <summary>
/// Explicit, versioned rules for one staged reduction. There are no ranking
/// defaults: the caller supplies every key and its direction.
/// </summary>
public sealed record StagedSelectionConfiguration
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string AlgorithmVersion { get; }

    public StagedSelectionStage Stage { get; }

    public int MaximumCandidates { get; }

    [JsonIgnore]
    public int Capacity => MaximumCandidates;

    public ImmutableValueList<StagedSelectionKey> RankingKeys { get; }

    public StagedSelectionConfiguration(string algorithmVersion,
        StagedSelectionStage stage, int maximumCandidates,
        IEnumerable<StagedSelectionKey> rankingKeys)
        : this(CurrentFormatVersion, algorithmVersion, stage, maximumCandidates,
            SnapshotKeys(rankingKeys))
    {
    }

    [JsonConstructor]
    public StagedSelectionConfiguration(int formatVersion, string algorithmVersion,
        StagedSelectionStage stage, int maximumCandidates,
        ImmutableValueList<StagedSelectionKey> rankingKeys)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                $"Staged selection format version {formatVersion} is not supported.");
        }

        ContractGuard.RequireText(algorithmVersion, nameof(algorithmVersion));
        ContractGuard.RequireDefined(stage, nameof(stage));
        if (maximumCandidates <= 0 || maximumCandidates > MaximumFor(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates), maximumCandidates,
                $"The {stage} stage capacity must be between one and {MaximumFor(stage)}.");
        }

        ArgumentNullException.ThrowIfNull(rankingKeys);
        StagedSelectionKey[] keys = rankingKeys.ToArray();
        if (keys.Length == 0 || keys.Any(static key => key is null))
        {
            throw new ArgumentException(
                "A staged selection configuration requires non-empty ranking keys.",
                nameof(rankingKeys));
        }

        if (keys.Select(static key => key.Feature).Distinct().Count() != keys.Length)
        {
            throw new ArgumentException("Ranking keys cannot repeat a feature.",
                nameof(rankingKeys));
        }

        HashSet<ScreenerFeatureKind> allowed = AllowedFeatures(stage);
        if (keys.Any(key => !allowed.Contains(key.Feature)))
        {
            throw new ArgumentException(
                $"The {stage} stage does not support one or more ranking features.",
                nameof(rankingKeys));
        }

        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        Stage = stage;
        MaximumCandidates = maximumCandidates;
        RankingKeys = new ImmutableValueList<StagedSelectionKey>(keys);
    }

    private static ImmutableValueList<StagedSelectionKey> SnapshotKeys(
        IEnumerable<StagedSelectionKey> rankingKeys)
    {
        ArgumentNullException.ThrowIfNull(rankingKeys);
        return new ImmutableValueList<StagedSelectionKey>(rankingKeys);
    }

    private static int MaximumFor(StagedSelectionStage stage) => stage switch
    {
        StagedSelectionStage.Universe => 250,
        StagedSelectionStage.CandidatePool => 50,
        StagedSelectionStage.RealtimeMonitor => 20,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
    };

    private static HashSet<ScreenerFeatureKind> AllowedFeatures(StagedSelectionStage stage) =>
        stage switch
        {
            StagedSelectionStage.Universe =>
            [ScreenerFeatureKind.Turnover, ScreenerFeatureKind.Spread],
            StagedSelectionStage.CandidatePool =>
            [
                ScreenerFeatureKind.RelativeVolume,
                ScreenerFeatureKind.PriceAcceleration,
                ScreenerFeatureKind.AtrExpansion,
                ScreenerFeatureKind.Turnover,
                ScreenerFeatureKind.Spread,
                ScreenerFeatureKind.OpenInterestAnomaly
            ],
            StagedSelectionStage.RealtimeMonitor =>
            [
                ScreenerFeatureKind.RelativeVolume,
                ScreenerFeatureKind.PriceAcceleration,
                ScreenerFeatureKind.AtrExpansion,
                ScreenerFeatureKind.Turnover,
                ScreenerFeatureKind.Spread,
                ScreenerFeatureKind.OpenInterestAnomaly
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
        };
}

/// <summary>One immutable input bundle evaluated at one causal cursor.</summary>
public sealed record StagedSelectionInput
{
    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<ExchangeEligibilityMetadata> Metadata { get; }

    public ImmutableValueList<ScreenerSnapshot> ScreenerSnapshots { get; }

    public StagedSelectionInput(CausalEventCursor cursor,
        IEnumerable<ExchangeEligibilityMetadata> metadata,
        IEnumerable<ScreenerSnapshot> screenerSnapshots)
        : this(cursor, SnapshotMetadata(metadata), SnapshotSnapshots(screenerSnapshots))
    {
    }

    [JsonConstructor]
    public StagedSelectionInput(CausalEventCursor cursor,
        ImmutableValueList<ExchangeEligibilityMetadata> metadata,
        ImmutableValueList<ScreenerSnapshot> screenerSnapshots)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(screenerSnapshots);
        if (metadata.Any(static item => item is null))
        {
            throw new ArgumentException("Metadata input cannot contain null values.",
                nameof(metadata));
        }

        if (screenerSnapshots.Any(static item => item is null))
        {
            throw new ArgumentException("Screener snapshots cannot contain null values.",
                nameof(screenerSnapshots));
        }

        Cursor = cursor;
        Metadata = new ImmutableValueList<ExchangeEligibilityMetadata>(metadata);
        ScreenerSnapshots = new ImmutableValueList<ScreenerSnapshot>(screenerSnapshots);
    }

    private static ImmutableValueList<ExchangeEligibilityMetadata> SnapshotMetadata(
        IEnumerable<ExchangeEligibilityMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new ImmutableValueList<ExchangeEligibilityMetadata>(metadata);
    }

    private static ImmutableValueList<ScreenerSnapshot> SnapshotSnapshots(
        IEnumerable<ScreenerSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        return new ImmutableValueList<ScreenerSnapshot>(snapshots);
    }
}

/// <summary>One numeric comparison retained in stage provenance.</summary>
public sealed record StagedSelectionComparison
{
    public ScreenerFeatureKind Feature { get; }

    public StagedSelectionDirection Direction { get; }

    public decimal Value { get; }

    [JsonIgnore]
    public ScreenerFeatureKind FeatureKind => Feature;

    [JsonConstructor]
    public StagedSelectionComparison(ScreenerFeatureKind feature,
        StagedSelectionDirection direction, decimal value)
    {
        ContractGuard.RequireDefined(feature, nameof(feature));
        ContractGuard.RequireDefined(direction, nameof(direction));
        Feature = feature;
        Direction = direction;
        Value = value;
    }
}

/// <summary>One selected universe item and all evidence used to rank it.</summary>
public sealed record StagedSelectionCandidate
{
    public ExchangeEligibilityMetadata Metadata { get; }

    public ScreenerSnapshot ScreenerEvidence { get; }

    public ScreenerFeatureResult RecalculatedFeatures { get; }

    public ImmutableValueList<StagedSelectionComparison> Comparisons { get; }

    public int Rank { get; }

    [JsonIgnore]
    public InstrumentId InstrumentId => Metadata.InstrumentId;

    public StagedSelectionCandidate(ExchangeEligibilityMetadata metadata,
        ScreenerSnapshot screenerEvidence, ScreenerFeatureResult recalculatedFeatures,
        IEnumerable<StagedSelectionComparison> comparisons, int rank)
        : this(metadata, screenerEvidence, recalculatedFeatures,
            SnapshotComparisons(comparisons), rank)
    {
    }

    [JsonConstructor]
    public StagedSelectionCandidate(ExchangeEligibilityMetadata metadata,
        ScreenerSnapshot screenerEvidence, ScreenerFeatureResult recalculatedFeatures,
        ImmutableValueList<StagedSelectionComparison> comparisons, int rank)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(screenerEvidence);
        ArgumentNullException.ThrowIfNull(recalculatedFeatures);
        ArgumentNullException.ThrowIfNull(comparisons);
        if (rank <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank,
                "A staged selection rank must be positive.");
        }

        Metadata = metadata;
        ScreenerEvidence = screenerEvidence;
        RecalculatedFeatures = recalculatedFeatures;
        Comparisons = new ImmutableValueList<StagedSelectionComparison>(comparisons);
        Rank = rank;
    }

    private static ImmutableValueList<StagedSelectionComparison> SnapshotComparisons(
        IEnumerable<StagedSelectionComparison> comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        return new ImmutableValueList<StagedSelectionComparison>(comparisons);
    }
}

/// <summary>One stage exclusion with immutable metadata and screener evidence.</summary>
public sealed record StagedSelectionExclusion
{
    public ExchangeEligibilityMetadata? Metadata { get; }

    public ImmutableValueList<ScreenerSnapshot> ScreenerEvidence { get; }

    public ImmutableValueList<string> Reasons { get; }

    public int? Rank { get; }

    public ImmutableValueList<StagedSelectionComparison> Comparisons { get; }

    [JsonIgnore]
    public InstrumentId? InstrumentId => Metadata?.InstrumentId
        ?? (ScreenerEvidence.Count == 0 ? null : ScreenerEvidence[0].Input.InstrumentId);

    public StagedSelectionExclusion(ExchangeEligibilityMetadata? metadata,
        IEnumerable<ScreenerSnapshot> screenerEvidence, IEnumerable<string> reasons,
        int? rank = null, IEnumerable<StagedSelectionComparison>? comparisons = null)
        : this(metadata, SnapshotEvidence(screenerEvidence), SnapshotReasons(reasons), rank,
            SnapshotComparisons(comparisons))
    {
    }

    [JsonConstructor]
    public StagedSelectionExclusion(ExchangeEligibilityMetadata? metadata,
        ImmutableValueList<ScreenerSnapshot> screenerEvidence,
        ImmutableValueList<string> reasons, int? rank,
        ImmutableValueList<StagedSelectionComparison> comparisons)
    {
        ArgumentNullException.ThrowIfNull(screenerEvidence);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentNullException.ThrowIfNull(comparisons);
        if (reasons.Count == 0 || reasons.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("An exclusion requires one or more reasons.",
                nameof(reasons));
        }

        if (rank is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rank), rank,
                "An exclusion rank must be positive when present.");
        }

        Metadata = metadata;
        ScreenerEvidence = new ImmutableValueList<ScreenerSnapshot>(screenerEvidence);
        Reasons = new ImmutableValueList<string>(reasons);
        Rank = rank;
        Comparisons = new ImmutableValueList<StagedSelectionComparison>(comparisons);
    }

    private static ImmutableValueList<ScreenerSnapshot> SnapshotEvidence(
        IEnumerable<ScreenerSnapshot> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new ImmutableValueList<ScreenerSnapshot>(evidence);
    }

    private static ImmutableValueList<string> SnapshotReasons(IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        return new ImmutableValueList<string>(reasons);
    }

    private static ImmutableValueList<StagedSelectionComparison> SnapshotComparisons(
        IEnumerable<StagedSelectionComparison>? comparisons)
    {
        return comparisons is null
            ? new ImmutableValueList<StagedSelectionComparison>([])
            : new ImmutableValueList<StagedSelectionComparison>(comparisons);
    }
}

/// <summary>
/// Immutable pointer to the exact output membership of the preceding stage.
/// The pointer binds a later stage to the same causal slice without embedding
/// another copy of the preceding result.
/// </summary>
public sealed record StagedSelectionStageReference
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public StagedSelectionStage Stage { get; }

    public string StageIdentity { get; }

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<InstrumentId> Membership { get; }

    [JsonIgnore]
    public string Identity => StageIdentity;

    public StagedSelectionStageReference(StagedSelectionStage stage,
        string stageIdentity, CausalEventCursor cursor,
        IEnumerable<InstrumentId> membership)
        : this(CurrentFormatVersion, stage, stageIdentity, cursor,
            SnapshotMembership(membership))
    {
    }

    [JsonConstructor]
    public StagedSelectionStageReference(int formatVersion,
        StagedSelectionStage stage, string stageIdentity, CausalEventCursor cursor,
        ImmutableValueList<InstrumentId> membership)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                $"Staged selection stage-reference format version {formatVersion} is not supported.");
        }

        ContractGuard.RequireDefined(stage, nameof(stage));
        ContractGuard.RequireText(stageIdentity, nameof(stageIdentity));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(membership);
        if (membership.Any(static item => item is null))
        {
            throw new ArgumentException("Stage membership cannot contain null values.",
                nameof(membership));
        }

        InstrumentId[] values = membership.ToArray();
        if (values.Distinct().Count() != values.Length)
        {
            throw new ArgumentException("Stage membership cannot contain duplicates.",
                nameof(membership));
        }

        FormatVersion = formatVersion;
        Stage = stage;
        StageIdentity = stageIdentity;
        Cursor = cursor;
        Membership = new ImmutableValueList<InstrumentId>(values);
    }

    private static ImmutableValueList<InstrumentId> SnapshotMembership(
        IEnumerable<InstrumentId> membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return new ImmutableValueList<InstrumentId>(membership);
    }
}

/// <summary>Versioned, explainable result of one staged selection.</summary>
public sealed record StagedSelectionResult
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public StagedSelectionStage Stage { get; }

    public StagedSelectionConfiguration Configuration { get; }

    public CausalEventCursor Cursor { get; }

    public MetadataCandidateFilterResult MetadataFilter { get; }

    public ImmutableValueList<StagedSelectionCandidate> Selected { get; }

    public ImmutableValueList<StagedSelectionExclusion> Exclusions { get; }

    /// <summary>
    /// Stable SHA-256 identity of this result's canonical JSON. The identity
    /// is excluded from that JSON so replay computes the same value.
    /// </summary>
    [JsonIgnore]
    public string StageIdentity { get; }

    [JsonIgnore]
    public string Identity => StageIdentity;

    public StagedSelectionStageReference? PreviousStage { get; }

    [JsonConstructor]
    public StagedSelectionResult(int formatVersion, StagedSelectionStage stage,
        StagedSelectionConfiguration configuration, CausalEventCursor cursor,
        MetadataCandidateFilterResult metadataFilter,
        ImmutableValueList<StagedSelectionCandidate> selected,
        ImmutableValueList<StagedSelectionExclusion> exclusions,
        StagedSelectionStageReference? previousStage = null)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                $"Staged selection format version {formatVersion} is not supported.");
        }

        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(metadataFilter);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(exclusions);
        if (configuration.Stage != stage)
        {
            throw new ArgumentException("Result stage must match its configuration.",
                nameof(stage));
        }

        if (selected.Count > configuration.MaximumCandidates)
        {
            throw new ArgumentException("Selected candidates exceed the stage capacity.",
                nameof(selected));
        }

        if (stage == StagedSelectionStage.Universe && previousStage is not null)
        {
            throw new ArgumentException(
                "The Universe stage cannot reference a preceding staged result.",
                nameof(previousStage));
        }

        if (stage != StagedSelectionStage.Universe)
        {
            if (previousStage is null)
            {
                throw new ArgumentException(
                    "A later staged result requires an explicit preceding-stage reference.",
                    nameof(previousStage));
            }

            if (previousStage.Stage >= stage)
            {
                throw new ArgumentException(
                    "A staged result must reference an earlier stage.",
                    nameof(previousStage));
            }

            if (!previousStage.Cursor.Equals(cursor))
            {
                throw new ArgumentException(
                    "A staged result and its preceding stage must share the same cursor.",
                    nameof(previousStage));
            }
        }

        FormatVersion = formatVersion;
        Stage = stage;
        Configuration = configuration;
        Cursor = cursor;
        MetadataFilter = metadataFilter;
        Selected = new ImmutableValueList<StagedSelectionCandidate>(selected);
        Exclusions = new ImmutableValueList<StagedSelectionExclusion>(exclusions);
        PreviousStage = previousStage;
        StageIdentity = ComputeIdentity(CoreJsonSerializer.Serialize(this));
    }

    public StagedSelectionResult(StagedSelectionStage stage,
        StagedSelectionConfiguration configuration, CausalEventCursor cursor,
        MetadataCandidateFilterResult metadataFilter,
        IEnumerable<StagedSelectionCandidate> selected,
        IEnumerable<StagedSelectionExclusion> exclusions,
        StagedSelectionStageReference? previousStage = null)
        : this(CurrentFormatVersion, stage, configuration, cursor, metadataFilter,
            Snapshot(selected), Snapshot(exclusions), previousStage)
    {
    }

    private static ImmutableValueList<T> Snapshot<T>(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ImmutableValueList<T>(values);
    }

    private static string ComputeIdentity(string canonicalJson)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

/// <summary>
/// First staged selection: metadata eligibility followed by deterministic
/// turnover/spread ranking into the bounded universe.
/// </summary>
public static class UniverseSelectionStage
{
    public static StagedSelectionResult Evaluate(StagedSelectionInput input,
        StagedSelectionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Stage != StagedSelectionStage.Universe)
        {
            throw new ArgumentException(
                "The first staged selection requires the Universe stage configuration.",
                nameof(configuration));
        }

        return EvaluateCore(input, configuration);
    }

    public static StagedSelectionResult Evaluate(
        IEnumerable<ExchangeEligibilityMetadata> metadata,
        IEnumerable<ScreenerSnapshot> screenerSnapshots, CausalEventCursor cursor,
        StagedSelectionConfiguration configuration) => Evaluate(
            new StagedSelectionInput(cursor, metadata, screenerSnapshots), configuration);

    private static StagedSelectionResult EvaluateCore(StagedSelectionInput input,
        StagedSelectionConfiguration configuration)
    {
        CausalEventCursor cursor = input.Cursor;
        ExchangeEligibilityMetadata[] allMetadata = input.Metadata.ToArray();
        Dictionary<string, ImmutableValueList<ScreenerSnapshot>> snapshotsByInstrument =
            BuildSnapshotIndex(input.ScreenerSnapshots);

        List<StagedSelectionExclusion> exclusions = [];
        foreach (ExchangeEligibilityMetadata metadata in allMetadata
            .Where(item => item.AvailabilityTime.Value > cursor.AsOf.Value)
            .OrderBy(static item => item.InstrumentId.Value, StringComparer.Ordinal))
        {
            exclusions.Add(new StagedSelectionExclusion(metadata, EvidenceFor(metadata,
                snapshotsByInstrument), ["metadata-not-available-at-cursor"]));
        }

        ExchangeEligibilityMetadata[] availableMetadata = allMetadata
            .Where(item => item.AvailabilityTime.Value <= cursor.AsOf.Value)
            .ToArray();
        MetadataCandidateFilterResult metadataFilter = MetadataCandidateFilter.Evaluate(
            availableMetadata);

        foreach (MetadataCandidateExclusion exclusion in metadataFilter.Exclusions)
        {
            exclusions.Add(new StagedSelectionExclusion(exclusion.Metadata,
                EvidenceFor(exclusion.Metadata, snapshotsByInstrument), exclusion.Reasons));
        }

        List<StagedSelectionCandidate> eligible = [];
        foreach (ExchangeEligibilityMetadata metadata in metadataFilter.Candidates)
        {
            string instrument = metadata.InstrumentId.Value;
            if (!snapshotsByInstrument.TryGetValue(instrument,
                out ImmutableValueList<ScreenerSnapshot>? evidence)
                || evidence.Count == 0)
            {
                exclusions.Add(new StagedSelectionExclusion(metadata, [],
                    ["screener-snapshot-missing"]));
                continue;
            }

            if (evidence.Count > 1)
            {
                exclusions.Add(new StagedSelectionExclusion(metadata, evidence,
                    ["screener-snapshot-conflict"]));
                continue;
            }

            ScreenerSnapshot snapshot = evidence[0];
            string[] causalReasons = ValidateSnapshot(snapshot, cursor);
            if (causalReasons.Length > 0)
            {
                exclusions.Add(new StagedSelectionExclusion(metadata, evidence,
                    causalReasons));
                continue;
            }

            ScreenerFeatureResult recalculated = ScreenerFeatureCalculator.Calculate(
                snapshot.Input, cursor, snapshot.Configuration);
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
                exclusions.Add(new StagedSelectionExclusion(metadata, evidence,
                    featureReasons, comparisons: comparisons));
                continue;
            }

            eligible.Add(new StagedSelectionCandidate(metadata, snapshot, recalculated,
                comparisons, rank: 1));
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
            .OrderBy(static item => item.Metadata?.InstrumentId.Value,
                StringComparer.Ordinal)
            .ThenBy(static item => item.Metadata is null ? 1 : 0)
            .ThenBy(static item => string.Join("\u001f", item.Reasons),
                StringComparer.Ordinal)
            .ToArray();
        return new StagedSelectionResult(StagedSelectionStage.Universe,
            configuration, cursor, metadataFilter, selected, orderedExclusions);
    }

    internal static Dictionary<string, ImmutableValueList<ScreenerSnapshot>> BuildSnapshotIndex(
        IEnumerable<ScreenerSnapshot> snapshots)
    {
        Dictionary<string, ImmutableValueList<ScreenerSnapshot>> index = new(
            StringComparer.Ordinal);
        foreach (IGrouping<string, ScreenerSnapshot> group in snapshots
            .GroupBy(static item => item.Input.InstrumentId.Value, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            ScreenerSnapshot[] distinct = group
                .GroupBy(static item => item.SnapshotId, StringComparer.Ordinal)
                .Select(static duplicate => duplicate.First())
                .OrderBy(static item => item.SnapshotId, StringComparer.Ordinal)
                .ToArray();
            index[group.Key] = new ImmutableValueList<ScreenerSnapshot>(distinct);
        }

        return index;
    }

    internal static ImmutableValueList<ScreenerSnapshot> EvidenceFor(
        ExchangeEligibilityMetadata metadata,
        Dictionary<string, ImmutableValueList<ScreenerSnapshot>> index)
    {
        return index.TryGetValue(metadata.InstrumentId.Value, out ImmutableValueList<ScreenerSnapshot>?
            evidence)
            ? evidence
            : new ImmutableValueList<ScreenerSnapshot>([]);
    }

    internal static string[] ValidateSnapshot(ScreenerSnapshot snapshot,
        CausalEventCursor cursor)
    {
        List<string> reasons = [];
        if (!snapshot.Cursor.Equals(cursor))
        {
            reasons.Add("screener-cursor-mismatch");
        }

        if (!snapshot.Input.IsEligible(cursor))
        {
            reasons.Add("screener-input-after-cursor");
        }

        bool observationAfterCursor = snapshot.Input.ClosedBars.Any(
                item => !item.IsEligible(cursor))
            || snapshot.Input.OpenInterest.Any(item => !item.IsEligible(cursor))
            || snapshot.Input.Quotes.Any(item => !item.IsEligible(cursor));
        if (observationAfterCursor)
        {
            reasons.Add("screener-observation-after-cursor");
        }

        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

}

internal sealed class StagedSelectionCandidateComparer(
    StagedSelectionConfiguration configuration) : IComparer<StagedSelectionCandidate>
{
    public int Compare(StagedSelectionCandidate? left,
        StagedSelectionCandidate? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        for (int index = 0; index < configuration.RankingKeys.Count; index++)
        {
            int comparison = left.Comparisons[index].Value.CompareTo(
                right.Comparisons[index].Value);
            if (comparison != 0)
            {
                return configuration.RankingKeys[index].Direction ==
                    StagedSelectionDirection.Ascending
                    ? comparison
                    : -comparison;
            }
        }

        return StringComparer.Ordinal.Compare(left.InstrumentId.Value,
            right.InstrumentId.Value);
    }
}
