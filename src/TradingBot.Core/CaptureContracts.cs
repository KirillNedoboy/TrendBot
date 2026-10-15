using System.Text.Json.Serialization;

namespace TradingBot.Core;

/// <summary>Identifies the bounded public-data records accepted by capture.</summary>
public enum CaptureRecordKind
{
    Depth,
    Trade,
    Diagnostic
}

/// <summary>Immutable source identity and raw provenance for one capture record.</summary>
public sealed record CaptureProvenance
{
    public string Source { get; }

    public string? StreamName { get; }

    public string? SessionId { get; }

    public string? SourceEventId { get; }

    public string? RawPayload { get; }

    public ImmutableValueList<string> QualityFlags { get; }

    public CaptureProvenance(string source, string? streamName, string? sessionId,
        string? sourceEventId, string? rawPayload, IEnumerable<string> qualityFlags)
        : this(source, streamName, sessionId, sourceEventId, rawPayload,
            SnapshotFlags(qualityFlags, nameof(qualityFlags)))
    {
    }

    [JsonConstructor]
    public CaptureProvenance(string source, string? streamName, string? sessionId,
        string? sourceEventId, string? rawPayload,
        ImmutableValueList<string> qualityFlags)
    {
        ContractGuard.RequireText(source, nameof(source));
        ArgumentNullException.ThrowIfNull(qualityFlags);
        if (qualityFlags.Any(static tag => string.IsNullOrWhiteSpace(tag)))
        {
            throw new ArgumentException("Capture provenance flags must be non-empty.",
                nameof(qualityFlags));
        }

        Source = source;
        StreamName = NormalizeOptional(streamName);
        SessionId = NormalizeOptional(sessionId);
        SourceEventId = NormalizeOptional(sourceEventId);
        RawPayload = NormalizeOptional(rawPayload);
        QualityFlags = new ImmutableValueList<string>(qualityFlags);
    }

    private static ImmutableValueList<string> SnapshotFlags(IEnumerable<string> tags,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(tags, parameterName);
        return new ImmutableValueList<string>(tags);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>One passive diagnostic preserved in the bounded capture stream.</summary>
public sealed record CaptureDiagnostic
{
    public string Code { get; }

    public string Message { get; }

    public ImmutableValueList<string> Details { get; }

    public CaptureDiagnostic(string code, string message, IEnumerable<string> details)
        : this(code, message, SnapshotDetails(details, nameof(details)))
    {
    }

    [JsonConstructor]
    public CaptureDiagnostic(string code, string message, ImmutableValueList<string> details)
    {
        ContractGuard.RequireText(code, nameof(code));
        ContractGuard.RequireText(message, nameof(message));
        ArgumentNullException.ThrowIfNull(details);
        if (details.Any(static detail => string.IsNullOrWhiteSpace(detail)))
        {
            throw new ArgumentException("Capture diagnostic details must be non-empty.",
                nameof(details));
        }

        Code = code;
        Message = message;
        Details = new ImmutableValueList<string>(details);
    }

    private static ImmutableValueList<string> SnapshotDetails(IEnumerable<string> details,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(details, parameterName);
        return new ImmutableValueList<string>(details);
    }
}

/// <summary>
/// Version-one passive capture envelope. The contract accepts depth deltas,
/// trades, and diagnostics only; it has no strategy, order, or execution state.
/// </summary>
public sealed record CaptureEnvelope
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string StreamId { get; }

    public long CaptureSequence { get; }

    public long StableSequence { get; }

    public CaptureRecordKind Kind { get; }

    public MarketEvent? Payload { get; }

    public CaptureDiagnostic? Diagnostic { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp ReceiveTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public UtcTimestamp? ExchangeTime { get; }

    public CaptureProvenance Provenance { get; }

    [JsonConstructor]
    public CaptureEnvelope(int formatVersion, string streamId, long captureSequence,
        long stableSequence, CaptureRecordKind kind, MarketEvent? payload,
        CaptureDiagnostic? diagnostic, UtcTimestamp eventTime, UtcTimestamp receiveTime,
        UtcTimestamp availabilityTime, UtcTimestamp? exchangeTime,
        CaptureProvenance provenance)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                $"Capture envelope format version {formatVersion} is not supported.");
        }

