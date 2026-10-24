using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum MultiSourceLaneKind
{
    L2,
    Trades,
    OpenInterest,
    Funding,
    Liquidations
}

public enum MultiSourceCoverage
{
    Complete,
    Partial,
    Unknown
}

public enum MultiSourceQuality
{
    Ready,
    Degraded,
    Unavailable,
    Unknown
}

public sealed record MultiSourceDatasetSource
{
    public string SourceId { get; }
    public MultiSourceLaneKind Kind { get; }
    public string SourceManifestIdentity { get; }
    public string? QualityEvidenceIdentity { get; }
    public ImmutableValueList<string> SupportingEvidenceIdentities { get; }
    public MultiSourceCoverage Coverage { get; }
    public MultiSourceQuality Quality { get; }
    public ImmutableValueList<string> QualityReasons { get; }
    public long RecordCount { get; }
    public UtcTimestamp? EarliestEventTime { get; }
    public UtcTimestamp? LatestEventTime { get; }
    public UtcTimestamp? EarliestAvailabilityTime { get; }
    public UtcTimestamp? LatestAvailabilityTime { get; }
    public long? FirstSourceSequence { get; }
    public long? LastSourceSequence { get; }

    [JsonConstructor]
    public MultiSourceDatasetSource(string sourceId, MultiSourceLaneKind kind,
        string sourceManifestIdentity, string? qualityEvidenceIdentity,
        ImmutableValueList<string> supportingEvidenceIdentities,
        MultiSourceCoverage coverage, MultiSourceQuality quality,
        ImmutableValueList<string> qualityReasons, long recordCount,
        UtcTimestamp? earliestEventTime, UtcTimestamp? latestEventTime,
        UtcTimestamp? earliestAvailabilityTime, UtcTimestamp? latestAvailabilityTime,
        long? firstSourceSequence, long? lastSourceSequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceManifestIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(recordCount);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(coverage) || !Enum.IsDefined(quality))
        {
            throw new ArgumentOutOfRangeException(nameof(kind),
                "A multi-source lane, coverage, or quality value is not defined.");
        }

