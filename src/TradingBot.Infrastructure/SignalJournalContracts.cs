using System.Security.Cryptography;
using System.Text;
using TradingBot.Core;

namespace TradingBot.Infrastructure;

/// <summary>One immutable diagnostic tied to a public input identity and content hash.</summary>
public sealed record SignalJournalDiagnostic
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string DiagnosticId { get; }

    public string StreamId { get; }

    public string InputEventId { get; }

    public string InputHash { get; }

    public string Code { get; }

    public ImmutableValueList<string> ReasonCodes { get; }

    public SignalJournalDiagnostic(int formatVersion, string diagnosticId,
        string streamId, string inputEventId, string inputHash, string code,
        ImmutableValueList<string> reasonCodes)
    {
        if (formatVersion != CurrentFormatVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion,
                "The signal journal diagnostic format is unsupported.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputEventId);
        RequireSha256(inputHash, nameof(inputHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(reasonCodes);
        if (reasonCodes.Any(string.IsNullOrWhiteSpace)
            || !reasonCodes.SequenceEqual(reasonCodes.Distinct(StringComparer.Ordinal)
                .OrderBy(static reason => reason, StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Diagnostic reasons must be nonblank, unique, and ordinally ordered.",
                nameof(reasonCodes));
        }

        string expected = ComputeId(formatVersion, streamId, inputEventId, inputHash,
            code, reasonCodes);
        if (!StringComparer.Ordinal.Equals(diagnosticId, expected))
        {
            throw new ArgumentException(
                "The diagnostic identity does not match its content.", nameof(diagnosticId));
        }

        FormatVersion = formatVersion;
        DiagnosticId = diagnosticId;
        StreamId = streamId;
        InputEventId = inputEventId;
        InputHash = inputHash;
        Code = code;
        ReasonCodes = new ImmutableValueList<string>(reasonCodes);
    }

    public static SignalJournalDiagnostic Create(string streamId, string inputEventId,
        string inputHash, string code, IEnumerable<string>? reasonCodes = null)
    {
        ImmutableValueList<string> reasons = new((reasonCodes ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal));
        string id = ComputeId(CurrentFormatVersion, streamId, inputEventId, inputHash,
            code, reasons);
        return new SignalJournalDiagnostic(CurrentFormatVersion, id, streamId,
            inputEventId, inputHash, code, reasons);
    }

    private static string ComputeId(int formatVersion, string streamId,
        string inputEventId, string inputHash, string code,
        ImmutableValueList<string> reasonCodes)
    {
        string canonical = CoreJsonSerializer.Serialize(new DiagnosticMaterial(
            formatVersion, streamId, inputEventId, inputHash, code, reasonCodes));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    internal static void RequireSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        byte[] hash;
        try
        {
            hash = Convert.FromHexString(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("A hash must be a SHA-256 hexadecimal value.",
                parameterName, exception);
        }

        if (hash.Length != SHA256.HashSizeInBytes
            || !StringComparer.Ordinal.Equals(value, value.ToLowerInvariant()))
        {
            throw new ArgumentException(
                "A hash must be a lowercase SHA-256 hexadecimal value.", parameterName);
        }
    }

    private sealed record DiagnosticMaterial(int FormatVersion, string StreamId,
        string InputEventId, string InputHash, string Code,
        ImmutableValueList<string> ReasonCodes);
}

/// <summary>One per-instrument atomic projection write and its prior-cursor guard.</summary>
public sealed record SignalJournalCommit
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; }

    public string StreamId { get; }

    public long ExpectedPreviousSequence { get; }

    public long InputSequence { get; }

    public string InputEventId { get; }

    public string InputHash { get; }

    public UtcTimestamp EventTime { get; }

    public UtcTimestamp AvailabilityTime { get; }

    public long StableSequence { get; }
    public string? ProcessedScenarioInputHash { get; }

    public SignalScenarioProjectionSnapshot ScenarioSnapshot { get; }

    public ImmutableValueList<SignalScenarioEvent> NewScenarioEvents { get; }

    public SignalRecord? SignalRecord { get; }

    public SignalEvidenceRecord? EvidenceRecord { get; }

    public SignalJournalDiagnostic? Diagnostic { get; }

    public SignalJournalCommit(string streamId, long expectedPreviousSequence,
        long inputSequence, string inputEventId, string inputHash,
        UtcTimestamp eventTime, UtcTimestamp availabilityTime, long stableSequence,
        string? processedScenarioInputHash,
        SignalScenarioProjectionSnapshot scenarioSnapshot,
        IEnumerable<SignalScenarioEvent> newScenarioEvents,
        SignalRecord? signalRecord = null, SignalEvidenceRecord? evidenceRecord = null,
        SignalJournalDiagnostic? diagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedPreviousSequence);
        if (inputSequence <= expectedPreviousSequence)
        {
            throw new ArgumentOutOfRangeException(nameof(inputSequence), inputSequence,
                "A signal journal input must advance its expected cursor.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(inputEventId);
        SignalJournalDiagnostic.RequireSha256(inputHash, nameof(inputHash));
        ArgumentNullException.ThrowIfNull(eventTime);
        ArgumentNullException.ThrowIfNull(availabilityTime);
        ArgumentOutOfRangeException.ThrowIfNegative(stableSequence);
        if (processedScenarioInputHash is not null)
        {
            SignalJournalDiagnostic.RequireSha256(processedScenarioInputHash,
                nameof(processedScenarioInputHash));
        }

        ArgumentNullException.ThrowIfNull(scenarioSnapshot);
        ArgumentNullException.ThrowIfNull(newScenarioEvents);
        if ((signalRecord is null) != (evidenceRecord is null))
        {
            throw new ArgumentException(
                "A signal record and its evidence must be committed together.");
        }

        ImmutableValueList<SignalScenarioEvent> events = new(newScenarioEvents);
        if (events.Any(item => item is null)
            || events.Select(static item => item.EventId).Distinct(StringComparer.Ordinal).Count()
                != events.Count)
        {
            throw new ArgumentException(
                "New scenario events must be non-null and have unique identities.",
                nameof(newScenarioEvents));
        }
        if (events.Count > 0 && processedScenarioInputHash is null)
        {
            throw new ArgumentException(
                "New scenario events require a committed scenario input hash.",
                nameof(processedScenarioInputHash));
        }

        foreach (SignalScenarioEvent item in events)
        {
            if (!StringComparer.Ordinal.Equals(item.CauseStreamId, streamId)
                || !StringComparer.Ordinal.Equals(item.CauseEventId, inputEventId)
                || item.EventTime != eventTime
                || item.AvailabilityTime != availabilityTime
                || item.StableSequence != stableSequence)
            {
                throw new ArgumentException(
                    "A scenario event must retain this commit's causal input.",
                    nameof(newScenarioEvents));
            }
        }

        SignalScenarioEvent[] alerts = events.Where(static item =>
            item.Kind == SignalScenarioEventKind.AlertCreated).ToArray();
        if (alerts.Length > 1 || alerts.Length == 1
            && (signalRecord is null
                || !StringComparer.Ordinal.Equals(alerts[0].SignalId,
                    signalRecord.Identity.SignalId)))
        {
            throw new ArgumentException(
                "Each newly created alert requires its matching signal and evidence record.",
                nameof(signalRecord));
        }

        if (signalRecord is not null)
        {
            signalRecord.ValidateEvidence(evidenceRecord!);
            if (!events.Any(item => StringComparer.Ordinal.Equals(item.SignalId,
                    signalRecord.Identity.SignalId)
                || StringComparer.Ordinal.Equals(item.RelatedSignalId,
                    signalRecord.Identity.SignalId)))
            {
                throw new ArgumentException(
                    "A signal record must be linked to a scenario event in this commit.",
                    nameof(signalRecord));
            }
        }

        if (diagnostic is not null
            && (!StringComparer.Ordinal.Equals(diagnostic.StreamId, streamId)
                || !StringComparer.Ordinal.Equals(diagnostic.InputEventId, inputEventId)
                || !StringComparer.Ordinal.Equals(diagnostic.InputHash, inputHash)))
        {
            throw new ArgumentException(
                "A diagnostic must identify the committed input.", nameof(diagnostic));
        }

        FormatVersion = CurrentFormatVersion;
        StreamId = streamId;
        ExpectedPreviousSequence = expectedPreviousSequence;
        InputSequence = inputSequence;
        InputEventId = inputEventId;
        InputHash = inputHash;
        EventTime = eventTime;
        AvailabilityTime = availabilityTime;
        StableSequence = stableSequence;
        ProcessedScenarioInputHash = processedScenarioInputHash;
        ScenarioSnapshot = scenarioSnapshot;
        NewScenarioEvents = events;
        SignalRecord = signalRecord;
        EvidenceRecord = evidenceRecord;
        Diagnostic = diagnostic;
    }
}

/// <summary>Committed per-instrument cursor and canonical scenario checkpoint.</summary>
public sealed record SignalJournalCheckpoint(
    long InputSequence,
    string InputEventId,
    string InputHash,
    UtcTimestamp EventTime,
    UtcTimestamp AvailabilityTime,
    long StableSequence,
    string CommitHash,
    string ScenarioSnapshotHash);

/// <summary>One immutable signal and its separately hashed evidence.</summary>
public sealed record SignalJournalSignal(
    SignalRecord Signal,
    SignalEvidenceRecord Evidence);

/// <summary>One committed, output-only scenario event awaiting a future delivery task.</summary>
public sealed record SignalJournalOutboxItem(
    string OutboxId,
    SignalScenarioEvent ScenarioEvent,
    string SignalRecordHash,
    string? RelatedSignalRecordHash,
    string PayloadHash);

/// <summary>Consistent persisted view of one signal stream.</summary>
public sealed record SignalJournalSnapshot(
    SignalJournalCheckpoint? Checkpoint,
    SignalScenarioProjectionSnapshot? ScenarioSnapshot,
    ImmutableValueList<SignalJournalSignal> Signals,
    ImmutableValueList<SignalScenarioEvent> ScenarioEvents,
    ImmutableValueList<SignalJournalDiagnostic> Diagnostics,
    ImmutableValueList<SignalJournalOutboxItem> Outbox);

/// <summary>Current durable cursor and scenario state used by a restarting consumer.</summary>
public sealed record SignalJournalCurrentState(
    SignalJournalCheckpoint? Checkpoint,
    SignalScenarioProjectionSnapshot? ScenarioSnapshot);
