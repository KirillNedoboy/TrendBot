using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Backtesting;

/// <summary>One caller-declared version for a component used by OOS evaluation.</summary>
public sealed record FrozenOosModelVersion
{
    public string Component { get; }

    public string? Version { get; }

    public FrozenOosModelVersion(string component, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        if (component.Contains('\r') || component.Contains('\n')
            || version?.Contains('\r') == true || version?.Contains('\n') == true)
        {
            throw new ArgumentException("Model version values cannot contain line breaks.");
        }

        Component = component;
        Version = string.IsNullOrWhiteSpace(version) ? null : version;
    }
}

/// <summary>Canonical immutable snapshot of known and unknown model versions.</summary>
public sealed class FrozenOosModelVersions
{
    public FrozenOosModelVersions(IEnumerable<FrozenOosModelVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        FrozenOosModelVersion[] snapshot = versions.ToArray();
        if (snapshot.Any(static value => value is null)
            || snapshot.Select(static value => value.Component)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Model component identifiers must be unique.",
                nameof(versions));
        }

        Versions = new ReadOnlyCollection<FrozenOosModelVersion>(snapshot
            .OrderBy(static value => value.Component, StringComparer.Ordinal).ToArray());
        Identity = Hash(CoreJsonSerializer.Serialize(Versions));
    }

    public IReadOnlyList<FrozenOosModelVersion> Versions { get; }

    public string Identity { get; }

    internal bool Matches(FrozenOosModelVersions other) =>
        StringComparer.Ordinal.Equals(Identity, other.Identity);

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public enum FrozenOosCriterionStatus
{
    Supported,
    NotSupported,
    Unknown
}

public sealed record FrozenOosEvidenceLink(string Requirement, string EvidenceIdentity)
{
    public string Requirement { get; } = RequireText(Requirement, nameof(Requirement));

    public string EvidenceIdentity { get; } = RequireText(EvidenceIdentity,
        nameof(EvidenceIdentity));

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Evidence links cannot contain line breaks.",
                parameterName);
        }

        return value;
    }
}

/// <summary>One explicit, evidence-linked assessment of a predeclared question.</summary>
public sealed class FrozenOosCriterionAssessment
{
    [JsonConstructor]
    public FrozenOosCriterionAssessment(string criterionId, FrozenOosCriterionStatus status,
        IReadOnlyList<FrozenOosEvidenceLink> evidence,
        IReadOnlyList<string> unresolvedReasons)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(criterionId);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        CriterionId = criterionId;
        Status = status;
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Any(static value => value is null)
            || evidence.Select(static value => value.Requirement)
                .Distinct(StringComparer.Ordinal).Count() != evidence.Count)
        {
            throw new ArgumentException("Each required evidence description must be linked once.",
                nameof(evidence));
        }

        Evidence = new ReadOnlyCollection<FrozenOosEvidenceLink>(evidence
            .OrderBy(static value => value.Requirement, StringComparer.Ordinal).ToArray());
        UnresolvedReasons = SnapshotText(unresolvedReasons, nameof(unresolvedReasons));
    }

    public string CriterionId { get; }

    public FrozenOosCriterionStatus Status { get; }

    public IReadOnlyList<FrozenOosEvidenceLink> Evidence { get; }

    public IReadOnlyList<string> UnresolvedReasons { get; }

    private static ReadOnlyCollection<string> SnapshotText(IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        string[] snapshot = values.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace)
            || snapshot.Any(static value => value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException("Evidence values must be non-empty single-line text.",
                parameterName);
        }

        return new ReadOnlyCollection<string>(snapshot.Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal).ToArray());
    }
}

public enum FrozenOosEvaluationStatus
{
    Evaluated,
    Blocked,
    Invalidated
}

public sealed record FrozenOosTraceEntry(string EventId, string SourceId,
    long StableSequence, UtcTimestamp EventTime, UtcTimestamp? ExchangeTime,
    UtcTimestamp ReceiveTime, UtcTimestamp AvailabilityTime, string? Projection);

/// <summary>
/// Immutable binding created before replay. It retains the existing research,
/// criteria, and multi-source manifest identities while hashing every supplied
/// causal replay record and model-version declaration.
/// </summary>
public sealed class FrozenOosEvaluationDefinition
{
    public const string CurrentFormatVersion = "frozen-oos-evaluation-definition-v1";

