using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Backtesting;
using TradingBot.Core;
using TradingBot.Infrastructure;

namespace TradingBot.App;

/// <summary>Promotion evidence categories are kept separate so missing operational proof is visible.</summary>
public enum PromotionEvidenceCategory
{
    DataQuality,
    Reconciliation,
    Shadow,
    Simulation,
    OperationalShadow,
    LocalPaper,
    ExchangeTest
}

public enum PromotionEvidenceStatus
{
    Provided,
    Missing,
    Unknown,
    Invalidated,
    Available = Provided,
    Absent = Missing
}

/// <summary>A causal field copied into an evidence snapshot without reinterpretation.</summary>
public sealed record PromotionEvidenceCausalField
{
    [JsonConstructor]
    public PromotionEvidenceCausalField(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\r') || name.Contains('\n')
            || value?.Contains('\r') == true || value?.Contains('\n') == true)
        {
            throw new ArgumentException("Causal evidence fields cannot contain line breaks.",
                nameof(name));
        }

        Name = name;
        Value = value;
    }

    public string Name { get; }

    /// <summary>Null is retained when the source did not know a causal value.</summary>
    public string? Value { get; }
}

/// <summary>
/// Immutable snapshot of one supplied artifact. Native identifiers and the
/// content hash are deliberately separate: a caller supplied ID is never
/// replaced by a hash and an unknown ID remains unknown.
/// </summary>
public sealed class PromotionEvidenceSnapshot
{
    public const string CurrentFormatVersion = "promotion-evidence-snapshot-v1";

    [JsonConstructor]
    public PromotionEvidenceSnapshot(string snapshotIdentity, PromotionEvidenceCategory category,
        PromotionEvidenceStatus status, string? nativeIdentity, string? canonicalJson,
        string? contentHash, IReadOnlyList<PromotionEvidenceCausalField> causalFields,
        IReadOnlyList<string> provenance, IReadOnlyList<string> exclusions,
        IReadOnlyList<string> unknowns, IReadOnlyList<string> blockers,
        IReadOnlyList<string> referencedIdentities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotIdentity);
        if (!Enum.IsDefined(category) || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (nativeIdentity?.Contains('\r') == true || nativeIdentity?.Contains('\n') == true)
        {
            throw new ArgumentException("Evidence identities cannot contain line breaks.",
                nameof(nativeIdentity));
        }

        if (canonicalJson is not null && canonicalJson.Length == 0)
        {
            throw new ArgumentException("A supplied canonical snapshot cannot be empty.",
                nameof(canonicalJson));
        }

        if (status == PromotionEvidenceStatus.Provided && canonicalJson is null)
        {
            throw new ArgumentException("Provided evidence requires a canonical snapshot.",
                nameof(canonicalJson));
        }

        if (canonicalJson is null && contentHash is not null)
        {
            throw new ArgumentException("An absent snapshot cannot have a content hash.",
                nameof(contentHash));
        }

        string? expectedContentHash = canonicalJson is null ? null : Hash(canonicalJson);
        if (!StringComparer.Ordinal.Equals(expectedContentHash, contentHash))
        {
            throw new ArgumentException("Evidence content hash does not match its snapshot.",
                nameof(contentHash));
        }

        Category = category;
        Status = status;
        NativeIdentity = nativeIdentity;
        CanonicalJson = canonicalJson;
        ContentHash = contentHash;
        CausalFields = SnapshotFields(causalFields);
        Provenance = SnapshotText(provenance, nameof(provenance));
        Exclusions = SnapshotText(exclusions, nameof(exclusions));
        Unknowns = SnapshotText(unknowns, nameof(unknowns));
        Blockers = SnapshotText(blockers, nameof(blockers));
        ReferencedIdentities = SnapshotText(referencedIdentities, nameof(referencedIdentities));

        string expectedIdentity = ComputeIdentity(Category, Status, NativeIdentity, CanonicalJson,
            ContentHash, CausalFields, Provenance, Exclusions, Unknowns, Blockers,
            ReferencedIdentities);
        if (!StringComparer.Ordinal.Equals(snapshotIdentity, expectedIdentity))
        {
            throw new ArgumentException("Evidence snapshot identity is invalid.",
                nameof(snapshotIdentity));
        }

        SnapshotIdentity = snapshotIdentity;
    }

    public PromotionEvidenceSnapshot(PromotionEvidenceCategory category,
        PromotionEvidenceStatus status, string? nativeIdentity, string? canonicalJson,
        IEnumerable<PromotionEvidenceCausalField>? causalFields = null,
        IEnumerable<string>? provenance = null, IEnumerable<string>? exclusions = null,
        IEnumerable<string>? unknowns = null, IEnumerable<string>? blockers = null,
        IEnumerable<string>? referencedIdentities = null)
        : this(ComputeIdentity(category, status, nativeIdentity, canonicalJson,
            canonicalJson is null ? null : Hash(canonicalJson),
            SnapshotFields(causalFields ?? []), SnapshotText(provenance ?? [], nameof(provenance)),
            SnapshotText(exclusions ?? [], nameof(exclusions)),
            SnapshotText(unknowns ?? [], nameof(unknowns)),
            SnapshotText(blockers ?? [], nameof(blockers)),
            SnapshotText(referencedIdentities ?? [], nameof(referencedIdentities))), category, status,
            nativeIdentity, canonicalJson, canonicalJson is null ? null : Hash(canonicalJson),
            SnapshotFields(causalFields ?? []), SnapshotText(provenance ?? [], nameof(provenance)),
            SnapshotText(exclusions ?? [], nameof(exclusions)),
            SnapshotText(unknowns ?? [], nameof(unknowns)),
            SnapshotText(blockers ?? [], nameof(blockers)),
            SnapshotText(referencedIdentities ?? [], nameof(referencedIdentities)))
    {
    }

    public PromotionEvidenceCategory Category { get; }

    public PromotionEvidenceStatus Status { get; }

    /// <summary>Identity supplied by the producer, or null when it was unknown.</summary>
    public string? NativeIdentity { get; }

