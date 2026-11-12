using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Backtesting;

public enum SignalStudyDeclarationState
{
    Declared,
    Unknown
}

public enum SignalStudyDefinitionState
{
    Ready,
    Unknown
}

public enum SignalStudyInputKind
{
    Dataset,
    PointInTimeUniverse,
    Configuration,
    Formula,
    SeedHistory,
    Labels,
    Horizons,
    CostModel,
    LatencyAssumptions,
    Exclusions,
    OutOfSampleDataset
}

public enum SignalStudyCriterionKind
{
    PrimaryEstimand,
    NullHypothesis,
    PrecisionPolicy,
    RejectPolicy,
    MultiplicityPolicy
}

public enum SignalStudyTrialRole
{
    Candidate,
    Control
}

/// <summary>A required hashed input, or an explicit unresolved declaration.</summary>
public sealed record SignalStudyHashDeclaration
{
    public SignalStudyInputKind Kind { get; }

    public SignalStudyDeclarationState State { get; }

    public string? ContentHash { get; }

    public string? UnknownReason { get; }

    private SignalStudyHashDeclaration(SignalStudyInputKind kind,
        SignalStudyDeclarationState state, string? contentHash, string? unknownReason)
    {
        Kind = kind;
        State = state;
        ContentHash = contentHash;
        UnknownReason = unknownReason;
    }

    public static SignalStudyHashDeclaration Declared(SignalStudyInputKind kind,
        string contentHash)
    {
        RequireDefined(kind, nameof(kind));
        RequireHash(contentHash, nameof(contentHash));
        return new SignalStudyHashDeclaration(kind,
            SignalStudyDeclarationState.Declared, contentHash, null);
    }

    public static SignalStudyHashDeclaration Unknown(SignalStudyInputKind kind,
        string reason)
    {
        RequireDefined(kind, nameof(kind));
        return new SignalStudyHashDeclaration(kind,
            SignalStudyDeclarationState.Unknown, null, RequireText(reason, nameof(reason)));
    }

    internal static SignalStudyHashDeclaration FromWire(
        SignalStudyHashDeclarationWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return wire.State switch
        {
            SignalStudyDeclarationState.Declared when wire.UnknownReason is null =>
                Declared(wire.Kind, wire.ContentHash!),
            SignalStudyDeclarationState.Unknown when wire.ContentHash is null =>
                Unknown(wire.Kind, wire.UnknownReason!),
            _ => throw new JsonException("A signal-study input declaration is malformed.")
        };
    }

    internal SignalStudyHashDeclarationWire ToWire() => new(Kind, State,
        ContentHash, UnknownReason);

    internal static void RequireHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        try
        {
            if (Convert.FromHexString(value).Length == SHA256.HashSizeInBytes
                && StringComparer.Ordinal.Equals(value, value.ToLowerInvariant()))
            {
                return;
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("A study input hash must be lowercase SHA-256.",
            parameterName);
    }

    internal static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("Study declarations cannot contain line breaks.",
                parameterName);
        }

        return value;
    }

    internal static void RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

/// <summary>A predeclared estimand, null, precision, reject, or multiplicity rule.</summary>
public sealed record SignalStudyCriterionDeclaration
{
    public SignalStudyCriterionKind Kind { get; }

    public SignalStudyDeclarationState State { get; }

    public string? Value { get; }

    public string? UnknownReason { get; }

    private SignalStudyCriterionDeclaration(SignalStudyCriterionKind kind,
        SignalStudyDeclarationState state, string? value, string? unknownReason)
    {
        Kind = kind;
        State = state;
        Value = value;
        UnknownReason = unknownReason;
    }

    public static SignalStudyCriterionDeclaration Declared(
        SignalStudyCriterionKind kind, string value)
    {
        SignalStudyHashDeclaration.RequireDefined(kind, nameof(kind));
        return new SignalStudyCriterionDeclaration(kind,
            SignalStudyDeclarationState.Declared, value is null
                ? throw new ArgumentNullException(nameof(value))
                : SignalStudyHashDeclaration.RequireText(value, nameof(value)), null);
    }

