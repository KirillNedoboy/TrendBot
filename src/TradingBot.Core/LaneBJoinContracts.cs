using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Concrete Lane B observation family retained by a causal join.</summary>
public enum LaneBObservationKind
{
    OpenInterest,
    Funding,
    Liquidation,
    FailedContinuation
}

/// <summary>
/// Stable, immutable reference to one Lane B observation. The reference keeps
/// the complete causal/provenance fields required to audit a joined prefix
/// without changing the existing MarketEvent contract.
/// </summary>
public sealed record LaneBObservationReference
{
    public LaneBObservationKind Kind { get; }

    public LaneBObservationIdentity Identity { get; }

    public string SourceId { get; }

    [JsonIgnore]
    public string SourceIdentity => SourceId;

    public string EventId { get; }

    public InstrumentId InstrumentId { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp? ExchangeTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }

    public string Provenance { get; }

    /// <summary>Canonical payload identity used to distinguish same-key conflicts.</summary>
    public string PayloadFingerprint { get; }

    [JsonConstructor]
    public LaneBObservationReference(LaneBObservationKind kind,
        LaneBObservationIdentity identity, string sourceId, string eventId,
        InstrumentId instrumentId, UtcTimestamp eventTime, UtcTimestamp? exchangeTime,
        UtcTimestamp receiveTime, UtcTimestamp availabilityTime, long stableSequence,
        string provenance, string? payloadFingerprint = null)
    {
        ContractGuard.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(identity);
        ContractGuard.RequireText(sourceId, nameof(sourceId));
        ContractGuard.RequireText(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ContractGuard.RequireText(provenance, nameof(provenance));
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException(
                "Observation availability cannot precede receive time.",
                nameof(availabilityTime));
        }

        if (stableSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stableSequence), stableSequence,
                "Observation stable sequence cannot be negative.");
        }

        if (!StringComparer.Ordinal.Equals(identity.SourceId, sourceId)
            || identity.InstrumentId != instrumentId
            || !StringComparer.Ordinal.Equals(identity.EventId, eventId)
            || identity.StableSequence != stableSequence)
        {
            throw new ArgumentException(
                "The observation identity does not match its retained fields.",
                nameof(identity));
        }

        Kind = kind;
        Identity = identity;
        SourceId = sourceId;
        EventId = eventId;
        InstrumentId = instrumentId;
        EventTime = eventTime;
        ExchangeTime = exchangeTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        Provenance = provenance;
        PayloadFingerprint = payloadFingerprint ?? string.Empty;
    }

    public static LaneBObservationReference From(ILaneBCausalObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        LaneBObservationKind kind = observation switch
        {
            LaneBOpenInterestObservation => LaneBObservationKind.OpenInterest,
            LaneBFundingObservation => LaneBObservationKind.Funding,
            LaneBLiquidationObservation => LaneBObservationKind.Liquidation,
            LaneBFailedContinuationObservation => LaneBObservationKind.FailedContinuation,
            _ => throw new ArgumentException(
                "The Lane B observation type is not supported by the join.", nameof(observation))
        };
        string payloadFingerprint = observation switch
        {
            LaneBOpenInterestObservation value => CoreJsonSerializer.Serialize(value.Update),
            LaneBFundingObservation value => CoreJsonSerializer.Serialize(value.Update),
            LaneBLiquidationObservation value => CoreJsonSerializer.Serialize(value),
            LaneBFailedContinuationObservation value => CoreJsonSerializer.Serialize(value),
            _ => string.Empty
        };
        return new LaneBObservationReference(kind, observation.Identity, observation.SourceId,
            observation.EventId, observation.InstrumentId, observation.EventTime,
            observation.ExchangeTime, observation.ReceiveTime, observation.AvailabilityTime,
            observation.StableSequence, observation.Provenance, payloadFingerprint);
    }

    public bool IsEligible(CausalEventCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return EventTime.Value <= cursor.AsOf.Value
            && AvailabilityTime.Value <= cursor.AsOf.Value
            && StableSequence <= cursor.MaxStableSequence;
    }
}

/// <summary>
/// Deterministic causal Lane B join of observations, quality, and shadow
/// evidence for one validated cursor prefix.
/// </summary>
public sealed record LaneBJoinSnapshot
{
    public const int CurrentFormatVersion = 1;

    public const string CurrentAlgorithmVersion = "lane-b-join-v1";

    public int FormatVersion { get; }

    public string AlgorithmVersion { get; }

    public CausalEventCursor Cursor { get; }

    public ImmutableValueList<LaneBObservationReference> Observations { get; }

    [JsonIgnore]
    public ImmutableValueList<LaneBObservationReference> ObservationReferences => Observations;

    public LaneBQualityState Quality { get; }

    [JsonIgnore]
    public LaneBQualityState QualityState => Quality;