    /// <summary>Canonical content captured at bundle creation, or null when absent.</summary>
    public string? CanonicalJson { get; }

    /// <summary>SHA-256 of CanonicalJson, kept distinct from NativeIdentity.</summary>
    public string? ContentHash { get; }

    public IReadOnlyList<PromotionEvidenceCausalField> CausalFields { get; }

    public IReadOnlyList<string> Provenance { get; }

    public IReadOnlyList<string> Exclusions { get; }

    public IReadOnlyList<string> Unknowns { get; }

    public IReadOnlyList<string> Blockers { get; }

    public IReadOnlyList<string> ReferencedIdentities { get; }

    public string SnapshotIdentity { get; }

    /// <summary>Alias for callers that use the generic evidence identity term.</summary>
    [JsonIgnore]
    public string Identity => SnapshotIdentity;

    [JsonIgnore]
    public string? EvidenceIdentity => NativeIdentity;

    [JsonIgnore]
    public string? NativeEvidenceIdentity => NativeIdentity;

    [JsonIgnore]
    public PromotionEvidenceCategory EvidenceKind => Category;

    public static PromotionEvidenceSnapshot FromArtifact(PromotionEvidenceCategory category,
        string? nativeIdentity, string canonicalJson, IEnumerable<PromotionEvidenceCausalField>? causalFields = null,
        IEnumerable<string>? provenance = null, IEnumerable<string>? exclusions = null,
        IEnumerable<string>? unknowns = null, IEnumerable<string>? blockers = null,
        IEnumerable<string>? referencedIdentities = null) => new(category,
            PromotionEvidenceStatus.Provided, nativeIdentity, canonicalJson, causalFields,
            provenance, exclusions, unknowns, blockers, referencedIdentities);

    public static PromotionEvidenceSnapshot Missing(PromotionEvidenceCategory category,
        string reason, IEnumerable<PromotionEvidenceCausalField>? causalFields = null,
        IEnumerable<string>? provenance = null, IEnumerable<string>? referencedIdentities = null) =>
        new(category, PromotionEvidenceStatus.Missing, null, null, causalFields, provenance,
            blockers: [RequireReason(reason)], referencedIdentities: referencedIdentities);

    public static PromotionEvidenceSnapshot Unknown(PromotionEvidenceCategory category,
        string reason, IEnumerable<PromotionEvidenceCausalField>? causalFields = null,
        IEnumerable<string>? provenance = null, IEnumerable<string>? referencedIdentities = null) =>
        new(category, PromotionEvidenceStatus.Unknown, null, null, causalFields, provenance,
            unknowns: [RequireReason(reason)], referencedIdentities: referencedIdentities);

    public static PromotionEvidenceSnapshot Invalidated(PromotionEvidenceCategory category,
        string reason, string? nativeIdentity = null, string? canonicalJson = null,
        IEnumerable<PromotionEvidenceCausalField>? causalFields = null,
        IEnumerable<string>? provenance = null, IEnumerable<string>? referencedIdentities = null) =>
        new(category, PromotionEvidenceStatus.Invalidated, nativeIdentity, canonicalJson,
            causalFields, provenance, blockers: [RequireReason(reason)],
            referencedIdentities: referencedIdentities);

    public static string ComputeIdentity(PromotionEvidenceCategory category,
        PromotionEvidenceStatus status, string? nativeIdentity, string? canonicalJson,
        string? contentHash, IEnumerable<PromotionEvidenceCausalField> causalFields,
        IEnumerable<string> provenance, IEnumerable<string> exclusions,
        IEnumerable<string> unknowns, IEnumerable<string> blockers,
        IEnumerable<string> referencedIdentities)
    {
        string canonical = CoreJsonSerializer.Serialize(new SnapshotWire(CurrentFormatVersion,
            null, category, status, nativeIdentity, canonicalJson, contentHash,
            SnapshotFields(causalFields), SnapshotText(provenance, nameof(provenance)),
            SnapshotText(exclusions, nameof(exclusions)), SnapshotText(unknowns, nameof(unknowns)),
            SnapshotText(blockers, nameof(blockers)),
            SnapshotText(referencedIdentities, nameof(referencedIdentities))));
        return Hash(canonical);
    }

    private static string RequireReason(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    private static ReadOnlyCollection<PromotionEvidenceCausalField> SnapshotFields(
        IEnumerable<PromotionEvidenceCausalField>? values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PromotionEvidenceCausalField[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null)
            || snapshot.Select(static value => value.Name).Distinct(StringComparer.Ordinal).Count()
                != snapshot.Length)
        {
            throw new ArgumentException("Causal field names must be unique.", nameof(values));
        }

        return new ReadOnlyCollection<PromotionEvidenceCausalField>(snapshot
            .OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray());
    }

    private static ReadOnlyCollection<string> SnapshotText(IEnumerable<string>? values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        string[] snapshot = values.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace)
            || snapshot.Any(static value => value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException("Evidence text values must be non-empty single-line text.",
                parameterName);
        }

        return new ReadOnlyCollection<string>(snapshot.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray());
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record SnapshotWire(string FormatVersion, string? SnapshotIdentity,
        PromotionEvidenceCategory Category, PromotionEvidenceStatus Status,
        string? NativeIdentity, string? CanonicalJson, string? ContentHash,
        IReadOnlyList<PromotionEvidenceCausalField> CausalFields,
        IReadOnlyList<string> Provenance, IReadOnlyList<string> Exclusions,
        IReadOnlyList<string> Unknowns, IReadOnlyList<string> Blockers,
        IReadOnlyList<string> ReferencedIdentities);
}

/// <summary>Typed source artifacts accepted by the promotion bundle builder.</summary>
public sealed class PromotionEvidenceBundleSources
{
    public PromotionEvidenceBundleSources(LaneCQualityReport? qualityReport = null,
        LaneCEvidenceBundle? shadowEvidence = null, BacktestReport? simulationReport = null,
        ReconciliationCheckpoint? reconciliation = null,
        IEnumerable<PromotionEvidenceSnapshot>? additionalEvidence = null,
        string? provenance = null)
    {
        QualityReport = qualityReport;
        ShadowEvidence = shadowEvidence;
        SimulationReport = simulationReport;
        Reconciliation = reconciliation;
        AdditionalEvidence = Snapshot(additionalEvidence ?? []);
        Provenance = provenance;
    }

