using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.App;

/// <summary>Review outcomes are evidence states, not execution states.</summary>
public enum PromotionReviewVerdict
{
    Blocked,
    Rejected,
    AcceptedForReviewGate
}

/// <summary>
/// Immutable copy of one bundle evidence reference. The causal fields retain
/// null source values so an unknown exchange/source timestamp is not inferred.
/// </summary>
public sealed class PromotionReviewEvidenceReference
{
    [JsonConstructor]
    public PromotionReviewEvidenceReference(string evidenceIdentity,
        PromotionEvidenceCategory category, PromotionEvidenceStatus status,
        string? nativeIdentity, string? contentHash,
        IReadOnlyList<PromotionEvidenceCausalField> causalFields,
        IReadOnlyList<string> provenance, IReadOnlyList<string> exclusions,
        IReadOnlyList<string> blockers, IReadOnlyList<string> unknowns,
        IReadOnlyList<string> referencedIdentities)
    {
        PromotionReviewCanonical.RequireIdentity(evidenceIdentity, nameof(evidenceIdentity));
        if (!Enum.IsDefined(category) || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        EvidenceIdentity = evidenceIdentity;
        Category = category;
        Status = status;
        NativeIdentity = PromotionReviewCanonical.OptionalText(nativeIdentity,
            nameof(nativeIdentity));
        ContentHash = PromotionReviewCanonical.OptionalText(contentHash, nameof(contentHash));
        CausalFields = PromotionReviewCanonical.SnapshotFields(causalFields);
        Provenance = PromotionReviewCanonical.SnapshotText(provenance, nameof(provenance));
        Exclusions = PromotionReviewCanonical.SnapshotText(exclusions, nameof(exclusions));
        Blockers = PromotionReviewCanonical.SnapshotText(blockers, nameof(blockers));
        Unknowns = PromotionReviewCanonical.SnapshotText(unknowns, nameof(unknowns));
        ReferencedIdentities = PromotionReviewCanonical.SnapshotText(referencedIdentities,
            nameof(referencedIdentities));
    }

    public string EvidenceIdentity { get; }

    public PromotionEvidenceCategory Category { get; }

    public PromotionEvidenceStatus Status { get; }

    public string? NativeIdentity { get; }

    public string? ContentHash { get; }

    public IReadOnlyList<PromotionEvidenceCausalField> CausalFields { get; }

    public IReadOnlyList<string> Provenance { get; }

    public IReadOnlyList<string> Exclusions { get; }

    public IReadOnlyList<string> Blockers { get; }

    public IReadOnlyList<string> Unknowns { get; }

    public IReadOnlyList<string> ReferencedIdentities { get; }

    public static PromotionReviewEvidenceReference FromSnapshot(
        PromotionEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new PromotionReviewEvidenceReference(snapshot.SnapshotIdentity, snapshot.Category,
            snapshot.Status, snapshot.NativeIdentity, snapshot.ContentHash, snapshot.CausalFields,
            snapshot.Provenance, snapshot.Exclusions, snapshot.Blockers, snapshot.Unknowns,
            snapshot.ReferencedIdentities);
    }
}

/// <summary>
/// Immutable review record for one frozen evidence bundle. A review verdict
/// records a gate result only; it never grants an execution capability.
/// </summary>
public sealed class PromotionReviewRecord
{
    public const string CurrentFormatVersion = "promotion-review-record-v1";
    private readonly bool _grantsExecutionAuthority;