    private FrozenOosEvaluationDefinition(FrozenResearchManifest researchManifest,
        PredeclaredPromotionCriteria criteria, MultiSourceDatasetManifest multiSourceManifest,
        IReadOnlyList<MultiSourceReplayRecord> records, FrozenOosModelVersions models,
        string replayInputIdentity)
    {
        ResearchManifest = researchManifest;
        Criteria = criteria;
        MultiSourceManifest = multiSourceManifest;
        Records = records;
        Models = models;
        ReplayInputIdentity = replayInputIdentity;
        ConfigurationIdentity = researchManifest.ConfigurationHash;
        string content = CoreJsonSerializer.Serialize(new DefinitionWire(
            CurrentFormatVersion, null, researchManifest.Identity, criteria.Identity,
            multiSourceManifest.Identity, replayInputIdentity, ConfigurationIdentity,
            models.Identity));
        Identity = Hash(content);
        CanonicalJson = CoreJsonSerializer.Serialize(new DefinitionWire(
            CurrentFormatVersion, Identity, researchManifest.Identity, criteria.Identity,
            multiSourceManifest.Identity, replayInputIdentity, ConfigurationIdentity,
            models.Identity));
    }

    public FrozenResearchManifest ResearchManifest { get; }

    public PredeclaredPromotionCriteria Criteria { get; }

    public MultiSourceDatasetManifest MultiSourceManifest { get; }

    public IReadOnlyList<MultiSourceReplayRecord> Records { get; }

    public FrozenOosModelVersions Models { get; }

    public string ReplayInputIdentity { get; }

    public string ConfigurationIdentity { get; }

    public string Identity { get; }

    public string CanonicalJson { get; }

    public static FrozenOosEvaluationDefinition Create(
        FrozenResearchManifest researchManifest, PredeclaredPromotionCriteria criteria,
        MultiSourceDatasetManifest multiSourceManifest,
        IEnumerable<MultiSourceReplayRecord> records, FrozenOosModelVersions models)
    {
        ArgumentNullException.ThrowIfNull(researchManifest);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(multiSourceManifest);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(models);
        if (!StringComparer.Ordinal.Equals(researchManifest.Identity, criteria.ManifestIdentity))
        {
            throw new FrozenResearchManifestMismatchException(
                "Promotion criteria do not match the frozen research manifest.");
        }

        MultiSourceReplayRecord[] snapshot = records.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException("A frozen OOS evaluation requires replay inputs.",
                nameof(records));
        }