    public LaneBShadowResult Shadow { get; }

    [JsonIgnore]
    public LaneBShadowResult ShadowResult => Shadow;

    public ImmutableValueList<string> Reasons { get; }

    [JsonIgnore]
    public bool IsValid => SnapshotId.Length == SHA256.HashSizeInBytes * 2;

    [JsonIgnore]
    public bool HasConflict => Quality.Status == LaneBQualityStatus.Conflict;

    /// <summary>Lower-case SHA-256 of canonical join content.</summary>
    [JsonIgnore]
    public string SnapshotId { get; }

    [JsonIgnore]
    public string Identity => SnapshotId;

    [JsonIgnore]
    public string JoinIdentity => SnapshotId;

    [JsonIgnore]
    public byte[] IdentityBytes => Convert.FromHexString(SnapshotId);

    public static LaneBJoinSnapshot Create(CausalEventCursor cursor,
        IEnumerable<ILaneBCausalObservation> observations, LaneBQualityState quality,
        LaneBShadowResult shadow)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(shadow);
        LaneBObservationReference[] source = observations.Select(
            LaneBObservationReference.From).ToArray();
        JoinAssembly assembly = Assemble(cursor, source, quality, shadow);
        return new LaneBJoinSnapshot(CurrentFormatVersion, CurrentAlgorithmVersion, cursor,
            new ImmutableValueList<LaneBObservationReference>(assembly.Observations),
            assembly.Quality, shadow,
            new ImmutableValueList<string>(assembly.Reasons));
    }

    public static LaneBJoinSnapshot Join(CausalEventCursor cursor,
        IEnumerable<ILaneBCausalObservation> observations, LaneBQualityState quality,
        LaneBShadowResult shadow) => Create(cursor, observations, quality, shadow);

    public static LaneBJoinSnapshot Build(CausalEventCursor cursor,
        IEnumerable<ILaneBCausalObservation> observations, LaneBQualityState quality,
        LaneBShadowResult shadow) => Create(cursor, observations, quality, shadow);

    [JsonConstructor]
    public LaneBJoinSnapshot(int formatVersion, string algorithmVersion,
        CausalEventCursor cursor,
        ImmutableValueList<LaneBObservationReference> observations,
        LaneBQualityState quality, LaneBShadowResult shadow,
        ImmutableValueList<string> reasons)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The Lane B join format version is unsupported.");
        }

        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion), algorithmVersion,
                "The Lane B join algorithm version is unsupported.");
        }

        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(shadow);
        ArgumentNullException.ThrowIfNull(reasons);
        if (observations.Any(static value => value is null))
        {
            throw new ArgumentException(
                "A Lane B join cannot contain null observation references.",
                nameof(observations));
        }

        if (quality.Cursor != cursor || shadow.Cursor != cursor)
        {
            throw new ArgumentException(
                "Lane B join evidence must use the join cursor.", nameof(cursor));
        }

        ValidateReferences(cursor, observations);
        ValidateQualityReferences(observations, quality);

        FormatVersion = formatVersion;
        AlgorithmVersion = algorithmVersion;
        Cursor = cursor;
        Observations = new ImmutableValueList<LaneBObservationReference>(observations);
        Quality = quality;
        Shadow = shadow;
        Reasons = new ImmutableValueList<string>(StableReasons(reasons));
        SnapshotId = ComputeIdentity(CoreJsonSerializer.Serialize(this));
    }

    private static JoinAssembly Assemble(CausalEventCursor cursor,
        IEnumerable<LaneBObservationReference> source, LaneBQualityState quality,
        LaneBShadowResult shadow)
    {
        if (quality.Cursor != cursor || shadow.Cursor != cursor)
        {
            throw new ArgumentException(
                "Lane B join evidence must use the join cursor.", nameof(cursor));
        }

        LaneBObservationReference[] eligible = source
            .Where(value => value.IsEligible(cursor))
            .OrderBy(static value => value.EventTime.Value)
            .ThenBy(static value => value.StableSequence)
            .ThenBy(static value => value.AvailabilityTime.Value)
            .ThenBy(static value => value.ReceiveTime.Value)
            .ThenBy(static value => value.ExchangeTime?.Value ?? DateTimeOffset.MinValue)
            .ThenBy(static value => value.Kind)
            .ThenBy(static value => value.SourceId, StringComparer.Ordinal)
            .ThenBy(static value => value.EventId, StringComparer.Ordinal)
            .ThenBy(static value => value.Provenance, StringComparer.Ordinal)
            .ThenBy(static value => value.PayloadFingerprint, StringComparer.Ordinal)
            .ToArray();

        List<LaneBObservationReference> normalized = [];
        Dictionary<string, LaneBObservationReference> seen = new(StringComparer.Ordinal);
        List<string> joinReasons = [];
        foreach (LaneBObservationReference candidate in eligible)
        {
            string key = IdentityKey(candidate);
            if (!seen.TryGetValue(key, out LaneBObservationReference? existing))
            {
                seen.Add(key, candidate);
                normalized.Add(candidate);
            }
            else if (existing == candidate)
            {
                joinReasons.Add($"duplicate-observation:{candidate.EventId}");
            }
            else
            {
                // Keep both rows so an operator can inspect the disagreement.
                normalized.Add(candidate);
                joinReasons.Add($"conflict-observation:{candidate.EventId}");
            }
        }

        LaneBObservationReference[] ordered = normalized
            .OrderBy(static value => value.EventTime.Value)
            .ThenBy(static value => value.StableSequence)
            .ThenBy(static value => value.AvailabilityTime.Value)
            .ThenBy(static value => value.ReceiveTime.Value)
            .ThenBy(static value => value.ExchangeTime?.Value ?? DateTimeOffset.MinValue)
            .ThenBy(static value => value.Kind)
            .ThenBy(static value => value.SourceId, StringComparer.Ordinal)
            .ThenBy(static value => value.EventId, StringComparer.Ordinal)
            .ThenBy(static value => value.Provenance, StringComparer.Ordinal)
            .ThenBy(static value => value.PayloadFingerprint, StringComparer.Ordinal)
            .ToArray();
        ValidateQualityReferences(ordered, quality);

        LaneBQualityState joinedQuality = quality;
        if (joinReasons.Any(static reason => reason.StartsWith("conflict-",
                StringComparison.Ordinal)))
        {
            joinedQuality = new LaneBQualityState(LaneBQualityState.CurrentFormatVersion,
                LaneBQualityState.CurrentAlgorithmVersion, cursor,
                new ImmutableValueList<LaneBObservationIdentity>(
                    ordered.Select(static value => value.Identity)), quality.Freshness,
                LaneBQualityStatus.Conflict,
                new ImmutableValueList<string>(StableReasons(
                    quality.Reasons.Concat(joinReasons))));
        }

        return new JoinAssembly(ordered, joinedQuality,
            StableReasons(quality.Reasons.Concat(joinReasons)));
    }

    private static void ValidateReferences(CausalEventCursor cursor,
        ImmutableValueList<LaneBObservationReference> observations)
    {
        LaneBObservationReference? previous = null;
        HashSet<LaneBObservationReference> exact = [];
        foreach (LaneBObservationReference current in observations)
        {
            if (!current.IsEligible(cursor))
            {
                throw new ArgumentException(
                    "A serialized Lane B join contains an ineligible observation.",
                    nameof(observations));
            }

            if (!exact.Add(current))
            {
                throw new ArgumentException(
                    "A serialized Lane B join contains an exact duplicate observation.",
                    nameof(observations));
            }

            if (previous is not null && Compare(previous, current) > 0)
            {
                throw new ArgumentException(
                    "A serialized Lane B join is not in deterministic causal order.",
                    nameof(observations));
            }

            previous = current;
        }
    }

    private static void ValidateQualityReferences(
        IEnumerable<LaneBObservationReference> observations, LaneBQualityState quality)
    {
        HashSet<LaneBObservationIdentity> available = observations
            .Select(static value => value.Identity).ToHashSet();
        if (quality.ObservationReferences.Any(value => !available.Contains(value)))
        {
            throw new ArgumentException(
                "Lane B quality references must be present in the causal join.",
                nameof(quality));
        }
    }

    private static int Compare(LaneBObservationReference left,
        LaneBObservationReference right)
    {
        int comparison = left.EventTime.Value.CompareTo(right.EventTime.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.StableSequence.CompareTo(right.StableSequence);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.AvailabilityTime.Value.CompareTo(right.AvailabilityTime.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.ReceiveTime.Value.CompareTo(right.ReceiveTime.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = (left.ExchangeTime?.Value ?? DateTimeOffset.MinValue).CompareTo(
            right.ExchangeTime?.Value ?? DateTimeOffset.MinValue);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Kind.CompareTo(right.Kind);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.SourceId, right.SourceId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.EventId, right.EventId);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.PayloadFingerprint,
                right.PayloadFingerprint);
    }

    private static string IdentityKey(LaneBObservationReference value) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{value.Kind}\u001f{value.SourceId}\u001f{value.InstrumentId.Value}\u001f{value.EventId}");

    private static IEnumerable<string> StableReasons(IEnumerable<string> reasons) => reasons
        .Where(static reason => !string.IsNullOrWhiteSpace(reason))
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static reason => reason, StringComparer.Ordinal);

    private static string ComputeIdentity(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)))
            .ToLowerInvariant();

    private sealed record JoinAssembly(LaneBObservationReference[] Observations,
        LaneBQualityState Quality, IEnumerable<string> Reasons);
}