    [JsonConstructor]
    public PromotionReviewRecord(string formatVersion, string decisionIdentity,
        string? reviewAttemptId, PromotionReviewVerdict verdict, string bundleIdentity,
        string bundleCanonicalJson, string definitionIdentity, string oosReportIdentity,
        IReadOnlyList<string> reviewReasons, IReadOnlyList<string> provenance,
        IReadOnlyList<string> exclusions,
        IReadOnlyList<PromotionReviewEvidenceReference> evidenceReferences,
        IReadOnlyList<string> blockers, IReadOnlyList<string> unknowns)
    {
        if (!StringComparer.Ordinal.Equals(formatVersion, CurrentFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        PromotionReviewCanonical.RequireIdentity(decisionIdentity, nameof(decisionIdentity));
        if (!Enum.IsDefined(verdict))
        {
            throw new ArgumentOutOfRangeException(nameof(verdict));
        }

        PromotionReviewCanonical.RequireIdentity(bundleIdentity, nameof(bundleIdentity));
        PromotionReviewCanonical.RequireCanonical(bundleCanonicalJson, nameof(bundleCanonicalJson));
        PromotionReviewCanonical.RequireIdentity(definitionIdentity, nameof(definitionIdentity));
        PromotionReviewCanonical.RequireIdentity(oosReportIdentity, nameof(oosReportIdentity));
        PromotionReviewCanonical.OptionalText(reviewAttemptId, nameof(reviewAttemptId));

        PromotionEvidenceBundle bundle = PromotionEvidenceBundle.Deserialize(bundleCanonicalJson);
        if (!StringComparer.Ordinal.Equals(bundleIdentity, bundle.BundleIdentity)
            || !StringComparer.Ordinal.Equals(definitionIdentity, bundle.DefinitionIdentity)
            || !StringComparer.Ordinal.Equals(oosReportIdentity, bundle.OosReportIdentity))
        {
            throw new ArgumentException(
                "The review record is not bound to the supplied promotion bundle.",
                nameof(bundleCanonicalJson));
        }

        FormatVersion = formatVersion;
        DecisionIdentity = decisionIdentity;
        ReviewAttemptId = PromotionReviewCanonical.OptionalText(reviewAttemptId,
            nameof(reviewAttemptId));
        Verdict = verdict;
        BundleIdentity = bundleIdentity;
        BundleCanonicalJson = bundleCanonicalJson;
        DefinitionIdentity = definitionIdentity;
        OosReportIdentity = oosReportIdentity;
        ReviewReasons = PromotionReviewCanonical.SnapshotText(reviewReasons,
            nameof(reviewReasons));
        Provenance = PromotionReviewCanonical.SnapshotText(provenance, nameof(provenance));
        Exclusions = PromotionReviewCanonical.SnapshotText(exclusions, nameof(exclusions));
        EvidenceReferences = PromotionReviewCanonical.SnapshotEvidenceReferences(
            evidenceReferences);
        Blockers = PromotionReviewCanonical.SnapshotText(blockers, nameof(blockers));
        Unknowns = PromotionReviewCanonical.SnapshotText(unknowns, nameof(unknowns));

        if (!bundle.Blockers.All(blocker => Blockers.Contains(blocker,
                StringComparer.Ordinal))
            || !bundle.Unknowns.All(unknown => Unknowns.Contains(unknown,
                StringComparer.Ordinal))
            || !Provenance.SequenceEqual(bundle.Provenance, StringComparer.Ordinal)
            || !Exclusions.SequenceEqual(bundle.Exclusions, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A review record must preserve the frozen bundle blockers, provenance, "
                + "unknowns, and exclusions.", nameof(blockers));
        }

        PromotionReviewCanonical.ValidateEvidenceReferences(bundle.Evidence, EvidenceReferences);
        if (verdict == PromotionReviewVerdict.AcceptedForReviewGate
            && PromotionReviewCanonical.HasAcceptanceBlockers(Blockers, Unknowns,
                EvidenceReferences, Provenance))
        {
            throw new InvalidOperationException(
                "A review cannot be accepted while evidence is unresolved or synthetic.");
        }

        string expectedIdentity = PromotionReviewCanonical.Hash(
            CoreJsonSerializer.Serialize(ToWire(identity: null)));
        if (!StringComparer.Ordinal.Equals(expectedIdentity, decisionIdentity))
        {
            throw new ArgumentException("Promotion review identity is invalid.",
                nameof(decisionIdentity));
        }

        _grantsExecutionAuthority = false;
        CanonicalJson = CoreJsonSerializer.Serialize(ToWire(DecisionIdentity));
    }

    public string FormatVersion { get; }

    public string DecisionIdentity { get; }

    [JsonIgnore]
    public string Identity => DecisionIdentity;

    public string? ReviewAttemptId { get; }

    public PromotionReviewVerdict Verdict { get; }

    public string BundleIdentity { get; }

    public string BundleCanonicalJson { get; }

    public string DefinitionIdentity { get; }

    public string OosReportIdentity { get; }

    public IReadOnlyList<string> ReviewReasons { get; }

    public IReadOnlyList<string> Provenance { get; }

    public IReadOnlyList<string> Exclusions { get; }

    public IReadOnlyList<PromotionReviewEvidenceReference> EvidenceReferences { get; }

    public IReadOnlyList<string> Blockers { get; }

    public IReadOnlyList<string> Unknowns { get; }

    [JsonIgnore]
    public bool GrantsExecutionAuthority => _grantsExecutionAuthority;

    [JsonIgnore]
    public bool CanExecute => _grantsExecutionAuthority;

    [JsonIgnore]
    public bool HasExecutionAuthority => _grantsExecutionAuthority;

    [JsonIgnore]
    public string CanonicalJson { get; }

    public static PromotionReviewRecord Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ReviewWire wire = CoreJsonSerializer.Deserialize<ReviewWire>(json);
        PromotionReviewRecord record = new(wire.FormatVersion,
            wire.DecisionIdentity ?? throw new JsonException(
                "Promotion review identity is missing."), wire.ReviewAttemptId, wire.Verdict,
            wire.BundleIdentity, wire.BundleCanonicalJson, wire.DefinitionIdentity,
            wire.OosReportIdentity, wire.ReviewReasons, wire.Provenance, wire.Exclusions,
            wire.EvidenceReferences, wire.Blockers, wire.Unknowns);
        if (!StringComparer.Ordinal.Equals(record.CanonicalJson, json))
        {
            throw new JsonException("Promotion review record is not in canonical form.");
        }

        return record;
    }

    internal static PromotionReviewRecord Create(PromotionEvidenceBundle bundle,
        PromotionReviewVerdict verdict, string? reviewAttemptId,
        IEnumerable<string> reviewReasons, IEnumerable<string> provenance,
        IEnumerable<string> exclusions, IEnumerable<PromotionReviewEvidenceReference> evidence,
        IEnumerable<string> blockers, IEnumerable<string> unknowns)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        PromotionReviewEvidenceReference[] evidenceSnapshot =
            PromotionReviewCanonical.SnapshotEvidenceReferences(evidence).ToArray();
        PromotionReviewCanonical.RequireIdentity(bundle.BundleIdentity, nameof(bundle));
        string definitionIdentity = bundle.DefinitionIdentity;
        string oosReportIdentity = bundle.OosReportIdentity;
        ReadOnlyCollection<string> reasonSnapshot = PromotionReviewCanonical.SnapshotText(
            reviewReasons, nameof(reviewReasons));
        ReadOnlyCollection<string> provenanceSnapshot = PromotionReviewCanonical.SnapshotText(
            provenance, nameof(provenance));
        ReadOnlyCollection<string> exclusionSnapshot = PromotionReviewCanonical.SnapshotText(
            exclusions, nameof(exclusions));
        ReadOnlyCollection<string> blockerSnapshot = PromotionReviewCanonical.SnapshotText(
            blockers, nameof(blockers));
        ReadOnlyCollection<string> unknownSnapshot = PromotionReviewCanonical.SnapshotText(
            unknowns, nameof(unknowns));

        string? attempt = PromotionReviewCanonical.OptionalText(reviewAttemptId,
            nameof(reviewAttemptId));
        ReviewWire wire = new(CurrentFormatVersion, null, attempt, verdict,
            bundle.BundleIdentity, bundle.CanonicalJson, definitionIdentity, oosReportIdentity,
            reasonSnapshot.ToArray(), provenanceSnapshot.ToArray(), exclusionSnapshot.ToArray(),
            evidenceSnapshot, blockerSnapshot.ToArray(), unknownSnapshot.ToArray());
        string identity = PromotionReviewCanonical.Hash(CoreJsonSerializer.Serialize(wire));
        return new PromotionReviewRecord(CurrentFormatVersion, identity, attempt, verdict,
            bundle.BundleIdentity, bundle.CanonicalJson, definitionIdentity, oosReportIdentity,
            reasonSnapshot, provenanceSnapshot, exclusionSnapshot, evidenceSnapshot,
            blockerSnapshot, unknownSnapshot);
    }

