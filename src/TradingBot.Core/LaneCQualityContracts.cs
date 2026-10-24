using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TradingBot.Core;

public enum LaneCQualityState
{
    Ready,
    InsufficientData,
    Stale,
    SequenceGap,
    Conflict
}

public sealed record LaneCQualitySignalCounts
{
    public long GapCount { get; }
    public long DuplicateCount { get; }
    public long ConflictCount { get; }
    public long MissingFieldCount { get; }
    public long ClockSkewCount { get; }
    public long StaleCount { get; }

    [JsonConstructor]
    public LaneCQualitySignalCounts(long gapCount, long duplicateCount, long conflictCount,
        long missingFieldCount, long clockSkewCount, long staleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gapCount);
        ArgumentOutOfRangeException.ThrowIfNegative(duplicateCount);
        ArgumentOutOfRangeException.ThrowIfNegative(conflictCount);
        ArgumentOutOfRangeException.ThrowIfNegative(missingFieldCount);
        ArgumentOutOfRangeException.ThrowIfNegative(clockSkewCount);
        ArgumentOutOfRangeException.ThrowIfNegative(staleCount);
        GapCount = gapCount;
        DuplicateCount = duplicateCount;
        ConflictCount = conflictCount;
        MissingFieldCount = missingFieldCount;
        ClockSkewCount = clockSkewCount;
        StaleCount = staleCount;
    }
}

/// <summary>Safe provenance for an unknown diagnostic at a causal capture position.</summary>
public sealed record LaneCQualityUnknownDiagnostic
{
    public string Code { get; }
    public string Source { get; }
    public string? StreamName { get; }
    public long StableSequence { get; }
    public UtcTimestamp EventTime { get; }
    public UtcTimestamp AvailabilityTime { get; }

    [JsonConstructor]
    public LaneCQualityUnknownDiagnostic(string code, string source, string? streamName,
        long stableSequence, UtcTimestamp eventTime, UtcTimestamp availabilityTime)
    {
        RequireSafeToken(code, nameof(code));
        RequireSafeToken(source, nameof(source));
        if (streamName is not null)
        {
            RequireSafeToken(streamName, nameof(streamName));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        Code = code;
        Source = source;
        StreamName = streamName;
        StableSequence = stableSequence;
        EventTime = eventTime;
        AvailabilityTime = availabilityTime;
    }

    private static void RequireSafeToken(string value, string parameterName)
    {
        ContractGuard.RequireText(value, parameterName);
        if (value.Length > 128 || value.Any(static character =>
            !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException("Quality provenance must be a safe token.",
                parameterName);
        }
    }
}

/// <summary>Explicit provider requirements and optional caller freshness policy.</summary>
public sealed record LaneCQualityPolicy
{
    public bool RequireOrderBook { get; }

    public bool RequireTrades { get; }

    public TimeSpan? MaximumProviderSilence { get; }

    [JsonConstructor]
    public LaneCQualityPolicy(bool requireOrderBook, bool requireTrades,
        TimeSpan? maximumProviderSilence)
    {
        if (maximumProviderSilence is { } silence && silence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumProviderSilence));
        }

        RequireOrderBook = requireOrderBook;
        RequireTrades = requireTrades;
        MaximumProviderSilence = maximumProviderSilence;
    }
}

/// <summary>Versioned Lane C quality assessment for one feature input and causal cursor.</summary>
public sealed record LaneCQualityEvidence
{
    public const string CurrentAlgorithmVersion = "lane-c-quality-v1";

    public string AlgorithmVersion { get; }

    public string EvidenceIdentity { get; }

    public InstrumentId InstrumentId { get; }

    public string InputIdentity { get; }

    public CausalEventCursor Cursor { get; }

    public LaneCQualityState State { get; }

    public ImmutableValueList<string> Reasons { get; }

    public LaneCQualitySignalCounts SignalCounts { get; }

    public ImmutableValueList<LaneCQualityUnknownDiagnostic> UnknownDiagnostics { get; }

