using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Core;

namespace TradingBot.Backtesting;

/// <summary>One stable, predeclared promotion question and its evidence contract.</summary>
public sealed class PromotionCriterion
{
    public PromotionCriterion(string criterionId, string question,
        IEnumerable<string> requiredEvidence, bool isRequired = true)
    {
        CriterionId = RequireText(criterionId, nameof(criterionId));
        Question = RequireText(question, nameof(question));
        ArgumentNullException.ThrowIfNull(requiredEvidence);
        string[] evidence = requiredEvidence.Select(value => RequireText(value,
                nameof(requiredEvidence))).Order(StringComparer.Ordinal).ToArray();
        if (evidence.Length == 0)
        {
            throw new ArgumentException(
                "A promotion criterion must declare required evidence.",
                nameof(requiredEvidence));
        }

        RequiredEvidence = new ReadOnlyCollection<string>(evidence);
        IsRequired = isRequired;
    }

    public string CriterionId { get; }

    public string Question { get; }

    public IReadOnlyList<string> RequiredEvidence { get; }

    public bool IsRequired { get; }

    public bool Required => IsRequired;

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException(
                "Promotion criteria text cannot contain line breaks.", parameterName);
        }

        return value;
    }
}

/// <summary>
/// Immutable, manifest-bound record of questions and promotion evidence known
/// before an out-of-sample evaluation begins.
/// </summary>
public sealed class PredeclaredPromotionCriteria
{
    public const string CurrentFormatVersion = "predeclared-promotion-criteria-v1";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    private PredeclaredPromotionCriteria(string manifestIdentity,
        IReadOnlyList<PromotionCriterion> criteria,
        IReadOnlyList<string> unresolvedBlockers)
    {
        ManifestIdentity = manifestIdentity;
        Criteria = criteria;
        UnresolvedBlockers = unresolvedBlockers;
        Identity = ComputeHash(Serialize(ToWire(identity: null)));
        CanonicalJson = Serialize(ToWire(Identity));
    }

    public string FormatVersion { get; } = CurrentFormatVersion;

    public string ManifestIdentity { get; }

    public IReadOnlyList<PromotionCriterion> Criteria { get; }

    public IReadOnlyList<string> UnresolvedBlockers { get; }

    public bool IsReady => Criteria.Count > 0 && UnresolvedBlockers.Count == 0;

    public bool IsResolved => IsReady;

    public string Identity { get; }

    public string CriteriaIdentity => Identity;

    public string CanonicalJson { get; }

    public IReadOnlyList<string> Questions => new ReadOnlyCollection<string>(
        Criteria.Select(static value => value.Question).ToArray());

    public IReadOnlyList<string> RequiredEvidence => new ReadOnlyCollection<string>(
        Criteria.SelectMany(static value => value.RequiredEvidence).ToArray());

    public static PredeclaredPromotionCriteria Create(
        FrozenResearchManifest manifest,
        IEnumerable<PromotionCriterion> criteria,
        IEnumerable<string> unresolvedBlockers)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(unresolvedBlockers);

        PromotionCriterion[] criterionSnapshot = criteria.ToArray();
        if (criterionSnapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Promotion criteria cannot contain null entries.",
                nameof(criteria));
        }

        if (criterionSnapshot.Select(static value => value.CriterionId)
            .Distinct(StringComparer.Ordinal).Count() != criterionSnapshot.Length)
        {
            throw new ArgumentException(
                "Promotion criterion identifiers must be unique.", nameof(criteria));
        }

        PromotionCriterion[] criterionCopy = criterionSnapshot
            .OrderBy(static value => value.CriterionId, StringComparer.Ordinal).ToArray();
        string[] blockers = unresolvedBlockers.Select(value => RequireText(value,
                nameof(unresolvedBlockers))).Order(StringComparer.Ordinal).ToArray();
        return new PredeclaredPromotionCriteria(manifest.Identity,
            new ReadOnlyCollection<PromotionCriterion>(criterionCopy),
            new ReadOnlyCollection<string>(blockers));
    }

    public static PredeclaredPromotionCriteria Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CriteriaWire? wire = JsonSerializer.Deserialize<CriteriaWire>(json, SerializerOptions);
        if (wire is null || !StringComparer.Ordinal.Equals(CurrentFormatVersion,
            wire.FormatVersion))
        {
            throw new JsonException("The predeclared promotion criteria format is invalid.");
        }

        try
        {
            PredeclaredPromotionCriteria criteria = CreateFromIdentity(
                wire.ManifestIdentity,
                wire.Criteria.Select(static value => new PromotionCriterion(
                    value.CriterionId, value.Question, value.RequiredEvidence,
                    value.IsRequired)), wire.UnresolvedBlockers);
            if (!StringComparer.Ordinal.Equals(criteria.Identity, wire.Identity)
                || !StringComparer.Ordinal.Equals(criteria.CanonicalJson, json))
            {
                throw new JsonException(
                    "The predeclared promotion criteria integrity is invalid.");
            }

            return criteria;
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException)
        {
            throw new JsonException("The predeclared promotion criteria are invalid.",
                exception);
        }
    }

    internal CriteriaWire ToWire(string? identity) => new(FormatVersion, identity,
        ManifestIdentity, Criteria.Select(static value => new CriterionWire(
            value.CriterionId, value.Question, value.RequiredEvidence.ToArray(),
            value.IsRequired)).ToArray(), UnresolvedBlockers.ToArray());

    private static PredeclaredPromotionCriteria CreateFromIdentity(string identity,
        IEnumerable<PromotionCriterion> criteria, IEnumerable<string> unresolvedBlockers)
    {
        // Criteria deserialization only needs a stable manifest identity. The
        // identity is validated as a SHA-256 value and retained verbatim; no
        // result or dataset is reconstructed from this placeholder.
        if (identity is null || identity.Length != 64
            || identity.Any(static value => !Uri.IsHexDigit(value)))
        {
            throw new JsonException("A manifest identity must be a SHA-256 value.");
        }

        PromotionCriterion[] criterionSnapshot = criteria.ToArray();
        if (criterionSnapshot.Length == 0 || criterionSnapshot.Any(static value => value is null)
            || criterionSnapshot.Select(static value => value.CriterionId)
                .Distinct(StringComparer.Ordinal).Count() != criterionSnapshot.Length)
        {
            throw new JsonException("The predeclared criteria collection is invalid.");
        }

        string[] blockers = unresolvedBlockers.Select(value => RequireText(value,
                nameof(unresolvedBlockers))).Order(StringComparer.Ordinal).ToArray();
        return new PredeclaredPromotionCriteria(identity,
            new ReadOnlyCollection<PromotionCriterion>(criterionSnapshot
                .OrderBy(static value => value.CriterionId, StringComparer.Ordinal).ToArray()),
            new ReadOnlyCollection<string>(blockers));
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException(
                "Promotion criteria text cannot contain line breaks.", parameterName);
        }

        return value;
    }

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

    internal sealed record CriteriaWire(
        string FormatVersion,
        string? Identity,
        string ManifestIdentity,
        IReadOnlyList<CriterionWire> Criteria,
        IReadOnlyList<string> UnresolvedBlockers);

    internal sealed record CriterionWire(
        string CriterionId,
        string Question,
        IReadOnlyList<string> RequiredEvidence,
        bool IsRequired);
}