    private ReviewWire ToWire(string? identity) => new(FormatVersion, identity, ReviewAttemptId,
        Verdict, BundleIdentity, BundleCanonicalJson, DefinitionIdentity, OosReportIdentity,
        ReviewReasons.ToArray(), Provenance.ToArray(), Exclusions.ToArray(),
        EvidenceReferences.ToArray(), Blockers.ToArray(), Unknowns.ToArray());

    private sealed record ReviewWire(string FormatVersion, string? DecisionIdentity,
        string? ReviewAttemptId, PromotionReviewVerdict Verdict, string BundleIdentity,
        string BundleCanonicalJson, string DefinitionIdentity, string OosReportIdentity,
        IReadOnlyList<string> ReviewReasons, IReadOnlyList<string> Provenance,
        IReadOnlyList<string> Exclusions,
        IReadOnlyList<PromotionReviewEvidenceReference> EvidenceReferences,
        IReadOnlyList<string> Blockers, IReadOnlyList<string> Unknowns);
}

/// <summary>A before/after value retained by a durable invalidation record.</summary>
public sealed record PromotionReviewChange(string Name, string? Before, string? After)
{
    public string Name { get; } = PromotionReviewCanonical.RequireChangeText(Name, nameof(Name));

    public string? Before { get; } = PromotionReviewCanonical.OptionalChangeText(Before,
        nameof(Before));

    public string? After { get; } = PromotionReviewCanonical.OptionalChangeText(After,
        nameof(After));
}

/// <summary>
/// Immutable evidence that one prior review is no longer valid. It is stored
/// separately from the review so restoring a source cannot mutate/reinstate it.
/// </summary>
public sealed class PromotionReviewInvalidationRecord
{
    public const string CurrentFormatVersion = "promotion-review-invalidation-v1";