    public LaneCQualityReport? QualityReport { get; }

    public LaneCEvidenceBundle? ShadowEvidence { get; }

    public BacktestReport? SimulationReport { get; }

    public ReconciliationCheckpoint? Reconciliation { get; }

    public IReadOnlyList<PromotionEvidenceSnapshot> AdditionalEvidence { get; }

    public string? Provenance { get; }

    private static ReadOnlyCollection<PromotionEvidenceSnapshot> Snapshot(
        IEnumerable<PromotionEvidenceSnapshot> values)
    {
        PromotionEvidenceSnapshot[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Additional evidence cannot contain null entries.",
                nameof(values));
        }

        return new ReadOnlyCollection<PromotionEvidenceSnapshot>(snapshot);
    }
}

/// <summary>Convenience input object that keeps the builder call self-describing.</summary>
public sealed class PromotionEvidenceBundleInput
{
    public PromotionEvidenceBundleInput(FrozenOosEvaluationDefinition definition,
        FrozenOosEvaluationReport oosReport, PromotionEvidenceBundleSources? sources = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        OosReport = oosReport ?? throw new ArgumentNullException(nameof(oosReport));
        Sources = sources ?? new PromotionEvidenceBundleSources();
    }

    public FrozenOosEvaluationDefinition Definition { get; }

    public FrozenOosEvaluationReport OosReport { get; }

    public PromotionEvidenceBundleSources Sources { get; }
}

public sealed class PromotionEvidenceValidationResult
{
    internal PromotionEvidenceValidationResult(bool isValid, IEnumerable<string> invalidatedEvidence,
        IEnumerable<string> reasons)
    {
        IsValid = isValid;
        InvalidatedEvidence = Snapshot(invalidatedEvidence);
        Reasons = Snapshot(reasons);
    }

    public bool IsValid { get; }

    public IReadOnlyList<string> InvalidatedEvidence { get; }

    /// <summary>Alias retained for review tooling that calls affected evidence.</summary>
    public IReadOnlyList<string> AffectedEvidence => InvalidatedEvidence;

    public IReadOnlyList<string> Reasons { get; }

    private static ReadOnlyCollection<string> Snapshot(IEnumerable<string> values) =>
        new(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
}

/// <summary>
/// Canonical, immutable promotion evidence bundle. It records evidence and
/// blockers; it does not make the T003 promotion decision.
/// </summary>
public sealed class PromotionEvidenceBundle
{
    public const string CurrentFormatVersion = "promotion-evidence-bundle-v1";

