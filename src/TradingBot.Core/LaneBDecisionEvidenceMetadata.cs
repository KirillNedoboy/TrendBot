using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>
/// Versioned, optional Lane B trace metadata attached to one DecisionEvidence.
/// It records the validated join identity and preserves the quality details
/// that explain an effective Decision Engine status.
/// </summary>
public sealed record LaneBDecisionEvidenceMetadata
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentAlgorithmVersion = "lane-b-decision-evidence-v1";

    public int FormatVersion { get; }

    public string AlgorithmVersion { get; }

    public string JoinIdentity { get; }

    [JsonIgnore]
    public string JoinId => JoinIdentity;

    public string ShadowEvidenceId { get; }

    [JsonIgnore]
    public string ShadowResultId => ShadowEvidenceId;

    public string ShadowAlgorithmVersion { get; }

    public LaneBShadowStatus ShadowStatus { get; }

    public LaneBQualityStatus QualityStatus { get; }

    [JsonIgnore]
    public LaneBQualityStatus Quality => QualityStatus;

    [JsonIgnore]
    public LaneBQualityStatus QualityState => QualityStatus;

    public DecisionEvidenceStatus EvidenceStatus { get; }

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<LaneBObservationIdentity> ObservationReferences { get; }

    [JsonIgnore]
    public ImmutableValueList<LaneBObservationIdentity> ObservationIds =>
        ObservationReferences;

    public ImmutableValueList<string> QualityReasons { get; }

    [JsonIgnore]
    public ImmutableValueList<string> Reasons => QualityReasons;

    [JsonIgnore]
    public bool IsReady => QualityStatus == LaneBQualityStatus.Ready;

    [JsonConstructor]
    public LaneBDecisionEvidenceMetadata(int formatVersion, string algorithmVersion,
        string joinIdentity, string shadowEvidenceId, string shadowAlgorithmVersion,
        LaneBShadowStatus shadowStatus, LaneBQualityStatus qualityStatus,
        DecisionEvidenceStatus evidenceStatus, CausalEventCursor cursor,
        ImmutableValueList<LaneBObservationIdentity> observationReferences,
        ImmutableValueList<string> qualityReasons)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The Lane B Decision Evidence metadata format is unsupported.");
        }

        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion), algorithmVersion,
                "The Lane B Decision Evidence metadata version is unsupported.");
        }

        ContractGuard.RequireText(joinIdentity, nameof(joinIdentity));
        ContractGuard.RequireText(shadowEvidenceId, nameof(shadowEvidenceId));
        ContractGuard.RequireText(shadowAlgorithmVersion, nameof(shadowAlgorithmVersion));
        RequireIdentity(joinIdentity, nameof(joinIdentity));
        RequireIdentity(shadowEvidenceId, nameof(shadowEvidenceId));
        ContractGuard.RequireDefined(shadowStatus, nameof(shadowStatus));
        ContractGuard.RequireDefined(qualityStatus, nameof(qualityStatus));
        ContractGuard.RequireDefined(evidenceStatus, nameof(evidenceStatus));
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(observationReferences);
        ArgumentNullException.ThrowIfNull(qualityReasons);
        if (observationReferences.Any(static value => value is null))
        {
            throw new ArgumentException(
                "Lane B metadata observation references cannot contain null values.",
                nameof(observationReferences));
        }

        DecisionEvidenceStatus expectedStatus = qualityStatus == LaneBQualityStatus.Ready
            ? ToEvidenceStatus(shadowStatus)
            : DecisionEvidenceStatus.Unknown;
        if (evidenceStatus != expectedStatus)
        {
            throw new ArgumentException(
                "Lane B metadata quality and evidence statuses are inconsistent.",
                nameof(evidenceStatus));
        }

        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        JoinIdentity = joinIdentity;
        ShadowEvidenceId = shadowEvidenceId;
        ShadowAlgorithmVersion = shadowAlgorithmVersion;
        ShadowStatus = shadowStatus;
        QualityStatus = qualityStatus;
        EvidenceStatus = evidenceStatus;
        Cursor = cursor;
        ObservationReferences = new ImmutableValueList<LaneBObservationIdentity>(
            observationReferences.OrderBy(static value => value.SourceId,
                    StringComparer.Ordinal)
                .ThenBy(static value => value.InstrumentId.Value, StringComparer.Ordinal)
                .ThenBy(static value => value.EventId, StringComparer.Ordinal)
                .ThenBy(static value => value.StableSequence));
        QualityReasons = new ImmutableValueList<string>(StableReasons(qualityReasons));
    }

    public static LaneBDecisionEvidenceMetadata Create(LaneBJoinSnapshot join)
    {
        ArgumentNullException.ThrowIfNull(join);
        if (!join.IsValid)
        {
            throw new ArgumentException("The Lane B join is not valid.", nameof(join));
        }

        string shadowEvidenceId = ComputeShadowIdentity(join.Shadow);
        return new LaneBDecisionEvidenceMetadata(CurrentFormatVersion,
            CurrentAlgorithmVersion, join.Identity, shadowEvidenceId,
            join.Shadow.AlgorithmVersion, join.Shadow.Status, join.Quality.Status,
            EffectiveStatus(join.Quality.Status, join.Shadow.Status), join.Cursor,
            join.Quality.ObservationReferences, join.Quality.Reasons);
    }

    public static LaneBDecisionEvidenceMetadata FromJoin(LaneBJoinSnapshot join) =>
        Create(join);

    public static LaneBDecisionEvidenceMetadata CreateFromJoin(LaneBJoinSnapshot join) =>
        Create(join);

    private static DecisionEvidenceStatus EffectiveStatus(LaneBQualityStatus quality,
        LaneBShadowStatus shadow) => quality == LaneBQualityStatus.Ready
            ? ToEvidenceStatus(shadow)
            : DecisionEvidenceStatus.Unknown;

    private static DecisionEvidenceStatus ToEvidenceStatus(LaneBShadowStatus shadow) =>
        shadow switch
        {
            LaneBShadowStatus.Confirm => DecisionEvidenceStatus.Confirm,
            LaneBShadowStatus.Neutral => DecisionEvidenceStatus.Neutral,
            LaneBShadowStatus.Veto => DecisionEvidenceStatus.Veto,
            LaneBShadowStatus.InsufficientData => DecisionEvidenceStatus.Unknown,
            _ => throw new ArgumentOutOfRangeException(nameof(shadow), shadow,
                "The Lane B shadow status is unsupported.")
        };

    private static IEnumerable<string> StableReasons(IEnumerable<string> reasons) => reasons
        .Where(static reason => !string.IsNullOrWhiteSpace(reason))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static reason => reason, StringComparer.Ordinal);

    private static void RequireIdentity(string value, string parameterName)
    {
        if (value.Length != SHA256.HashSizeInBytes * 2
            || !StringComparer.Ordinal.Equals(value, value.ToLowerInvariant()))
        {
            throw new ArgumentException(
                "Lane B metadata identities must be lower-case SHA-256 values.",
                parameterName);
        }

        try
        {
            _ = Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "Lane B metadata identities must be hexadecimal SHA-256 values.",
                parameterName, exception);
        }
    }

    private static string ComputeShadowIdentity(LaneBShadowResult shadow)
    {
        string canonical = CoreJsonSerializer.Serialize(shadow);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}
