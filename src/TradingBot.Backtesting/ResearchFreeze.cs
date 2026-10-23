using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Backtesting;

/// <summary>
/// Raised when a replay stream no longer matches the inputs that were frozen
/// before an out-of-sample evaluation.
/// </summary>
public sealed class FrozenResearchManifestMismatchException : InvalidOperationException
{
    public FrozenResearchManifestMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Immutable, versioned description of the inputs declared for one research
/// evaluation. It contains no derived result or promotion decision.
/// </summary>
public sealed class FrozenResearchManifest
{
    public const string CurrentFormatVersion = "frozen-research-manifest-v1";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private FrozenResearchManifest(
        string datasetId,
        string datasetVersion,
        string provenance,
        IReadOnlyList<string> composition,
        IReadOnlyList<string> exclusions,
        IReadOnlyList<ReplayEnvelope> events,
        string configurationId,
        IReadOnlyList<BacktestConfigurationEntry> configuration,
        IReadOnlyList<BacktestConfigurationEntry> selectionSnapshot,
        string riskPolicy,
        string fillPolicy,
        IReadOnlyList<string> assumptions)
    {
        DatasetId = datasetId;
        DatasetVersion = datasetVersion;
        Provenance = provenance;
        Composition = composition;
        Exclusions = exclusions;
        Events = CanonicalizeEvents(events);
        StreamId = Events.Count == 0 ? string.Empty : Events[0].StreamId;
        ConfigurationId = configurationId;
        Configuration = configuration;
        SelectionSnapshot = selectionSnapshot;
        RiskPolicy = riskPolicy;
        FillPolicy = fillPolicy;
        Assumptions = assumptions;

        EventRows = CanonicalEventRows(Events);
        DatasetContentHash = ComputeHash(Serialize(EventRows));
        ConfigurationRows = CanonicalConfigurationRows(configuration);
        ConfigurationHash = ComputeHash(Serialize(ConfigurationRows));
        SelectionRows = CanonicalConfigurationRows(selectionSnapshot);

        string identityInput = Serialize(ToWire(identity: null));
        Identity = ComputeHash(identityInput);
        CanonicalJson = Serialize(ToWire(Identity));
    }

    /// <summary>Gets the frozen manifest format version.</summary>
    public string FormatVersion { get; } = CurrentFormatVersion;

    /// <summary>Gets the caller supplied dataset identifier.</summary>
    public string DatasetId { get; }

    /// <summary>Alias for callers that use the identifier terminology.</summary>
    public string DatasetIdentifier => DatasetId;

    /// <summary>Gets the caller supplied dataset version.</summary>
    public string DatasetVersion { get; }

    /// <summary>Gets bounded provenance metadata for the dataset.</summary>
    public string Provenance { get; }

    /// <summary>Gets the immutable composition declaration.</summary>
    public IReadOnlyList<string> Composition { get; }

    /// <summary>Gets the immutable exclusion declaration.</summary>
    public IReadOnlyList<string> Exclusions { get; }

    /// <summary>Gets the stream represented by the causal event set.</summary>
    public string StreamId { get; }

    /// <summary>Gets the complete frozen causal event set, including repeats.</summary>
    public IReadOnlyList<ReplayEnvelope> Events { get; }

    /// <summary>Alias for callers that use persistence terminology.</summary>
    public IReadOnlyList<ReplayEnvelope> Envelopes => Events;

    /// <summary>Gets the canonical SHA-256 of the event set.</summary>
    public string DatasetContentHash { get; }

    /// <summary>Alias for the event-set content hash.</summary>
    public string EventSetHash => DatasetContentHash;

    /// <summary>Gets the caller supplied configuration identifier.</summary>
    public string ConfigurationId { get; }

    /// <summary>Alias for callers that use the identifier terminology.</summary>
    public string ConfigurationIdentifier => ConfigurationId;

    /// <summary>Gets the canonical configuration snapshot.</summary>
    public IReadOnlyList<BacktestConfigurationEntry> Configuration { get; }

    /// <summary>Alias for the configuration snapshot.</summary>
    public IReadOnlyList<BacktestConfigurationEntry> ConfigurationSnapshot => Configuration;

    /// <summary>Gets the canonical SHA-256 of the configuration snapshot.</summary>
    public string ConfigurationHash { get; }

    /// <summary>Gets the immutable selection snapshot.</summary>
    public IReadOnlyList<BacktestConfigurationEntry> SelectionSnapshot { get; }