    [JsonConstructor]
    public PromotionEvidenceBundle(string formatVersion, string bundleIdentity,
        string definitionIdentity, string oosReportIdentity, string oosReportCanonicalJson,
        string researchManifestIdentity, string researchManifestCanonicalJson,
        string criteriaIdentity, string criteriaCanonicalJson,
        string multiSourceManifestIdentity, string multiSourceManifestCanonicalJson,
        string replayInputIdentity, string configurationIdentity, string frozenModelVersionsIdentity,
        IReadOnlyList<FrozenOosModelVersion> frozenModelVersions,
        IReadOnlyList<FrozenOosModelVersion> currentModelVersions,
        IReadOnlyList<PromotionEvidenceSnapshot> evidence, IReadOnlyList<string> provenance,
        IReadOnlyList<string> exclusions, IReadOnlyList<string> blockers,
        IReadOnlyList<string> unknowns)
    {
        if (!StringComparer.Ordinal.Equals(formatVersion, CurrentFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(bundleIdentity);
        RequireIdentity(definitionIdentity, nameof(definitionIdentity));
        RequireIdentity(oosReportIdentity, nameof(oosReportIdentity));
        RequireCanonical(oosReportCanonicalJson, nameof(oosReportCanonicalJson));
        RequireIdentity(researchManifestIdentity, nameof(researchManifestIdentity));
        RequireCanonical(researchManifestCanonicalJson, nameof(researchManifestCanonicalJson));
        RequireIdentity(criteriaIdentity, nameof(criteriaIdentity));
        RequireCanonical(criteriaCanonicalJson, nameof(criteriaCanonicalJson));
        RequireIdentity(multiSourceManifestIdentity, nameof(multiSourceManifestIdentity));
        RequireCanonical(multiSourceManifestCanonicalJson, nameof(multiSourceManifestCanonicalJson));
        RequireIdentity(replayInputIdentity, nameof(replayInputIdentity));
        RequireIdentity(configurationIdentity, nameof(configurationIdentity));
        RequireIdentity(frozenModelVersionsIdentity, nameof(frozenModelVersionsIdentity));
        ArgumentNullException.ThrowIfNull(frozenModelVersions);
        ArgumentNullException.ThrowIfNull(currentModelVersions);
        ArgumentNullException.ThrowIfNull(evidence);

        FormatVersion = formatVersion;
        DefinitionIdentity = definitionIdentity;
        OosReportIdentity = oosReportIdentity;
        OosReportCanonicalJson = oosReportCanonicalJson;
        ResearchManifestIdentity = researchManifestIdentity;
        ResearchManifestCanonicalJson = researchManifestCanonicalJson;
        CriteriaIdentity = criteriaIdentity;
        CriteriaCanonicalJson = criteriaCanonicalJson;
        MultiSourceManifestIdentity = multiSourceManifestIdentity;
        MultiSourceManifestCanonicalJson = multiSourceManifestCanonicalJson;
        ReplayInputIdentity = replayInputIdentity;
        ConfigurationIdentity = configurationIdentity;
        FrozenModelVersionsIdentity = frozenModelVersionsIdentity;
        FrozenModelVersions = SnapshotModels(frozenModelVersions);
        CurrentModelVersions = SnapshotModels(currentModelVersions);
        Evidence = SnapshotEvidence(evidence);
        Provenance = SnapshotText(provenance, nameof(provenance));
        Exclusions = SnapshotText(exclusions, nameof(exclusions));
        Blockers = SnapshotText(blockers, nameof(blockers));
        Unknowns = SnapshotText(unknowns, nameof(unknowns));

        string expectedModelIdentity = ComputeModelsIdentity(FrozenModelVersions);
        if (!StringComparer.Ordinal.Equals(expectedModelIdentity, frozenModelVersionsIdentity))
        {
            throw new ArgumentException("Frozen model identity does not match its versions.",
                nameof(frozenModelVersionsIdentity));
        }

        ValidateCanonicalBindings(oosReportCanonicalJson, oosReportIdentity,
            researchManifestCanonicalJson, researchManifestIdentity,
            criteriaCanonicalJson, criteriaIdentity,
            multiSourceManifestCanonicalJson, multiSourceManifestIdentity);

        string expectedBundleIdentity = ComputeIdentity(FormatVersion, DefinitionIdentity,
            OosReportIdentity, OosReportCanonicalJson, ResearchManifestIdentity,
            ResearchManifestCanonicalJson, CriteriaIdentity, CriteriaCanonicalJson,
            MultiSourceManifestIdentity, MultiSourceManifestCanonicalJson, ReplayInputIdentity,
            ConfigurationIdentity,
            FrozenModelVersionsIdentity, FrozenModelVersions, CurrentModelVersions, Evidence,
            Provenance, Exclusions, Blockers, Unknowns);
        if (!StringComparer.Ordinal.Equals(bundleIdentity, expectedBundleIdentity))
        {
            throw new ArgumentException("Promotion evidence bundle identity is invalid.",
                nameof(bundleIdentity));
        }

        BundleIdentity = bundleIdentity;
        CanonicalJson = CoreJsonSerializer.Serialize(ToWire(BundleIdentity));
    }

    public string FormatVersion { get; }

    public string BundleIdentity { get; }

    [JsonIgnore]
    public string Identity => BundleIdentity;

    public string DefinitionIdentity { get; }

    public string OosReportIdentity { get; }

    public string OosReportCanonicalJson { get; }

    [JsonIgnore]
    public string EvaluationReportIdentity => OosReportIdentity;

    public string ResearchManifestIdentity { get; }

    public string ResearchManifestCanonicalJson { get; }

    public string CriteriaIdentity { get; }

    public string CriteriaCanonicalJson { get; }

    public string MultiSourceManifestIdentity { get; }

    public string MultiSourceManifestCanonicalJson { get; }

    public string ReplayInputIdentity { get; }

    public string ConfigurationIdentity { get; }

    public string FrozenModelVersionsIdentity { get; }

    [JsonIgnore]
    public string ModelVersionsIdentity => FrozenModelVersionsIdentity;

    public IReadOnlyList<FrozenOosModelVersion> FrozenModelVersions { get; }

    public IReadOnlyList<FrozenOosModelVersion> CurrentModelVersions { get; }

    public IReadOnlyList<PromotionEvidenceSnapshot> Evidence { get; }

    [JsonIgnore]
    public IReadOnlyList<PromotionEvidenceSnapshot> EvidenceReferences => Evidence;

    public IReadOnlyList<string> Provenance { get; }

    public IReadOnlyList<string> Exclusions { get; }

    public IReadOnlyList<string> Blockers { get; }

    public IReadOnlyList<string> Unknowns { get; }

    [JsonIgnore]
    public string CanonicalJson { get; }

    [JsonIgnore]
    public bool HasUnresolvedBlockers => Blockers.Count > 0 || Unknowns.Count > 0
        || Evidence.Any(static value => value.Status is PromotionEvidenceStatus.Missing
            or PromotionEvidenceStatus.Unknown or PromotionEvidenceStatus.Invalidated);

    public PromotionEvidenceSnapshot? Find(PromotionEvidenceCategory category) => Evidence
        .Where(value => value.Category == category)
        .OrderBy(value => value.SnapshotIdentity, StringComparer.Ordinal)
        .FirstOrDefault();

    public static PromotionEvidenceBundle Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        BundleWire wire = CoreJsonSerializer.Deserialize<BundleWire>(json);
        PromotionEvidenceBundle bundle = new(wire.FormatVersion,
            wire.BundleIdentity ?? throw new JsonException("Promotion bundle identity is missing."),
            wire.DefinitionIdentity, wire.OosReportIdentity, wire.OosReportCanonicalJson,
            wire.ResearchManifestIdentity, wire.ResearchManifestCanonicalJson,
            wire.CriteriaIdentity, wire.CriteriaCanonicalJson,
            wire.MultiSourceManifestIdentity, wire.MultiSourceManifestCanonicalJson,
            wire.ReplayInputIdentity,
            wire.ConfigurationIdentity, wire.FrozenModelVersionsIdentity,
            wire.FrozenModelVersions, wire.CurrentModelVersions, wire.Evidence, wire.Provenance,
            wire.Exclusions, wire.Blockers, wire.Unknowns);
        if (!StringComparer.Ordinal.Equals(bundle.CanonicalJson, json))
        {
            throw new JsonException("Promotion evidence bundle is not in canonical form.");
        }

        return bundle;
    }

    /// <summary>
    /// Re-evaluates links and content against current artifacts without mutating
    /// this immutable bundle. Changed frozen context invalidates all affected
    /// evidence categories and retains the old blockers in this instance.
    /// </summary>
    public PromotionEvidenceValidationResult ValidateAgainst(
        FrozenOosEvaluationDefinition definition, FrozenOosEvaluationReport oosReport,
        PromotionEvidenceBundleSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(oosReport);
        try
        {
            PromotionEvidenceBundle current = PromotionEvidenceBundleBuilder.Build(definition,
                oosReport, sources);
            List<string> invalidated = [];
            List<string> reasons = [];
            if (!StringComparer.Ordinal.Equals(DefinitionIdentity, current.DefinitionIdentity))
            {
                invalidated.Add("frozen-definition");
                reasons.Add("frozen-definition-changed");
            }

            if (!StringComparer.Ordinal.Equals(OosReportIdentity, current.OosReportIdentity))
            {
                invalidated.Add("oos-report");
                reasons.Add("oos-report-changed");
            }

            foreach (PromotionEvidenceCategory category in Enum.GetValues<PromotionEvidenceCategory>())
            {
                PromotionEvidenceSnapshot? before = Find(category);
                PromotionEvidenceSnapshot? after = current.Find(category);
                if (before is null && after is null)
                {
                    continue;
                }

                if (before is null || after is null
                    || !StringComparer.Ordinal.Equals(before.SnapshotIdentity,
                        after.SnapshotIdentity))
                {
                    invalidated.Add(category.ToString());
                    reasons.Add($"evidence-changed:{category}");
                }
            }

            return new PromotionEvidenceValidationResult(invalidated.Count == 0,
                invalidated, reasons);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or JsonException)
        {
            return new PromotionEvidenceValidationResult(false,
                Enum.GetNames<PromotionEvidenceCategory>(),
                [$"bundle-context-invalidated:{exception.Message}"]);
        }
    }

    private BundleWire ToWire(string? identity) => new(FormatVersion, identity,
        DefinitionIdentity, OosReportIdentity, OosReportCanonicalJson,
        ResearchManifestIdentity, ResearchManifestCanonicalJson, CriteriaIdentity,
        CriteriaCanonicalJson, MultiSourceManifestIdentity, MultiSourceManifestCanonicalJson,
        ReplayInputIdentity, ConfigurationIdentity,
        FrozenModelVersionsIdentity, FrozenModelVersions.ToArray(), CurrentModelVersions.ToArray(),
        Evidence.ToArray(), Provenance.ToArray(), Exclusions.ToArray(), Blockers.ToArray(),
        Unknowns.ToArray());

    internal static PromotionEvidenceBundle Create(FrozenOosEvaluationDefinition definition,
        FrozenOosEvaluationReport oosReport, IEnumerable<PromotionEvidenceSnapshot> evidence,
        IEnumerable<string> provenance, IEnumerable<string> exclusions,
        IEnumerable<string> blockers, IEnumerable<string> unknowns)
    {
        FrozenOosModelVersion[] frozenModels = definition.Models.Versions.ToArray();
        FrozenOosModelVersion[] currentModels = oosReport.CurrentModelVersions.Versions.ToArray();
        PromotionEvidenceSnapshot[] evidenceSnapshot = SnapshotEvidence(evidence).ToArray();
        ReadOnlyCollection<string> provenanceSnapshot = SnapshotText(provenance, nameof(provenance));
        ReadOnlyCollection<string> exclusionsSnapshot = SnapshotText(exclusions, nameof(exclusions));
        ReadOnlyCollection<string> blockerSnapshot = SnapshotText(blockers, nameof(blockers));
        ReadOnlyCollection<string> unknownSnapshot = SnapshotText(unknowns, nameof(unknowns));
        string oosReportCanonicalJson = oosReport.CanonicalJson;
        string researchManifestCanonicalJson = definition.ResearchManifest.CanonicalJson;
        string criteriaCanonicalJson = definition.Criteria.CanonicalJson;
        string multiSourceManifestCanonicalJson = CoreJsonSerializer.Serialize(
            definition.MultiSourceManifest);
        string identity = ComputeIdentity(CurrentFormatVersion, definition.Identity,
            oosReport.Identity, oosReportCanonicalJson, definition.ResearchManifest.Identity,
            researchManifestCanonicalJson, definition.Criteria.Identity, criteriaCanonicalJson,
            definition.MultiSourceManifest.Identity, multiSourceManifestCanonicalJson,
            definition.ReplayInputIdentity,
            definition.ConfigurationIdentity, definition.Models.Identity, frozenModels,
            currentModels, evidenceSnapshot, provenanceSnapshot, exclusionsSnapshot,
            blockerSnapshot, unknownSnapshot);
        return new PromotionEvidenceBundle(CurrentFormatVersion, identity, definition.Identity,
            oosReport.Identity, oosReportCanonicalJson, definition.ResearchManifest.Identity,
            researchManifestCanonicalJson, definition.Criteria.Identity, criteriaCanonicalJson,
            definition.MultiSourceManifest.Identity, multiSourceManifestCanonicalJson,
            definition.ReplayInputIdentity,
            definition.ConfigurationIdentity, definition.Models.Identity, frozenModels,
            currentModels, evidenceSnapshot, provenanceSnapshot, exclusionsSnapshot,
            blockerSnapshot, unknownSnapshot);
    }

    internal static string ComputeModelsIdentity(IEnumerable<FrozenOosModelVersion> models) =>
        Hash(CoreJsonSerializer.Serialize(models.OrderBy(static value => value.Component,
            StringComparer.Ordinal).ToArray()));

    internal static string ComputeIdentity(string formatVersion, string definitionIdentity,
        string oosReportIdentity, string oosReportCanonicalJson,
        string researchManifestIdentity, string researchManifestCanonicalJson,
        string criteriaIdentity, string criteriaCanonicalJson,
        string multiSourceManifestIdentity, string multiSourceManifestCanonicalJson,
        string replayInputIdentity,
        string configurationIdentity, string frozenModelVersionsIdentity,
        IEnumerable<FrozenOosModelVersion> frozenModelVersions,
        IEnumerable<FrozenOosModelVersion> currentModelVersions,
        IEnumerable<PromotionEvidenceSnapshot> evidence, IEnumerable<string> provenance,
        IEnumerable<string> exclusions, IEnumerable<string> blockers, IEnumerable<string> unknowns)
    {
        string canonical = CoreJsonSerializer.Serialize(new BundleWire(formatVersion, null,
            definitionIdentity, oosReportIdentity, oosReportCanonicalJson,
            researchManifestIdentity, researchManifestCanonicalJson, criteriaIdentity,
            criteriaCanonicalJson, multiSourceManifestIdentity, multiSourceManifestCanonicalJson,
            replayInputIdentity, configurationIdentity,
            frozenModelVersionsIdentity, frozenModelVersions.ToArray(), currentModelVersions.ToArray(),
            SnapshotEvidence(evidence), SnapshotText(provenance, nameof(provenance)),
            SnapshotText(exclusions, nameof(exclusions)), SnapshotText(blockers, nameof(blockers)),
            SnapshotText(unknowns, nameof(unknowns))));
        return Hash(canonical);
    }

    private static ReadOnlyCollection<FrozenOosModelVersion> SnapshotModels(
        IEnumerable<FrozenOosModelVersion> values)
    {
        FrozenOosModelVersion[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null)
            || snapshot.Select(static value => value.Component).Distinct(StringComparer.Ordinal)
                .Count() != snapshot.Length)
        {
            throw new ArgumentException("Model components must be unique.", nameof(values));
        }

        return new ReadOnlyCollection<FrozenOosModelVersion>(snapshot
            .OrderBy(static value => value.Component, StringComparer.Ordinal).ToArray());
    }