    [JsonConstructor]
    public PromotionReviewInvalidationRecord(string formatVersion, string invalidationIdentity,
        string originalDecisionIdentity, string originalBundleIdentity,
        string? currentBundleIdentity, string? currentBundleCanonicalJson,
        IReadOnlyList<PromotionReviewChange> changedContext,
        IReadOnlyList<PromotionReviewChange> changedEvidence,
        IReadOnlyList<PromotionEvidenceCategory> changedCategories,
        IReadOnlyList<string> reasons)
    {
        if (!StringComparer.Ordinal.Equals(formatVersion, CurrentFormatVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        PromotionReviewCanonical.RequireIdentity(invalidationIdentity,
            nameof(invalidationIdentity));
        PromotionReviewCanonical.RequireIdentity(originalDecisionIdentity,
            nameof(originalDecisionIdentity));
        PromotionReviewCanonical.RequireIdentity(originalBundleIdentity,
            nameof(originalBundleIdentity));
        if (currentBundleCanonicalJson is not null)
        {
            PromotionReviewCanonical.RequireCanonical(currentBundleCanonicalJson,
                nameof(currentBundleCanonicalJson));
            PromotionEvidenceBundle current = PromotionEvidenceBundle.Deserialize(
                currentBundleCanonicalJson);
            if (!StringComparer.Ordinal.Equals(currentBundleIdentity, current.BundleIdentity))
            {
                throw new ArgumentException(
                    "Current bundle identity does not match its canonical bundle.",
                    nameof(currentBundleIdentity));
            }
        }
        else if (currentBundleIdentity is not null)
        {
            PromotionReviewCanonical.RequireIdentity(currentBundleIdentity,
                nameof(currentBundleIdentity));
        }

        FormatVersion = formatVersion;
        InvalidationIdentity = invalidationIdentity;
        OriginalDecisionIdentity = originalDecisionIdentity;
        OriginalBundleIdentity = originalBundleIdentity;
        CurrentBundleIdentity = currentBundleIdentity;
        CurrentBundleCanonicalJson = currentBundleCanonicalJson;
        ChangedContext = PromotionReviewCanonical.SnapshotChanges(changedContext,
            nameof(changedContext));
        ChangedEvidence = PromotionReviewCanonical.SnapshotChanges(changedEvidence,
            nameof(changedEvidence));
        ChangedCategories = PromotionReviewCanonical.SnapshotCategories(changedCategories);
        Reasons = PromotionReviewCanonical.SnapshotText(reasons, nameof(reasons));

        if (Reasons.Count == 0)
        {
            throw new ArgumentException("An invalidation must retain at least one reason.",
                nameof(reasons));
        }

        if (ChangedContext.Count == 0 && ChangedEvidence.Count == 0
            && ChangedCategories.Count == 0)
        {
            throw new ArgumentException("An invalidation must record a changed value or category.",
                nameof(changedContext));
        }

        string expectedIdentity = PromotionReviewCanonical.Hash(
            CoreJsonSerializer.Serialize(ToWire(identity: null)));
        if (!StringComparer.Ordinal.Equals(expectedIdentity, invalidationIdentity))
        {
            throw new ArgumentException("Promotion invalidation identity is invalid.",
                nameof(invalidationIdentity));
        }

        CanonicalJson = CoreJsonSerializer.Serialize(ToWire(InvalidationIdentity));
    }

    public string FormatVersion { get; }

    public string InvalidationIdentity { get; }

    [JsonIgnore]
    public string Identity => InvalidationIdentity;

    public string OriginalDecisionIdentity { get; }

    public string OriginalBundleIdentity { get; }

    public string? CurrentBundleIdentity { get; }

    public string? CurrentBundleCanonicalJson { get; }

    public IReadOnlyList<PromotionReviewChange> ChangedContext { get; }

    public IReadOnlyList<PromotionReviewChange> ChangedEvidence { get; }

    public IReadOnlyList<PromotionEvidenceCategory> ChangedCategories { get; }

    public IReadOnlyList<string> Reasons { get; }

    [JsonIgnore]
    public string CanonicalJson { get; }

    public static PromotionReviewInvalidationRecord Create(PromotionReviewRecord original,
        IEnumerable<PromotionReviewChange> changedContext,
        IEnumerable<PromotionReviewChange> changedEvidence,
        IEnumerable<PromotionEvidenceCategory> changedCategories,
        IEnumerable<string> reasons) => Create(original, currentBundle: null, changedContext,
            changedEvidence, changedCategories, reasons);

    public static PromotionReviewInvalidationRecord Create(PromotionReviewRecord original,
        PromotionEvidenceBundle? currentBundle,
        IEnumerable<PromotionReviewChange> changedContext,
        IEnumerable<PromotionReviewChange> changedEvidence,
        IEnumerable<PromotionEvidenceCategory> changedCategories,
        IEnumerable<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(original);
        string? currentIdentity = currentBundle?.BundleIdentity;
        string? currentCanonical = currentBundle?.CanonicalJson;
        PromotionReviewChange[] context = PromotionReviewCanonical.SnapshotChanges(
            changedContext, nameof(changedContext)).ToArray();
        PromotionReviewChange[] evidence = PromotionReviewCanonical.SnapshotChanges(
            changedEvidence, nameof(changedEvidence)).ToArray();
        PromotionEvidenceCategory[] categories = PromotionReviewCanonical.SnapshotCategories(
            changedCategories).ToArray();
        ReadOnlyCollection<string> reasonSnapshot = PromotionReviewCanonical.SnapshotText(
            reasons, nameof(reasons));
        InvalidationWire wire = new(CurrentFormatVersion, null, original.DecisionIdentity,
            original.BundleIdentity, currentIdentity, currentCanonical, context, evidence,
            categories, reasonSnapshot.ToArray());
        string identity = PromotionReviewCanonical.Hash(CoreJsonSerializer.Serialize(wire));
        return new PromotionReviewInvalidationRecord(CurrentFormatVersion, identity,
            original.DecisionIdentity, original.BundleIdentity, currentIdentity, currentCanonical,
            context, evidence, categories, reasonSnapshot);
    }

    public static PromotionReviewInvalidationRecord FromValidation(
        PromotionReviewRecord original, PromotionEvidenceBundle currentBundle,
        PromotionEvidenceValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(currentBundle);
        ArgumentNullException.ThrowIfNull(validation);
        PromotionEvidenceBundle originalBundle = PromotionEvidenceBundle.Deserialize(
            original.BundleCanonicalJson);
        bool bundleChanged = !StringComparer.Ordinal.Equals(originalBundle.BundleIdentity,
            currentBundle.BundleIdentity);
        if (validation.IsValid && !bundleChanged)
        {
            throw new InvalidOperationException(
                "An unchanged bundle comparison cannot create an invalidation record.");
        }

        List<PromotionReviewChange> context = [];
        AddChange(context, "bundleIdentity", originalBundle.BundleIdentity,
            currentBundle.BundleIdentity);
        AddChange(context, "definitionIdentity", originalBundle.DefinitionIdentity,
            currentBundle.DefinitionIdentity);
        AddChange(context, "oosReportIdentity", originalBundle.OosReportIdentity,
            currentBundle.OosReportIdentity);
        AddChange(context, "researchManifestIdentity", originalBundle.ResearchManifestIdentity,
            currentBundle.ResearchManifestIdentity);
        AddChange(context, "criteriaIdentity", originalBundle.CriteriaIdentity,
            currentBundle.CriteriaIdentity);
        AddChange(context, "multiSourceManifestIdentity",
            originalBundle.MultiSourceManifestIdentity, currentBundle.MultiSourceManifestIdentity);
        AddChange(context, "replayInputIdentity", originalBundle.ReplayInputIdentity,
            currentBundle.ReplayInputIdentity);
        AddChange(context, "configurationIdentity", originalBundle.ConfigurationIdentity,
            currentBundle.ConfigurationIdentity);
        AddChange(context, "frozenModelVersionsIdentity",
            originalBundle.FrozenModelVersionsIdentity, currentBundle.FrozenModelVersionsIdentity);

        List<PromotionReviewChange> evidence = [];
        PromotionEvidenceCategory[] categories = Enum.GetValues<PromotionEvidenceCategory>()
            .Where(category =>
            {
                PromotionEvidenceSnapshot? before = originalBundle.Find(category);
                PromotionEvidenceSnapshot? after = currentBundle.Find(category);
                bool changed = before is null || after is null
                    || !StringComparer.Ordinal.Equals(before.SnapshotIdentity,
                        after.SnapshotIdentity);
                if (changed)
                {
                    evidence.Add(new PromotionReviewChange($"evidence:{category}",
                        before?.SnapshotIdentity, after?.SnapshotIdentity));
                }

                return changed;
            }).ToArray();

        List<PromotionEvidenceCategory> allCategories = categories.ToList();
        foreach (string value in validation.InvalidatedEvidence)
        {
            if (Enum.TryParse(value, ignoreCase: true,
                    out PromotionEvidenceCategory parsed))
            {
                allCategories.Add(parsed);
            }
        }

        if (context.Count == 0 && evidence.Count == 0 && allCategories.Count == 0)
        {
            context.Add(new PromotionReviewChange("bundleValidation", "valid", "invalid"));
        }

        List<string> reasons = validation.Reasons.ToList();
        if (bundleChanged)
        {
            reasons.Add("bundle-identity-changed");
        }

        return Create(original, currentBundle, context, evidence, allCategories,
            reasons.Count == 0 ? ["bundle-context-invalidated"] : reasons);
    }

    public static PromotionReviewInvalidationRecord Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        InvalidationWire wire = CoreJsonSerializer.Deserialize<InvalidationWire>(json);
        PromotionReviewInvalidationRecord record = new(wire.FormatVersion,
            wire.InvalidationIdentity ?? throw new JsonException(
                "Promotion invalidation identity is missing."), wire.OriginalDecisionIdentity,
            wire.OriginalBundleIdentity, wire.CurrentBundleIdentity,
            wire.CurrentBundleCanonicalJson, wire.ChangedContext, wire.ChangedEvidence,
            wire.ChangedCategories, wire.Reasons);
        if (!StringComparer.Ordinal.Equals(record.CanonicalJson, json))
        {
            throw new JsonException("Promotion invalidation is not in canonical form.");
        }

        return record;
    }

    private static void AddChange(List<PromotionReviewChange> changes, string name,
        string? before, string? after)
    {
        if (!StringComparer.Ordinal.Equals(before, after))
        {
            changes.Add(new PromotionReviewChange(name, before, after));
        }
    }

    private InvalidationWire ToWire(string? identity) => new(FormatVersion, identity,
        OriginalDecisionIdentity, OriginalBundleIdentity, CurrentBundleIdentity,
        CurrentBundleCanonicalJson, ChangedContext.ToArray(), ChangedEvidence.ToArray(),
        ChangedCategories.ToArray(), Reasons.ToArray());

    private sealed record InvalidationWire(string FormatVersion, string? InvalidationIdentity,
        string OriginalDecisionIdentity, string OriginalBundleIdentity,
        string? CurrentBundleIdentity, string? CurrentBundleCanonicalJson,
        IReadOnlyList<PromotionReviewChange> ChangedContext,
        IReadOnlyList<PromotionReviewChange> ChangedEvidence,
        IReadOnlyList<PromotionEvidenceCategory> ChangedCategories,
        IReadOnlyList<string> Reasons);
}

/// <summary>Creates a review record without adding criteria or trading policy.</summary>
public static class PromotionReviewRecordBuilder
{
    public static PromotionReviewRecord Review(PromotionEvidenceBundle bundle,
        PromotionReviewVerdict requestedVerdict,
        IEnumerable<string>? reviewReasons = null, string? reviewAttemptId = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (!Enum.IsDefined(requestedVerdict))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedVerdict));
        }

