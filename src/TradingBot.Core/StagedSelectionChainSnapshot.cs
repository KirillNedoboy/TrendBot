using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Canonical, replayable evidence for the complete three-stage reduction.
/// </summary>
public sealed record StagedSelectionChainSnapshot
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentAlgorithmVersionValue = "staged-selection-v1";

    public static string CurrentAlgorithmVersion => CurrentAlgorithmVersionValue;

    public int FormatVersion { get; }

    public string AlgorithmVersion { get; }

    public CausalEventCursor Cursor { get; }

    public StagedSelectionInput UniverseInput { get; }

    public ImmutableValueList<ScreenerSnapshot> CandidatePoolEvidence { get; }

    public ImmutableValueList<ScreenerSnapshot> RealtimeMonitorEvidence { get; }

    public StagedSelectionConfiguration UniverseConfiguration { get; }

    public StagedSelectionConfiguration CandidatePoolConfiguration { get; }

    public StagedSelectionConfiguration RealtimeMonitorConfiguration { get; }

    public StagedSelectionResult UniverseResult { get; }

    public StagedSelectionResult CandidatePoolResult { get; }

    public StagedSelectionResult RealtimeMonitorResult { get; }

    /// <summary>
    /// Lower-case SHA-256 of the canonical chain JSON without this identity.
    /// </summary>
    [JsonIgnore]
    public string SnapshotIdentity { get; }

    [JsonIgnore]
    public string Identity => SnapshotIdentity;

    public static StagedSelectionChainSnapshot Create(
        StagedSelectionInput universeInput,
        StagedSelectionConfiguration universeConfiguration,
        IEnumerable<ScreenerSnapshot> candidatePoolEvidence,
        StagedSelectionConfiguration candidatePoolConfiguration,
        IEnumerable<ScreenerSnapshot> realtimeMonitorEvidence,
        StagedSelectionConfiguration realtimeMonitorConfiguration)
    {
        ArgumentNullException.ThrowIfNull(universeInput);
        ArgumentNullException.ThrowIfNull(universeConfiguration);
        ArgumentNullException.ThrowIfNull(candidatePoolEvidence);
        ArgumentNullException.ThrowIfNull(candidatePoolConfiguration);
        ArgumentNullException.ThrowIfNull(realtimeMonitorEvidence);
        ArgumentNullException.ThrowIfNull(realtimeMonitorConfiguration);

        StagedSelectionInput normalizedInput = NormalizeInput(universeInput);
        ImmutableValueList<ScreenerSnapshot> normalizedCandidateEvidence =
            NormalizeEvidence(candidatePoolEvidence);
        ImmutableValueList<ScreenerSnapshot> normalizedMonitorEvidence =
            NormalizeEvidence(realtimeMonitorEvidence);
        StagedSelectionResult universeResult = UniverseSelectionStage.Evaluate(
            normalizedInput, universeConfiguration);
        StagedSelectionResult candidatePoolResult = CandidatePoolSelectionStage.Evaluate(
            universeResult, normalizedCandidateEvidence, candidatePoolConfiguration);
        StagedSelectionResult realtimeMonitorResult = RealtimeMonitorSelectionStage.Evaluate(
            candidatePoolResult, normalizedMonitorEvidence,
            realtimeMonitorConfiguration);
        return new StagedSelectionChainSnapshot(CurrentFormatVersion,
            CurrentAlgorithmVersionValue, normalizedInput.Cursor, normalizedInput,
            normalizedCandidateEvidence, normalizedMonitorEvidence,
            universeConfiguration, candidatePoolConfiguration,
            realtimeMonitorConfiguration, universeResult, candidatePoolResult,
            realtimeMonitorResult);
    }

    [JsonConstructor]
    public StagedSelectionChainSnapshot(int formatVersion, string algorithmVersion,
        CausalEventCursor cursor, StagedSelectionInput universeInput,
        ImmutableValueList<ScreenerSnapshot> candidatePoolEvidence,
        ImmutableValueList<ScreenerSnapshot> realtimeMonitorEvidence,
        StagedSelectionConfiguration universeConfiguration,
        StagedSelectionConfiguration candidatePoolConfiguration,
        StagedSelectionConfiguration realtimeMonitorConfiguration,
        StagedSelectionResult universeResult,
        StagedSelectionResult candidatePoolResult,
        StagedSelectionResult realtimeMonitorResult)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                $"Staged selection chain format version {formatVersion} is not supported.");
        }

        if (!StringComparer.Ordinal.Equals(algorithmVersion,
            CurrentAlgorithmVersionValue))
        {
            throw new ArgumentException(
                $"Staged selection chain algorithm version {algorithmVersion} is not supported.",
                nameof(algorithmVersion));
        }

        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(universeInput);
        ArgumentNullException.ThrowIfNull(candidatePoolEvidence);
        ArgumentNullException.ThrowIfNull(realtimeMonitorEvidence);
        ArgumentNullException.ThrowIfNull(universeConfiguration);
        ArgumentNullException.ThrowIfNull(candidatePoolConfiguration);
        ArgumentNullException.ThrowIfNull(realtimeMonitorConfiguration);
        ArgumentNullException.ThrowIfNull(universeResult);
        ArgumentNullException.ThrowIfNull(candidatePoolResult);
        ArgumentNullException.ThrowIfNull(realtimeMonitorResult);

        StagedSelectionInput normalizedInput = NormalizeInput(universeInput);
        ImmutableValueList<ScreenerSnapshot> normalizedCandidateEvidence =
            NormalizeEvidence(candidatePoolEvidence);
        ImmutableValueList<ScreenerSnapshot> normalizedMonitorEvidence =
            NormalizeEvidence(realtimeMonitorEvidence);
        ValidateChain(cursor, normalizedInput, normalizedCandidateEvidence,
            normalizedMonitorEvidence, universeConfiguration,
            candidatePoolConfiguration, realtimeMonitorConfiguration,
            universeResult, candidatePoolResult, realtimeMonitorResult);

        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        Cursor = cursor;
        UniverseInput = normalizedInput;
        CandidatePoolEvidence = normalizedCandidateEvidence;
        RealtimeMonitorEvidence = normalizedMonitorEvidence;
        UniverseConfiguration = universeConfiguration;
        CandidatePoolConfiguration = candidatePoolConfiguration;
        RealtimeMonitorConfiguration = realtimeMonitorConfiguration;
        UniverseResult = universeResult;
        CandidatePoolResult = candidatePoolResult;
        RealtimeMonitorResult = realtimeMonitorResult;
        SnapshotIdentity = ComputeIdentity(CoreJsonSerializer.Serialize(this));
    }

    /// <summary>Rebuilds all three stages from the retained inputs.</summary>
    public StagedSelectionChainSnapshot Replay()
    {
        StagedSelectionChainSnapshot replayed = Create(UniverseInput,
            UniverseConfiguration, CandidatePoolEvidence, CandidatePoolConfiguration,
            RealtimeMonitorEvidence, RealtimeMonitorConfiguration);
        if (!StringComparer.Ordinal.Equals(replayed.SnapshotIdentity,
            SnapshotIdentity))
        {
            throw new InvalidOperationException(
                "Staged selection chain replay produced a different identity.");
        }

        return replayed;
    }

    private static void ValidateChain(CausalEventCursor cursor,
        StagedSelectionInput universeInput,
        ImmutableValueList<ScreenerSnapshot> candidatePoolEvidence,
        ImmutableValueList<ScreenerSnapshot> realtimeMonitorEvidence,
        StagedSelectionConfiguration universeConfiguration,
        StagedSelectionConfiguration candidatePoolConfiguration,
        StagedSelectionConfiguration realtimeMonitorConfiguration,
        StagedSelectionResult universeResult,
        StagedSelectionResult candidatePoolResult,
        StagedSelectionResult realtimeMonitorResult)
    {
        if (!universeInput.Cursor.Equals(cursor))
        {
            throw new ArgumentException(
                "The chain input and chain cursor must be identical.",
                nameof(universeInput));
        }

        ValidateEvidenceCursors(universeInput.ScreenerSnapshots, cursor,
            nameof(universeInput));
        ValidateEvidenceCursors(candidatePoolEvidence, cursor,
            nameof(candidatePoolEvidence));
        ValidateEvidenceCursors(realtimeMonitorEvidence, cursor,
            nameof(realtimeMonitorEvidence));

        if (universeResult.Stage != StagedSelectionStage.Universe
            || candidatePoolResult.Stage != StagedSelectionStage.CandidatePool
            || realtimeMonitorResult.Stage != StagedSelectionStage.RealtimeMonitor)
        {
            throw new ArgumentException(
                "A chain must contain Universe, CandidatePool, and RealtimeMonitor results.",
                nameof(universeResult));
        }

        if (!universeConfiguration.Equals(universeResult.Configuration)
            || !candidatePoolConfiguration.Equals(candidatePoolResult.Configuration)
            || !realtimeMonitorConfiguration.Equals(realtimeMonitorResult.Configuration))
        {
            throw new ArgumentException(
                "Chain configurations must match their corresponding stage results.",
                nameof(universeConfiguration));
        }

        if (!universeResult.Cursor.Equals(cursor)
            || !candidatePoolResult.Cursor.Equals(cursor)
            || !realtimeMonitorResult.Cursor.Equals(cursor))
        {
            throw new ArgumentException(
                "Every chain stage must use the same causal cursor.", nameof(cursor));
        }

        if (universeResult.PreviousStage is not null)
        {
            throw new ArgumentException(
                "The Universe result cannot reference a preceding stage.",
                nameof(universeResult));
        }

        ValidateReference(candidatePoolResult.PreviousStage,
            StagedSelectionStage.Universe, universeResult.StageIdentity, cursor,
            universeResult.Selected, nameof(candidatePoolResult));
        ValidateReference(realtimeMonitorResult.PreviousStage,
            StagedSelectionStage.CandidatePool, candidatePoolResult.StageIdentity,
            cursor, candidatePoolResult.Selected, nameof(realtimeMonitorResult));

        StagedSelectionResult replayedUniverse = UniverseSelectionStage.Evaluate(
            universeInput, universeConfiguration);
        RequireSameResult(replayedUniverse, universeResult,
            "Universe result cannot be replayed from the retained input.");
        StagedSelectionResult replayedCandidatePool =
            CandidatePoolSelectionStage.Evaluate(replayedUniverse, candidatePoolEvidence,
                candidatePoolConfiguration);
        RequireSameResult(replayedCandidatePool, candidatePoolResult,
            "CandidatePool result cannot be replayed from the retained evidence.");
        StagedSelectionResult replayedRealtimeMonitor =
            RealtimeMonitorSelectionStage.Evaluate(replayedCandidatePool,
                realtimeMonitorEvidence, realtimeMonitorConfiguration);
        RequireSameResult(replayedRealtimeMonitor, realtimeMonitorResult,
            "RealtimeMonitor result cannot be replayed from the retained evidence.");
    }

    private static void ValidateEvidenceCursors(
        IEnumerable<ScreenerSnapshot> evidence, CausalEventCursor cursor,
        string parameterName)
    {
        foreach (ScreenerSnapshot snapshot in evidence)
        {
            if (!snapshot.Cursor.Equals(cursor))
            {
                throw new ArgumentException(
                    "Every chain screener snapshot must use the chain cursor.",
                    parameterName);
            }
        }
    }

    private static void ValidateReference(
        StagedSelectionStageReference? reference,
        StagedSelectionStage expectedStage, string expectedIdentity,
        CausalEventCursor expectedCursor,
        IEnumerable<StagedSelectionCandidate> expectedMembership,
        string parameterName)
    {
        if (reference is null
            || reference.Stage != expectedStage
            || !StringComparer.Ordinal.Equals(reference.StageIdentity,
                expectedIdentity)
            || !reference.Cursor.Equals(expectedCursor)
            || !reference.Membership.SequenceEqual(expectedMembership.Select(
                static candidate => candidate.InstrumentId)))
        {
            throw new ArgumentException(
                "A chain stage reference does not match its preceding result.",
                parameterName);
        }
    }

    private static void RequireSameResult(StagedSelectionResult expected,
        StagedSelectionResult actual, string message)
    {
        if (!StringComparer.Ordinal.Equals(CoreJsonSerializer.Serialize(expected),
            CoreJsonSerializer.Serialize(actual)))
        {
            throw new ArgumentException(message, nameof(actual));
        }
    }

    private static StagedSelectionInput NormalizeInput(StagedSelectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new StagedSelectionInput(input.Cursor,
            input.Metadata
                .OrderBy(static item => item.InstrumentId.Value, StringComparer.Ordinal)
                .ThenBy(static item => CoreJsonSerializer.Serialize(item),
                    StringComparer.Ordinal),
            NormalizeEvidence(input.ScreenerSnapshots));
    }

    private static ImmutableValueList<ScreenerSnapshot> NormalizeEvidence(
        IEnumerable<ScreenerSnapshot> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ScreenerSnapshot[] values = evidence.ToArray();
        if (values.Any(static item => item is null))
        {
            throw new ArgumentException(
                "A staged selection chain cannot contain null screener evidence.",
                nameof(evidence));
        }

        return new ImmutableValueList<ScreenerSnapshot>(values
            .OrderBy(static item => item.Input.InstrumentId.Value,
                StringComparer.Ordinal)
            .ThenBy(static item => item.SnapshotId, StringComparer.Ordinal));
    }

    private static string ComputeIdentity(string canonicalJson)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