    public static SignalStudyCriterionDeclaration Unknown(
        SignalStudyCriterionKind kind, string reason)
    {
        SignalStudyHashDeclaration.RequireDefined(kind, nameof(kind));
        return new SignalStudyCriterionDeclaration(kind,
            SignalStudyDeclarationState.Unknown, null,
            SignalStudyHashDeclaration.RequireText(reason, nameof(reason)));
    }

    internal static SignalStudyCriterionDeclaration FromWire(
        SignalStudyCriterionDeclarationWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return wire.State switch
        {
            SignalStudyDeclarationState.Declared when wire.UnknownReason is null =>
                Declared(wire.Kind, wire.Value!),
            SignalStudyDeclarationState.Unknown when wire.Value is null =>
                Unknown(wire.Kind, wire.UnknownReason!),
            _ => throw new JsonException("A signal-study criterion is malformed.")
        };
    }

    internal SignalStudyCriterionDeclarationWire ToWire() => new(Kind, State,
        Value, UnknownReason);
}

/// <summary>A single pre-evaluation candidate or control registration.</summary>
public sealed record SignalStudyTrial
{
    public string TrialId { get; }

    public string HypothesisId { get; }

    public string InputHash { get; }

    public SignalStudyTrialRole Role { get; }

    public SignalStudyTrial(string trialId, string hypothesisId, string inputHash,
        SignalStudyTrialRole role)
    {
        TrialId = SignalStudyHashDeclaration.RequireText(trialId, nameof(trialId));
        HypothesisId = SignalStudyHashDeclaration.RequireText(hypothesisId,
            nameof(hypothesisId));
        SignalStudyHashDeclaration.RequireHash(inputHash, nameof(inputHash));
        SignalStudyHashDeclaration.RequireDefined(role, nameof(role));
        InputHash = inputHash;
        Role = role;
    }

    internal SignalStudyTrialWire ToWire() => new(TrialId, HypothesisId, InputHash,
        Role);
}

/// <summary>A canonically ordered, immutable registry of attempted hypotheses.</summary>
public sealed class SignalStudyTrialRegistry
{
    private readonly ReadOnlyCollection<SignalStudyTrial> _trials;

    private SignalStudyTrialRegistry(string? unknownReason,
        IReadOnlyList<SignalStudyTrial> trials)
    {
        UnknownReason = unknownReason;
        _trials = new ReadOnlyCollection<SignalStudyTrial>(trials.ToArray());
    }

    public SignalStudyDefinitionState State => UnknownReason is null
        ? SignalStudyDefinitionState.Ready : SignalStudyDefinitionState.Unknown;

    public string? UnknownReason { get; }

    public IReadOnlyList<SignalStudyTrial> Trials => _trials;

    public static SignalStudyTrialRegistry Unknown(string reason) => new(
        SignalStudyHashDeclaration.RequireText(reason, nameof(reason)), []);

    public static SignalStudyTrialRegistry Declared(
        IEnumerable<SignalStudyTrial> trials)
    {
        ArgumentNullException.ThrowIfNull(trials);
        SignalStudyTrial[] snapshot = trials.ToArray();
        if (snapshot.Length == 0 || snapshot.Any(static trial => trial is null))
        {
            throw new ArgumentException(
                "A declared trial registry must contain non-null trial entries.",
                nameof(trials));
        }

        if (snapshot.Select(static trial => trial.TrialId)
            .Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Trial IDs must be unique.", nameof(trials));
        }

        return new SignalStudyTrialRegistry(null, snapshot
            .OrderBy(static trial => trial.TrialId, StringComparer.Ordinal).ToArray());
    }

    internal SignalStudyTrialRegistryWire ToWire() => new(State, UnknownReason,
        _trials.Select(static trial => trial.ToWire()).ToArray());