    private static ReadOnlyCollection<PromotionEvidenceSnapshot> SnapshotEvidence(
        IEnumerable<PromotionEvidenceSnapshot> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PromotionEvidenceSnapshot[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Evidence cannot contain null entries.", nameof(values));
        }

        return new ReadOnlyCollection<PromotionEvidenceSnapshot>(snapshot
            .OrderBy(static value => value.Category)
            .ThenBy(static value => value.SnapshotIdentity, StringComparer.Ordinal).ToArray());
    }

    private static ReadOnlyCollection<string> SnapshotText(IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        string[] snapshot = values.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace)
            || snapshot.Any(static value => value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException("Bundle text values must be non-empty single-line text.",
                parameterName);
        }

        return new ReadOnlyCollection<string>(snapshot.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray());
    }

    private static void RequireIdentity(string value, string parameterName) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

    private static void RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Canonical evidence JSON cannot contain line breaks.",
                parameterName);
        }
    }

    private static void ValidateCanonicalBindings(string oosReportJson, string oosReportIdentity,
        string researchJson, string researchIdentity, string criteriaJson, string criteriaIdentity,
        string multiSourceJson, string multiSourceIdentity)
    {
        if (!StringComparer.Ordinal.Equals(FrozenOosEvaluationReport.Deserialize(oosReportJson).Identity,
                oosReportIdentity)
            || !StringComparer.Ordinal.Equals(FrozenResearchManifest.Deserialize(researchJson).Identity,
                researchIdentity)
            || !StringComparer.Ordinal.Equals(PredeclaredPromotionCriteria.Deserialize(criteriaJson).Identity,
                criteriaIdentity)
            || !StringComparer.Ordinal.Equals(
                CoreJsonSerializer.Deserialize<MultiSourceDatasetManifest>(multiSourceJson).Identity,
                multiSourceIdentity))
        {
            throw new JsonException("Promotion bundle canonical bindings do not match their identities.");
        }
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record BundleWire(string FormatVersion, string? BundleIdentity,
        string DefinitionIdentity, string OosReportIdentity, string OosReportCanonicalJson,
        string ResearchManifestIdentity, string ResearchManifestCanonicalJson,
        string CriteriaIdentity, string CriteriaCanonicalJson,
        string MultiSourceManifestIdentity, string MultiSourceManifestCanonicalJson,
        string ReplayInputIdentity,
        string ConfigurationIdentity, string FrozenModelVersionsIdentity,
        IReadOnlyList<FrozenOosModelVersion> FrozenModelVersions,
        IReadOnlyList<FrozenOosModelVersion> CurrentModelVersions,
        IReadOnlyList<PromotionEvidenceSnapshot> Evidence, IReadOnlyList<string> Provenance,
        IReadOnlyList<string> Exclusions, IReadOnlyList<string> Blockers,
        IReadOnlyList<string> Unknowns);
}