        ContractGuard.RequireText(streamId, nameof(streamId));
        ArgumentOutOfRangeException.ThrowIfNegative(captureSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        ContractGuard.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(receiveTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ArgumentNullException.ThrowIfNull(provenance);
        if (availabilityTime.Value < receiveTime.Value)
        {
            throw new ArgumentException(
                "Capture availability cannot precede receive time.", nameof(availabilityTime));
        }

        bool hasPayload = payload is not null;
        bool hasDiagnostic = diagnostic is not null;
        if (hasPayload == hasDiagnostic)
        {
            throw new ArgumentException(
                "A capture envelope must contain exactly one payload or diagnostic.");
        }

        if (hasPayload)
        {
            if (kind is CaptureRecordKind.Depth && payload is not BookDelta
                || kind is CaptureRecordKind.Trade && payload is not Trade
                || kind is CaptureRecordKind.Diagnostic)
            {
                throw new ArgumentException(
                    "Capture kind does not match the supported market payload.", nameof(kind));
            }

            if (payload!.EventTime != eventTime)
            {
                throw new ArgumentException(
                    "Capture event time must match the market payload event time.",
                    nameof(eventTime));
            }
        }
        else if (kind != CaptureRecordKind.Diagnostic)
        {
            throw new ArgumentException(
                "A diagnostic payload must use the diagnostic capture kind.", nameof(kind));
        }

        FormatVersion = formatVersion;
        StreamId = streamId;
        CaptureSequence = captureSequence;
        StableSequence = stableSequence;
        Kind = kind;
        Payload = payload;
        Diagnostic = diagnostic;
        EventTime = eventTime;
        ReceiveTime = receiveTime;
        AvailabilityTime = availabilityTime;
        ExchangeTime = exchangeTime;
        Provenance = provenance;
    }

    /// <summary>Creates an envelope using the public field order used by capture writers.</summary>
    public CaptureEnvelope(int formatVersion, string streamId, long captureSequence,
        CaptureRecordKind kind, MarketEvent? payload, CaptureDiagnostic? diagnostic,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, UtcTimestamp? exchangeTime, CaptureProvenance provenance)
        : this(formatVersion, streamId, captureSequence, stableSequence, kind, payload,
            diagnostic, eventTime, receiveTime, availabilityTime, exchangeTime, provenance)
    {
    }

    public static CaptureEnvelope FromMarketEvent(string streamId, long captureSequence,
        MarketEvent payload, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, CaptureProvenance provenance, UtcTimestamp? exchangeTime = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        CaptureRecordKind kind = payload switch
        {
            BookDelta => CaptureRecordKind.Depth,
            Trade => CaptureRecordKind.Trade,
            _ => throw new ArgumentException(
                "Capture accepts only depth BookDelta or Trade payloads.", nameof(payload))
        };
        return new CaptureEnvelope(CurrentFormatVersion, streamId, captureSequence,
            stableSequence, kind, payload, null, payload.EventTime, receiveTime,
            availabilityTime, exchangeTime, provenance);
    }

    public static CaptureEnvelope FromMarketEvent(string streamId, long captureSequence,
        MarketEvent payload, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, UtcTimestamp? exchangeTime, CaptureProvenance provenance)
    {
        return FromMarketEvent(streamId, captureSequence, payload, receiveTime,
            availabilityTime, stableSequence, provenance, exchangeTime);
    }

    public static CaptureEnvelope FromDiagnostic(string streamId, long captureSequence,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, long stableSequence,
        CaptureDiagnostic diagnostic, CaptureProvenance provenance,
        UtcTimestamp? exchangeTime = null)
    {
        return FromDiagnostic(streamId, captureSequence, eventTime, receiveTime, receiveTime,
            stableSequence, diagnostic, provenance, exchangeTime);
    }

    public static CaptureEnvelope FromDiagnostic(string streamId, long captureSequence,
        UtcTimestamp eventTime, UtcTimestamp receiveTime, UtcTimestamp availabilityTime,
        long stableSequence, CaptureDiagnostic diagnostic, CaptureProvenance provenance,
        UtcTimestamp? exchangeTime = null)
    {
        return new CaptureEnvelope(CurrentFormatVersion, streamId, captureSequence,
            stableSequence, CaptureRecordKind.Diagnostic, null, diagnostic, eventTime,
            receiveTime, availabilityTime, exchangeTime, provenance);
    }

    [JsonIgnore]
    public CausalMarketEvent? CausalEvent => Payload is null
        ? null
        : new CausalMarketEvent(Payload, ReceiveTime, AvailabilityTime, StableSequence,
            ExchangeTime);
}