    internal static SignalStudyTrialRegistry FromWire(
        SignalStudyTrialRegistryWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (!Enum.IsDefined(wire.State))
        {
            throw new JsonException("The signal-study trial registry state is invalid.");
        }

        if (wire.State == SignalStudyDefinitionState.Unknown)
        {
            if (wire.UnknownReason is null || wire.Trials.Count != 0)
            {
                throw new JsonException("The unknown signal-study trial registry is malformed.");
            }

            return Unknown(wire.UnknownReason);
        }

        if (wire.UnknownReason is not null)
        {
            throw new JsonException("The declared trial registry cannot have an unknown reason.");
        }

        return Declared(wire.Trials.Select(static trial => new SignalStudyTrial(
            trial.TrialId, trial.HypothesisId, trial.InputHash, trial.Role)));
    }
}

public sealed class SignalStudyManifestMismatchException : InvalidOperationException
{
    public SignalStudyManifestMismatchException(string message) : base(message)
    {
    }
}

/// <summary>
/// Immutable signal-only study definition. It contains declarations and split
/// rules only; it runs no evaluation and grants no promotion or execution authority.
/// </summary>
public sealed class SignalStudyManifest
{
    public const string CurrentFormatVersion = "signal-study-manifest-v1";

    public const string WalkForwardMethodVersion = "chronological-walk-forward-v1";

    public const string PurgingMethodVersion = "held-out-label-availability-purging-v1";

    public const string EmbargoMethodVersion = "validation-availability-derived-embargo-v1";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();
    private readonly ReadOnlyCollection<SignalStudyHashDeclaration> _inputHashes;
    private readonly ReadOnlyCollection<SignalStudyCriterionDeclaration> _criteria;
    private readonly ReadOnlyCollection<string> _unknownReasons;

    private SignalStudyManifest(string studyId,
        IReadOnlyList<SignalStudyHashDeclaration> inputHashes,
        IReadOnlyList<SignalStudyCriterionDeclaration> criteria,
        SignalStudyTemporalSplitPlan splitPlan,
        SignalStudyTrialRegistry trialRegistry)
    {
        StudyId = studyId;
        WalkForwardVersion = WalkForwardMethodVersion;
        PurgingVersion = PurgingMethodVersion;
        EmbargoVersion = EmbargoMethodVersion;
        _inputHashes = new ReadOnlyCollection<SignalStudyHashDeclaration>(
            inputHashes.ToArray());
        _criteria = new ReadOnlyCollection<SignalStudyCriterionDeclaration>(
            criteria.ToArray());
        SplitPlan = splitPlan;
        TrialRegistry = trialRegistry;
        _unknownReasons = new ReadOnlyCollection<string>(CollectUnknownReasons(
            _inputHashes, _criteria, splitPlan, trialRegistry));
        State = _unknownReasons.Count == 0
            ? SignalStudyDefinitionState.Ready : SignalStudyDefinitionState.Unknown;

        Identity = ComputeHash(Serialize(ToWire(identity: null)));
        CanonicalJson = Serialize(ToWire(Identity));
    }

    public string FormatVersion { get; } = CurrentFormatVersion;

    public string StudyId { get; }

    public IReadOnlyList<SignalStudyHashDeclaration> InputHashes => _inputHashes;

    public IReadOnlyList<SignalStudyCriterionDeclaration> Criteria => _criteria;

    public SignalStudyTemporalSplitPlan SplitPlan { get; }

    public SignalStudyTrialRegistry TrialRegistry { get; }

    public string WalkForwardVersion { get; }

    public string PurgingVersion { get; }

    public string EmbargoVersion { get; }

    public SignalStudyDefinitionState State { get; }

    public IReadOnlyList<string> UnknownReasons => _unknownReasons;

    public string? OutOfSampleDatasetHash => _inputHashes.Single(item =>
        item.Kind == SignalStudyInputKind.OutOfSampleDataset).ContentHash;

    public string Identity { get; }

    public string CanonicalJson { get; }