/// <summary>A BCL-only write-once journal for predeclared criteria.</summary>
public sealed class PredeclaredPromotionCriteriaJournal
{
    private PredeclaredPromotionCriteria? declaration;

    public PredeclaredPromotionCriteria? Declaration => declaration;

    public void Register(PredeclaredPromotionCriteria value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (declaration is null)
        {
            declaration = value;
            return;
        }

        if (!StringComparer.Ordinal.Equals(declaration.CanonicalJson, value.CanonicalJson))
        {
            throw new InvalidOperationException(
                "A predeclared promotion criteria journal is write-once.");
        }
    }

    public PredeclaredPromotionCriteria Read() => declaration ?? throw new InvalidOperationException(
        "The predeclared promotion criteria journal has no registered declaration.");
}

/// <summary>
/// Coordinates a frozen manifest and predeclared criteria. Replay cannot start
/// until both are registered and all required blockers are resolved.
/// </summary>
public sealed class FrozenResearchEvaluationContext
{
    private PredeclaredPromotionCriteria? criteria;

    public FrozenResearchEvaluationContext(FrozenResearchManifest manifest,
        PredeclaredPromotionCriteria? criteria = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Manifest = manifest;
        if (criteria is not null)
        {
            ValidateCriteria(criteria);
        }

        this.criteria = criteria;
    }

    public FrozenResearchManifest Manifest { get; }

    public PredeclaredPromotionCriteria? Criteria => criteria;

    public bool IsReady => criteria is not null && criteria.IsReady;

    public void RegisterCriteria(PredeclaredPromotionCriteria value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateCriteria(value);
        if (criteria is null)
        {
            criteria = value;
            return;
        }

        if (!StringComparer.Ordinal.Equals(criteria.CanonicalJson, value.CanonicalJson))
        {
            throw new InvalidOperationException(
                "Predeclared promotion criteria cannot be replaced after registration.");
        }
    }

    public ReplayRunResult RunReplay(string streamId, IEnumerable<ReplayEnvelope> events,
        Action<ReplayStepContext> handler)
    {
        EnsureReady();
        return Manifest.RunReplay(streamId, events, handler);
    }

    public ReplayRunResult RunReplay(string streamId, IEnumerable<ReplayEnvelope> events,
        Func<ReplayStepContext, object?> handler)
    {
        EnsureReady();
        return Manifest.RunReplay(streamId, events, handler);
    }

    public FrozenResearchEvaluationSnapshot Capture(BacktestReport report)
    {
        EnsureReady();
        return FrozenResearchEvaluationSnapshot.Create(Manifest, criteria!, report);
    }

    private void EnsureReady()
    {
        if (criteria is null)
        {
            throw new InvalidOperationException(
                "A predeclared promotion criteria record must be registered before evaluation.");
        }

        if (!criteria.IsReady)
        {
            throw new InvalidOperationException(
                "Unresolved promotion criteria block evaluation readiness.");
        }
    }

    private void ValidateCriteria(PredeclaredPromotionCriteria value)
    {
        if (!StringComparer.Ordinal.Equals(Manifest.Identity, value.ManifestIdentity))
        {
            throw new FrozenResearchManifestMismatchException(
                "The promotion criteria do not match the evaluation manifest.");
        }
    }
}