        List<string> blockers = bundle.Blockers.ToList();
        List<string> unknowns = bundle.Unknowns.ToList();
        List<string> reasons = (reviewReasons ?? []).ToList();
        PromotionReviewEvidenceReference[] references = bundle.Evidence
            .Select(PromotionReviewEvidenceReference.FromSnapshot).ToArray();

        foreach (PromotionEvidenceSnapshot evidence in bundle.Evidence)
        {
            switch (evidence.Status)
            {
                case PromotionEvidenceStatus.Missing:
                    blockers.Add($"evidence-missing:{evidence.Category}");
                    break;
                case PromotionEvidenceStatus.Unknown:
                    unknowns.Add($"evidence-unknown:{evidence.Category}");
                    blockers.Add($"evidence-unknown:{evidence.Category}");
                    break;
                case PromotionEvidenceStatus.Invalidated:
                    blockers.Add($"evidence-invalidated:{evidence.Category}");
                    break;
            }
        }

        bool synthetic = PromotionReviewCanonical.ContainsSynthetic(bundle.Provenance)
            || references.Any(reference =>
                PromotionReviewCanonical.ContainsSynthetic(reference.Provenance));
        if (synthetic)
        {
            reasons.Add("synthetic-provenance-present");
            blockers.Add("synthetic-provenance-present");
        }