    public static SignalStudyManifest Create(string studyId,
        IEnumerable<SignalStudyHashDeclaration> inputHashes,
        IEnumerable<SignalStudyCriterionDeclaration> criteria,
        SignalStudyTemporalSplitPlan splitPlan,
        SignalStudyTrialRegistry trialRegistry)
    {
        string checkedStudyId = SignalStudyHashDeclaration.RequireText(studyId,
            nameof(studyId));
        ArgumentNullException.ThrowIfNull(inputHashes);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(splitPlan);
        ArgumentNullException.ThrowIfNull(trialRegistry);
        SignalStudyHashDeclaration[] inputSnapshot = inputHashes.ToArray();
        SignalStudyCriterionDeclaration[] criterionSnapshot = criteria.ToArray();

        if (inputSnapshot.Any(static value => value is null)
            || criterionSnapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Study declarations cannot contain null entries.");
        }

        RequireExactKinds(inputSnapshot.Select(static value => value.Kind),
            Enum.GetValues<SignalStudyInputKind>(), nameof(inputHashes));
        RequireExactKinds(criterionSnapshot.Select(static value => value.Kind),
            Enum.GetValues<SignalStudyCriterionKind>(), nameof(criteria));

        return new SignalStudyManifest(checkedStudyId,
            inputSnapshot.OrderBy(static value => value.Kind).ToArray(),
            criterionSnapshot.OrderBy(static value => value.Kind).ToArray(),
            splitPlan, trialRegistry);
    }

    public static SignalStudyManifest Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SignalStudyManifestWire? wire;
        try
        {
            wire = JsonSerializer.Deserialize<SignalStudyManifestWire>(json,
                SerializerOptions);
        }
        catch (JsonException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            throw new JsonException("The signal-study manifest is invalid.", exception);
        }

        if (wire is null
            || !StringComparer.Ordinal.Equals(CurrentFormatVersion, wire.FormatVersion)
            || !StringComparer.Ordinal.Equals(WalkForwardMethodVersion,
                wire.WalkForwardVersion)
            || !StringComparer.Ordinal.Equals(PurgingMethodVersion,
                wire.PurgingVersion)
            || !StringComparer.Ordinal.Equals(EmbargoMethodVersion,
                wire.EmbargoVersion))
        {
            throw new JsonException("The signal-study manifest version is unsupported.");
        }

        try
        {
            SignalStudyManifest manifest = Create(wire.StudyId,
                wire.InputHashes.Select(SignalStudyHashDeclaration.FromWire),
                wire.Criteria.Select(SignalStudyCriterionDeclaration.FromWire),
                SignalStudyTemporalSplitPlan.FromWire(wire.SplitPlan),
                SignalStudyTrialRegistry.FromWire(wire.TrialRegistry));
            if (!StringComparer.Ordinal.Equals(manifest.Identity, wire.Identity)
                || !StringComparer.Ordinal.Equals(manifest.CanonicalJson, json))
            {
                throw new JsonException(
                    "The signal-study manifest identity or canonical form is invalid.");
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
            throw new JsonException("The signal-study manifest is malformed.", exception);
        }
    }

    /// <summary>Fails closed when evidence is presented under changed study inputs.</summary>
    public void RequireEvidenceIdentity(string evidenceManifestIdentity)
    {
        SignalStudyHashDeclaration.RequireHash(evidenceManifestIdentity,
            nameof(evidenceManifestIdentity));
        if (!StringComparer.Ordinal.Equals(Identity, evidenceManifestIdentity))
        {
            throw new SignalStudyManifestMismatchException(
                "The evidence is invalidated by a different signal-study manifest identity.");
        }
    }

    private SignalStudyManifestWire ToWire(string? identity) => new(
        FormatVersion, identity, StudyId,
        WalkForwardMethodVersion, PurgingMethodVersion, EmbargoMethodVersion,
        _inputHashes.Select(static value => value.ToWire()).ToArray(),
        _criteria.Select(static value => value.ToWire()).ToArray(),
        SplitPlan.ToWire(), TrialRegistry.ToWire());