        ValidateRecords(multiSourceManifest, snapshot);
        researchManifest.ValidateConfiguration(researchManifest.ConfigurationId,
            researchManifest.Configuration);
        string replayInputIdentity = ComputeReplayInputIdentity(snapshot);
        return new FrozenOosEvaluationDefinition(researchManifest, criteria,
            multiSourceManifest, new ReadOnlyCollection<MultiSourceReplayRecord>(snapshot),
            models, replayInputIdentity);
    }

    internal static string ComputeReplayInputIdentity(
        IEnumerable<MultiSourceReplayRecord> records)
    {
        ReplayInputWire[] rows = records.Select(static record => new ReplayInputWire(
                record.Ordering, record.Payload.GetType().FullName ?? record.Payload.GetType().Name,
                SerializePayload(record.Payload)))
            .OrderBy(static row => row.Ordering.GlobalStableSequence)
            .ThenBy(static row => row.Ordering.SourceId, StringComparer.Ordinal)
            .ThenBy(static row => row.Ordering.EventId, StringComparer.Ordinal)
            .ToArray();
        return Hash(CoreJsonSerializer.Serialize(rows));
    }

    internal static void ValidateRecords(MultiSourceDatasetManifest manifest,
        IReadOnlyList<MultiSourceReplayRecord> records)
    {
        Dictionary<RecordIdentity, MultiSourceReplayOrderEntry> expected = manifest.Ordering
            .ToDictionary(static entry => RecordIdentity.From(entry));
        Dictionary<RecordIdentity, string> seen = [];
        foreach (MultiSourceReplayRecord? record in records)
        {
            if (record is null)
            {
                throw new ArgumentException("Frozen replay records cannot contain null entries.",
                    nameof(records));
            }

            RecordIdentity identity = RecordIdentity.From(record.Ordering);
            if (!expected.TryGetValue(identity, out MultiSourceReplayOrderEntry? ordering)
                || ordering != record.Ordering)
            {
                throw new FrozenResearchManifestMismatchException(
                    "A replay record does not match the multi-source causal manifest.");
            }

            string payload = SerializePayload(record.Payload);
            if (seen.TryGetValue(identity, out string? previous)
                && !StringComparer.Ordinal.Equals(previous, payload))
            {
                throw new FrozenResearchManifestMismatchException(
                    "A replay identity has conflicting payloads.");
            }

            seen[identity] = payload;
        }

        if (expected.Keys.Any(key => !seen.ContainsKey(key)))
        {
            throw new FrozenResearchManifestMismatchException(
                "A multi-source manifest record has no frozen replay payload.");
        }
    }

    private static string SerializePayload(object payload) => payload switch
    {
        ReplayEnvelope value => CoreJsonSerializer.Serialize(value),
        CaptureEnvelope value => CoreJsonSerializer.Serialize(value),
        LaneBOpenInterestObservation value => CoreJsonSerializer.Serialize(value),
        LaneBFundingObservation value => CoreJsonSerializer.Serialize(value),
        LaneBLiquidationObservation value => CoreJsonSerializer.Serialize(value),
        LaneBFailedContinuationObservation value => CoreJsonSerializer.Serialize(value),
        _ => throw new ArgumentException("Unsupported frozen OOS replay payload type.",
            nameof(payload))
    };

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private readonly record struct RecordIdentity(string SourceId, string EventId,
        long SourceSequence)
    {
        public static RecordIdentity From(MultiSourceReplayOrderEntry entry) =>
            new(entry.SourceId, entry.EventId, entry.SourceSequence);
    }

    private sealed record ReplayInputWire(MultiSourceReplayOrderEntry Ordering,
        string PayloadType, string PayloadJson);

    private sealed record DefinitionWire(string FormatVersion, string? Identity,
        string ResearchManifestIdentity, string CriteriaIdentity,
        string MultiSourceManifestIdentity, string ReplayInputIdentity,
        string ConfigurationIdentity, string ModelVersionsIdentity);
}

/// <summary>Canonical OOS evaluation record; it never grants execution authority.</summary>
public sealed class FrozenOosEvaluationReport
{
    public const string CurrentFormatVersion = "frozen-oos-evaluation-report-v1";

    private FrozenOosEvaluationReport(string? identity, string definitionIdentity,
        string researchManifestIdentity, string criteriaIdentity,
        string multiSourceManifestIdentity, string replayInputIdentity,
        string configurationIdentity, string frozenModelVersionsIdentity,
        FrozenOosModelVersions currentModelVersions, FrozenOosEvaluationStatus status,
        IEnumerable<string> blockers, IEnumerable<string> qualityReasons,
        IEnumerable<MultiSourceDatasetSource> qualityEvidence,
        IEnumerable<string> exclusions,
        IEnumerable<FrozenOosTraceEntry> trace,
        IEnumerable<MultiSourceReplayDiagnostic> replayDiagnostics,
        IEnumerable<FrozenOosCriterionAssessment> assessments,
        ReconciliationCheckpoint? reconciliation)
    {
        DefinitionIdentity = definitionIdentity;
        ResearchManifestIdentity = researchManifestIdentity;
        CriteriaIdentity = criteriaIdentity;
        MultiSourceManifestIdentity = multiSourceManifestIdentity;
        ReplayInputIdentity = replayInputIdentity;
        ConfigurationIdentity = configurationIdentity;
        FrozenModelVersionsIdentity = frozenModelVersionsIdentity;
        CurrentModelVersions = currentModelVersions;
        Status = status;
        Blockers = Snapshot(blockers);
        QualityReasons = Snapshot(qualityReasons);
        QualityEvidence = new ReadOnlyCollection<MultiSourceDatasetSource>(qualityEvidence
            .OrderBy(static value => value.SourceId, StringComparer.Ordinal).ToArray());
        Exclusions = Snapshot(exclusions);
        Trace = new ReadOnlyCollection<FrozenOosTraceEntry>(trace.ToArray());
        ReplayDiagnostics = new ReadOnlyCollection<MultiSourceReplayDiagnostic>(
            replayDiagnostics.ToArray());
        CriterionAssessments = new ReadOnlyCollection<FrozenOosCriterionAssessment>(
            assessments.OrderBy(static value => value.CriterionId,
                StringComparer.Ordinal).ToArray());
        Reconciliation = reconciliation;
        Identity = identity ?? Hash(CoreJsonSerializer.Serialize(ToWire(null)));
        CanonicalJson = CoreJsonSerializer.Serialize(ToWire(Identity));
    }