public static class PromotionEvidenceBundleBuilder
{
    public static PromotionEvidenceBundle Build(FrozenOosEvaluationDefinition definition,
        FrozenOosEvaluationReport oosReport, LaneCQualityReport? qualityReport,
        LaneCEvidenceBundle? shadowEvidence, BacktestReport? simulationReport,
        ReconciliationCheckpoint? reconciliation = null,
        IEnumerable<PromotionEvidenceSnapshot>? additionalEvidence = null,
        string? provenance = null) => Build(definition, oosReport,
            new PromotionEvidenceBundleSources(qualityReport, shadowEvidence, simulationReport,
                reconciliation, additionalEvidence, provenance));

    public static PromotionEvidenceBundle Build(PromotionEvidenceBundleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Build(input.Definition, input.OosReport, input.Sources);
    }

    public static PromotionEvidenceBundle Build(FrozenOosEvaluationDefinition definition,
        FrozenOosEvaluationReport oosReport, PromotionEvidenceBundleSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(oosReport);
        sources ??= new PromotionEvidenceBundleSources();
        ValidateOosBinding(definition, oosReport);

        List<PromotionEvidenceSnapshot> evidence = [];
        LaneCQualityReport? qualityReport = sources.QualityReport;
        if (qualityReport is null)
        {
            evidence.Add(PromotionEvidenceSnapshot.Unknown(PromotionEvidenceCategory.DataQuality,
                "lane-c-quality-report-not-provided", QualityFields(oosReport),
                provenance: ["frozen-oos-report"], referencedIdentities: QualityReferences(definition)));
        }
        else
        {
            ValidateQualityBinding(definition, qualityReport);
            List<string> reasons = qualityReport.Quality.Reasons.ToList();
            List<string> blockers = qualityReport.Quality.State == LaneCQualityState.Ready
                ? [] : reasons.Select(reason => $"quality:{reason}").ToList();
            List<string> unknowns = qualityReport.Quality.UnknownDiagnostics.Count == 0
                ? [] : ["quality-unknown-diagnostics-present"];
            evidence.Add(PromotionEvidenceSnapshot.FromArtifact(
                PromotionEvidenceCategory.DataQuality, qualityReport.ReportIdentity,
                CoreJsonSerializer.Serialize(qualityReport),
                [new("manifestId", qualityReport.ManifestId),
                 new("checkpoint.streamId", qualityReport.Checkpoint.StreamId),
                 new("checkpoint.nextCaptureSequence", qualityReport.Checkpoint.NextCaptureSequence.ToString(CultureInfo.InvariantCulture)),
                 new("inputIdentity", qualityReport.Quality.InputIdentity),
                 new("cursor", CoreJsonSerializer.Serialize(qualityReport.Quality.Cursor))],
                provenance: ["lane-c-quality-report"],
                unknowns: unknowns, blockers: blockers,
                referencedIdentities: QualityReferences(definition)));
        }

        ReconciliationCheckpoint? reconciliation = sources.Reconciliation ?? oosReport.Reconciliation;
        if (reconciliation is null)
        {
            evidence.Add(PromotionEvidenceSnapshot.Missing(PromotionEvidenceCategory.Reconciliation,
                "reconciliation-evidence-missing", provenance: ["frozen-oos-report"],
                referencedIdentities: [oosReport.Identity]));
        }
        else
        {
            List<string> blockers = reconciliation.Status == ReconciliationStatus.Acknowledged
                ? [] : ["reconciliation-unacknowledged"];
            List<string> unknowns = reconciliation.UnknownMarkers.Count == 0
                ? [] : ["reconciliation-unknown-markers-present"];
            evidence.Add(PromotionEvidenceSnapshot.FromArtifact(
                PromotionEvidenceCategory.Reconciliation, null,
                CoreJsonSerializer.Serialize(reconciliation),
                [new("portfolioId", reconciliation.PortfolioId.Value),
                 new("revision", reconciliation.Revision.ToString(CultureInfo.InvariantCulture)),
                 new("status", reconciliation.Status.ToString())],
                provenance: ["frozen-oos-report"], unknowns: unknowns, blockers: blockers,
                referencedIdentities: [oosReport.Identity]));
        }

        LaneCEvidenceBundle? shadow = sources.ShadowEvidence;
        if (shadow is null)
        {
            evidence.Add(PromotionEvidenceSnapshot.Missing(PromotionEvidenceCategory.Shadow,
                "research-shadow-evidence-missing", provenance: ["frozen-oos-report"],
                referencedIdentities: [oosReport.Identity]));
        }
        else
        {
            if (qualityReport is not null && !StringComparer.Ordinal.Equals(
                    shadow.QualityReportIdentity, qualityReport.ReportIdentity))
            {
                throw new ArgumentException(
                    "Shadow evidence does not reference the supplied quality report.",
                    nameof(sources));
            }

            if (qualityReport is null && !QualityReferences(definition).Contains(
                    shadow.QualityReportIdentity, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "Shadow evidence quality identity is not referenced by the frozen source manifest.",
                    nameof(sources));
            }

            List<string> blockers = ["research-shadow-evidence-is-not-operational-shadow"];
            if (shadow.DecisionEvidence.Status == DecisionEvidenceStatus.Unknown)
            {
                blockers.Add("shadow-decision-unknown");
            }

            evidence.Add(PromotionEvidenceSnapshot.FromArtifact(PromotionEvidenceCategory.Shadow,
                shadow.BundleIdentity, CoreJsonSerializer.Serialize(shadow),
                [new("manifestId", shadow.ManifestId),
                 new("checkpoint.streamId", shadow.Checkpoint.StreamId),
                 new("checkpoint.nextCaptureSequence", shadow.Checkpoint.NextCaptureSequence.ToString(CultureInfo.InvariantCulture)),
                 new("qualityReportIdentity", shadow.QualityReportIdentity),
                 new("featureInputIdentity", shadow.Features.InputIdentity),
                 new("featureCursor", CoreJsonSerializer.Serialize(shadow.Features.Cursor)),
                 new("shadowResultIdentity", shadow.Shadow.ResultIdentity)],
                provenance: ["lane-c-evidence-bundle", "synthetic-fixture-only"],
                unknowns: shadow.DecisionEvidence.Status == DecisionEvidenceStatus.Unknown
                    ? ["shadow-decision-status-unknown"] : [], blockers: blockers,
                referencedIdentities: [shadow.BundleIdentity, shadow.QualityReportIdentity,
                    shadow.ExclusionLedgerIdentity]));
        }

        BacktestReport? simulation = sources.SimulationReport;
        if (simulation is null)
        {
            evidence.Add(PromotionEvidenceSnapshot.Missing(PromotionEvidenceCategory.Simulation,
                "research-simulation-evidence-missing", provenance: ["frozen-oos-report"],
                referencedIdentities: [oosReport.Identity]));
        }
        else
        {
            ValidateSimulationBinding(definition, simulation);
            List<string> unknowns = [];
            if (simulation.FillSimulationVersion is null)
            {
                unknowns.Add("fill-simulation-version-unknown");
            }

            List<string> blockers = ["research-simulation-evidence-is-not-operational-proof"];
            evidence.Add(PromotionEvidenceSnapshot.FromArtifact(
                PromotionEvidenceCategory.Simulation, simulation.Identity,
                simulation.CanonicalJson,
                [new("runId", simulation.RunId), new("streamId", simulation.StreamId),
                 new("deterministicFingerprint", simulation.DeterministicFingerprint),
                 new("fillSimulationVersion", simulation.FillSimulationVersion?.ToString()),
                 new("configurationIdentity", definition.ConfigurationIdentity),
                 new("currentModelVersionsIdentity", oosReport.CurrentModelVersions.Identity)],
                provenance: ["backtest-report", "synthetic-fixture-only"], unknowns: unknowns,
                blockers: blockers, referencedIdentities: [simulation.Identity,
                    definition.ConfigurationIdentity, definition.Models.Identity,
                    oosReport.CurrentModelVersions.Identity]));
        }

        PromotionEvidenceCategory[] operationalCategories =
            [PromotionEvidenceCategory.OperationalShadow, PromotionEvidenceCategory.LocalPaper,
             PromotionEvidenceCategory.ExchangeTest];
        foreach (PromotionEvidenceCategory category in operationalCategories)
        {
            if (sources.AdditionalEvidence.Any(value => value.Category == category))
            {
                continue;
            }

            string label = category switch
            {
                PromotionEvidenceCategory.OperationalShadow => "operational-shadow",
                PromotionEvidenceCategory.LocalPaper => "local-paper",
                PromotionEvidenceCategory.ExchangeTest => "exchange-test",
                _ => category.ToString()
            };
            evidence.Add(PromotionEvidenceSnapshot.Missing(category,
                $"{label}-evidence-not-provided", provenance: ["not-captured"],
                referencedIdentities: [definition.Identity, oosReport.Identity]));
        }

        foreach (PromotionEvidenceSnapshot additional in sources.AdditionalEvidence)
        {
            if (evidence.Any(value => value.Category == additional.Category))
            {
                throw new ArgumentException(
                    $"Additional evidence duplicates category {additional.Category}.",
                    nameof(sources));
            }

            evidence.Add(additional);
        }

        List<string> blockersFromReport = oosReport.Blockers.ToList();
        blockersFromReport.AddRange(oosReport.QualityReasons.Select(reason => $"quality:{reason}"));
        blockersFromReport.AddRange(definition.Criteria.UnresolvedBlockers.Select(reason =>
            $"criteria-declaration-unresolved:{reason}"));
        blockersFromReport.AddRange(oosReport.CriterionAssessments.SelectMany(static assessment =>
            assessment.UnresolvedReasons.Select(reason =>
                $"criterion-unresolved:{assessment.CriterionId}:{reason}")));
        blockersFromReport.AddRange(evidence.SelectMany(static value => value.Blockers));
        List<string> unknownsFromReport = oosReport.CriterionAssessments
            .Where(static assessment => assessment.Status == FrozenOosCriterionStatus.Unknown)
            .Select(static assessment => $"criterion-unknown:{assessment.CriterionId}").ToList();
        unknownsFromReport.AddRange(evidence.SelectMany(static value => value.Unknowns));

        List<string> exclusions = definition.ResearchManifest.Exclusions
            .Concat(oosReport.Exclusions).Distinct(StringComparer.Ordinal).ToList();
        List<string> provenance = [definition.ResearchManifest.Provenance,
            "frozen-oos-report", sources.Provenance ?? "synthetic-fixture-only"];

        return PromotionEvidenceBundle.Create(definition, oosReport, evidence, provenance,
            exclusions, blockersFromReport, unknownsFromReport);
    }