    /// <summary>Gets the declared shared risk policy version or identifier.</summary>
    public string RiskPolicy { get; }

    /// <summary>Gets the declared fill policy version or identifier.</summary>
    public string FillPolicy { get; }

    /// <summary>Gets the explicit research assumptions.</summary>
    public IReadOnlyList<string> Assumptions { get; }

    /// <summary>Gets the canonical identity of this manifest.</summary>
    public string Identity { get; }

    /// <summary>Alias for the manifest identity.</summary>
    public string ManifestIdentity => Identity;

    /// <summary>Gets the canonical JSON including the integrity identity.</summary>
    public string CanonicalJson { get; }

    private ManifestEventWire[] EventRows { get; }

    private ConfigurationWire[] ConfigurationRows { get; }

    private ConfigurationWire[] SelectionRows { get; }

    /// <summary>Creates and freezes one research input declaration.</summary>
    public static FrozenResearchManifest Create(
        string datasetId,
        string datasetVersion,
        string provenance,
        IEnumerable<string> composition,
        IEnumerable<string> exclusions,
        IEnumerable<ReplayEnvelope> events,
        string configurationId,
        IEnumerable<BacktestConfigurationEntry> configuration,
        IEnumerable<BacktestConfigurationEntry> selectionSnapshot,
        string riskPolicy,
        string fillPolicy,
        IEnumerable<string> assumptions)
    {
        string checkedDatasetId = RequireText(datasetId, nameof(datasetId));
        string checkedDatasetVersion = RequireText(datasetVersion, nameof(datasetVersion));
        string checkedProvenance = RequireText(provenance, nameof(provenance));
        string checkedConfigurationId = RequireText(configurationId, nameof(configurationId));
        string checkedRiskPolicy = RequireText(riskPolicy, nameof(riskPolicy));
        string checkedFillPolicy = RequireText(fillPolicy, nameof(fillPolicy));

        ReadOnlyCollection<string> compositionSnapshot = SnapshotTextList(
            composition, nameof(composition));
        ReadOnlyCollection<string> exclusionSnapshot = SnapshotTextList(
            exclusions, nameof(exclusions));
        ReadOnlyCollection<string> assumptionSnapshot = SnapshotTextList(
            assumptions, nameof(assumptions));
        ReadOnlyCollection<ReplayEnvelope> eventSnapshot = SnapshotEvents(events);
        ReadOnlyCollection<BacktestConfigurationEntry> configurationSnapshot =
            SnapshotConfiguration(configuration, nameof(configuration));
        ReadOnlyCollection<BacktestConfigurationEntry> selectionSnapshotCopy =
            SnapshotConfiguration(selectionSnapshot, nameof(selectionSnapshot));

        return new FrozenResearchManifest(checkedDatasetId, checkedDatasetVersion,
            checkedProvenance, compositionSnapshot, exclusionSnapshot, eventSnapshot,
            checkedConfigurationId, configurationSnapshot, selectionSnapshotCopy,
            checkedRiskPolicy, checkedFillPolicy, assumptionSnapshot);
    }

    /// <summary>Restores a manifest and verifies canonical JSON and all hashes.</summary>
    public static FrozenResearchManifest Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ManifestWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<ManifestWire>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            throw new JsonException("The frozen research manifest is invalid.", exception);
        }

        if (wire is null)
        {
            throw new JsonException("A frozen research manifest cannot be null.");
        }