    public string DefinitionIdentity { get; }
    public string ResearchManifestIdentity { get; }
    public string CriteriaIdentity { get; }
    public string MultiSourceManifestIdentity { get; }
    public string ReplayInputIdentity { get; }
    public string ConfigurationIdentity { get; }
    public string FrozenModelVersionsIdentity { get; }
    public FrozenOosModelVersions CurrentModelVersions { get; }
    public FrozenOosEvaluationStatus Status { get; }
    public IReadOnlyList<string> Blockers { get; }
    public IReadOnlyList<string> QualityReasons { get; }
    public IReadOnlyList<MultiSourceDatasetSource> QualityEvidence { get; }
    public IReadOnlyList<string> Exclusions { get; }
    public IReadOnlyList<FrozenOosTraceEntry> Trace { get; }
    public IReadOnlyList<MultiSourceReplayDiagnostic> ReplayDiagnostics { get; }
    public IReadOnlyList<FrozenOosCriterionAssessment> CriterionAssessments { get; }
    public ReconciliationCheckpoint? Reconciliation { get; }
    public string Identity { get; }
    public string CanonicalJson { get; }

    internal static FrozenOosEvaluationReport Create(FrozenOosEvaluationDefinition definition,
        FrozenOosModelVersions currentModels, FrozenOosEvaluationStatus status,
        IEnumerable<string> blockers, IEnumerable<string> qualityReasons,
        IEnumerable<MultiSourceDatasetSource> qualityEvidence,
        IEnumerable<string> exclusions,
        IEnumerable<FrozenOosTraceEntry> trace,
        IEnumerable<MultiSourceReplayDiagnostic> replayDiagnostics,
        IEnumerable<FrozenOosCriterionAssessment> assessments,
        ReconciliationCheckpoint? reconciliation) => new(null, definition.Identity,
            definition.ResearchManifest.Identity, definition.Criteria.Identity,
            definition.MultiSourceManifest.Identity, definition.ReplayInputIdentity,
            definition.ConfigurationIdentity, definition.Models.Identity, currentModels,
            status, blockers, qualityReasons, qualityEvidence, exclusions, trace,
            replayDiagnostics, assessments, reconciliation);

    public static FrozenOosEvaluationReport Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        EvaluationWire wire = CoreJsonSerializer.Deserialize<EvaluationWire>(json);
        if (!StringComparer.Ordinal.Equals(CurrentFormatVersion, wire.FormatVersion)
            || !Enum.IsDefined(wire.Status))
        {
            throw new JsonException("The frozen OOS evaluation report format is invalid.");
        }

        FrozenOosEvaluationReport report = new(wire.Identity, wire.DefinitionIdentity,
            wire.ResearchManifestIdentity, wire.CriteriaIdentity,
            wire.MultiSourceManifestIdentity, wire.ReplayInputIdentity,
            wire.ConfigurationIdentity, wire.FrozenModelVersionsIdentity,
            new FrozenOosModelVersions(wire.CurrentModelVersions), wire.Status, wire.Blockers,
            wire.QualityReasons, wire.QualityEvidence, wire.Exclusions, wire.Trace,
            wire.ReplayDiagnostics,
            wire.CriterionAssessments, wire.Reconciliation);
        if (!StringComparer.Ordinal.Equals(report.Identity,
                Hash(CoreJsonSerializer.Serialize(report.ToWire(null))))
            || !StringComparer.Ordinal.Equals(report.CanonicalJson, json))
        {
            throw new JsonException("The frozen OOS evaluation report integrity is invalid.");
        }