    private static string[] CollectUnknownReasons(
        IEnumerable<SignalStudyHashDeclaration> inputHashes,
        IEnumerable<SignalStudyCriterionDeclaration> criteria,
        SignalStudyTemporalSplitPlan splitPlan,
        SignalStudyTrialRegistry trialRegistry) => inputHashes
        .Where(static item => item.State == SignalStudyDeclarationState.Unknown)
        .Select(static item => item.UnknownReason!)
        .Concat(criteria.Where(static item =>
                item.State == SignalStudyDeclarationState.Unknown)
            .Select(static item => item.UnknownReason!))
        .Concat(splitPlan.UnknownReason is null ? [] : [splitPlan.UnknownReason])
        .Concat(trialRegistry.UnknownReason is null ? [] : [trialRegistry.UnknownReason])
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static void RequireExactKinds<TEnum>(IEnumerable<TEnum> actual,
        TEnum[] expected, string parameterName) where TEnum : struct, Enum
    {
        TEnum[] values = actual.ToArray();
        if (values.Length != expected.Length
            || values.Distinct().Count() != expected.Length
            || !expected.ToHashSet().SetEquals(values))
        {
            throw new ArgumentException(
                "A signal-study manifest requires exactly one declaration for every input and criterion kind.",
                parameterName);
        }
    }

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

    private sealed record SignalStudyManifestWire(
        string FormatVersion,
        string? Identity,
        string StudyId,
        string WalkForwardVersion,
        string PurgingVersion,
        string EmbargoVersion,
        IReadOnlyList<SignalStudyHashDeclarationWire> InputHashes,
        IReadOnlyList<SignalStudyCriterionDeclarationWire> Criteria,
        SignalStudyTemporalSplitPlanWire SplitPlan,
        SignalStudyTrialRegistryWire TrialRegistry);
}

internal sealed record SignalStudyHashDeclarationWire(
    SignalStudyInputKind Kind,
    SignalStudyDeclarationState State,
    string? ContentHash,
    string? UnknownReason);

internal sealed record SignalStudyCriterionDeclarationWire(
    SignalStudyCriterionKind Kind,
    SignalStudyDeclarationState State,
    string? Value,
    string? UnknownReason);

internal sealed record SignalStudyTrialRegistryWire(
    SignalStudyDefinitionState State,
    string? UnknownReason,
    IReadOnlyList<SignalStudyTrialWire> Trials);

internal sealed record SignalStudyTrialWire(
    string TrialId,
    string HypothesisId,
    string InputHash,
    SignalStudyTrialRole Role);

/// <summary>A write-once local journal for one canonical signal-study manifest.</summary>
public static class SignalStudyManifestJournal
{
    public static void WriteOnce(SignalStudyManifest manifest, string path)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string fullPath = Path.GetFullPath(
            SignalStudyHashDeclaration.RequireText(path, nameof(path)));
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The manifest path has no directory.",
                nameof(path));
        Directory.CreateDirectory(directory);

        if (File.Exists(fullPath))
        {
            RequireSameManifest(manifest, Read(fullPath));
            return;
        }

        try
        {
            using FileStream stream = new(fullPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
            byte[] payload = Encoding.UTF8.GetBytes(manifest.CanonicalJson);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            RequireSameManifest(manifest, Read(fullPath));
        }
    }

    public static SignalStudyManifest Read(string path)
    {
        string fullPath = Path.GetFullPath(
            SignalStudyHashDeclaration.RequireText(path, nameof(path)));
        return SignalStudyManifest.Deserialize(File.ReadAllText(fullPath,
            Encoding.UTF8));
    }

    private static void RequireSameManifest(SignalStudyManifest expected,
        SignalStudyManifest actual)
    {
        if (!StringComparer.Ordinal.Equals(expected.CanonicalJson,
            actual.CanonicalJson))
        {
            throw new InvalidOperationException(
                "A signal-study manifest journal is write-once; changed inputs require a new study identity and journal.");
        }
    }
}