        ArgumentNullException.ThrowIfNull(qualityReasons);
        ArgumentNullException.ThrowIfNull(supportingEvidenceIdentities);
        if (supportingEvidenceIdentities.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Supporting evidence identities must be non-empty.",
                nameof(supportingEvidenceIdentities));
        }
        if (qualityReasons.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Quality reasons must be non-empty.",
                nameof(qualityReasons));
        }

        ValidateBounds(earliestEventTime, latestEventTime, nameof(earliestEventTime));
        ValidateBounds(earliestAvailabilityTime, latestAvailabilityTime,
            nameof(earliestAvailabilityTime));
        if ((firstSourceSequence is null) != (lastSourceSequence is null)
            || firstSourceSequence is < 0 || lastSourceSequence is < 0
            || firstSourceSequence > lastSourceSequence
            || (recordCount == 0) != (firstSourceSequence is null))
        {
            throw new ArgumentException(
                "Source sequence bounds must match the source record count.",
                nameof(firstSourceSequence));
        }

        if (coverage == MultiSourceCoverage.Complete && quality is not MultiSourceQuality.Ready)
        {
            throw new ArgumentException(
                "Complete source coverage requires ready quality evidence.", nameof(quality));
        }

        SourceId = sourceId;
        Kind = kind;
        SourceManifestIdentity = sourceManifestIdentity;
        QualityEvidenceIdentity = NormalizeOptional(qualityEvidenceIdentity);
        SupportingEvidenceIdentities = new ImmutableValueList<string>(
            supportingEvidenceIdentities.Distinct(StringComparer.Ordinal)
                .OrderBy(static identity => identity, StringComparer.Ordinal));
        Coverage = coverage;
        Quality = quality;
        QualityReasons = new ImmutableValueList<string>(qualityReasons
            .Distinct(StringComparer.Ordinal).OrderBy(static reason => reason,
                StringComparer.Ordinal));
        RecordCount = recordCount;
        EarliestEventTime = earliestEventTime;
        LatestEventTime = latestEventTime;
        EarliestAvailabilityTime = earliestAvailabilityTime;
        LatestAvailabilityTime = latestAvailabilityTime;
        FirstSourceSequence = firstSourceSequence;
        LastSourceSequence = lastSourceSequence;
    }

    public MultiSourceDatasetSource(string sourceId, MultiSourceLaneKind kind,
        string sourceManifestIdentity, string? qualityEvidenceIdentity,
        IEnumerable<string> supportingEvidenceIdentities,
        MultiSourceCoverage coverage, MultiSourceQuality quality,
        IEnumerable<string> qualityReasons, long recordCount,
        UtcTimestamp? earliestEventTime, UtcTimestamp? latestEventTime,
        UtcTimestamp? earliestAvailabilityTime, UtcTimestamp? latestAvailabilityTime,
        long? firstSourceSequence, long? lastSourceSequence)
        : this(sourceId, kind, sourceManifestIdentity, qualityEvidenceIdentity,
            new ImmutableValueList<string>(supportingEvidenceIdentities), coverage, quality,
            new ImmutableValueList<string>(qualityReasons), recordCount,
            earliestEventTime, latestEventTime, earliestAvailabilityTime,
            latestAvailabilityTime, firstSourceSequence, lastSourceSequence)
    {
    }

    private static void ValidateBounds(UtcTimestamp? earliest, UtcTimestamp? latest,
        string parameterName)
    {
        if ((earliest is null) != (latest is null)
            || earliest is not null && earliest.Value > latest!.Value)
        {
            throw new ArgumentException("Time bounds must both be known and ordered.",
                parameterName);
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed record MultiSourceReplayOrderEntry
{
    public string SourceId { get; }
    public string EventId { get; }
    public long SourceSequence { get; }
    public long GlobalStableSequence { get; }
    public UtcTimestamp EventTime { get; }
    public UtcTimestamp? ExchangeTime { get; }
    public UtcTimestamp ReceiveTime { get; }
    public UtcTimestamp AvailabilityTime { get; }

    [JsonConstructor]
    public MultiSourceReplayOrderEntry(string sourceId, string eventId,
        long sourceSequence, long globalStableSequence, UtcTimestamp eventTime,
        UtcTimestamp? exchangeTime, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(globalStableSequence);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException("Availability cannot precede receive time.",
                nameof(availabilityTime));
        }

        SourceId = sourceId;
        EventId = eventId;
        SourceSequence = sourceSequence;
        GlobalStableSequence = globalStableSequence;
        EventTime = eventTime;
        ExchangeTime = exchangeTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
    }
}

public sealed record MultiSourceDatasetManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }
    public string ManifestId { get; }
    public string Identity { get; }
    public ImmutableValueList<MultiSourceDatasetSource> Sources { get; }
    public ImmutableValueList<MultiSourceReplayOrderEntry> Ordering { get; }

    [JsonConstructor]
    public MultiSourceDatasetManifest(int formatVersion, string manifestId,
        string identity, ImmutableValueList<MultiSourceDatasetSource> sources,
        ImmutableValueList<MultiSourceReplayOrderEntry> ordering)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(formatVersion, CurrentFormatVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(ordering);

        MultiSourceDatasetSource[] normalizedSources = sources
            .OrderBy(static source => source.SourceId, StringComparer.Ordinal).ToArray();
        MultiSourceReplayOrderEntry[] normalizedOrdering = ordering
            .OrderBy(static entry => entry.GlobalStableSequence).ToArray();
        ValidateOrdering(normalizedSources, normalizedOrdering);

        Sources = new ImmutableValueList<MultiSourceDatasetSource>(normalizedSources);
        Ordering = new ImmutableValueList<MultiSourceReplayOrderEntry>(normalizedOrdering);
        FormatVersion = formatVersion;
        ManifestId = manifestId;
        Identity = identity;
        if (!StringComparer.Ordinal.Equals(identity, ComputeIdentity(manifestId,
            Sources, Ordering)))
        {
            throw new ArgumentException("Manifest identity does not match its contents.",
                nameof(identity));
        }
    }

    public static MultiSourceDatasetManifest Create(string manifestId,
        IEnumerable<MultiSourceDatasetSource> sources,
        IEnumerable<MultiSourceReplayOrderEntry> ordering)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(ordering);
        MultiSourceDatasetSource[] sourceSnapshot = sources.ToArray();
        MultiSourceReplayOrderEntry[] orderSnapshot = ordering.ToArray();
        ImmutableValueList<MultiSourceDatasetSource> sourceList = new(sourceSnapshot);
        ImmutableValueList<MultiSourceReplayOrderEntry> orderList = new(orderSnapshot);
        ValidateOrdering(sourceSnapshot, orderSnapshot);
        string identity = ComputeIdentity(manifestId, sourceList, orderList);
        return new MultiSourceDatasetManifest(CurrentFormatVersion, manifestId, identity,
            sourceList, orderList);
    }

    private static string ComputeIdentity(string manifestId,
        ImmutableValueList<MultiSourceDatasetSource> sources,
        ImmutableValueList<MultiSourceReplayOrderEntry> ordering)
    {
        string canonical = CoreJsonSerializer.Serialize(new
        {
            FormatVersion = CurrentFormatVersion,
            ManifestId = manifestId,
            Sources = sources.OrderBy(static source => source.SourceId,
                StringComparer.Ordinal).ToArray(),
            Ordering = ordering.OrderBy(static entry => entry.GlobalStableSequence).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static void ValidateOrdering(MultiSourceDatasetSource[] sources,
        MultiSourceReplayOrderEntry[] ordering)
    {
        if (sources.Select(static source => source.SourceId)
            .Distinct(StringComparer.Ordinal).Count() != sources.Length)
        {
            throw new ArgumentException("Source identifiers must be unique.", nameof(sources));
        }

        Dictionary<string, MultiSourceDatasetSource> byId = sources.ToDictionary(
            static source => source.SourceId, StringComparer.Ordinal);
        if (ordering.Length != sources.Sum(static source => source.RecordCount))
        {
            throw new ArgumentException(
                "Every source record must have one causal ordering entry.", nameof(ordering));
        }

        if (ordering.Select(static entry => entry.GlobalStableSequence).Distinct().Count()
                != ordering.Length
            || ordering.Select(static entry => (entry.SourceId, entry.SourceSequence)).Distinct()
                .Count() != ordering.Length
            || ordering.Select(static entry => (entry.SourceId, entry.EventId)).Distinct()
                .Count() != ordering.Length)
        {
            throw new ArgumentException("Replay ordering contains a sequence or event conflict.",
                nameof(ordering));
        }

        foreach (IGrouping<string, MultiSourceReplayOrderEntry> group in ordering
            .GroupBy(static entry => entry.SourceId, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(group.Key, out MultiSourceDatasetSource? source))
            {
                throw new ArgumentException("Replay ordering refers to an unknown source.",
                    nameof(ordering));
            }

            MultiSourceReplayOrderEntry[] rows = group.OrderBy(static entry =>
                entry.SourceSequence).ToArray();
            if (rows.Length != source.RecordCount
                || rows[0].SourceSequence != source.FirstSourceSequence
                || rows[^1].SourceSequence != source.LastSourceSequence
                || source.EarliestEventTime is null
                || source.LatestEventTime is null
                || source.EarliestAvailabilityTime is null
                || source.LatestAvailabilityTime is null
                || rows.Any(row => row.EventTime.Value < source.EarliestEventTime.Value
                    || row.EventTime.Value > source.LatestEventTime.Value
                    || row.AvailabilityTime.Value < source.EarliestAvailabilityTime.Value
                    || row.AvailabilityTime.Value > source.LatestAvailabilityTime.Value))
            {
                throw new ArgumentException(
                    "Replay ordering does not match its source bounds.", nameof(ordering));
            }

            for (int index = 1; index < rows.Length; index++)
            {
                if (rows[index].SourceSequence <= rows[index - 1].SourceSequence
                    || rows[index].GlobalStableSequence <= rows[index - 1].GlobalStableSequence)
                {
                    throw new ArgumentException(
                        "Source and global sequences must preserve source order.", nameof(ordering));
                }
            }
        }
    }
}