        return report;
    }

    private EvaluationWire ToWire(string? identity) => new(CurrentFormatVersion, identity,
        DefinitionIdentity, ResearchManifestIdentity, CriteriaIdentity,
        MultiSourceManifestIdentity, ReplayInputIdentity, ConfigurationIdentity,
        FrozenModelVersionsIdentity, CurrentModelVersions.Versions.ToArray(), Status,
        Blockers.ToArray(), QualityReasons.ToArray(), QualityEvidence.ToArray(),
        Exclusions.ToArray(), Trace.ToArray(), ReplayDiagnostics.ToArray(),
        CriterionAssessments.ToArray(), Reconciliation);

    private static ReadOnlyCollection<string> Snapshot(IEnumerable<string> values) =>
        new ReadOnlyCollection<string>(values.Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal).ToArray());

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record EvaluationWire(string FormatVersion, string? Identity,
        string DefinitionIdentity, string ResearchManifestIdentity, string CriteriaIdentity,
        string MultiSourceManifestIdentity, string ReplayInputIdentity,
        string ConfigurationIdentity, string FrozenModelVersionsIdentity,
        IReadOnlyList<FrozenOosModelVersion> CurrentModelVersions,
        FrozenOosEvaluationStatus Status, IReadOnlyList<string> Blockers,
        IReadOnlyList<string> QualityReasons,
        IReadOnlyList<MultiSourceDatasetSource> QualityEvidence,
        IReadOnlyList<string> Exclusions, IReadOnlyList<FrozenOosTraceEntry> Trace,
        IReadOnlyList<MultiSourceReplayDiagnostic> ReplayDiagnostics,
        IReadOnlyList<FrozenOosCriterionAssessment> CriterionAssessments,
        ReconciliationCheckpoint? Reconciliation);
}

public static class FrozenOosEvaluator
{
    public static FrozenOosEvaluationReport Evaluate(FrozenOosEvaluationDefinition definition,
        FrozenResearchManifest currentResearchManifest,
        PredeclaredPromotionCriteria currentCriteria,
        MultiSourceDatasetManifest currentMultiSourceManifest,
        IEnumerable<MultiSourceReplayRecord> records, string configurationId,
        IEnumerable<BacktestConfigurationEntry> configuration,
        FrozenOosModelVersions currentModels,
        IEnumerable<FrozenOosCriterionAssessment> criterionAssessments,
        ReconciliationCheckpoint? reconciliation,
        Func<MultiSourceReplayStepContext, string?> traceProjection)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(currentResearchManifest);
        ArgumentNullException.ThrowIfNull(currentCriteria);
        ArgumentNullException.ThrowIfNull(currentMultiSourceManifest);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(currentModels);
        ArgumentNullException.ThrowIfNull(criterionAssessments);
        ArgumentNullException.ThrowIfNull(traceProjection);
        MultiSourceReplayRecord[] recordSnapshot = records.ToArray();
        FrozenOosCriterionAssessment[] assessmentSnapshot = criterionAssessments.ToArray();
        List<string> blockers = [];
        List<string> qualityReasons = QualityReasonsFor(definition.MultiSourceManifest);

        string replayInputIdentity;
        try
        {
            FrozenOosEvaluationDefinition.ValidateRecords(currentMultiSourceManifest,
                recordSnapshot);
            replayInputIdentity = FrozenOosEvaluationDefinition.ComputeReplayInputIdentity(
                recordSnapshot);
        }
        catch (Exception exception) when (exception is ArgumentException
            or FrozenResearchManifestMismatchException or NotSupportedException)
        {
            replayInputIdentity = string.Empty;
            blockers.Add($"input-invalid:{exception.Message}");
            return Invalidated(definition, currentModels, blockers, qualityReasons,
                assessmentSnapshot, reconciliation);
        }

        if (!StringComparer.Ordinal.Equals(currentResearchManifest.Identity,
                definition.ResearchManifest.Identity))
        {
            blockers.Add("input-invalid:research manifest identity changed");
        }

        if (!StringComparer.Ordinal.Equals(currentCriteria.Identity, definition.Criteria.Identity))
        {
            blockers.Add("input-invalid:predeclared criteria identity changed");
        }