        try
        {
            FrozenResearchManifest manifest = CreateFromWire(wire);
            if (!StringComparer.Ordinal.Equals(manifest.CanonicalJson, json))
            {
                throw new JsonException(
                    "The frozen research manifest is not in canonical form.");
            }

            return manifest;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            throw new JsonException("The frozen research manifest violates its invariants.",
                exception);
        }
    }

    /// <summary>Runs replay only after validating the frozen causal event set.</summary>
    public ReplayRunResult RunReplay(string streamId, IEnumerable<ReplayEnvelope> events,
        Action<ReplayStepContext> handler)
    {
        ValidateReplayInputs(streamId, events);
        return HistoricalReplayRunner.Run(streamId, Events, handler);
    }

    /// <summary>Runs replay and retains handler outputs after input validation.</summary>
    public ReplayRunResult RunReplay(string streamId, IEnumerable<ReplayEnvelope> events,
        Func<ReplayStepContext, object?> handler)
    {
        ValidateReplayInputs(streamId, events);
        return HistoricalReplayRunner.Run(streamId, Events, handler);
    }

    /// <summary>Validates a candidate stream against the frozen event identity.</summary>
    public void ValidateReplayInputs(string streamId, IEnumerable<ReplayEnvelope> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        ArgumentNullException.ThrowIfNull(events);
        ReadOnlyCollection<ReplayEnvelope> snapshot = SnapshotEvents(events);
        if (StreamId.Length > 0 && !StringComparer.Ordinal.Equals(StreamId, streamId))
        {
            throw new FrozenResearchManifestMismatchException(
                "The replay stream does not match the frozen manifest stream.");
        }

        string candidateHash = ComputeHash(Serialize(CanonicalEventRows(snapshot)));
        if (!StringComparer.Ordinal.Equals(DatasetContentHash, candidateHash))
        {
            throw new FrozenResearchManifestMismatchException(
                "The replay event set does not match the frozen dataset content hash.");
        }
    }

    /// <summary>Validates a configuration snapshot against the frozen identity.</summary>
    public void ValidateConfiguration(string configurationId,
        IEnumerable<BacktestConfigurationEntry> configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationId);
        if (!StringComparer.Ordinal.Equals(ConfigurationId, configurationId))
        {
            throw new FrozenResearchManifestMismatchException(
                "The research configuration identifier does not match the frozen manifest.");
        }

        ReadOnlyCollection<BacktestConfigurationEntry> snapshot = SnapshotConfiguration(
            configuration, nameof(configuration));
        string candidateHash = ComputeHash(Serialize(CanonicalConfigurationRows(snapshot)));
        if (!StringComparer.Ordinal.Equals(ConfigurationHash, candidateHash))
        {
            throw new FrozenResearchManifestMismatchException(
                "The research configuration does not match the frozen configuration hash.");
        }
    }

    /// <summary>
    /// Validates that a completed report contains the same causal event
    /// identities and payloads as this frozen manifest. Exact duplicate rows
    /// are compared once because the replay runner's committed report retains
    /// its established idempotent delivery semantics.
    /// </summary>
    public void ValidateReport(BacktestReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (StreamId.Length > 0
            && !StringComparer.Ordinal.Equals(StreamId, report.StreamId))
        {
            throw new FrozenResearchManifestMismatchException(
                "The backtest report stream does not match the frozen manifest.");
        }

        string[] expected = EventRows
            .Select(Serialize)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = report.Events.Select(value => new ManifestEventWire(
                ReplayEnvelope.CurrentFormatVersion, report.StreamId, value.EventId,
                value.EventType, value.InstrumentId, value.EventTime, value.ReceiveTime,
                value.AvailabilityTime, value.ExchangeTime, value.StableSequence,
                value.PayloadJson))
            .Select(Serialize)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new FrozenResearchManifestMismatchException(
                "The backtest report events do not match the frozen manifest.");
        }
    }

    internal ManifestWire ToWire(string? identity) => new(
        FormatVersion,
        identity,
        DatasetId,
        DatasetVersion,
        Provenance,
        Composition.ToArray(),
        Exclusions.ToArray(),
        StreamId,
        EventRows,
        DatasetContentHash,
        ConfigurationId,
        ConfigurationRows,
        ConfigurationHash,
        SelectionRows,
        RiskPolicy,
        FillPolicy,
        Assumptions.ToArray());

    private static FrozenResearchManifest CreateFromWire(ManifestWire wire)
    {
        if (!StringComparer.Ordinal.Equals(CurrentFormatVersion, wire.FormatVersion))
        {
            throw new JsonException($"Unsupported frozen manifest format '{wire.FormatVersion}'.");
        }

        ReplayEnvelope[] events = wire.Events.Select(ToEnvelope).ToArray();
        FrozenResearchManifest manifest = Create(wire.DatasetId, wire.DatasetVersion,
            wire.Provenance, wire.Composition, wire.Exclusions, events, wire.ConfigurationId,
            wire.Configuration.Select(ToConfiguration), wire.SelectionSnapshot
                .Select(ToConfiguration), wire.RiskPolicy, wire.FillPolicy, wire.Assumptions);

        if (!StringComparer.Ordinal.Equals(manifest.StreamId, wire.StreamId)
            || !StringComparer.Ordinal.Equals(manifest.DatasetContentHash,
                wire.DatasetContentHash)
            || !StringComparer.Ordinal.Equals(manifest.ConfigurationHash,
                wire.ConfigurationHash)
            || !StringComparer.Ordinal.Equals(manifest.Identity, wire.Identity))
        {
            throw new JsonException("The frozen research manifest integrity fields do not match.");
        }

        return manifest;
    }

    private static ReplayEnvelope ToEnvelope(ManifestEventWire value)
    {
        ArgumentNullException.ThrowIfNull(value);
        MarketEvent payload = CoreJsonSerializer.Deserialize<MarketEvent>(value.PayloadJson);
        CausalMarketEvent causal = new(payload, new UtcTimestamp(value.ReceiveTime),
            new UtcTimestamp(value.AvailabilityTime), value.StableSequence,
            value.ExchangeTime is null ? null : new UtcTimestamp(value.ExchangeTime.Value));
        return new ReplayEnvelope(value.FormatVersion, value.StreamId, causal);
    }

    private static BacktestConfigurationEntry ToConfiguration(ConfigurationWire value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new BacktestConfigurationEntry(value.Name, value.Value);
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Manifest metadata cannot contain line breaks.",
                parameterName);
        }

        return value;
    }

    private static ReadOnlyCollection<string> SnapshotTextList(IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        string[] snapshot = values.Select(value => RequireText(value, parameterName))
            .Order(StringComparer.Ordinal).ToArray();
        return new ReadOnlyCollection<string>(snapshot);
    }

    private static ReadOnlyCollection<ReplayEnvelope> SnapshotEvents(
        IEnumerable<ReplayEnvelope> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ReplayEnvelope[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("A frozen event set cannot contain null envelopes.",
                nameof(values));
        }

        string? streamId = snapshot.Length == 0 ? null : snapshot[0].StreamId;
        if (snapshot.Any(value => streamId is not null
            && !StringComparer.Ordinal.Equals(streamId, value.StreamId)))
        {
            throw new ArgumentException(
                "A frozen manifest can contain only one replay stream.", nameof(values));
        }

        return new ReadOnlyCollection<ReplayEnvelope>(snapshot);
    }

    private static ReadOnlyCollection<ReplayEnvelope> CanonicalizeEvents(
        IEnumerable<ReplayEnvelope> values) => new(values
            .OrderBy(static value => value.CausalEvent.StableSequence)
            .ThenBy(static value => CoreJsonSerializer.Serialize(value.CausalEvent.Payload),
                StringComparer.Ordinal)
            .ThenBy(static value => value.CausalEvent.Payload.EventId,
                StringComparer.Ordinal)
            .ToArray());

    private static ReadOnlyCollection<BacktestConfigurationEntry> SnapshotConfiguration(
        IEnumerable<BacktestConfigurationEntry> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        BacktestConfigurationEntry[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("A configuration snapshot cannot contain null entries.",
                parameterName);
        }

        return new ReadOnlyCollection<BacktestConfigurationEntry>(snapshot
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ThenBy(static value => value.Value, StringComparer.Ordinal).ToArray());
    }

    private static ManifestEventWire[] CanonicalEventRows(
        IEnumerable<ReplayEnvelope> values) => values.Select(static value =>
    {
        CausalMarketEvent causal = value.CausalEvent;
        return new ManifestEventWire(value.FormatVersion, value.StreamId,
            causal.Payload.EventId, causal.Payload.GetType().Name,
            causal.Payload.InstrumentId.Value, causal.Payload.EventTime.Value,
            causal.ReceiveTime.Value, causal.AvailabilityTime.Value,
            causal.ExchangeTime?.Value, causal.StableSequence,
            CoreJsonSerializer.Serialize(causal.Payload));
    })
        .OrderBy(static value => value.StableSequence)
        .ThenBy(static value => value.PayloadJson, StringComparer.Ordinal)
        .ThenBy(static value => value.EventId, StringComparer.Ordinal)
        .ToArray();

    private static ConfigurationWire[] CanonicalConfigurationRows(
        IEnumerable<BacktestConfigurationEntry> values) => values
        .Select(static value => new ConfigurationWire(value.Name, value.Value))
        .OrderBy(static value => value.Name, StringComparer.Ordinal)
        .ThenBy(static value => value.Value, StringComparer.Ordinal)
        .ToArray();

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

    private static string ComputeHash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    internal sealed record ManifestWire(
        string FormatVersion,
        string? Identity,
        string DatasetId,
        string DatasetVersion,
        string Provenance,
        IReadOnlyList<string> Composition,
        IReadOnlyList<string> Exclusions,
        string StreamId,
        IReadOnlyList<ManifestEventWire> Events,
        string DatasetContentHash,
        string ConfigurationId,
        IReadOnlyList<ConfigurationWire> Configuration,
        string ConfigurationHash,
        IReadOnlyList<ConfigurationWire> SelectionSnapshot,
        string RiskPolicy,
        string FillPolicy,
        IReadOnlyList<string> Assumptions);

    internal sealed record ManifestEventWire(
        int FormatVersion,
        string StreamId,
        string EventId,
        string EventType,
        string InstrumentId,
        DateTimeOffset EventTime,
        DateTimeOffset ReceiveTime,
        DateTimeOffset AvailabilityTime,
        DateTimeOffset? ExchangeTime,
        long StableSequence,
        string PayloadJson);

    internal sealed record ConfigurationWire(string Name, string Value);
}