    private static void ValidateOosBinding(FrozenOosEvaluationDefinition definition,
        FrozenOosEvaluationReport report)
    {
        if (!StringComparer.Ordinal.Equals(report.DefinitionIdentity, definition.Identity)
            || !StringComparer.Ordinal.Equals(report.ResearchManifestIdentity,
                definition.ResearchManifest.Identity)
            || !StringComparer.Ordinal.Equals(report.CriteriaIdentity, definition.Criteria.Identity)
            || !StringComparer.Ordinal.Equals(report.MultiSourceManifestIdentity,
                definition.MultiSourceManifest.Identity)
            || !StringComparer.Ordinal.Equals(report.ReplayInputIdentity,
                definition.ReplayInputIdentity)
            || !StringComparer.Ordinal.Equals(report.ConfigurationIdentity,
                definition.ConfigurationIdentity)
            || !StringComparer.Ordinal.Equals(report.FrozenModelVersionsIdentity,
                definition.Models.Identity))
        {
            throw new ArgumentException("The OOS report is not bound to the frozen definition.",
                nameof(report));
        }
    }

    private static void ValidateQualityBinding(FrozenOosEvaluationDefinition definition,
        LaneCQualityReport report)
    {
        bool linked = definition.MultiSourceManifest.Sources.Any(source =>
            StringComparer.Ordinal.Equals(source.QualityEvidenceIdentity, report.ReportIdentity)
            || source.SupportingEvidenceIdentities.Contains(report.ReportIdentity,
                StringComparer.Ordinal));
        if (!linked)
        {
            throw new ArgumentException(
                "The quality report identity is not referenced by the frozen source manifest.",
                nameof(report));
        }
    }