        if (!StringComparer.Ordinal.Equals(currentMultiSourceManifest.Identity,
                definition.MultiSourceManifest.Identity))
        {
            blockers.Add("input-invalid:multi-source manifest identity changed");
        }

        if (!StringComparer.Ordinal.Equals(replayInputIdentity, definition.ReplayInputIdentity))
        {
            blockers.Add("input-invalid:replay payloads or causal fields changed");
        }

        try
        {
            currentResearchManifest.ValidateConfiguration(configurationId,
                configuration);
        }
        catch (FrozenResearchManifestMismatchException exception)
        {
            blockers.Add($"input-invalid:configuration changed:{exception.Message}");
        }

        if (!definition.Models.Matches(currentModels))
        {
            IReadOnlyList<string> changed = ChangedComponents(definition.Models,
                currentModels);
            blockers.AddRange(changed.Select(static component =>
                $"model-version-changed:{component}"));
        }

        if (blockers.Count > 0)
        {
            return Invalidated(definition, currentModels, blockers, qualityReasons,
                assessmentSnapshot, reconciliation);
        }

        blockers.AddRange(UnknownModelBlockers(currentModels));

        if (!definition.Criteria.IsReady)
        {
            blockers.Add("criteria-not-ready");
            blockers.AddRange(definition.Criteria.UnresolvedBlockers.Select(static value =>
                $"criteria-unresolved:{value}"));
            return FrozenOosEvaluationReport.Create(definition, currentModels,
                FrozenOosEvaluationStatus.Blocked, blockers, qualityReasons,
                definition.MultiSourceManifest.Sources,
                definition.ResearchManifest.Exclusions, [], [], [], reconciliation);
        }

        ValidateAssessments(definition.Criteria, assessmentSnapshot, blockers);
        if (reconciliation is null)
        {
            blockers.Add("reconciliation-unknown");
        }
        else if (reconciliation.Status != ReconciliationStatus.Acknowledged)
        {
            blockers.Add($"reconciliation-not-acknowledged:{reconciliation.Revision}");
        }

        List<FrozenOosTraceEntry> trace = [];
        MultiSourceReplayResult replay = MultiSourceReplayRunner.Run(
            definition.MultiSourceManifest, recordSnapshot, context =>
            {
                string? value = traceProjection(context);
                MultiSourceReplayOrderEntry ordering = context.Current.Ordering;
                trace.Add(new FrozenOosTraceEntry(ordering.EventId, ordering.SourceId,
                    ordering.SourceSequence, ordering.EventTime, ordering.ExchangeTime,
                    ordering.ReceiveTime, ordering.AvailabilityTime, value));
            });
        if (!replay.Succeeded)
        {
            blockers.AddRange(replay.Diagnostics.Select(static diagnostic =>
                $"replay-{diagnostic.Kind}:{diagnostic.SourceId ?? "unknown"}:" +
                $"{diagnostic.EventId ?? "unknown"}:{diagnostic.Message}"));
        }

        if (!replay.DecisionReadinessConfirmed)
        {
            blockers.AddRange(replay.ReadinessReasons.Select(static reason =>
                $"data-quality:{reason}"));
        }

        blockers.AddRange(qualityReasons.Select(static reason => $"data-quality:{reason}"));