/// <summary>A BCL-only write-once journal for one frozen manifest.</summary>
public sealed class FrozenResearchManifestJournal
{
    private FrozenResearchManifest? manifest;

    public FrozenResearchManifest? Manifest => manifest;

    public void Register(FrozenResearchManifest value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (manifest is null)
        {
            manifest = value;
            return;
        }

        if (!StringComparer.Ordinal.Equals(manifest.CanonicalJson, value.CanonicalJson))
        {
            throw new InvalidOperationException(
                "A frozen research manifest journal is write-once.");
        }
    }

    public FrozenResearchManifest Read()
    {
        return manifest ?? throw new InvalidOperationException(
            "The frozen research manifest journal has no registered manifest.");
    }

    public static void WriteOnce(FrozenResearchManifest value, string path)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The journal path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        if (File.Exists(fullPath))
        {
            FrozenResearchManifest existing = FrozenResearchManifest.Deserialize(
                File.ReadAllText(fullPath, Encoding.UTF8));
            if (!StringComparer.Ordinal.Equals(existing.CanonicalJson, value.CanonicalJson))
            {
                throw new InvalidOperationException(
                    "A frozen research manifest journal is write-once.");
            }

            return;
        }

        using FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        byte[] payload = Encoding.UTF8.GetBytes(value.CanonicalJson);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    public static FrozenResearchManifest Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FrozenResearchManifest.Deserialize(File.ReadAllText(
            Path.GetFullPath(path), Encoding.UTF8));
    }
}