        if (blockers.Count > 0)
        {
            reasons.Add("unresolved-blockers-present");
        }

        if (unknowns.Count > 0)
        {
            reasons.Add("unknown-values-present");
        }

        if (requestedVerdict == PromotionReviewVerdict.Rejected && reasons.Count == 0)
        {
            reasons.Add("reviewer-rejected");
        }
        else if (requestedVerdict == PromotionReviewVerdict.Blocked && reasons.Count == 0)
        {
            reasons.Add("reviewer-blocked");
        }

        PromotionReviewVerdict verdict = requestedVerdict;
        if (requestedVerdict == PromotionReviewVerdict.AcceptedForReviewGate
            && PromotionReviewCanonical.HasAcceptanceBlockers(blockers, unknowns, references,
                bundle.Provenance))
        {
            verdict = PromotionReviewVerdict.Blocked;
        }

        return PromotionReviewRecord.Create(bundle, verdict, reviewAttemptId, reasons,
            bundle.Provenance, bundle.Exclusions, references, blockers, unknowns);
    }

    public static PromotionReviewRecord Reject(PromotionEvidenceBundle bundle,
        IEnumerable<string> reasons, string? reviewAttemptId = null) =>
        Review(bundle, PromotionReviewVerdict.Rejected, reasons, reviewAttemptId);

    public static PromotionReviewRecord Evaluate(PromotionEvidenceBundle bundle,
        PromotionReviewVerdict requestedVerdict, string? reviewAttemptId = null) => Review(bundle,
            requestedVerdict, reviewAttemptId: reviewAttemptId);
}

/// <summary>
/// Append-only file journal. Decisions and invalidations intentionally use
/// separate files so invalidation never rewrites a historical decision.
/// </summary>
public sealed class PromotionReviewJournal : IDisposable
{
    private const string ReviewDirectoryName = "reviews";
    private const string InvalidationDirectoryName = "invalidations";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _sync = new();
    private readonly string _reviewDirectory;
    private readonly string _invalidationDirectory;

    private PromotionReviewJournal(string directoryPath)
    {
        DirectoryPath = directoryPath;
        _reviewDirectory = Path.Combine(directoryPath, ReviewDirectoryName);
        _invalidationDirectory = Path.Combine(directoryPath, InvalidationDirectoryName);
    }

    public static PromotionReviewJournal Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string directoryPath = Path.GetFullPath(path);
        if (File.Exists(directoryPath))
        {
            throw new IOException("The promotion review journal path is a file, not a directory.");
        }