    [JsonConstructor]
    public LaneCQualityEvidence(string algorithmVersion, string evidenceIdentity,
        InstrumentId instrumentId, string inputIdentity, CausalEventCursor cursor,
        LaneCQualityState state,
        ImmutableValueList<string> reasons, LaneCQualitySignalCounts signalCounts,
        ImmutableValueList<LaneCQualityUnknownDiagnostic> unknownDiagnostics)
    {
        ContractGuard.RequireText(algorithmVersion, nameof(algorithmVersion));
        if (!StringComparer.Ordinal.Equals(algorithmVersion, CurrentAlgorithmVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion),
                "Lane C quality algorithm version is unsupported.");
        }
        ContractGuard.RequireText(evidenceIdentity, nameof(evidenceIdentity));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(inputIdentity, nameof(inputIdentity));
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentNullException.ThrowIfNull(signalCounts);
        ArgumentNullException.ThrowIfNull(unknownDiagnostics);
        string expectedIdentity = ComputeIdentity(algorithmVersion, instrumentId,
            inputIdentity, cursor, state, reasons, signalCounts, unknownDiagnostics);
        if (!StringComparer.Ordinal.Equals(expectedIdentity, evidenceIdentity))
        {
            throw new ArgumentException("Quality evidence identity does not match its content.",
                nameof(evidenceIdentity));
        }
        AlgorithmVersion = algorithmVersion;
        EvidenceIdentity = evidenceIdentity;
        InstrumentId = instrumentId;
        InputIdentity = inputIdentity;
        Cursor = cursor;
        State = state;
        Reasons = new ImmutableValueList<string>(reasons);
        SignalCounts = signalCounts;
        UnknownDiagnostics = new ImmutableValueList<LaneCQualityUnknownDiagnostic>(
            unknownDiagnostics);
    }

    internal static string ComputeIdentity(string algorithmVersion, InstrumentId instrumentId,
        string inputIdentity, CausalEventCursor cursor, LaneCQualityState state,
        ImmutableValueList<string> reasons, LaneCQualitySignalCounts signalCounts,
        ImmutableValueList<LaneCQualityUnknownDiagnostic> unknownDiagnostics)
    {
        string canonical = CoreJsonSerializer.Serialize(new
        {
            AlgorithmVersion = algorithmVersion,
            InstrumentId = instrumentId,
            InputIdentity = inputIdentity,
            Cursor = cursor,
            State = state,
            Reasons = reasons,
            SignalCounts = signalCounts,
            UnknownDiagnostics = unknownDiagnostics
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

/// <summary>Decision trace that retains the Lane C quality result identity and cause.</summary>
public sealed record LaneCQualityDecisionMetadata
{
    public string QualityEvidenceIdentity { get; }

    public InstrumentId InstrumentId { get; }

    public string InputIdentity { get; }

    public string AlgorithmVersion { get; }

    public CausalEventCursor Cursor { get; }

    public LaneCQualityState State { get; }

    public ImmutableValueList<string> Reasons { get; }

    [JsonConstructor]
    public LaneCQualityDecisionMetadata(string qualityEvidenceIdentity,
        InstrumentId instrumentId, string inputIdentity, string algorithmVersion,
        CausalEventCursor cursor, LaneCQualityState state,
        ImmutableValueList<string> reasons)
    {
        ContractGuard.RequireText(qualityEvidenceIdentity, nameof(qualityEvidenceIdentity));
        ArgumentNullException.ThrowIfNull(instrumentId);
        ContractGuard.RequireText(inputIdentity, nameof(inputIdentity));
        ContractGuard.RequireText(algorithmVersion, nameof(algorithmVersion));
        if (!StringComparer.Ordinal.Equals(algorithmVersion,
            LaneCQualityEvidence.CurrentAlgorithmVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithmVersion),
                "Lane C quality algorithm version is unsupported.");
        }
        ArgumentNullException.ThrowIfNull(cursor);
        ContractGuard.RequireDefined(state, nameof(state));
        ArgumentNullException.ThrowIfNull(reasons);
        QualityEvidenceIdentity = qualityEvidenceIdentity;
        InstrumentId = instrumentId;
        InputIdentity = inputIdentity;
        AlgorithmVersion = algorithmVersion;
        Cursor = cursor;
        State = state;
        Reasons = new ImmutableValueList<string>(reasons);
    }
}