/// <summary>
/// Immutable evaluation record linking a frozen manifest to one canonical
/// completed backtest report. The report JSON is captured at creation time.
/// </summary>
public sealed class FrozenResearchEvaluationSnapshot
{
    public const string CurrentFormatVersion = "frozen-research-evaluation-v1";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private FrozenResearchEvaluationSnapshot(string manifestIdentity,
        string? criteriaIdentity, BacktestReport report)
    {
        ManifestIdentity = manifestIdentity;
        CriteriaIdentity = criteriaIdentity;
        StreamId = report.StreamId;
        ReportIdentity = report.Identity;
        ReportCanonicalJson = report.CanonicalJson;
        Identity = ComputeHash(Serialize(ToWire(identity: null)));
        CanonicalJson = Serialize(ToWire(Identity));
    }

    private FrozenResearchEvaluationSnapshot(string manifestIdentity, string? criteriaIdentity,
        string streamId, string reportIdentity, string reportCanonicalJson)
    {
        ManifestIdentity = manifestIdentity;
        CriteriaIdentity = criteriaIdentity;
        StreamId = streamId;
        ReportIdentity = reportIdentity;
        ReportCanonicalJson = reportCanonicalJson;
        Identity = ComputeHash(Serialize(ToWire(identity: null)));
        CanonicalJson = Serialize(ToWire(Identity));
    }

    public string FormatVersion { get; } = CurrentFormatVersion;

    public string ManifestIdentity { get; }

    /// <summary>Gets the optional predeclared criteria identity used for capture.</summary>
    public string? CriteriaIdentity { get; }

    public string StreamId { get; }

    public string ReportIdentity { get; }

    public string ReportCanonicalJson { get; }

    public string ReportJson => ReportCanonicalJson;

    public string Identity { get; }

    public string CanonicalJson { get; }

    public static FrozenResearchEvaluationSnapshot Create(
        FrozenResearchManifest manifest, BacktestReport report)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(report);
        if (manifest.StreamId.Length > 0
            && !StringComparer.Ordinal.Equals(manifest.StreamId, report.StreamId))
        {
            throw new FrozenResearchManifestMismatchException(
                "The backtest report stream does not match the frozen manifest.");
        }

        manifest.ValidateReport(report);