        Directory.CreateDirectory(directoryPath);
        Directory.CreateDirectory(Path.Combine(directoryPath, ReviewDirectoryName));
        Directory.CreateDirectory(Path.Combine(directoryPath, InvalidationDirectoryName));
        return new PromotionReviewJournal(directoryPath);
    }

    public string DirectoryPath { get; }

    [JsonIgnore]
    public string ReviewJournalPath => _reviewDirectory;

    [JsonIgnore]
    public string InvalidationJournalPath => _invalidationDirectory;

    public bool AppendReview(PromotionReviewRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            return WriteImmutableFile(_reviewDirectory, record.DecisionIdentity,
                record.CanonicalJson);
        }
    }

    public bool AppendInvalidation(PromotionReviewInvalidationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            if (!ReadReviewsCore().Any(value => StringComparer.Ordinal.Equals(
                    value.DecisionIdentity, record.OriginalDecisionIdentity)))
            {
                throw new InvalidOperationException(
                    "An invalidation must reference a persisted review decision.");
            }

            return WriteImmutableFile(_invalidationDirectory, record.InvalidationIdentity,
                record.CanonicalJson);
        }
    }

    public IReadOnlyList<PromotionReviewRecord> ReadReviews()
    {
        lock (_sync)
        {
            return ReadReviewsCore();
        }
    }

    public IReadOnlyList<PromotionReviewInvalidationRecord> ReadInvalidations()
    {
        lock (_sync)
        {
            return ReadInvalidationsCore();
        }
    }

    public IReadOnlyList<PromotionReviewInvalidationRecord> ReadInvalidations(
        string decisionIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionIdentity);
        return ReadInvalidations().Where(value => StringComparer.Ordinal.Equals(
            value.OriginalDecisionIdentity, decisionIdentity)).ToArray();
    }

    public PromotionReviewRecord? FindActiveReview(string decisionIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionIdentity);
        lock (_sync)
        {
            PromotionReviewRecord? record = ReadReviewsCore().FirstOrDefault(value =>
                StringComparer.Ordinal.Equals(value.DecisionIdentity, decisionIdentity));
            if (record is null || ReadInvalidationsCore().Any(value =>
                    StringComparer.Ordinal.Equals(value.OriginalDecisionIdentity,
                        decisionIdentity)))
            {
                return null;
            }

            return record;
        }
    }

    public bool IsInvalidated(string decisionIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionIdentity);
        lock (_sync)
        {
            return ReadInvalidationsCore().Any(value => StringComparer.Ordinal.Equals(
                value.OriginalDecisionIdentity, decisionIdentity));
        }
    }

    public void Dispose()
    {
    }

    private ReadOnlyCollection<PromotionReviewRecord> ReadReviewsCore()
    {
        return ReadRecords(_reviewDirectory, PromotionReviewRecord.Deserialize,
            static value => value.DecisionIdentity);
    }

    private ReadOnlyCollection<PromotionReviewInvalidationRecord> ReadInvalidationsCore()
    {
        return ReadRecords(_invalidationDirectory, PromotionReviewInvalidationRecord.Deserialize,
            static value => value.InvalidationIdentity);
    }

    private static ReadOnlyCollection<TRecord> ReadRecords<TRecord>(string directoryPath,
        Func<string, TRecord> deserialize, Func<TRecord, string> identity)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new ReadOnlyCollection<TRecord>([]);
        }

        List<TRecord> records = [];
        foreach (string path in Directory.EnumerateFiles(directoryPath, "*.json",
                     SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            string canonicalJson = File.ReadAllText(path, Utf8NoBom);
            TRecord record = deserialize(canonicalJson);
            if (!StringComparer.Ordinal.Equals(fileName, identity(record)))
            {
                throw new InvalidDataException(
                    "A promotion journal filename does not match its content identity.");
            }

            records.Add(record);
        }

        return new ReadOnlyCollection<TRecord>(records);
    }

    private static bool WriteImmutableFile(string directoryPath, string identity,
        string canonicalJson)
    {
        string targetPath = Path.Combine(directoryPath, $"{identity}.json");
        if (File.Exists(targetPath))
        {
            string existing = File.ReadAllText(targetPath, Utf8NoBom);
            if (StringComparer.Ordinal.Equals(existing, canonicalJson))
            {
                return false;
            }

            throw new InvalidOperationException(
                "A content-addressed identity already contains different canonical content.");
        }

        string temporaryPath = Path.Combine(directoryPath,
            $".{identity}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                byte[] bytes = Utf8NoBom.GetBytes(canonicalJson);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, targetPath, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                string existing = File.ReadAllText(targetPath, Utf8NoBom);
                if (StringComparer.Ordinal.Equals(existing, canonicalJson))
                {
                    return false;
                }

                throw new InvalidOperationException(
                    "A concurrent content-addressed write contains different canonical content.");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal static class PromotionReviewCanonical
{
    public static string RequireIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Identities cannot contain line breaks.", parameterName);
        }

        return value;
    }

    public static string RequireCanonical(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Canonical JSON cannot contain line breaks.", parameterName);
        }

        return value;
    }

    public static string? OptionalText(string? value, string parameterName)
    {
        if (value?.Contains('\r') == true || value?.Contains('\n') == true)
        {
            throw new ArgumentException("Optional text cannot contain line breaks.", parameterName);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string RequireChangeText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Change names cannot contain line breaks.", parameterName);
        }

        return value;
    }

    public static string? OptionalChangeText(string? value, string parameterName)
    {
        if (value?.Contains('\r') == true || value?.Contains('\n') == true)
        {
            throw new ArgumentException("Change values cannot contain line breaks.", parameterName);
        }

        return value;
    }

    public static ReadOnlyCollection<PromotionEvidenceCausalField> SnapshotFields(
        IEnumerable<PromotionEvidenceCausalField> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PromotionEvidenceCausalField[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null)
            || snapshot.Select(static value => value.Name).Distinct(StringComparer.Ordinal)
                .Count() != snapshot.Length)
        {
            throw new ArgumentException("Causal field names must be unique.", nameof(values));
        }

        return new ReadOnlyCollection<PromotionEvidenceCausalField>(snapshot
            .OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray());
    }

    public static ReadOnlyCollection<string> SnapshotText(IEnumerable<string> values,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        string[] snapshot = values.ToArray();
        if (snapshot.Any(string.IsNullOrWhiteSpace)
            || snapshot.Any(static value => value.Contains('\r') || value.Contains('\n')))
        {
            throw new ArgumentException("Text values must be non-empty single-line text.",
                parameterName);
        }

        return new ReadOnlyCollection<string>(snapshot.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray());
    }

    public static ReadOnlyCollection<PromotionReviewEvidenceReference> SnapshotEvidenceReferences(
        IEnumerable<PromotionReviewEvidenceReference> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PromotionReviewEvidenceReference[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null)
            || snapshot.Select(static value => value.EvidenceIdentity)
                .Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Evidence identities must be unique.", nameof(values));
        }

        return new ReadOnlyCollection<PromotionReviewEvidenceReference>(snapshot
            .OrderBy(static value => value.Category)
            .ThenBy(static value => value.EvidenceIdentity, StringComparer.Ordinal).ToArray());
    }

    public static ReadOnlyCollection<PromotionReviewChange> SnapshotChanges(
        IEnumerable<PromotionReviewChange> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        PromotionReviewChange[] snapshot = values.ToArray();
        if (snapshot.Any(static value => value is null)
            || snapshot.Select(static value => value.Name).Distinct(StringComparer.Ordinal)
                .Count() != snapshot.Length)
        {
            throw new ArgumentException("Change names must be unique.", parameterName);
        }

        return new ReadOnlyCollection<PromotionReviewChange>(snapshot
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ThenBy(static value => value.Before, StringComparer.Ordinal)
            .ThenBy(static value => value.After, StringComparer.Ordinal).ToArray());
    }

    public static ReadOnlyCollection<PromotionEvidenceCategory> SnapshotCategories(
        IEnumerable<PromotionEvidenceCategory> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PromotionEvidenceCategory[] snapshot = values.ToArray();
        if (snapshot.Any(value => !Enum.IsDefined(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(values));
        }

        return new ReadOnlyCollection<PromotionEvidenceCategory>(snapshot.Distinct().Order()
            .ToArray());
    }

    public static void ValidateEvidenceReferences(
        IReadOnlyList<PromotionEvidenceSnapshot> bundleEvidence,
        IReadOnlyList<PromotionReviewEvidenceReference> references)
    {
        if (bundleEvidence.Count != references.Count)
        {
            throw new ArgumentException(
                "A review record must retain every bundle evidence reference.",
                nameof(references));
        }

        foreach (PromotionEvidenceSnapshot snapshot in bundleEvidence)
        {
            PromotionReviewEvidenceReference? reference = references.FirstOrDefault(value =>
                StringComparer.Ordinal.Equals(value.EvidenceIdentity, snapshot.SnapshotIdentity));
            if (reference is null || reference.Category != snapshot.Category
                || reference.Status != snapshot.Status
                || !StringComparer.Ordinal.Equals(reference.NativeIdentity,
                    snapshot.NativeIdentity)
                || !StringComparer.Ordinal.Equals(reference.ContentHash, snapshot.ContentHash)
                || !reference.CausalFields.SequenceEqual(snapshot.CausalFields)
                || !reference.Provenance.SequenceEqual(snapshot.Provenance,
                    StringComparer.Ordinal)
                || !reference.Exclusions.SequenceEqual(snapshot.Exclusions,
                    StringComparer.Ordinal)
                || !reference.Blockers.SequenceEqual(snapshot.Blockers,
                    StringComparer.Ordinal)
                || !reference.Unknowns.SequenceEqual(snapshot.Unknowns,
                    StringComparer.Ordinal)
                || !reference.ReferencedIdentities.SequenceEqual(snapshot.ReferencedIdentities,
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "A review evidence reference does not match the frozen bundle.",
                    nameof(references));
            }
        }
    }

    public static bool HasAcceptanceBlockers(
        IReadOnlyList<string> blockers, IReadOnlyList<string> unknowns,
        IReadOnlyList<PromotionReviewEvidenceReference> references,
        IReadOnlyList<string> provenance)
    {
        return blockers.Count > 0 || unknowns.Count > 0
            || references.Any(static value => value.Status is PromotionEvidenceStatus.Missing
                or PromotionEvidenceStatus.Unknown or PromotionEvidenceStatus.Invalidated)
            || ContainsSynthetic(provenance)
            || references.Any(static value => ContainsSynthetic(value.Provenance));
    }

    public static bool ContainsSynthetic(IEnumerable<string> values) => values.Any(value =>
        value.Contains("synthetic", StringComparison.OrdinalIgnoreCase));

    public static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