        FrozenOosEvaluationStatus status = blockers.Count == 0
            ? FrozenOosEvaluationStatus.Evaluated : FrozenOosEvaluationStatus.Blocked;
        return FrozenOosEvaluationReport.Create(definition, currentModels, status,
            blockers, qualityReasons, definition.MultiSourceManifest.Sources,
            definition.ResearchManifest.Exclusions, trace, replay.Diagnostics,
            assessmentSnapshot, reconciliation);
    }

    private static FrozenOosEvaluationReport Invalidated(
        FrozenOosEvaluationDefinition definition, FrozenOosModelVersions currentModels,
        IEnumerable<string> blockers, IEnumerable<string> qualityReasons,
        IEnumerable<FrozenOosCriterionAssessment> assessments,
        ReconciliationCheckpoint? reconciliation) =>
        FrozenOosEvaluationReport.Create(definition, currentModels,
            FrozenOosEvaluationStatus.Invalidated, blockers, qualityReasons,
            definition.MultiSourceManifest.Sources,
            definition.ResearchManifest.Exclusions, [], [], assessments, reconciliation);

    private static void ValidateAssessments(PredeclaredPromotionCriteria criteria,
        FrozenOosCriterionAssessment[] assessments, List<string> blockers)
    {
        if (assessments.Any(static item => item is null)
            || assessments.Select(static item => item.CriterionId)
                .Distinct(StringComparer.Ordinal).Count() != assessments.Length)
        {
            blockers.Add("criteria-assessments-invalid");
            return;
        }

        Dictionary<string, FrozenOosCriterionAssessment> byId = assessments.ToDictionary(
            static item => item.CriterionId, StringComparer.Ordinal);
        foreach (PromotionCriterion criterion in criteria.Criteria)
        {
            if (!byId.TryGetValue(criterion.CriterionId,
                    out FrozenOosCriterionAssessment? assessment))
            {
                if (criterion.IsRequired)
                {
                    blockers.Add($"criterion-assessment-missing:{criterion.CriterionId}");
                }

                continue;
            }

            if (assessment.Status == FrozenOosCriterionStatus.Unknown)
            {
                blockers.Add($"criterion-unknown:{criterion.CriterionId}");
            }

            if (assessment.Status == FrozenOosCriterionStatus.Supported
                || criterion.IsRequired)
            {
                foreach (string requiredEvidence in criterion.RequiredEvidence)
                {
                    if (!assessment.Evidence.Any(link =>
                            StringComparer.Ordinal.Equals(link.Requirement, requiredEvidence)
                            && !string.IsNullOrWhiteSpace(link.EvidenceIdentity)))
                    {
                        blockers.Add($"criterion-evidence-missing:{criterion.CriterionId}:" +
                            requiredEvidence);
                    }
                }
            }

            blockers.AddRange(assessment.UnresolvedReasons.Select(reason =>
                $"criterion-unresolved:{criterion.CriterionId}:{reason}").ToArray());
        }

        foreach (string unexpected in byId.Keys.Where(id =>
                     criteria.Criteria.All(criterion => criterion.CriterionId != id)))
        {
            blockers.Add($"criterion-not-predeclared:{unexpected}");
        }
    }

    private static List<string> QualityReasonsFor(MultiSourceDatasetManifest manifest)
    {
        List<string> reasons = [];
        foreach (MultiSourceDatasetSource source in manifest.Sources)
        {
            if (source.QualityEvidenceIdentity is null
                && source.SupportingEvidenceIdentities.Count == 0)
            {
                reasons.Add($"quality-evidence-missing:{source.SourceId}");
            }

            if (source.Coverage != MultiSourceCoverage.Complete)
            {
                reasons.Add($"source-coverage:{source.SourceId}:{source.Coverage}");
            }

            if (source.Quality != MultiSourceQuality.Ready)
            {
                if (source.QualityReasons.Count == 0)
                {
                    reasons.Add($"source-quality:{source.SourceId}:{source.Quality}");
                }
                else
                {
                    reasons.AddRange(source.QualityReasons.Select(reason =>
                        $"source-quality:{source.SourceId}:{source.Quality}:{reason}"));
                }
            }
        }

        return reasons;
    }

    private static string[] ChangedComponents(FrozenOosModelVersions frozen,
        FrozenOosModelVersions current)
    {
        Dictionary<string, string?> before = frozen.Versions.ToDictionary(
            static value => value.Component, static value => value.Version,
            StringComparer.Ordinal);
        Dictionary<string, string?> after = current.Versions.ToDictionary(
            static value => value.Component, static value => value.Version,
            StringComparer.Ordinal);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(component => !before.TryGetValue(component, out string? oldVersion)
                || !after.TryGetValue(component, out string? newVersion)
                || !StringComparer.Ordinal.Equals(oldVersion, newVersion))
            .OrderBy(static component => component, StringComparer.Ordinal).ToArray();
    }

    private static string[] UnknownModelBlockers(FrozenOosModelVersions models)
    {
        if (models.Versions.Count == 0)
        {
            return ["model-versions-unknown:no model versions declared"];
        }

        return models.Versions.Where(static value => value.Version is null)
            .Select(static value => $"model-version-unknown:{value.Component}")
            .ToArray();
    }
}