    private static void ValidateSimulationBinding(FrozenOosEvaluationDefinition definition,
        BacktestReport report)
    {
        if (definition.ResearchManifest.StreamId.Length > 0
            && report.StreamId.Length > 0
            && !StringComparer.Ordinal.Equals(definition.ResearchManifest.StreamId,
                report.StreamId))
        {
            throw new ArgumentException("The simulation stream does not match the frozen manifest.",
                nameof(report));
        }

        if (report.Configuration.Count > 0)
        {
            definition.ResearchManifest.ValidateConfiguration(
                definition.ResearchManifest.ConfigurationId, report.Configuration);
        }
    }

    private static PromotionEvidenceCausalField[] QualityFields(
        FrozenOosEvaluationReport report) => report.QualityEvidence.SelectMany(source => new[]
        {
            new PromotionEvidenceCausalField($"source.{source.SourceId}.coverage",
                source.Coverage.ToString()),
            new PromotionEvidenceCausalField($"source.{source.SourceId}.quality",
                source.Quality.ToString()),
            new PromotionEvidenceCausalField($"source.{source.SourceId}.recordCount",
                source.RecordCount.ToString(CultureInfo.InvariantCulture))
        }).ToArray();

    private static string[] QualityReferences(
        FrozenOosEvaluationDefinition definition) => definition.MultiSourceManifest.Sources
        .SelectMany(source => new[] { source.SourceManifestIdentity, source.QualityEvidenceIdentity }
            .Where(static value => value is not null).Select(static value => value!))
        .Concat(definition.MultiSourceManifest.Sources.SelectMany(static source =>
            source.SupportingEvidenceIdentities))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