        return new FrozenResearchEvaluationSnapshot(manifest.Identity, null, report);
    }

    /// <summary>Captures a completed report with its predeclared criteria identity.</summary>
    public static FrozenResearchEvaluationSnapshot Create(
        FrozenResearchManifest manifest, PredeclaredPromotionCriteria criteria,
        BacktestReport report)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(report);
        if (!StringComparer.Ordinal.Equals(manifest.Identity, criteria.ManifestIdentity))
        {
            throw new FrozenResearchManifestMismatchException(
                "The promotion criteria do not match the frozen manifest.");
        }

        if (!criteria.IsReady)
        {
            throw new InvalidOperationException(
                "Unresolved promotion criteria cannot be bound to an evaluation snapshot.");
        }

        if (manifest.StreamId.Length > 0
            && !StringComparer.Ordinal.Equals(manifest.StreamId, report.StreamId))
        {
            throw new FrozenResearchManifestMismatchException(
                "The backtest report stream does not match the frozen manifest.");
        }

        manifest.ValidateReport(report);

        return new FrozenResearchEvaluationSnapshot(manifest.Identity, criteria.Identity,
            report);
    }

    public static FrozenResearchEvaluationSnapshot Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        EvaluationWire? wire = JsonSerializer.Deserialize<EvaluationWire>(json,
            SerializerOptions);
        if (wire is null || !StringComparer.Ordinal.Equals(CurrentFormatVersion,
            wire.FormatVersion))
        {
            throw new JsonException("The frozen evaluation snapshot format is invalid.");
        }

        if (!StringComparer.Ordinal.Equals(ComputeHash(wire.ReportCanonicalJson),
            wire.ReportIdentity))
        {
            throw new JsonException("The frozen report identity does not match its JSON.");
        }

        FrozenResearchEvaluationSnapshot snapshot = new(wire.ManifestIdentity,
            wire.CriteriaIdentity, wire.StreamId, wire.ReportIdentity,
            wire.ReportCanonicalJson);
        if (!StringComparer.Ordinal.Equals(snapshot.ReportIdentity, wire.ReportIdentity)
            || !StringComparer.Ordinal.Equals(snapshot.Identity, wire.Identity)
            || !StringComparer.Ordinal.Equals(snapshot.CanonicalJson, json))
        {
            throw new JsonException("The frozen evaluation snapshot integrity is invalid.");
        }

        return snapshot;
    }

    private EvaluationWire ToWire(string? identity) => new(FormatVersion, identity,
        ManifestIdentity, CriteriaIdentity, StreamId, ReportIdentity, ReportCanonicalJson);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value,
        SerializerOptions);

    private static string ComputeHash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = false
    };

    private sealed record EvaluationWire(
        string FormatVersion,
        string? Identity,
        string ManifestIdentity,
        string? CriteriaIdentity,
        string StreamId,
        string ReportIdentity,
        string ReportCanonicalJson);
}

/// <summary>A BCL-only write-once journal for completed evaluation snapshots.</summary>
public sealed class FrozenResearchEvaluationJournal
{
    private FrozenResearchEvaluationSnapshot? snapshot;

    public FrozenResearchEvaluationSnapshot? Snapshot => snapshot;

    public void Register(FrozenResearchEvaluationSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (snapshot is null)
        {
            snapshot = value;
            return;
        }

        if (!StringComparer.Ordinal.Equals(snapshot.CanonicalJson, value.CanonicalJson))
        {
            throw new InvalidOperationException(
                "A frozen research evaluation journal is write-once.");
        }
    }

    public FrozenResearchEvaluationSnapshot Read() => snapshot ?? throw new InvalidOperationException(
        "The frozen research evaluation journal has no registered snapshot.");

    public static void WriteOnce(FrozenResearchEvaluationSnapshot value, string path)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The journal path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);
        if (File.Exists(fullPath))
        {
            FrozenResearchEvaluationSnapshot existing =
                FrozenResearchEvaluationSnapshot.Deserialize(File.ReadAllText(fullPath,
                    Encoding.UTF8));
            if (!StringComparer.Ordinal.Equals(existing.CanonicalJson, value.CanonicalJson))
            {
                throw new InvalidOperationException(
                    "A frozen research evaluation journal is write-once.");
            }

            return;
        }

        using FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        byte[] payload = Encoding.UTF8.GetBytes(value.CanonicalJson);
        stream.Write(payload);
        stream.Flush(flushToDisk: true);
    }

    public static FrozenResearchEvaluationSnapshot Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return FrozenResearchEvaluationSnapshot.Deserialize(File.ReadAllText(
            Path.GetFullPath(path), Encoding.UTF8));
    }
}
